using System.Text.Json.Serialization;

namespace InitiativeMerger.Core.Models;

/// <summary>
/// Result of merging multiple initiatives.
/// Contains the generated JSON, statistics and any conflicts.
/// </summary>
public class MergeResult
{
    /// <summary>Indicates whether the merge succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Error message when the merge failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The generated initiative JSON, ready for deployment via Azure CLI.</summary>
    public string? GeneratedJson { get; set; }

    /// <summary>Name of the generated initiative.</summary>
    public string? InitiativeName { get; set; }

    /// <summary>Statistics about the merge process.</summary>
    public MergeStatistics Statistics { get; set; } = new();

    /// <summary>Conflicts that were detected and resolved (or reported).</summary>
    public List<ConflictReport> Conflicts { get; set; } = [];

    /// <summary>Deployment result, populated when DeployToAzure = true.</summary>
    public DeploymentResult? Deployment { get; set; }

    /// <summary>List of initiatives that were merged (for audit trail).</summary>
    public List<SourceInitiativeSummary> SourceInitiatives { get; set; } = [];

    /// <summary>All unique policies in the merged initiative, including their origin.</summary>
    public List<MergedPolicySummary> MergedPolicies { get; set; } = [];

    /// <summary>All unique control groups in the merged initiative, including their origin.</summary>
    public List<MergedGroupSummary> MergedGroups { get; set; } = [];

    /// <summary>Parameter alias candidates detected during the merge. Empty when no aliases were found.</summary>
    public List<ParameterAliasCandidate> SuggestedAliases { get; set; } = [];

    /// <summary>
    /// Validation warnings generated after the merge (e.g. policy count exceeds Azure limits).
    /// Non-fatal: the JSON is still generated, but the user should review these before deploying.
    /// </summary>
    public List<MergeWarning> Warnings { get; set; } = [];
}

public class MergeWarning
{
    public MergeWarningSeverity Severity { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public enum MergeWarningSeverity { Info, Warning, Error }

public class MergeStatistics
{
    /// <summary>Total number of policy definitions across all source initiatives (including duplicates).</summary>
    public int TotalPoliciesBeforeMerge { get; set; }

    /// <summary>Number of unique policy definitions in the merged initiative.</summary>
    public int UniquePoliciesAfterMerge { get; set; }

    /// <summary>Number of duplicates removed.</summary>
    public int DuplicatesRemoved { get; set; }

    /// <summary>Number of parameter conflicts found.</summary>
    public int ParameterConflictsFound { get; set; }

    /// <summary>Number of parameter conflicts automatically resolved.</summary>
    public int ParameterConflictsResolved { get; set; }

    /// <summary>Time of generation (UTC).</summary>
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SourceInitiativeSummary
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public int PolicyCount { get; set; }
}

/// <summary>
/// Summary of a single control group in the merged initiative (for the filters UI).
/// </summary>
public class MergedGroupSummary
{
    /// <summary>Technical group name as used in policyDefinitions[].groupNames.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable display name of the group, if available.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Key of the source initiative that contributed this group.</summary>
    public string SourceInitiativeKey { get; set; } = string.Empty;

    /// <summary>Number of policies in the merged initiative that belong to this group.</summary>
    public int PolicyCount { get; set; }
}

/// <summary>
/// Summary of a single unique policy definition in the merged initiative.
/// Contains the origin (which source initiative) for traceability.
/// </summary>
public class MergedPolicySummary
{
    /// <summary>Full resource ID of the policy definition.</summary>
    public string PolicyDefinitionId { get; set; } = string.Empty;

    /// <summary>Short name (last segment of the resource ID).</summary>
    public string ShortName { get; set; } = string.Empty;

    /// <summary>Reference ID as used in the initiative (e.g. for parameter binding).</summary>
    public string? ReferenceId { get; set; }

    /// <summary>Groups/controls this policy belongs to.</summary>
    public List<string>? GroupNames { get; set; }

    /// <summary>Key of the first source initiative that contained this policy.</summary>
    public string SourceInitiativeKey { get; set; } = string.Empty;
}

public class DeploymentResult
{
    public bool Success { get; set; }
    public string? DeployedResourceId { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Scope { get; set; }

    /// <summary>Resource ID of the created policy assignment, if applicable.</summary>
    public string? AssignmentId { get; set; }

    /// <summary>True if an assignment was created in addition to the definition.</summary>
    public bool AssignmentCreated { get; set; }
}
