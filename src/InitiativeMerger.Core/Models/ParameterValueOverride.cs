namespace InitiativeMerger.Core.Models;

/// <summary>
/// Specifies overrides for a single initiative-level parameter.
/// Only fields with the corresponding Update flag set are applied.
/// </summary>
public class ParameterValueOverride
{
    /// <summary>When true, the defaultValue in the JSON will be updated (or removed when DefaultValueJson is null).</summary>
    public bool UpdateDefaultValue { get; set; }

    /// <summary>
    /// The new default value as a JSON string (e.g. <c>"Audit"</c>, <c>true</c>, <c>["East US"]</c>).
    /// Set to <c>null</c> to remove the defaultValue field entirely.
    /// </summary>
    public string? DefaultValueJson { get; set; }

    /// <summary>When true, the allowedValues in the JSON will be updated.</summary>
    public bool UpdateAllowedValues { get; set; }

    /// <summary>
    /// New list of allowed values (each entry is a plain string).
    /// Set to <c>null</c> to remove the allowedValues field.
    /// Set to an empty list to set <c>[]</c>.
    /// </summary>
    public List<string>? AllowedValues { get; set; }
}
