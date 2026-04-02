using System.ComponentModel.DataAnnotations;

namespace InitiativeMerger.Core.Models;

/// <summary>
/// Input for merging multiple initiatives into a single new initiative.
/// Contains the initiatives to combine and configuration options.
/// </summary>
public class MergeRequest
{
    /// <summary>
    /// List of keys of well-known initiatives (e.g. "MCSB", "CIS", "ISO27001", "NIST", "BIO").
    /// </summary>
    public List<string> WellKnownKeys { get; set; } = [];

    /// <summary>
    /// Optional additional initiative IDs or names (for non-standard initiatives).
    /// Format: full resource ID or display name.
    /// </summary>
    public List<string> CustomInitiativeIds { get; set; } = [];

    /// <summary>Name of the merged initiative to be generated.</summary>
    [Required, MinLength(3), MaxLength(128)]
    public string OutputDisplayName { get; set; } = "Merged Compliance Initiative";

    /// <summary>Description of the merged initiative.</summary>
    [MaxLength(512)]
    public string OutputDescription { get; set; } = string.Empty;

    /// <summary>Category for the merged initiative (visible in Azure Portal).</summary>
    public string OutputCategory { get; set; } = "Regulatory Compliance";

    /// <summary>
    /// Strategy for resolving parameter conflicts between initiatives.
    /// </summary>
    public ConflictResolutionStrategy ConflictResolution { get; set; } = ConflictResolutionStrategy.PreferFirst;

    /// <summary>
    /// When true: deploy the merged initiative directly to Azure after generation.
    /// Requires az login and sufficient permissions (Policy Contributor or higher).
    /// </summary>
    public bool DeployToAzure { get; set; } = false;

    /// <summary>Subscription ID for deployment. Required when DeployToAzure = true.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>Management Group ID for deployment. Alternative to SubscriptionId.</summary>
    public string? ManagementGroupId { get; set; }

    /// <summary>
    /// Determines what happens after the initiative definition is generated.
    /// Only applicable when DeployToAzure = true.
    /// </summary>
    public DeploymentTarget DeploymentTarget { get; set; } = DeploymentTarget.DefinitionOnly;

    /// <summary>
    /// Adds "ASC":"true" to the metadata so the initiative is visible under
    /// Microsoft Defender for Cloud → Security policies.
    /// </summary>
    public bool IncludeAscMetadata { get; set; } = true;
}

/// <summary>
/// Determines what happens after the initiative definition is created when deploying to Azure.
/// </summary>
public enum DeploymentTarget
{
    /// <summary>Create only the initiative definition. Visible under Azure Policy → Definitions.</summary>
    DefinitionOnly,

    /// <summary>
    /// Create the definition and immediately assign it to the specified scope.
    /// This makes the initiative appear in Microsoft Defender for Cloud → Regulatory Compliance.
    /// </summary>
    AssignToScope
}

/// <summary>
/// Determines how conflicting parameters (same name, different definition) are resolved.
/// </summary>
public enum ConflictResolutionStrategy
{
    /// <summary>Use the parameter value from the first selected initiative.</summary>
    PreferFirst,

    /// <summary>Use the most restrictive value (lowest numeric value or smallest set).</summary>
    MostRestrictive,

    /// <summary>Skip the conflicting field and use the Azure Policy default.</summary>
    UseDefault,

    /// <summary>Throw an error when a conflict is found — let the user choose manually.</summary>
    FailOnConflict
}
