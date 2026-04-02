using InitiativeMerger.Core.Models;

namespace InitiativeMerger.Core.Services;

/// <summary>
/// Contract for detecting and resolving parameter conflicts
/// between multiple Azure Policy initiatives.
/// </summary>
public interface IConflictResolutionService
{
    /// <summary>
    /// Analyses all initiative parameters, detects conflicts and resolves them
    /// based on the specified strategy.
    /// </summary>
    /// <param name="initiatives">The initiatives to analyse.</param>
    /// <param name="strategy">Strategy for resolving detected conflicts.</param>
    /// <returns>List of conflict reports (resolved and unresolved).</returns>
    List<ConflictReport> DetectAndResolve(
        IReadOnlyList<PolicyInitiative> initiatives,
        ConflictResolutionStrategy strategy);
}
