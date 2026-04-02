using System.Text.Json;
using InitiativeMerger.Core.Models;
using Microsoft.Extensions.Logging;

namespace InitiativeMerger.Core.Services;

/// <summary>
/// Detects and resolves conflicts in initiative parameters.
/// A conflict arises when two initiatives use the same parameter name
/// but with different types, default values or allowed values.
/// </summary>
public sealed class ConflictResolutionService : IConflictResolutionService
{
    private readonly ILogger<ConflictResolutionService> _logger;

    public ConflictResolutionService(ILogger<ConflictResolutionService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public List<ConflictReport> DetectAndResolve(
        IReadOnlyList<PolicyInitiative> initiatives,
        ConflictResolutionStrategy strategy)
    {
        var conflicts = new List<ConflictReport>();
        var parametersByName = CollectParametersByName(initiatives);

        foreach (var (paramName, occurrences) in parametersByName)
        {
            if (occurrences.Count <= 1)
                continue;

            var conflict = AnalyzeConflict(paramName, occurrences);
            if (conflict is null)
                continue;

            ResolveConflict(conflict, strategy, occurrences);
            conflicts.Add(conflict);

            _logger.LogInformation(
                "Conflict found in parameter '{Name}' ({Type}). Resolved: {Resolved}",
                paramName, conflict.Type, conflict.IsAutoResolved);
        }

        return conflicts;
    }

    /// <summary>
    /// Collects all parameter definitions by name, including the name of the initiative.
    /// </summary>
    private static Dictionary<string, List<(string InitiativeKey, PolicyParameterDefinition Definition)>>
        CollectParametersByName(IReadOnlyList<PolicyInitiative> initiatives)
    {
        var result = new Dictionary<string, List<(string, PolicyParameterDefinition)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var initiative in initiatives)
        {
            foreach (var (paramName, paramDef) in initiative.Parameters)
            {
                if (!result.TryGetValue(paramName, out var list))
                {
                    list = [];
                    result[paramName] = list;
                }
                list.Add((initiative.DisplayName, paramDef));
            }
        }

        return result;
    }

    /// <summary>
    /// Analyses whether a real conflict exists.
    /// AllowedValues are compared case-insensitively: ["Audit","audit"] == ["Audit"].
    /// Returns null if all occurrences are semantically equivalent.
    /// </summary>
    private static ConflictReport? AnalyzeConflict(
        string paramName,
        List<(string InitiativeKey, PolicyParameterDefinition Definition)> occurrences)
    {
        var first = occurrences[0].Definition;

        var typeMismatch = occurrences.Any(o =>
            !o.Definition.Type.Equals(first.Type, StringComparison.OrdinalIgnoreCase));

        var defaultMismatch = occurrences.Any(o =>
            !JsonEqual(o.Definition.DefaultValue, first.DefaultValue));

        // AllowedValues are compared as a case-insensitive set.
        // BIO uses ["audit","Audit","deny","Deny"] while CIS uses ["Audit","Deny"] —
        // semantically equivalent, so no conflict is reported.
        var firstAllowedSet = ToNormalizedSet(first.AllowedValues);
        var allowedMismatch = occurrences.Any(o =>
            !ToNormalizedSet(o.Definition.AllowedValues).SetEquals(firstAllowedSet));

        if (!typeMismatch && !defaultMismatch && !allowedMismatch)
            return null; // Semantically no conflict

        var conflictType = typeMismatch ? ConflictType.TypeMismatch
            : allowedMismatch ? ConflictType.AllowedValuesMismatch
            : ConflictType.DefaultValueMismatch;

        // Show the actually conflicting fields (not always the defaultValue)
        var conflictingValues = conflictType switch
        {
            ConflictType.TypeMismatch =>
                occurrences.ToDictionary(o => o.InitiativeKey, o => (object?)o.Definition.Type),
            ConflictType.AllowedValuesMismatch =>
                occurrences.ToDictionary(o => o.InitiativeKey,
                    o => (object?)(o.Definition.AllowedValues is null
                        ? "(none)"
                        : string.Join(", ", o.Definition.AllowedValues))),
            _ =>
                occurrences.ToDictionary(o => o.InitiativeKey, o => (object?)o.Definition.DefaultValue)
        };

        return new ConflictReport
        {
            ParameterName = paramName,
            DisplayName = first.Metadata?.DisplayName,
            Type = conflictType,
            InvolvedInitiatives = occurrences.Select(o => o.InitiativeKey).ToList(),
            ConflictingValues = conflictingValues,
            Description = BuildConflictDescription(paramName, conflictType, occurrences)
        };
    }

