using System.Text.Json.Serialization;

namespace GiantIsopod.Contracts.Protocol.Aieos;

/// <summary>
/// AIEOS v1.2 AI Entity Object. Will be replaced by quicktype-generated types
/// from aieos.schema.json.
/// </summary>
public record AieosEntity
{
    [JsonPropertyName("metadata")]
    public AieosMetadata? Metadata { get; init; }

    [JsonPropertyName("identity")]
    public AieosIdentity? Identity { get; init; }

    [JsonPropertyName("physicality")]
    public AieosPhysicality? Physicality { get; init; }

    [JsonPropertyName("psychology")]
    public AieosPsychology? Psychology { get; init; }

    [JsonPropertyName("linguistics")]
    public AieosLinguistics? Linguistics { get; init; }

    [JsonPropertyName("capabilities")]
    public AieosCapabilities? Capabilities { get; init; }

    [JsonPropertyName("presence")]
    public AieosPresence? Presence { get; init; }
}

public record AieosMetadata
{
    [JsonPropertyName("entity_id")]
    public string? EntityId { get; init; }

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    [JsonPropertyName("public_key")]
    public string? PublicKey { get; init; }
}

public record AieosIdentity
{
    [JsonPropertyName("names")]
    public AieosNames? Names { get; init; }
}

public record AieosNames
{
    [JsonPropertyName("first")]
    public string? First { get; init; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; init; }
}

public record AieosPhysicality
{
    [JsonPropertyName("face")]
    public AieosFace? Face { get; init; }

    [JsonPropertyName("body")]
    public AieosBody? Body { get; init; }

    [JsonPropertyName("style")]
    public AieosStyle? Style { get; init; }
}

public record AieosFace
{
    [JsonPropertyName("skin")]
    public AieosSkin? Skin { get; init; }

    [JsonPropertyName("hair")]
    public AieosHair? Hair { get; init; }

    [JsonPropertyName("eyes")]
    public AieosEyes? Eyes { get; init; }
}

public record AieosSkin
{
    [JsonPropertyName("tone")]
    public string? Tone { get; init; }
}

public record AieosHair
{
    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("style")]
    public string? Style { get; init; }
}

public record AieosEyes
{
    [JsonPropertyName("color")]
    public string? Color { get; init; }
}

public record AieosBody
{
    [JsonPropertyName("somatotype")]
    public string? Somatotype { get; init; }
}

public record AieosStyle
{
    [JsonPropertyName("aesthetic_archetype")]
    public string? AestheticArchetype { get; init; }

    [JsonPropertyName("color_palette")]
    public IReadOnlyList<string>? ColorPalette { get; init; }
}

public record AieosPsychology
{
    [JsonPropertyName("neural_matrix")]
    public AieosNeuralMatrix? NeuralMatrix { get; init; }
}

public record AieosNeuralMatrix
{
    [JsonPropertyName("creativity")]
    public float Creativity { get; init; }

    [JsonPropertyName("empathy")]
    public float Empathy { get; init; }

    [JsonPropertyName("logic")]
    public float Logic { get; init; }

    [JsonPropertyName("adaptability")]
    public float Adaptability { get; init; }

    [JsonPropertyName("charisma")]
    public float Charisma { get; init; }

    [JsonPropertyName("reliability")]
    public float Reliability { get; init; }
}

public record AieosLinguistics
{
    [JsonPropertyName("text_style")]
    public AieosTextStyle? TextStyle { get; init; }
}

public record AieosTextStyle
{
    [JsonPropertyName("formality_level")]
    public float FormalityLevel { get; init; }

    [JsonPropertyName("verbosity_level")]
    public float VerbosityLevel { get; init; }
}

public record AieosCapabilities
{
    [JsonPropertyName("skills")]
    public IReadOnlyList<AieosSkill>? Skills { get; init; }
}

public record AieosSkill
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("priority")]
    public int Priority { get; init; } = 5;
}

public record AieosPresence
{
    [JsonPropertyName("network")]
    public AieosNetwork? Network { get; init; }
}

public record AieosNetwork
{
    [JsonPropertyName("webhook")]
    public string? Webhook { get; init; }
}
