using InitiativeMerger.Core.Models;

namespace InitiativeMerger.Core.Services;

/// <summary>
/// Contract for deploying a generated initiative to Azure.
/// </summary>
public interface IDeploymentService
{
    /// <summary>
    /// Deploys the initiative JSON to Azure via Azure CLI.
    /// Requires: az login, Policy Contributor or Owner role on the target scope.
    /// </summary>
    /// <param name="initiativeJson">The initiative JSON to deploy.</param>
    /// <param name="request">The merge request with deployment configuration (scope, subscription ID).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DeploymentResult> DeployAsync(string initiativeJson, MergeRequest request, CancellationToken cancellationToken = default);
}
