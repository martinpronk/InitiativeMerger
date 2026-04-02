using InitiativeMerger.Core.Models;

namespace InitiativeMerger.Core.Services;

/// <summary>
/// Contract for the core merge logic: combines multiple Azure Policy initiatives
/// into a single new initiative without duplicate policy definitions.
/// </summary>
public interface IInitiativeMergerService
{
    /// <summary>
    /// Executes the full merge operation based on the specified request.
    /// Fetches initiatives, removes duplicates, resolves conflicts and generates JSON.
    /// </summary>
    /// <param name="request">Configuration of the merge operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Complete merge result including statistics and conflict reports.</returns>
    Task<MergeResult> MergeAsync(MergeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates the initiative JSON from a set of fetched initiatives.
    /// Useful for unit testing without Azure CLI.
    /// </summary>
    string GenerateInitiativeJson(
        IReadOnlyList<PolicyInitiative> initiatives,
        MergeRequest request,
        List<ConflictReport> conflicts);

    /// <summary>
    /// Filters a previously generated initiative JSON based on selected control groups.
    /// Policies that do not belong to a selected group are removed.
    /// Initiative-level parameters that are no longer referenced anywhere after filtering are also removed
    /// to prevent UnusedPolicyParameters deployment errors.
    /// </summary>
    /// <param name="result">Previous merge result with the full JSON and policy metadata.</param>
    /// <param name="selectedGroupNames">Groups that should be retained.</param>
    /// <param name="includeUngrouped">When true, policies without a group assignment are always retained.</param>
    string FilterByGroups(MergeResult result, ISet<string> selectedGroupNames, bool includeUngrouped = true);

    /// <summary>
    /// Applies approved parameter aliases to an initiative JSON string.
    /// For each alias, all original parameter names are replaced by the canonical name
    /// throughout both the initiative-level parameters section and the policy definition bindings.
    /// Initiative parameters that are replaced are removed; the canonical parameter is kept or added.
    /// </summary>
    /// <param name="initiativeJson">Initiative JSON to transform (may be original or already filtered).</param>
    /// <param name="approvedAliases">Alias candidates to apply. CanonicalName may differ from the original suggestion.</param>
    string ApplyAliases(string initiativeJson, IEnumerable<ParameterAliasCandidate> approvedAliases);

    /// <summary>
    /// Applies user-specified value overrides (defaultValue / allowedValues) to initiative-level parameters.
    /// Only parameters with at least one Update flag set are modified; the rest are left unchanged.
    /// </summary>
    /// <param name="initiativeJson">Initiative JSON to transform.</param>
    /// <param name="overrides">Map of parameter name → override specification.</param>
    string ApplyParameterValues(string initiativeJson, IReadOnlyDictionary<string, ParameterValueOverride> overrides);

    /// <summary>
    /// Computes a semantic diff between two initiative JSON strings.
    /// Highlights which policies and parameters were added, removed or changed.
    /// </summary>
    InitiativeDiff ComputeDiff(string originalJson, string modifiedJson);

    /// <summary>
    /// Converts an initiative JSON string to a Bicep resource definition.
    /// The result is a standalone <c>.bicep</c> file that can be deployed with <c>az deployment sub create</c>.
    /// </summary>
    /// <param name="initiativeJson">Initiative JSON as produced by the merger.</param>
    /// <param name="resourceName">Optional resource name override (defaults to the initiative displayName, slugified).</param>
    string ConvertToBicep(string initiativeJson, string? resourceName = null);

    /// <summary>
    /// Generates a Bicep assignment template that creates both the initiative definition
    /// and assigns it to a scope. Parameters are pre-filled with their default values.
    /// DINE/Modify policies require a managed identity — this template includes one.
    /// </summary>
    /// <param name="initiativeJson">Initiative JSON as produced by the merger.</param>
    /// <param name="resourceName">Optional resource name override.</param>
    string GenerateAssignmentTemplate(string initiativeJson, string? resourceName = null);
}
