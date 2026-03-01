using System.Text.Json;
using GiantIsopod.Contracts.Protocol.A2UI;

namespace GiantIsopod.Plugin.Protocol;

/// <summary>
/// Parses A2UI JSON messages into flat GenUISurfaceSpec descriptions
/// that the Godot GenUI renderer can render as native Control nodes.
/// Supports all 4 A2UI message types: createSurface, updateComponents,
/// updateDataModel, deleteSurface.
/// </summary>
public static class A2UIRenderer
{
    /// <summary>
    /// Parses an A2UI JSON message and returns a surface spec.
    /// Returns null for deleteSurface or invalid input.
    /// </summary>
    public static GenUISurfaceSpec? ParseMessage(string a2uiJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(a2uiJson);
            var root = doc.RootElement;

            var messageType = root.TryGetProperty("type", out var t)
                ? t.GetString() ?? ""
                : "";

            var surfaceId = root.TryGetProperty("surfaceId", out var sid)
                ? sid.GetString() ?? "default"
                : "default";

            return messageType switch
            {
                "createSurface" => ParseCreateSurface(surfaceId, root),
                "updateComponents" => ParseUpdateComponents(surfaceId, root),
                "updateDataModel" => ParseUpdateDataModel(surfaceId, root),
                "deleteSurface" => null,
                // Legacy: no type field — treat as component list
                _ => ParseLegacy(surfaceId, root)
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Legacy compatibility: parses old-style {components:[...]} format.
    /// </summary>
    public static IReadOnlyList<GenUIComponentSpec> Parse(string a2uiJson)
    {
        var surface = ParseMessage(a2uiJson);
        if (surface == null) return Array.Empty<GenUIComponentSpec>();

        // Convert flat A2UIComponents to legacy tree format
        return surface.Components
            .Select(c => new GenUIComponentSpec(
                c.Type,
                c.Props ?? new Dictionary<string, string>(),
                Array.Empty<GenUIComponentSpec>()))
            .ToList();
    }

    private static GenUISurfaceSpec ParseCreateSurface(string surfaceId, JsonElement root)
    {
        var components = ParseFlatComponents(root);
        var dataModel = ParseDataModel(root);
        return new GenUISurfaceSpec(surfaceId, components, dataModel);
    }

    private static GenUISurfaceSpec ParseUpdateComponents(string surfaceId, JsonElement root)
    {
        var components = ParseFlatComponents(root);
        return new GenUISurfaceSpec(surfaceId, components);
    }

    private static GenUISurfaceSpec ParseUpdateDataModel(string surfaceId, JsonElement root)
    {
        var dataModel = ParseDataModel(root);
        return new GenUISurfaceSpec(surfaceId, Array.Empty<A2UIComponent>(), dataModel);
    }

    private static GenUISurfaceSpec ParseLegacy(string surfaceId, JsonElement root)
    {
        var components = new List<A2UIComponent>();

        if (root.TryGetProperty("components", out var comps) &&
            comps.ValueKind == JsonValueKind.Array)
        {
            FlattenComponents(comps, components, parentId: null);
        }

        var dataModel = ParseDataModel(root);
        return new GenUISurfaceSpec(surfaceId, components, dataModel);
    }

    private static IReadOnlyList<A2UIComponent> ParseFlatComponents(JsonElement root)
    {
        var components = new List<A2UIComponent>();

        if (root.TryGetProperty("components", out var comps) &&
            comps.ValueKind == JsonValueKind.Array)
        {
            FlattenComponents(comps, components, parentId: null);
        }

        return components;
    }

    private static void FlattenComponents(JsonElement array, List<A2UIComponent> output, string? parentId)
    {
        foreach (var element in array.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idProp)
                ? idProp.GetString() ?? $"auto-{output.Count}"
                : $"auto-{output.Count}";

            var type = element.TryGetProperty("type", out var typeProp)
                ? typeProp.GetString() ?? "label"
                : "label";

            var props = ParseProps(element);
            var dataBinding = element.TryGetProperty("dataBinding", out var db)
                ? db.GetString()
                : null;

            var actions = ParseActions(element);

            output.Add(new A2UIComponent(id, type, props, parentId, actions, dataBinding));

            // Recursively flatten children with this component as parent
            if (element.TryGetProperty("children", out var children) &&
                children.ValueKind == JsonValueKind.Array)
            {
                FlattenComponents(children, output, parentId: id);
            }
        }
    }

    private static IReadOnlyDictionary<string, string>? ParseProps(JsonElement element)
    {
        if (!element.TryGetProperty("props", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;

        var props = new Dictionary<string, string>();
        foreach (var prop in p.EnumerateObject())
        {
            props[prop.Name] = prop.Value.ToString();
        }
        return props.Count > 0 ? props : null;
    }

    private static IReadOnlyList<A2UIAction>? ParseActions(JsonElement element)
    {
        if (!element.TryGetProperty("actions", out var a) || a.ValueKind != JsonValueKind.Array)
            return null;

        var actions = new List<A2UIAction>();
        foreach (var action in a.EnumerateArray())
        {
            var actionId = action.TryGetProperty("id", out var ai) ? ai.GetString() ?? "" : "";
            var actionType = action.TryGetProperty("type", out var at) ? at.GetString() ?? "click" : "click";
            var label = action.TryGetProperty("label", out var al) ? al.GetString() : null;
            actions.Add(new A2UIAction(actionId, actionType, label));
        }
        return actions.Count > 0 ? actions : null;
    }

    private static IReadOnlyDictionary<string, object?>? ParseDataModel(JsonElement root)
    {
        if (!root.TryGetProperty("dataModel", out var dm) || dm.ValueKind != JsonValueKind.Object)
            return null;

        var model = new Dictionary<string, object?>();
        foreach (var prop in dm.EnumerateObject())
        {
            model[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText()
            };
        }
        return model.Count > 0 ? model : null;
    }
}

public record GenUIComponentSpec(
    string Type,
    IReadOnlyDictionary<string, string> Props,
    IReadOnlyList<GenUIComponentSpec> Children
);
