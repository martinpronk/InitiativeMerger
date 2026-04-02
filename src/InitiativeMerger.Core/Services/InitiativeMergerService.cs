using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InitiativeMerger.Core.Models;
using Microsoft.Extensions.Logging;

namespace InitiativeMerger.Core.Services;

/// <summary>
/// Core service that merges multiple Azure Policy initiatives into a single new initiative.
/// Removes duplicate policy definitions (by policyDefinitionId), resolves parameter conflicts
/// and generates the final ARM-compatible initiative JSON.
/// </summary>
public sealed class InitiativeMergerService : IInitiativeMergerService
{
    private readonly IAzurePolicyService _azurePolicyService;
    private readonly IConflictResolutionService _conflictResolution;
    private readonly IDeploymentService _deploymentService;
    private readonly ILogger<InitiativeMergerService> _logger;

    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Separate options for re-serialising already-parsed JsonNode (keys are already correctly cased)
    private static readonly JsonSerializerOptions NodeJsonOptions = new() { WriteIndented = true };

    private static readonly Regex InitiativeParamRegex =
        new(@"\[parameters\('([^']+)'\)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public InitiativeMergerService(
        IAzurePolicyService azurePolicyService,
        IConflictResolutionService conflictResolution,
        IDeploymentService deploymentService,
        ILogger<InitiativeMergerService> logger)
    {
        _azurePolicyService = azurePolicyService;
        _conflictResolution = conflictResolution;
        _deploymentService = deploymentService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MergeResult> MergeAsync(MergeRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Merge started. WellKnown: [{Keys}], Custom: [{Custom}]",
            string.Join(", ", request.WellKnownKeys),
            string.Join(", ", request.CustomInitiativeIds));

        // 1. Collect all initiative IDs to fetch
        var allIds = CollectInitiativeIds(request);
        if (allIds.Count == 0)
        {
            return FailResult("No initiatives selected. Please select at least one initiative.");
        }

        // 2. Fetch initiatives via Azure CLI
        var (initiatives, fetchErrors) = await _azurePolicyService.GetInitiativesAsync(allIds, cancellationToken);
        if (initiatives.Count == 0)
        {
            var detail = fetchErrors.Count > 0
                ? $"\n\nDetails:\n{string.Join("\n", fetchErrors.Select((e, i) => $"  [{i + 1}] {e}"))}"
                : string.Empty;
            return FailResult($"None of the initiatives could be retrieved.{detail}");
        }

        // 3. Detect and resolve parameter conflicts
        var conflicts = _conflictResolution.DetectAndResolve(initiatives, request.ConflictResolution);

        // 4. Check whether FailOnConflict is in effect
        if (request.ConflictResolution == ConflictResolutionStrategy.FailOnConflict
            && conflicts.Any(c => !c.IsAutoResolved))
        {
            var unresolved = conflicts.Where(c => !c.IsAutoResolved).Select(c => c.ParameterName);
            return FailResult($"Conflicts found that require manual action: {string.Join(", ", unresolved)}");
        }

        // 5. Generate the merged initiative JSON + policy overview + group overview
        var (json, mergedPolicySummaries, mergedGroupSummaries, suggestedAliases) = BuildMergedOutput(initiatives, request, conflicts);

        // 6. Calculate statistics
        var totalBefore = initiatives.Sum(i => i.PolicyDefinitions.Count);
        var uniqueAfter = mergedPolicySummaries.Count;

        var result = new MergeResult
        {
            Success = true,
            GeneratedJson = json,
            InitiativeName = request.OutputDisplayName,
            Conflicts = conflicts,
            MergedPolicies = mergedPolicySummaries,
            MergedGroups = mergedGroupSummaries,
            SuggestedAliases = suggestedAliases,
            Statistics = new MergeStatistics
            {
                TotalPoliciesBeforeMerge = totalBefore,
                UniquePoliciesAfterMerge = uniqueAfter,
                DuplicatesRemoved = totalBefore - uniqueAfter,
                ParameterConflictsFound = conflicts.Count,
                ParameterConflictsResolved = conflicts.Count(c => c.IsAutoResolved)
            },
            SourceInitiatives = initiatives.Select(i => new SourceInitiativeSummary
            {
                Key = ExtractKey(i.Id, request),
                DisplayName = i.DisplayName,
                ResourceId = i.Id,
                PolicyCount = i.PolicyDefinitions.Count
            }).ToList()
        };

        // 7. Validate the merge result and add warnings
        result.Warnings = ValidateMergeResult(result, mergedPolicySummaries, json);

        // 8. Optionally: deploy to Azure
        if (request.DeployToAzure)
        {
            result.Deployment = await _deploymentService.DeployAsync(json, request, cancellationToken);
        }

        _logger.LogInformation("Merge completed. {Unique} unique policies, {Dupes} duplicates removed, {Conflicts} conflicts.",
            uniqueAfter, totalBefore - uniqueAfter, conflicts.Count);

        return result;
    }

    /// <inheritdoc />
    public string GenerateInitiativeJson(
        IReadOnlyList<PolicyInitiative> initiatives,
        MergeRequest request,
        List<ConflictReport> conflicts) => BuildMergedOutput(initiatives, request, conflicts).Item1;

