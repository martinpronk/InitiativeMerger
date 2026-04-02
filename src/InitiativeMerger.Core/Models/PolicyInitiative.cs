using System.Text.Json.Serialization;

namespace InitiativeMerger.Core.Models;

/// <summary>
/// Represents an Azure Policy Initiative (policySetDefinition) as returned by
/// 'az policy set-definition show'. The Azure CLI returns a flat structure — all
/// fields are placed directly on the root object, WITHOUT a 'properties' wrapper.
/// (The ARM REST API does use a properties wrapper; the CLI flattens this automatically.)
/// </summary>
public record PolicyInitiative
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("policyType")]
    public string PolicyType { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("metadata")]
    public PolicyMetadata? Metadata { get; init; }

    [JsonPropertyName("parameters")]
    public Dictionary<string, PolicyParameterDefinition> Parameters { get; init; } = [];

    [JsonPropertyName("policyDefinitions")]
    public List<PolicyDefinitionReference> PolicyDefinitions { get; init; } = [];

    [JsonPropertyName("policyDefinitionGroups")]
    public List<PolicyDefinitionGroup>? PolicyDefinitionGroups { get; init; }
}

public record PolicyMetadata
{
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonExtensionData]
    public Dictionary<string, object?>? AdditionalData { get; init; }
}
