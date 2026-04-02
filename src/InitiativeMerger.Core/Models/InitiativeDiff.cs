namespace InitiativeMerger.Core.Models;

/// <summary>
/// Semantic diff between two versions of an initiative JSON.
/// Captures which policies and parameters were added, removed or changed.
/// </summary>
public class InitiativeDiff
{
    public List<string> RemovedPolicies  { get; set; } = [];
    public List<string> AddedPolicies    { get; set; } = [];
    public List<string> RemovedParameters { get; set; } = [];
    public List<string> AddedParameters  { get; set; } = [];
    public List<ParameterValueDiff> ChangedParameters { get; set; } = [];
    public int RemovedGroupsCount { get; set; }
    public int AddedGroupsCount   { get; set; }

    public bool HasChanges =>
        RemovedPolicies.Count    > 0 ||
        AddedPolicies.Count      > 0 ||
        RemovedParameters.Count  > 0 ||
        AddedParameters.Count    > 0 ||
        ChangedParameters.Count  > 0 ||
        RemovedGroupsCount       > 0 ||
        AddedGroupsCount         > 0;
}

public class ParameterValueDiff
{
    public string Name { get; set; } = string.Empty;
    public string? OldDefaultValue  { get; set; }
    public string? NewDefaultValue  { get; set; }
    public bool DefaultValueChanged { get; set; }
    public string? OldAllowedValues { get; set; }
    public string? NewAllowedValues { get; set; }
    public bool AllowedValuesChanged { get; set; }
}
