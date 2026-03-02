using Akka.Actor;
using CliWrap;
using CliWrap.Buffered;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/validator — runs pluggable validators against task artifacts (ADR-011).
/// Supports Script validators (CLI exit-code gate) and AgentReview (placeholder).
/// </summary>
public sealed class ValidatorActor : UntypedActor
{
    private readonly IActorRef _artifactRegistry;
    private readonly ILogger<ValidatorActor> _logger;

    private readonly Dictionary<ArtifactType, List<ValidatorSpec>> _validators = new();
    private readonly Dictionary<string, PendingValidation> _pending = new();

    internal static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(5);

    public ValidatorActor(IActorRef artifactRegistry, ILogger<ValidatorActor> logger)
    {
        _artifactRegistry = artifactRegistry;
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case RegisterValidator register:
                HandleRegister(register);
                break;

            case ValidateArtifact validate:
                HandleValidate(validate);
                break;

            case ScriptResult result:
                HandleScriptResult(result);
                break;
        }
    }

    private void HandleRegister(RegisterValidator msg)
    {
        var spec = msg.Spec;
        if (!_validators.TryGetValue(spec.AppliesTo, out var list))
        {
            list = new List<ValidatorSpec>();
            _validators[spec.AppliesTo] = list;
        }

        // Replace existing validator with same name
        list.RemoveAll(v => v.Name == spec.Name);
        list.Add(spec);

        _logger.LogInformation("Validator registered: {Name} ({Kind}) for {Type}",
            spec.Name, spec.Kind, spec.AppliesTo);
        Sender.Tell(new ValidatorRegistered(spec.Name));
    }

    private void HandleValidate(ValidateArtifact msg)
    {
        var sender = Sender;
        var applicable = GetApplicableValidators(msg.Artifact.Type, msg.RequiredValidators);

        if (applicable.Count == 0)
        {
            // If the caller explicitly requested validators by name but none matched,
            // report each missing validator as a failure rather than silently auto-passing.
            if (msg.RequiredValidators is { Count: > 0 })
            {
                var failures = msg.RequiredValidators
                    .Select(name => new ValidatorResult(name, false, "Required validator not registered"))
                    .ToArray();
                sender.Tell(new ValidationComplete(msg.ArtifactId, failures, msg.TaskId));
                return;
            }

            sender.Tell(new ValidationComplete(msg.ArtifactId, Array.Empty<ValidatorResult>(), msg.TaskId));
            return;
        }

        if (_pending.ContainsKey(msg.ArtifactId))
        {
            _logger.LogWarning("Validation already in-flight for artifact {ArtifactId}, ignoring duplicate", msg.ArtifactId);
            return;
        }

        var pending = new PendingValidation(sender, applicable.Count, new List<ValidatorResult>(), msg.TaskId);
        _pending[msg.ArtifactId] = pending;

        foreach (var spec in applicable)
        {
            switch (spec.Kind)
            {
                case ValidatorKind.Script:
                    RunScriptValidator(msg.ArtifactId, msg.Artifact, spec);
                    break;

                case ValidatorKind.AgentReview:
                    _logger.LogWarning("AgentReview validator '{Name}' not yet implemented, treating as pass",
                        spec.Name);
                    Self.Tell(new ScriptResult(msg.ArtifactId, spec.Name, Passed: true,
                        Details: "AgentReview placeholder — auto-pass"));
                    break;
            }
        }
    }

    private void RunScriptValidator(string artifactId, ArtifactRef artifact, ValidatorSpec spec)
    {
        var self = Self;

        RunCommandAsync(spec.Command, artifact.Uri)
            .ContinueWith(t =>
            {
                if (t.IsCanceled)
                {
                    return (object)new ScriptResult(artifactId, spec.Name, Passed: false,
                        Details: "Script timed out or was cancelled");
                }

                if (t.IsFaulted)
                {
                    return (object)new ScriptResult(artifactId, spec.Name, Passed: false,
                        Details: $"Script exception: {t.Exception?.GetBaseException().Message}");
                }

                var (exitCode, stderr) = t.Result;
                return new ScriptResult(artifactId, spec.Name,
                    Passed: exitCode == 0,
                    Details: exitCode == 0 ? null : $"Exit code {exitCode}: {stderr}");
            })
            .PipeTo(self);
    }

    private void HandleScriptResult(ScriptResult result)
    {
        if (!_pending.TryGetValue(result.ArtifactId, out var pending))
        {
            _logger.LogWarning("Received script result for unknown artifact {ArtifactId}", result.ArtifactId);
            return;
        }

        var validatorResult = new ValidatorResult(result.ValidatorName, result.Passed, result.Details);
        pending.Results.Add(validatorResult);

        _logger.LogDebug("Validator '{Name}' for artifact {ArtifactId}: {Status}",
            result.ValidatorName, result.ArtifactId, result.Passed ? "PASS" : "FAIL");

        if (pending.Results.Count < pending.Expected)
            return;

        // All validators completed for this artifact
        _pending.Remove(result.ArtifactId);

        // Forward each result to artifact registry
        foreach (var vr in pending.Results)
            _artifactRegistry.Tell(new UpdateValidation(result.ArtifactId, vr));

        pending.Requester.Tell(new ValidationComplete(result.ArtifactId, pending.Results, pending.TaskId));
    }

    private List<ValidatorSpec> GetApplicableValidators(ArtifactType type, IReadOnlyList<string>? requiredValidators)
    {
        if (!_validators.TryGetValue(type, out var all))
            return new List<ValidatorSpec>();

        if (requiredValidators is null or { Count: 0 })
            return new List<ValidatorSpec>(all);

        var required = new HashSet<string>(requiredValidators);
        return all.Where(v => required.Contains(v.Name)).ToList();
    }

    private static async Task<(int ExitCode, string Stderr)> RunCommandAsync(string command, string artifactUri)
    {
        using var cts = new CancellationTokenSource(ScriptTimeout);

        // Split command into tokens; artifactUri is appended as a distinct element so CliWrap
        // escapes it properly (handles spaces and shell metacharacters without injection risk).
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var exe = parts[0];
        var argTokens = parts.Length > 1
            ? [..parts[1..], artifactUri]
            : (string[])[artifactUri];

        var result = await Cli.Wrap(exe)
            .WithArguments(argTokens)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cts.Token);

        return (result.ExitCode, result.StandardError);
    }

    // ── Internal types ──

    internal sealed record ScriptResult(string ArtifactId, string ValidatorName, bool Passed, string? Details);

    private sealed record PendingValidation(
        IActorRef Requester, int Expected, List<ValidatorResult> Results, string? TaskId);
}