    /// <summary>
    /// Applies the specified strategy. For AllowedValuesMismatch the union of all allowed values
    /// is always used so that no valid values are lost.
    /// </summary>
    private static void ResolveConflict(
        ConflictReport conflict,
        ConflictResolutionStrategy strategy,
        List<(string InitiativeKey, PolicyParameterDefinition Definition)> occurrences)
    {
        // AllowedValuesMismatch: always use the union, regardless of strategy.
        // Reason: discarding allowed values makes the parameter unusable.
        if (conflict.Type == ConflictType.AllowedValuesMismatch)
        {
            var union = occurrences
                .Where(o => o.Definition.AllowedValues is not null)
                .SelectMany(o => o.Definition.AllowedValues!)
                .Select(v => v?.ToString() ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v)
                .ToList<object>();

            conflict.ResolvedValue = union.Count > 0 ? union : null;
            conflict.ResolutionDescription =
                $"Union of allowed values used: [{string.Join(", ", union)}]";
            conflict.IsAutoResolved = true;
            return;
        }

        switch (strategy)
        {
            case ConflictResolutionStrategy.PreferFirst:
                conflict.ResolvedValue = conflict.ConflictingValues.Values.FirstOrDefault();
                conflict.ResolutionDescription = $"Value from first initiative used: {conflict.ResolvedValue}";
                conflict.IsAutoResolved = true;
                break;

            case ConflictResolutionStrategy.MostRestrictive:
                conflict.ResolvedValue = SelectMostRestrictive(conflict.ConflictingValues.Values);
                conflict.ResolutionDescription = $"Most restrictive value selected: {conflict.ResolvedValue}";
                conflict.IsAutoResolved = true;
                break;

            case ConflictResolutionStrategy.UseDefault:
                conflict.ResolvedValue = null;
                conflict.ResolutionDescription = "Azure Policy default value will be used (parameter omitted).";
                conflict.IsAutoResolved = true;
                break;

            case ConflictResolutionStrategy.FailOnConflict:
                conflict.ResolutionDescription = "Manual action required — conflict not automatically resolved.";
                conflict.IsAutoResolved = false;
                break;
        }
    }

    /// <summary>
    /// Selects the most restrictive value: lowest number, false over true, first for strings.
    /// </summary>
    private static object? SelectMostRestrictive(IEnumerable<object?> values)
    {
        var list = values.Where(v => v is not null).ToList();
        if (list.Count == 0) return null;

        var numerics = list.Select(v =>
        {
            if (v is JsonElement el && el.TryGetDouble(out var d)) return (double?)d;
            if (double.TryParse(v?.ToString(), out var p)) return (double?)p;
            return null;
        }).Where(v => v.HasValue).Select(v => v!.Value).ToList();

        if (numerics.Count == list.Count)
            return numerics.Min();

        var booleans = list.OfType<bool>().ToList();
        if (booleans.Count > 0)
            return booleans.Contains(false) ? false : (object)true;

        return list.First();
    }

    /// <summary>
    /// Converts an AllowedValues list to a case-insensitive set of strings.
    /// Null and empty lists are treated as an equivalent empty set.
    /// </summary>
    private static HashSet<string> ToNormalizedSet(List<object>? values)
    {
        if (values is null || values.Count == 0)
            return [];

        return values
            .Select(v => v?.ToString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Compares two objects via JSON serialisation for structural equality.</summary>
    private static bool JsonEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    }

    private static string BuildConflictDescription(
        string paramName,
        ConflictType conflictType,
        List<(string InitiativeKey, PolicyParameterDefinition Definition)> occurrences)
    {
        var values = conflictType switch
        {
            ConflictType.TypeMismatch =>
                occurrences.Select(o => $"{o.InitiativeKey}: {o.Definition.Type}"),
            ConflictType.AllowedValuesMismatch =>
                occurrences.Select(o => $"{o.InitiativeKey}: [{string.Join(", ", o.Definition.AllowedValues ?? [])}]"),
            _ =>
                occurrences.Select(o => $"{o.InitiativeKey}: {o.Definition.DefaultValue}")
        };
        return $"Parameter '{paramName}' has {conflictType}: {string.Join(" | ", values)}";
    }
}
