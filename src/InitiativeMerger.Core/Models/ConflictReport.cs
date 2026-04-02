namespace InitiativeMerger.Core.Models;

/// <summary>
/// Report of a detected parameter conflict between two or more initiatives.
/// Describes what is conflicting, which values exist and how it was resolved.
/// </summary>
public class ConflictReport
{
    /// <summary>Name of the parameter that is conflicting.</summary>
    public string ParameterName { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name of the parameter from the metadata (e.g. "Effect for policy: Storage account public access should be disallowed").
    /// Is null if no metadata.displayName is available.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>Type of conflict.</summary>
    public ConflictType Type { get; set; }

    /// <summary>Description of the conflict in human-readable form.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Involved initiatives (keys, e.g. "MCSB", "CIS").</summary>
    public List<string> InvolvedInitiatives { get; set; } = [];

    /// <summary>Conflicting values per initiative (key → value).</summary>
    public Dictionary<string, object?> ConflictingValues { get; set; } = [];

    /// <summary>The value that was ultimately used in the merged initiative.</summary>
    public object? ResolvedValue { get; set; }

    /// <summary>How the conflict was resolved.</summary>
    public string ResolutionDescription { get; set; } = string.Empty;

    /// <summary>True if the conflict was automatically resolved, false if manual action is required.</summary>
    public bool IsAutoResolved { get; set; }
}

public enum ConflictType
{
    /// <summary>Parameter exists in multiple initiatives but has different default values.</summary>
    DefaultValueMismatch,

    /// <summary>Parameter exists in multiple initiatives but has a different type.</summary>
    TypeMismatch,

    /// <summary>Parameter has different allowed values.</summary>
    AllowedValuesMismatch,

    /// <summary>Parameter exists in one initiative but not in another.</summary>
    MissingInSomeInitiatives,

    /// <summary>Same policy definition with different parameter values.</summary>
    PolicyParameterValueConflict
}