    /// <summary>
    /// Performs deduplication and JSON generation and returns both the JSON and the policy and group overview.
    /// </summary>
    private (string Json, List<MergedPolicySummary> Summaries, List<MergedGroupSummary> Groups, List<ParameterAliasCandidate> Aliases) BuildMergedOutput(
        IReadOnlyList<PolicyInitiative> initiatives,
        MergeRequest request,
        List<ConflictReport> conflicts)
    {
        // Deduplicate: use the first occurrence of each policyDefinitionId.
        // For groupNames we accumulate across ALL occurrences so that control mappings
        // from every framework (e.g. BIO 12.6.1 AND CIS 7.4) are preserved on the merged policy.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstOccurrences = new List<PolicyDefinitionReference>();
        var policySourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupNameAccumulator = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var mergedGroups = new List<PolicyDefinitionGroup>();
        var seenGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupSourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var initiative in initiatives)
        {
            var initiativeKey = ExtractKey(initiative.Id, request);

            foreach (var policyRef in initiative.PolicyDefinitions)
            {
                // Always accumulate groupNames from every framework, even for duplicates.
                // This ensures cross-framework control mappings (BIO 12.6.1 + CIS 7.4 on the same policy) are retained.
                if (policyRef.GroupNames is { Count: > 0 })
                {
                    if (!groupNameAccumulator.TryGetValue(policyRef.PolicyDefinitionId, out var acc))
                        groupNameAccumulator[policyRef.PolicyDefinitionId] = acc = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in policyRef.GroupNames) acc.Add(g);
                }

                if (seen.Add(policyRef.PolicyDefinitionId))
                {
                    firstOccurrences.Add(policyRef);
                    policySourceMap[policyRef.PolicyDefinitionId] = initiativeKey;
                }
                else
                {
                    _logger.LogDebug("Duplicate skipped: {PolicyId}", policyRef.PolicyDefinitionId);
                }
            }

