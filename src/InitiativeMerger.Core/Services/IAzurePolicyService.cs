using InitiativeMerger.Core.Models;

namespace InitiativeMerger.Core.Services;

/// <summary>
/// Contract for retrieving Azure Policy initiatives via the Azure CLI.
/// </summary>
public interface IAzurePolicyService
{
    /// <summary>
    /// Retrieves an initiative by its resource ID or display name.
    /// </summary>
    /// <param name="initiativeIdOrName">Full resource ID or display name of the initiative.</param>
    /// <param name="cancellationToken">Cancellation token for async operations.</param>
    /// <returns>The retrieved initiative, or null if not found.</returns>
    Task<PolicyInitiative?> GetInitiativeAsync(string initiativeIdOrName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves multiple initiatives in bulk.
    /// Returns both the found initiatives and the error messages for initiatives that could not be found.
    /// </summary>
    Task<(IReadOnlyList<PolicyInitiative> Initiatives, IReadOnlyList<string> Errors)> GetInitiativesAsync(
        IEnumerable<string> initiativeIdsOrNames, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the displayName for multiple policy definitions in parallel via az CLI.
    /// Returns a dictionary of policyDefinitionId → displayName.
    /// Policies that cannot be retrieved are silently skipped.
    /// </summary>
    /// <param name="progress">Optional: reports the number of completed lookups (for progress indicator).</param>
    Task<IReadOnlyDictionary<string, string>> GetPolicyDisplayNamesAsync(
        IEnumerable<string> policyDefinitionIds,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the Azure CLI is available and the user is logged in.
    /// </summary>
    Task<AzureCliStatus> CheckAzureCliStatusAsync(CancellationToken cancellationToken = default);
}

public record AzureCliStatus(bool IsAvailable, bool IsLoggedIn, string? TenantId, string? AccountName, string? ErrorMessage);
