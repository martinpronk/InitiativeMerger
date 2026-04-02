namespace InitiativeMerger.Core.Models;

/// <summary>
/// Serialisable snapshot of the Index page form state.
/// Can be saved to / loaded from a JSON file so users can share or reuse configurations.
/// </summary>
public class MergeConfiguration
{
    public List<string> SelectedFrameworks { get; set; } = [];
    public string CustomInitiatives { get; set; } = string.Empty;
    public string OutputName { get; set; } = "Merged Compliance Initiative";
    public string OutputDescription { get; set; } = string.Empty;
    public string OutputCategory { get; set; } = "Regulatory Compliance";
    public ConflictResolutionStrategy ConflictStrategy { get; set; } = ConflictResolutionStrategy.PreferFirst;
    public bool IncludeAscMetadata { get; set; } = true;
    public bool DeployToAzure { get; set; } = false;
    public DeploymentTarget DeploymentTarget { get; set; } = DeploymentTarget.DefinitionOnly;
    public string? SubscriptionId { get; set; }
    public string? ManagementGroupId { get; set; }
}
