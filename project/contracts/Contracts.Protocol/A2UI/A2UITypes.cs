namespace GiantIsopod.Contracts.Protocol.A2UI;

// ── A2UI Protocol Types (Agent-to-UI rendering) ──

public enum A2UIMessageType
{
    CreateSurface,
    UpdateComponents,
    UpdateDataModel,
    DeleteSurface
}

public record A2UIComponent(
    string Id,
    string Type,
    IReadOnlyDictionary<string, string>? Props = null,
    string? ParentId = null,
    IReadOnlyList<A2UIAction>? Actions = null,
    string? DataBinding = null);

public record A2UIAction(string Id, string Type, string? Label = null);

/// <summary>
/// Flat surface spec output from A2UIRenderer. Components use ParentId for adjacency
/// instead of nested children.
/// </summary>
public record GenUISurfaceSpec(
    string SurfaceId,
    IReadOnlyList<A2UIComponent> Components,
    IReadOnlyDictionary<string, object?>? DataModel = null);
