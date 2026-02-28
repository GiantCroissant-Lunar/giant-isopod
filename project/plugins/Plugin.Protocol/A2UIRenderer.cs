using System.Text.Json;

namespace GiantIsopod.Plugin.Protocol;

/// <summary>
/// Parses A2UI JSON messages into a component description tree
/// that the Godot GenUI PCK can render as native Control nodes.
/// </summary>
public static class A2UIRenderer
{
    /// <summary>
    /// Parses A2UI JSON into a list of component specs for Godot rendering.
    /// The actual scene instantiation happens in GDScript (loaded from PCK).
    /// </summary>
    public static IReadOnlyList<GenUIComponentSpec> Parse(string a2uiJson)
    {
        var specs = new List<GenUIComponentSpec>();

        try
        {
            using var doc = JsonDocument.Parse(a2uiJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("components", out var components) &&
                components.ValueKind == JsonValueKind.Array)
            {
                foreach (var component in components.EnumerateArray())
                {
                    specs.Add(ParseComponent(component));
                }
            }
        }
        catch (JsonException)
        {
            // Invalid JSON — return empty
        }

        return specs;
    }

    private static GenUIComponentSpec ParseComponent(JsonElement element)
    {
        var type = element.TryGetProperty("type", out var t) ? t.GetString() ?? "label" : "label";
        var props = new Dictionary<string, string>();
        var children = new List<GenUIComponentSpec>();

        if (element.TryGetProperty("props", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in p.EnumerateObject())
            {
                props[prop.Name] = prop.Value.ToString();
            }
        }

        if (element.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in c.EnumerateArray())
            {
                children.Add(ParseComponent(child));
            }
        }

        return new GenUIComponentSpec(type, props, children);
    }
}

public record GenUIComponentSpec(
    string Type,
    IReadOnlyDictionary<string, string> Props,
    IReadOnlyList<GenUIComponentSpec> Children
);
