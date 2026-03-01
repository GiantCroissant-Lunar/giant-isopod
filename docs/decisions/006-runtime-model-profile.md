# ADR-006: Runtime / Model / Profile Separation

## Status

Accepted

## Context

The agent execution model conflated three orthogonal concerns:

1. **How** the agent executes — hardcoded as `IAgentProcess` / `CliAgentProcess`
2. **Which model** powers it — buried in `CliProviderEntry.Defaults["model"]`
3. **Who** the agent is — `AieosEntity` profile (already decoupled)

Additionally, pi-specific naming (`_piConnected`, `StartPiProcess`, `SendToPiAsync`)
obscured the generality of the design, which already supported multiple CLI tools
(pi, kilo, codex, kimi).

## Decision

Separate the three concerns into **Runtime**, **Model**, and **Profile**:

| Concept | Old | New |
|---------|-----|-----|
| How it executes | `IAgentProcess` | `IAgentRuntime` |
| Which model | `CliProviderEntry.Defaults` | `ModelSpec` (first-class record) |
| Who the agent is | `AieosEntity` | Unchanged |
| Config entry | `CliProviderEntry` | `RuntimeConfig` (polymorphic) |
| Registry | `CliProviderRegistry` | `RuntimeRegistry` |
| RPC actor | `AgentRpcActor` | `AgentRuntimeActor` |

### RuntimeConfig hierarchy

`RuntimeConfig` uses `[JsonPolymorphic]` with a `"type"` discriminator:

- `CliRuntimeConfig` — CLI subprocess via CliWrap (executable, args, env)
- `ApiRuntimeConfig` — HTTP/API agent (stub: BaseUrl, ApiKeyEnvVar)
- `SdkRuntimeConfig` — SDK-based agent (stub: SdkName, Options)

### ModelSpec

A provider-agnostic record: `ModelSpec(Provider?, ModelId?, Parameters?)`.
Explicit model on `SpawnAgent` overrides `RuntimeConfig.DefaultModel` via
field-level null-coalescing in `RuntimeFactory.MergeModel()`.

### RuntimeFactory

Static factory that pattern-matches on `RuntimeConfig` type to create the
correct `IAgentRuntime`. Keeps the actor layer clean.

## Consequences

- New runtime types (API, SDK, in-process) can be added by implementing
  `IAgentRuntime` and adding a `RuntimeConfig` subclass — no actor changes needed.
- Model selection is decoupled from runtime config — the same CLI runtime can
  be used with different models per agent.
- Legacy `cli-providers.json` is still supported via
  `RuntimeRegistry.LoadFromLegacyCliProviders()`.
- `runtimes.json` is the new preferred format with polymorphic type discriminators.