            // Add unique groups (controls) and remember the source initiative
            if (initiative.PolicyDefinitionGroups is { } groups)
            {
                foreach (var group in groups)
                {
                    if (seenGroupNames.Add(group.Name))
                    {
                        mergedGroups.Add(group);
                        groupSourceMap[group.Name] = initiativeKey;
                    }
                }
            }
        }

        // Apply the union of all accumulated groupNames to each policy reference.
        var mergedPolicies = firstOccurrences.Select(p =>
            groupNameAccumulator.TryGetValue(p.PolicyDefinitionId, out var acc) && acc.Count > 0
                ? p with { GroupNames = acc.OrderBy(g => g).ToList() }
                : p
        ).ToList();

        // Build summaries (uses merged groupNames so the filter UI reflects all frameworks)
        var summaries = mergedPolicies.Select(p => new MergedPolicySummary
        {
            PolicyDefinitionId = p.PolicyDefinitionId,
            ShortName = p.ShortName,
            ReferenceId = p.PolicyDefinitionReferenceId,
            GroupNames = p.GroupNames,
            SourceInitiativeKey = policySourceMap.TryGetValue(p.PolicyDefinitionId, out var src) ? src : string.Empty
        }).ToList();

        // Build group summaries with policy counts (based on merged groupNames)
        var groupSummaries = mergedGroups
            .Select(g => new MergedGroupSummary
            {
                Name = g.Name,
                DisplayName = g.DisplayName,
                SourceInitiativeKey = groupSourceMap.TryGetValue(g.Name, out var gsrc) ? gsrc : string.Empty,
                PolicyCount = mergedPolicies.Count(p => p.GroupNames?.Contains(g.Name) == true)
            })
            .ToList();

        // Merge parameters (resolved conflicts have already been processed)
        var mergedParameters = MergeParameters(initiatives, conflicts);
        var suggestedAliases = DetectAliasCandidates(mergedParameters);

        // Metadata as JsonObject so that "ASC" is serialised exactly as-is (not camelCased)
        var metadata = new JsonObject
        {
            ["category"] = request.OutputCategory,
            ["version"] = "1.0.0",
            ["generatedBy"] = "InitiativeMerger",
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["sourceInitiatives"] = new JsonArray(
                initiatives.Select(i => JsonValue.Create(i.DisplayName)).ToArray<JsonNode?>())
        };
        if (request.IncludeAscMetadata)
            metadata["ASC"] = "true";

        // Build the initiative JSON structure as an anonymous object for flexible serialisation
        var initiativeDefinition = new
        {
            properties = new
            {
                displayName = request.OutputDisplayName,
                description = BuildDescription(request, initiatives),
                metadata,
                parameters = mergedParameters,
                policyDefinitions = mergedPolicies.Select(MapPolicyReference).ToList(),
                policyDefinitionGroups = mergedGroups.Count > 0 ? mergedGroups : null
            }
        };

        // Serialize first, then prune parameters that became orphaned after deduplication.
        // When policy A from initiative 2 is discarded as a duplicate, its parameters may no
        // longer be referenced by any remaining policy — pruning removes them to stay within
        // Azure's 400-parameter limit.
        var json = JsonSerializer.Serialize(initiativeDefinition, OutputJsonOptions);
        var jsonNode = JsonNode.Parse(json)!;
        var jsonProps = jsonNode["properties"]!.AsObject();
        var jsonDefs  = jsonProps["policyDefinitions"]!.AsArray();
        PruneUnusedParameters(jsonProps, jsonDefs);
        json = jsonNode.ToJsonString(NodeJsonOptions);

        return (json, summaries, groupSummaries, suggestedAliases);
    }

    /// <inheritdoc />
    public string FilterByGroups(MergeResult result, ISet<string> selectedGroupNames, bool includeUngrouped = true)
    {
        if (string.IsNullOrEmpty(result.GeneratedJson))
            return result.GeneratedJson ?? string.Empty;

        // Build lookup table: policyDefinitionId → groupNames
        var policyGroupMap = result.MergedPolicies
            .ToDictionary(p => p.PolicyDefinitionId, p => p.GroupNames, StringComparer.OrdinalIgnoreCase);

        var root = JsonNode.Parse(result.GeneratedJson)!;
        var props = root["properties"]!.AsObject();
        var defsArray = props["policyDefinitions"]?.AsArray();
        if (defsArray is null) return result.GeneratedJson;

        // Filter policy definitions
        var toRemove = defsArray
            .Where(def =>
            {
                var policyId = def?["policyDefinitionId"]?.GetValue<string>();
                if (policyId is null) return true;
                var groups = policyGroupMap.TryGetValue(policyId, out var g) ? g : null;
                if (groups is null || groups.Count == 0) return !includeUngrouped;
                return !groups.Any(gn => selectedGroupNames.Contains(gn));
            })
            .ToList();

        foreach (var def in toRemove)
            defsArray.Remove(def);

        // Filter policyDefinitionGroups: remove groups that are not selected
        var groupsArray = props["policyDefinitionGroups"]?.AsArray();
        if (groupsArray is not null)
        {
            var staleGroups = groupsArray
                .Where(g => !selectedGroupNames.Contains(g?["name"]?.GetValue<string>() ?? string.Empty))
                .ToList();
            foreach (var g in staleGroups) groupsArray.Remove(g);
            if (groupsArray.Count == 0) props.Remove("policyDefinitionGroups");
        }

        // Remove initiative-level parameters that are no longer referenced by any remaining policy
        PruneUnusedParameters(props, defsArray);

        return root.ToJsonString(NodeJsonOptions);
    }

    /// <summary>
    /// Removes initiative-level parameters that are no longer referenced by
    /// the remaining policy definitions (to prevent UnusedPolicyParameters errors).
    /// </summary>
    private static void PruneUnusedParameters(JsonObject props, JsonArray defsArray)
    {
        var paramsNode = props["parameters"]?.AsObject();
        if (paramsNode is null) return;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in defsArray)
        {
            if (def?["parameters"]?.AsObject() is not { } paramBindings) continue;
            foreach (var binding in paramBindings)
            {
                var valueStr = binding.Value?["value"]?.ToString();
                if (valueStr is null) continue;
                var match = InitiativeParamRegex.Match(valueStr);
                if (match.Success) referenced.Add(match.Groups[1].Value);
            }
        }

        var unused = paramsNode.Select(kv => kv.Key).Where(k => !referenced.Contains(k)).ToList();
        foreach (var key in unused) paramsNode.Remove(key);
    }

    /// <inheritdoc />
    public string ApplyAliases(string initiativeJson, IEnumerable<ParameterAliasCandidate> approvedAliases)
    {
        if (string.IsNullOrEmpty(initiativeJson)) return initiativeJson;

        var root = JsonNode.Parse(initiativeJson)!;
        var props = root["properties"]!.AsObject();
        var paramsNode = props["parameters"]?.AsObject();
        var defsArray = props["policyDefinitions"]?.AsArray();

        if (paramsNode is null || defsArray is null) return initiativeJson;

        foreach (var alias in approvedAliases)
        {
            var canonicalName = alias.CanonicalName.Trim();
            if (string.IsNullOrWhiteSpace(canonicalName)) continue;

            // Names to replace: all original names except the canonical itself
            var toReplace = alias.OriginalNames
                .Where(n => !n.Equals(canonicalName, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (toReplace.Count == 0) continue;

            // Ensure the canonical parameter exists in the initiative parameters.
            // If it doesn't, copy the definition from one of the originals.
            if (!paramsNode.ContainsKey(canonicalName))
            {
                var sourceParam = toReplace
                    .Select(n => paramsNode[n])
                    .FirstOrDefault(n => n is not null);
                if (sourceParam is not null)
                    paramsNode[canonicalName] = sourceParam.DeepClone();
            }

            // Rewrite [parameters('originalName')] → [parameters('canonicalName')] in all policy bindings
            foreach (var def in defsArray)
            {
                if (def?["parameters"]?.AsObject() is not { } policyParams) continue;

                foreach (var bindingKey in policyParams.Select(kv => kv.Key).ToList())
                {
                    var valueStr = policyParams[bindingKey]?["value"]?.ToString();
                    if (valueStr is null) continue;

                    var match = InitiativeParamRegex.Match(valueStr);
                    if (!match.Success) continue;

                    var referencedParam = match.Groups[1].Value;
                    if (!toReplace.Contains(referencedParam)) continue;

                    policyParams[bindingKey] = new JsonObject
                    {
                        ["value"] = $"[parameters('{canonicalName}')]"
                    };
                }
            }

            // Remove original (non-canonical) parameters from initiative-level parameters
            foreach (var name in toReplace)
                paramsNode.Remove(name);
        }

        return root.ToJsonString(NodeJsonOptions);
    }

    /// <inheritdoc />
    public string ApplyParameterValues(string initiativeJson, IReadOnlyDictionary<string, ParameterValueOverride> overrides)
    {
        if (string.IsNullOrEmpty(initiativeJson) || overrides.Count == 0)
            return initiativeJson;

        var root = JsonNode.Parse(initiativeJson) as JsonObject
            ?? throw new InvalidOperationException("Invalid initiative JSON.");

        var props = (root["properties"] as JsonObject) ?? root;
        var paramsNode = props["parameters"] as JsonObject;
        if (paramsNode is null) return initiativeJson;

        foreach (var (paramName, ov) in overrides)
        {
            if (!ov.UpdateDefaultValue && !ov.UpdateAllowedValues) continue;

            var paramNode = paramsNode[paramName] as JsonObject;
            if (paramNode is null) continue;

            if (ov.UpdateDefaultValue)
            {
                if (ov.DefaultValueJson is null)
                {
                    paramNode.Remove("defaultValue");
                }
                else
                {
                    // Try to parse as a JSON token; fall back to string literal
                    JsonNode? parsed;
                    try { parsed = JsonNode.Parse(ov.DefaultValueJson); }
                    catch { parsed = JsonValue.Create(ov.DefaultValueJson); }
                    paramNode["defaultValue"] = parsed;
                }
            }

            if (ov.UpdateAllowedValues)
            {
                if (ov.AllowedValues is null)
                {
                    paramNode.Remove("allowedValues");
                }
                else
                {
                    var arr = new JsonArray();
                    foreach (var v in ov.AllowedValues)
                    {
                        // Try numeric / bool parse first, else treat as string
                        if (long.TryParse(v, out var l)) arr.Add(l);
                        else if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var d)) arr.Add(d);
                        else if (bool.TryParse(v, out var b)) arr.Add(b);
                        else arr.Add(v);
                    }
                    paramNode["allowedValues"] = arr;
                }
            }
        }

        return root.ToJsonString(NodeJsonOptions);
    }

    /// <summary>
    /// Detects initiative-level parameters that likely represent the same value under different names.
    /// Heuristic: strip 'listOf'/'arrayOf' camelCase prefix, then group by (normalised name, type).
    /// Groups with 2 or more members are alias candidates.
    /// </summary>
    private static List<ParameterAliasCandidate> DetectAliasCandidates(
        Dictionary<string, PolicyParameterDefinition> parameters)
    {
        static string Normalize(string name)
        {
            var prefixes = new[] { "listOf", "arrayOf" };
            foreach (var prefix in prefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && name.Length > prefix.Length
                    && char.IsUpper(name[prefix.Length]))
                {
                    var rest = name[prefix.Length..];
                    return char.ToLowerInvariant(rest[0]) + rest[1..];
                }
            }
            return char.ToLowerInvariant(name[0]) + name[1..];
        }

        var candidates = parameters
            .Select(kv => (
                Key: kv.Key,
                Def: kv.Value,
                Normalized: Normalize(kv.Key),
                Type: (kv.Value.Type ?? string.Empty).ToLowerInvariant()
            ))
            .GroupBy(x => (x.Normalized, x.Type))
            .Where(g => g.Count() >= 2)
            .Select(g =>
            {
                // Prefer names WITHOUT the listOf/arrayOf prefix as canonical; then shortest; then alphabetical
                var ordered = g
                    .OrderBy(x => x.Key.StartsWith("listOf", StringComparison.OrdinalIgnoreCase)
                                  || x.Key.StartsWith("arrayOf", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(x => x.Key.Length)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var canonical = ordered[0];

                // Pick the display name from whichever entry has the best metadata
                var displayName = g
                    .Select(x => x.Def.Metadata?.DisplayName)
                    .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

                return new ParameterAliasCandidate
                {
                    CanonicalName = canonical.Key,
                    Type = canonical.Def.Type ?? "String",
                    DisplayName = displayName,
                    OriginalNames = g.Select(x => x.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    Definition = canonical.Def
                };
            })
            .ToList();

        return candidates;
    }

    // --- Private helpers ---

    /// <summary>Combines well-known and custom initiative IDs into a single list.</summary>
    private static List<string> CollectInitiativeIds(MergeRequest request)
    {
        var ids = new List<string>();

        foreach (var key in request.WellKnownKeys)
        {
            var known = WellKnownInitiative.FindByKey(key);
            if (known is not null)
                ids.Add(known.ResourceId);
            else
                ids.Add(key); // Try the key directly as an ID
        }

        ids.AddRange(request.CustomInitiativeIds
            .Where(id => !string.IsNullOrWhiteSpace(id)));

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Merges initiative-level parameters. For conflicts, the resolved values from
    /// the conflict report are used.
    /// </summary>
    private static Dictionary<string, PolicyParameterDefinition> MergeParameters(
        IReadOnlyList<PolicyInitiative> initiatives,
        List<ConflictReport> conflicts)
    {
        var merged = new Dictionary<string, PolicyParameterDefinition>(StringComparer.OrdinalIgnoreCase);
        var resolvedConflicts = conflicts.ToDictionary(c => c.ParameterName, c => c.ResolvedValue);

        foreach (var initiative in initiatives)
        {
            foreach (var (paramName, paramDef) in initiative.Parameters)
            {
                if (merged.ContainsKey(paramName))
                    continue; // Already processed (first occurrence wins, unless conflict resolved)

                // Use resolved value if there was a conflict
                if (resolvedConflicts.TryGetValue(paramName, out var resolvedDefault))
                {
                    merged[paramName] = paramDef with { DefaultValue = resolvedDefault };
                }
                else
                {
                    merged[paramName] = paramDef;
                }
            }
        }

        return merged;
    }

    /// <summary>Maps a PolicyDefinitionReference to an anonymous object for JSON output.</summary>
    private static object MapPolicyReference(PolicyDefinitionReference pref) => new
    {
        policyDefinitionId = pref.PolicyDefinitionId,
        policyDefinitionReferenceId = pref.PolicyDefinitionReferenceId ?? pref.ShortName,
        parameters = pref.Parameters.Count > 0 ? pref.Parameters : null,
        groupNames = pref.GroupNames is { Count: > 0 } ? pref.GroupNames : null
    };

    /// <inheritdoc />
    public InitiativeDiff ComputeDiff(string originalJson, string modifiedJson)
    {
        static JsonObject? GetProps(string json)
        {
            var root = JsonNode.Parse(json) as JsonObject;
            return (root?["properties"] as JsonObject) ?? root;
        }

        static HashSet<string> PolicyIds(JsonObject? props)
        {
            var arr = props?["policyDefinitions"] as JsonArray;
            if (arr is null) return [];
            return arr
                .Select(n => n?["policyDefinitionId"]?.GetValue<string>())
                .Where(id => id is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }

        static Dictionary<string, string> ParamNodes(JsonObject? props)
        {
            var obj = props?["parameters"] as JsonObject;
            if (obj is null) return [];
            return obj.ToDictionary(kv => kv.Key, kv => kv.Value?.ToJsonString() ?? "null");
        }

        static int GroupCount(JsonObject? props)
            => (props?["policyDefinitionGroups"] as JsonArray)?.Count ?? 0;

        var origProps = GetProps(originalJson);
        var modProps  = GetProps(modifiedJson);

        var origPolicies = PolicyIds(origProps);
        var modPolicies  = PolicyIds(modProps);

        var origParams = ParamNodes(origProps);
        var modParams  = ParamNodes(modProps);

        var removedPolicies = origPolicies.Except(modPolicies).OrderBy(x => x).ToList();
        var addedPolicies   = modPolicies.Except(origPolicies).OrderBy(x => x).ToList();

        var allParamNames = origParams.Keys.Union(modParams.Keys).OrderBy(x => x);
        var removedParams = new List<string>();
        var addedParams   = new List<string>();
        var changedParams = new List<ParameterValueDiff>();

        foreach (var name in allParamNames)
        {
            var inOrig = origParams.TryGetValue(name, out var origJson2);
            var inMod  = modParams.TryGetValue(name, out var modJson2);

            if (inOrig && !inMod)  { removedParams.Add(name); continue; }
            if (!inOrig && inMod)  { addedParams.Add(name);   continue; }

            // Both present — check for value changes
            if (origJson2 == modJson2) continue;

            var origNode = JsonNode.Parse(origJson2 ?? "{}") as JsonObject;
            var modNode  = JsonNode.Parse(modJson2  ?? "{}") as JsonObject;

            var origDefault  = origNode?["defaultValue"]?.ToJsonString();
            var modDefault   = modNode?["defaultValue"]?.ToJsonString();
            var origAllowed  = origNode?["allowedValues"]?.ToJsonString();
            var modAllowed   = modNode?["allowedValues"]?.ToJsonString();

            if (origDefault != modDefault || origAllowed != modAllowed)
            {
                changedParams.Add(new ParameterValueDiff
                {
                    Name                 = name,
                    OldDefaultValue      = origDefault,
                    NewDefaultValue      = modDefault,
                    DefaultValueChanged  = origDefault != modDefault,
                    OldAllowedValues     = origAllowed,
                    NewAllowedValues     = modAllowed,
                    AllowedValuesChanged = origAllowed != modAllowed
                });
            }
        }

        return new InitiativeDiff
        {
            RemovedPolicies   = removedPolicies,
            AddedPolicies     = addedPolicies,
            RemovedParameters = removedParams,
            AddedParameters   = addedParams,
            ChangedParameters = changedParams,
            RemovedGroupsCount = Math.Max(0, GroupCount(origProps) - GroupCount(modProps)),
            AddedGroupsCount   = Math.Max(0, GroupCount(modProps)  - GroupCount(origProps))
        };
    }

    /// <inheritdoc />
    public string ConvertToBicep(string initiativeJson, string? resourceName = null)
    {
        var root = JsonNode.Parse(initiativeJson) as JsonObject
            ?? throw new InvalidOperationException("Invalid initiative JSON.");

        // The generated JSON wraps everything under "properties" (ARM format)
        var props = (root["properties"] as JsonObject) ?? root;

        // Extract top-level fields
        var displayName  = props["displayName"]?.GetValue<string>() ?? "Merged Initiative";
        var description  = props["description"]?.GetValue<string>() ?? string.Empty;
        var metaExtra    = props["metadata"] as JsonObject;
        var category     = metaExtra?["category"]?.GetValue<string>() ?? "Regulatory Compliance";

        // Bicep resource name: slug of the displayName or explicit override
        var slug = resourceName ?? SlugifyName(displayName);

        // Serialise complex sub-nodes (parameters, policyDefinitions, policyDefinitionGroups)
        // with 4-space Bicep indentation. We re-use NodeJsonOptions (WriteIndented=true).
        var paramsNode      = props["parameters"];
        var defsNode        = props["policyDefinitions"];
        var groupsNode      = props["policyDefinitionGroups"];
        var ascValue        = metaExtra?["ASC"]?.GetValue<string>();

        string Indent(string json, int spaces)
        {
            var pad = new string(' ', spaces);
            return string.Join("\n", json.Split('\n').Select((line, i) => i == 0 ? line : pad + line));
        }

        string paramsJson = paramsNode?.ToJsonString(NodeJsonOptions) ?? "{}";
        string defsJson   = defsNode?.ToJsonString(NodeJsonOptions) ?? "[]";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// Generated by Initiative Merger");
        sb.AppendLine($"// Initiative: {displayName}");
        sb.AppendLine($"// Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine("targetScope = 'subscription'");
        sb.AppendLine();
        sb.AppendLine($"resource initiative 'Microsoft.Authorization/policySetDefinitions@2021-06-01' = {{");
        sb.AppendLine($"  name: '{slug}'");
        sb.AppendLine($"  properties: {{");
        sb.AppendLine($"    displayName: '{EscapeBicepString(displayName)}'");
        if (!string.IsNullOrWhiteSpace(description))
            sb.AppendLine($"    description: '{EscapeBicepString(description)}'");
        sb.AppendLine($"    policyType: 'Custom'");
        sb.AppendLine($"    metadata: {{");
        sb.AppendLine($"      category: '{EscapeBicepString(category)}'");
        if (ascValue is not null)
            sb.AppendLine($"      ASC: '{ascValue}'");
        sb.AppendLine($"    }}");
        sb.AppendLine($"    parameters: {Indent(paramsJson, 4)}");
        sb.AppendLine($"    policyDefinitions: {Indent(defsJson, 4)}");
        if (groupsNode is JsonArray groupsArr && groupsArr.Count > 0)
        {
            var groupsJson = groupsNode.ToJsonString(NodeJsonOptions);
            sb.AppendLine($"    policyDefinitionGroups: {Indent(groupsJson, 4)}");
        }
        sb.AppendLine($"  }}");
        sb.AppendLine($"}}");
        sb.AppendLine();
        sb.AppendLine($"output initiativeId string = initiative.id");

        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateAssignmentTemplate(string initiativeJson, string? resourceName = null)
    {
        var root = JsonNode.Parse(initiativeJson) as JsonObject
            ?? throw new InvalidOperationException("Invalid initiative JSON.");
        var props = (root["properties"] as JsonObject) ?? root;

        var displayName = props["displayName"]?.GetValue<string>() ?? "Merged Initiative";
        var slug = resourceName ?? SlugifyName(displayName);
        var paramsNode = props["parameters"] as JsonObject;

        // Check if any DINE or Modify policies exist (need managed identity)
        var defs = props["policyDefinitions"] as JsonArray;
        bool needsIdentity = false;
        if (defs is not null)
        {
            foreach (var def in defs)
            {
                var defParams = def?["parameters"] as JsonObject;
                if (defParams is null) continue;
                foreach (var kv in defParams)
                {
                    var val = kv.Value?["value"]?.ToString() ?? string.Empty;
                    if (val.Contains("DeployIfNotExists", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("Modify", StringComparison.OrdinalIgnoreCase))
                    {
                        needsIdentity = true;
                        break;
                    }
                }
                if (needsIdentity) break;
            }
        }
        // Also check initiative-level defaults
        if (!needsIdentity && paramsNode is not null)
        {
            foreach (var kv in paramsNode)
            {
                var dv = kv.Value?["defaultValue"]?.ToString() ?? string.Empty;
                if (dv.Equals("DeployIfNotExists", StringComparison.OrdinalIgnoreCase) ||
                    dv.Equals("Modify", StringComparison.OrdinalIgnoreCase))
                {
                    needsIdentity = true;
                    break;
                }
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// Generated by Initiative Merger — Assignment Template");
        sb.AppendLine($"// Initiative: {displayName}");
        sb.AppendLine($"// Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine("//");
        sb.AppendLine("// Deploy with:");
        sb.AppendLine("//   az deployment sub create --location westeurope --template-file assignment.bicep");
        sb.AppendLine();
        sb.AppendLine("targetScope = 'subscription'");
        sb.AppendLine();

        // Parameters block — scope + one param per initiative parameter that has no defaultValue
        sb.AppendLine("@description('Scope to assign the initiative to. Defaults to the current subscription.')");
        sb.AppendLine("param scope string = subscription().id");
        sb.AppendLine();
        sb.AppendLine("@description('Display name for the policy assignment.')");
        sb.AppendLine($"param assignmentDisplayName string = '{EscapeBicepString(displayName)}'");
        sb.AppendLine();

        if (paramsNode is not null)
        {
            foreach (var kv in paramsNode)
            {
                var paramDef = kv.Value as JsonObject;
                var type = paramDef?["type"]?.GetValue<string>() ?? "String";
                var defaultNode = paramDef?["defaultValue"];
                var meta = paramDef?["metadata"] as JsonObject;
                var desc = meta?["description"]?.GetValue<string>() ?? meta?["displayName"]?.GetValue<string>();

                // Only expose parameters that have no default (assignment must provide them)
                if (defaultNode is not null) continue;

                var bicepType = type.ToLowerInvariant() switch
                {
                    "string"  => "string",
                    "integer" => "int",
                    "boolean" => "bool",
                    "array"   => "array",
                    "object"  => "object",
                    _         => "string"
                };

                if (desc is not null)
                    sb.AppendLine($"@description('{EscapeBicepString(desc)}')");
                sb.AppendLine($"param {kv.Key} {bicepType}");
                sb.AppendLine();
            }
        }

        // Initiative definition resource
        sb.AppendLine($"resource initiative 'Microsoft.Authorization/policySetDefinitions@2021-06-01' = {{");
        sb.AppendLine($"  name: '{slug}'");
        sb.AppendLine($"  properties: {{");
        sb.AppendLine($"    displayName: '{EscapeBicepString(displayName)}'");
        sb.AppendLine($"    policyType: 'Custom'");

        var metaNode = props["metadata"] as JsonObject;
        var category = metaNode?["category"]?.GetValue<string>() ?? "Regulatory Compliance";
        var ascVal   = metaNode?["ASC"]?.GetValue<string>();
        sb.AppendLine($"    metadata: {{");
        sb.AppendLine($"      category: '{EscapeBicepString(category)}'");
        if (ascVal is not null) sb.AppendLine($"      ASC: '{ascVal}'");
        sb.AppendLine($"    }}");

        string Indent(string json, int spaces) {
            var pad = new string(' ', spaces);
            return string.Join("\n", json.Split('\n').Select((l, i) => i == 0 ? l : pad + l));
        }

        var paramsForDef = props["parameters"]?.ToJsonString(NodeJsonOptions) ?? "{}";
        var defsJson     = props["policyDefinitions"]?.ToJsonString(NodeJsonOptions) ?? "[]";
        var groupsNode   = props["policyDefinitionGroups"] as JsonArray;

        sb.AppendLine($"    parameters: {Indent(paramsForDef, 4)}");
        sb.AppendLine($"    policyDefinitions: {Indent(defsJson, 4)}");
        if (groupsNode is { Count: > 0 })
            sb.AppendLine($"    policyDefinitionGroups: {Indent(groupsNode.ToJsonString(NodeJsonOptions), 4)}");
        sb.AppendLine($"  }}");
        sb.AppendLine($"}}");
        sb.AppendLine();

        // Assignment resource
        if (needsIdentity)
        {
            sb.AppendLine("// A SystemAssigned identity is required for DeployIfNotExists/Modify policies.");
            sb.AppendLine("// Grant it the 'Contributor' role (or a narrower role) after deployment.");
        }

        sb.AppendLine($"resource assignment 'Microsoft.Authorization/policyAssignments@2022-06-01' = {{");
        sb.AppendLine($"  name: '{slug}-assignment'");
        sb.AppendLine($"  scope: resourceGroup() // Replace with desired scope, e.g. subscription()");
        if (needsIdentity)
        {
            sb.AppendLine($"  identity: {{");
            sb.AppendLine($"    type: 'SystemAssigned'");
            sb.AppendLine($"  }}");
            sb.AppendLine($"  location: 'westeurope' // Required for SystemAssigned identity");
        }
        sb.AppendLine($"  properties: {{");
        sb.AppendLine($"    displayName: assignmentDisplayName");
        sb.AppendLine($"    policyDefinitionId: initiative.id");
        sb.AppendLine($"    scope: scope");

        // Build parameters object — use defaultValue for params that have one, else reference the Bicep param
        if (paramsNode is not null && paramsNode.Count > 0)
        {
            sb.AppendLine($"    parameters: {{");
            foreach (var kv in paramsNode)
            {
                var paramDef  = kv.Value as JsonObject;
                var defaultNode2 = paramDef?["defaultValue"];
                string valueExpr;
                if (defaultNode2 is not null)
                    valueExpr = $"value: {defaultNode2.ToJsonString()}";
                else
                    valueExpr = $"value: {kv.Key}";

                sb.AppendLine($"      {kv.Key}: {{ {valueExpr} }}");
            }
            sb.AppendLine($"    }}");
        }

        sb.AppendLine($"  }}");
        sb.AppendLine($"}}");
        sb.AppendLine();
        sb.AppendLine($"output initiativeId string = initiative.id");
        sb.AppendLine($"output assignmentId string = assignment.id");
        if (needsIdentity)
            sb.AppendLine($"output principalId string = assignment.identity.principalId");

        return sb.ToString();
    }

    private static string SlugifyName(string name)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-");
        return slug.Trim('-');
    }

    private static string EscapeBicepString(string s) => s.Replace("'", "\\'");

    /// <summary>
    /// Performs post-merge validation checks and returns a list of warnings.
    /// </summary>
    private static List<MergeWarning> ValidateMergeResult(
        MergeResult result, List<MergedPolicySummary> policies, string json)
    {
        const int AzurePolicyLimit = 1000;
        const int AzureParamLimit = 400;

        var warnings = new List<MergeWarning>();
        var policyCount = policies.Count;

        // Policy count
        if (policyCount >= AzurePolicyLimit)
        {
            warnings.Add(new MergeWarning
            {
                Severity = MergeWarningSeverity.Error,
                Code = "POLICY_LIMIT_EXCEEDED",
                Message = $"The merged initiative contains {policyCount} policies, which exceeds Azure's limit of {AzurePolicyLimit}. " +
                          $"Use the Controls filter to reduce the number of policies before deploying."
            });
        }
        else if (policyCount > 800)
        {
            warnings.Add(new MergeWarning
            {
                Severity = MergeWarningSeverity.Warning,
                Code = "POLICY_COUNT_HIGH",
                Message = $"The merged initiative contains {policyCount} policies (limit: {AzurePolicyLimit}). " +
                          $"Consider filtering controls to stay well below the limit."
            });
        }

        // Parameter count (approximate from JSON parse)
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            var props2 = (root?["properties"] as JsonObject) ?? root;
            var paramCount = (props2?["parameters"] as JsonObject)?.Count ?? 0;
            if (paramCount >= AzureParamLimit)
            {
                warnings.Add(new MergeWarning
                {
                    Severity = MergeWarningSeverity.Error,
                    Code = "PARAM_LIMIT_EXCEEDED",
                    Message = $"The merged initiative has {paramCount} parameters, which exceeds Azure's limit of {AzureParamLimit}. " +
                              $"Apply parameter aliases or filter controls to reduce the count."
                });
            }
            else if (paramCount > 350)
            {
                warnings.Add(new MergeWarning
                {
                    Severity = MergeWarningSeverity.Warning,
                    Code = "PARAM_COUNT_HIGH",
                    Message = $"The merged initiative has {paramCount} parameters (limit: {AzureParamLimit}). " +
                              $"This is approaching the Azure limit."
                });
            }
        }
        catch { /* ignore JSON parse errors here */ }

        // Policies without any effect parameter (always Audit/Deny, cannot be softened)
        var effectParamPattern = new System.Text.RegularExpressions.Regex(
            @"\[parameters\('(?:effect|Effect)[^']*'\)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            var props3 = (root?["properties"] as JsonObject) ?? root;
            var defs = props3?["policyDefinitions"] as JsonArray;
            int withoutEffect = 0;
            if (defs is not null)
            {
                foreach (var def in defs)
                {
                    var paramsNode = def?["parameters"] as JsonObject;
                    if (paramsNode is null) continue;
                    var hasEffect = paramsNode.Any(p =>
                        p.Key.StartsWith("effect", StringComparison.OrdinalIgnoreCase));
                    if (!hasEffect) withoutEffect++;
                }
            }
            if (withoutEffect > 0)
            {
                warnings.Add(new MergeWarning
                {
                    Severity = MergeWarningSeverity.Info,
                    Code = "FIXED_EFFECT_POLICIES",
                    Message = $"{withoutEffect} polic{(withoutEffect == 1 ? "y has" : "ies have")} a fixed effect (no effect parameter). " +
                              $"Their effect (e.g. Audit or Deny) cannot be changed via the initiative assignment."
                });
            }
        }
        catch { /* ignore */ }

        return warnings;
    }

    /// <summary>Generates a combined description based on the source initiatives.</summary>
    private static string BuildDescription(MergeRequest request, IReadOnlyList<PolicyInitiative> initiatives)
    {
        if (!string.IsNullOrWhiteSpace(request.OutputDescription))
            return request.OutputDescription;

        var sourceNames = initiatives.Select(i => i.DisplayName);
        return $"Composite initiative generated by InitiativeMerger on {DateTimeOffset.UtcNow:yyyy-MM-dd}. " +
               $"Sources: {string.Join(", ", sourceNames)}.";
    }

    /// <summary>Extracts a human-readable key for an initiative based on the request.</summary>
    private static string ExtractKey(string resourceId, MergeRequest request)
    {
        var known = WellKnownInitiative.All.FirstOrDefault(k =>
            k.ResourceId.Equals(resourceId, StringComparison.OrdinalIgnoreCase));
        return known?.Key ?? resourceId.Split('/').LastOrDefault() ?? resourceId;
    }

    private static MergeResult FailResult(string message) =>
        new() { Success = false, ErrorMessage = message };
}
