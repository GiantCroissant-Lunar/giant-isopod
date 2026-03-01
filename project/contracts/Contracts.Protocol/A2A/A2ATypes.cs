namespace GiantIsopod.Contracts.Protocol.A2A;

// ── A2A Protocol Types (Google Agent-to-Agent spec) ──

public record AgentCard(
    string AgentId,
    string Name,
    string? Description = null,
    string? Url = null,
    IReadOnlyList<AgentSkillRef>? Skills = null);

public record AgentSkillRef(string Name, string? Description = null);

public enum A2ATaskStatus { Submitted, Working, InputRequired, Completed, Failed, Canceled }

public record A2ATask(
    string TaskId,
    A2ATaskStatus Status,
    IReadOnlyList<A2AMessage>? History = null,
    IReadOnlyList<A2AArtifact>? Artifacts = null);

public record A2AMessage(string Role, IReadOnlyList<A2APart> Parts);

// Polymorphic parts — discriminated by type
public abstract record A2APart;
public record TextPart(string Text) : A2APart;
public record FilePart(string Name, string MimeType, byte[]? Data = null, string? Uri = null) : A2APart;
public record DataPart(string MimeType, IReadOnlyDictionary<string, object?> Data) : A2APart;

public record A2AArtifact(
    string ArtifactId,
    string Name,
    IReadOnlyList<A2APart> Parts,
    string? Description = null);
