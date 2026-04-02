namespace InitiativeMerger.Core.Models;

/// <summary>
/// A group of initiative-level parameters that are semantically equivalent
/// (same type, normalised name) but have different names across source frameworks.
/// Approving an alias reduces them to a single canonical parameter so the user
/// only needs to fill in a value once.
/// </summary>
public class ParameterAliasCandidate
{
    /// <summary>Suggested canonical parameter name (user-editable before applying).</summary>
    public string CanonicalName { get; set; } = string.Empty;

    /// <summary>Parameter type (String, Array, Boolean, Integer, Object, Float).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Human-readable display name from parameter metadata, if available.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// All original parameter names that will be merged into the canonical name.
    /// Includes the canonical name itself.
    /// </summary>
    public List<string> OriginalNames { get; set; } = [];

    /// <summary>Full parameter definition to use for the canonical parameter.</summary>
    public PolicyParameterDefinition Definition { get; set; } = new();
}
