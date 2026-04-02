using System.Text.Json.Serialization;

namespace InitiativeMerger.Core.Models;

/// <summary>
/// Reference to an individual policy definition within an initiative.
/// Contains the policy ID, parameter values and optional group names (controls).
/// </summary>
public record PolicyDefinitionReference
{
    [JsonPropertyName("policyDefinitionId")]
    public string PolicyDefinitionId { get; init; } = string.Empty;

    [JsonPropertyName("policyDefinitionReferenceId")]
    public string? PolicyDefinitionReferenceId { get; init; }

    [JsonPropertyName("parameters")]
    public Dictionary<string, PolicyParameterValue> Parameters { get; init; } = [];

    [JsonPropertyName("groupNames")]
    public List<string>? GroupNames { get; init; }

    /// <summary>Derived field: the short name of the policy definition (last segment of the ID).</summary>
    [JsonIgnore]
    public string ShortName => PolicyDefinitionId.Split('/').LastOrDefault() ?? PolicyDefinitionId;
}

/// <summary>
/// The value of a parameter as provided in a policy definition reference.
/// </summary>
public record PolicyParameterValue
{
    [JsonPropertyName("value")]
    public object? Value { get; init; }
}

/// <summary>
/// A parameter definition at initiative level with type, default value and allowed values.
/// </summary>
public record PolicyParameterDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("defaultValue")]
    public object? DefaultValue { get; init; }

    [JsonPropertyName("allowedValues")]
    public List<object>? AllowedValues { get; init; }

    [JsonPropertyName("metadata")]
    public ParameterMetadata? Metadata { get; init; }
}

public record ParameterMetadata
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonExtensionData]
    public Dictionary<string, object?>? AdditionalData { get; init; }
}

/// <summary>
/// Grouping of policies within an initiative (corresponds to a control or domain).
/// </summary>
public record PolicyDefinitionGroup
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("additionalMetadataId")]
    public string? AdditionalMetadataId { get; init; }
}
