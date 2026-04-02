using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InitiativeMerger.Core.Models;
using Microsoft.Extensions.Logging;

namespace InitiativeMerger.Core.Services;

/// <summary>
/// Deploys a generated initiative JSON to Azure via Azure CLI.
/// Writes the JSON to a temporary file (secured with file system permissions)
/// and calls 'az policy set-definition create'.
/// </summary>
public sealed class DeploymentService : IDeploymentService
{
    private readonly ILogger<DeploymentService> _logger;

    public DeploymentService(ILogger<DeploymentService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DeploymentResult> DeployAsync(
        string initiativeJson,
        MergeRequest request,
        CancellationToken cancellationToken = default)
    {
        // AssignToScope requires an explicit scope; DefinitionOnly uses the az context when no scope is specified
        if (request.DeploymentTarget == DeploymentTarget.AssignToScope
            && string.IsNullOrWhiteSpace(request.SubscriptionId)
            && string.IsNullOrWhiteSpace(request.ManagementGroupId))
        {
            return new DeploymentResult
            {
                Success = false,
                ErrorMessage = "Assigning to scope requires a Subscription ID or Management Group ID."
            };
        }

        // az policy set-definition create expects policyDefinitions as a JSON array (--definitions)
        // and the parameters as a separate JSON object (--params). Extract these from the full initiative JSON.
        var uniqueId = Guid.NewGuid().ToString("N");
        var defsFile = Path.Combine(Path.GetTempPath(), $"initiative-defs-{uniqueId}.json");
        var paramsFile = Path.Combine(Path.GetTempPath(), $"initiative-params-{uniqueId}.json");

        try
        {
            using var doc = JsonDocument.Parse(initiativeJson);
            var props = doc.RootElement.GetProperty("properties");

            var initiativeParams = props.TryGetProperty("parameters", out var ip) ? ip : (JsonElement?)null;
            var defsElement = props.GetProperty("policyDefinitions");

            // --params: parameters object (if any exist)
            var hasParams = props.TryGetProperty("parameters", out var paramsEl)
                            && paramsEl.ValueKind == JsonValueKind.Object
                            && paramsEl.EnumerateObject().Any();

            var args = BuildDeploymentArguments(defsFile, hasParams ? paramsFile : null, request);

            // Retry loop: after each UndefinedPolicyParameter error we strip the named
            // parameters specifically for the affected policy and try again.
            // After filtering we also rewrite the params, so that initiative-params that
            // are no longer used by any policy (UnusedPolicyParameters) are omitted.
            var forcedStrips = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            const int maxAttempts = 15;
            (bool Success, string StdOut, string StdErr) result = default;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var (defsJson, usedParams) = BuildDeployableDefinitions(defsElement, initiativeParams, forcedStrips);
                await File.WriteAllTextAsync(defsFile, defsJson, cancellationToken);
                if (hasParams)
                    await File.WriteAllTextAsync(paramsFile, FilterInitiativeParams(paramsEl, usedParams), cancellationToken);

                _logger.LogInformation("Deployment attempt {N} (force-stripped: {Count} policies)", attempt, forcedStrips.Count);
                result = await RunAzCliAsync(args, cancellationToken);

                if (result.Success) break;

                // Try to parse the error as UndefinedPolicyParameter
                var (policyShortId, badParams) = ParseUndefinedPolicyParameterError(result.StdErr);
                if (policyShortId is null || badParams.Count == 0 || attempt == maxAttempts)
                {
                    _logger.LogError("Deployment failed after {N} attempts: {Error}", attempt, result.StdErr);
                    break;
                }

                _logger.LogWarning("Attempt {N}: stripping {Params} from policy {Id}", attempt, string.Join(",", badParams), policyShortId);
                if (!forcedStrips.TryGetValue(policyShortId, out var existing))
                    forcedStrips[policyShortId] = existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in badParams) existing.Add(p);
            }

            if (!result.Success)
            {
                return new DeploymentResult
                {
                    Success = false,
                    ErrorMessage = $"Azure CLI error: {result.StdErr}"
                };
            }

            var deployedId = ExtractResourceId(result.StdOut ?? string.Empty);
            var scopeLabel = !string.IsNullOrWhiteSpace(request.ManagementGroupId)
                ? $"Management Group: {request.ManagementGroupId}"
                : !string.IsNullOrWhiteSpace(request.SubscriptionId)
                    ? $"Subscription: {request.SubscriptionId}"
                    : "Current az context";

            _logger.LogInformation("Initiative definition successfully deployed: {Id}", deployedId);

            var deploymentResult = new DeploymentResult
            {
                Success = true,
                DeployedResourceId = deployedId,
                Scope = scopeLabel
            };

            // Step 2 (optional): assign the initiative to the scope so it appears in Regulatory Compliance
            if (request.DeploymentTarget == DeploymentTarget.AssignToScope && deployedId is not null)
            {
                var assignmentResult = await CreateAssignmentAsync(deployedId, request, cancellationToken);
                if (assignmentResult.Success)
                {
                    deploymentResult.AssignmentCreated = true;
                    deploymentResult.AssignmentId = assignmentResult.AssignmentId;
                    _logger.LogInformation("Policy assignment created: {Id}", assignmentResult.AssignmentId);
                }
                else
                {
                    // Definition was created, but assignment failed — not a hard failure
                    _logger.LogWarning("Creating assignment failed: {Error}", assignmentResult.ErrorMessage);
                    deploymentResult.AssignmentCreated = false;
                    deploymentResult.ErrorMessage = $"Definition created, but assignment failed: {assignmentResult.ErrorMessage}";
                }
            }

            return deploymentResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during deployment");
            return new DeploymentResult
            {
                Success = false,
                ErrorMessage = $"Unexpected error: {ex.Message}"
            };
        }
        finally
        {
            TryDeleteTempFile(defsFile);
            TryDeleteTempFile(paramsFile);
        }
    }

    /// <summary>
    /// Builds the argument list for 'az policy set-definition create'.
    /// --definitions expects a JSON array (policyDefinitions), --params a JSON object.
    /// </summary>
    private static string[] BuildDeploymentArguments(string defsFilePath, string? paramsFilePath, MergeRequest request)
    {
        var args = new List<string>
        {
            "policy", "set-definition", "create",
            "--name", SanitizeName(request.OutputDisplayName),
            "--definitions", $"@{defsFilePath}",
            "--display-name", request.OutputDisplayName,
            "--output", "json"
        };

        if (!string.IsNullOrWhiteSpace(request.OutputDescription))
            args.AddRange(["--description", request.OutputDescription]);

        if (paramsFilePath is not null)
            args.AddRange(["--params", $"@{paramsFilePath}"]);

        if (!string.IsNullOrWhiteSpace(request.ManagementGroupId))
            args.AddRange(["--management-group", request.ManagementGroupId]);
        else if (!string.IsNullOrWhiteSpace(request.SubscriptionId))
            args.AddRange(["--subscription", request.SubscriptionId]);

        return [.. args];
    }

    /// <summary>
    /// Builds a deployment-safe version of the policyDefinitions array.
    ///
    /// Two sources of deployment errors in merged initiatives:
    ///   UndefinedPolicyParameter  — a policy reference binds parameter X but the current
    ///                               policy definition version no longer knows X (schema drift).
    ///   MissingPolicyParameter    — a policy requires parameter Y but the binding is missing.
    ///
    /// Solution: filter per parameter binding:
    ///   [parameters('xxx')] reference → retain only if 'xxx' exists in the initiative parameters.
    ///   Literal value                 → always retain (no external dependency).
    ///   groupNames                    → always remove (--policy-groups not reliable in all az CLI versions).
    /// </summary>
    /// <returns>
    /// Tuple of the filtered policyDefinitions JSON and the set of initiative parameter names
    /// that are still used by at least one policy binding. The set is needed to remove
    /// unused initiative parameters from the params file so that Azure does not raise an
    /// UnusedPolicyParameters error.
    /// </returns>
    private static (string DefsJson, HashSet<string> UsedInitiativeParams) BuildDeployableDefinitions(
        JsonElement defsArray,
        JsonElement? initiativeParams,
        Dictionary<string, HashSet<string>>? forcedStrips = null)
    {
        var validParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (initiativeParams is { } ip && ip.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in ip.EnumerateObject())
                validParams.Add(p.Name);
        }

        var usedInitiativeParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartArray();

        foreach (var item in defsArray.EnumerateArray())
        {
            // Determine the short GUID of this policy for the forced-strip lookup
            HashSet<string>? forcedForPolicy = null;
            if (forcedStrips is { Count: > 0 }
                && item.TryGetProperty("policyDefinitionId", out var pidEl)
                && pidEl.GetString()?.Split('/').LastOrDefault() is { } shortId)
            {
                forcedStrips.TryGetValue(shortId, out forcedForPolicy);
            }

            writer.WriteStartObject();
            foreach (var prop in item.EnumerateObject())
            {
                if (prop.Name == "groupNames")
                    continue;

                if (prop.Name == "parameters" && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var kept = new List<JsonProperty>();
                    foreach (var paramBinding in prop.Value.EnumerateObject())
                    {
                        // Skip force-stripped parameters for this specific policy
                        if (forcedForPolicy?.Contains(paramBinding.Name) == true)
                            continue;

                        if (TryGetInitiativeParamReference(paramBinding.Value, out var refName))
                        {
                            if (refName is not null && validParams.Contains(refName))
                            {
                                kept.Add(paramBinding);
                                usedInitiativeParams.Add(refName); // track which initiative-params are still in use
                            }
                            // else: stale [parameters('xxx')] reference — remove
                        }
                        else
                        {
                            kept.Add(paramBinding); // literal value: always retain
                        }
                    }

                    if (kept.Count > 0)
                    {
                        writer.WritePropertyName("parameters");
                        writer.WriteStartObject();
                        foreach (var kv in kept)
                            kv.WriteTo(writer);
                        writer.WriteEndObject();
                    }
                    continue;
                }

                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.Flush();
        return (Encoding.UTF8.GetString(ms.ToArray()), usedInitiativeParams);
    }

    /// <summary>
    /// Filters the initiative parameters: retains only params that are used by at least one
    /// policy binding. This prevents UnusedPolicyParameters errors when schema drift causes
    /// bindings to be omitted.
    /// </summary>
    private static string FilterInitiativeParams(JsonElement paramsEl, HashSet<string> usedParams)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        foreach (var param in paramsEl.EnumerateObject())
        {
            if (usedParams.Contains(param.Name))
                param.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Detects ARM expressions of the form <c>{"value": "[parameters('name')]"}</c>.
    /// Returns true and populates <paramref name="paramName"/> if the binding is an
    /// initiative parameter reference; false if it is a literal value.
    /// </summary>
    private static bool TryGetInitiativeParamReference(JsonElement binding, out string? paramName)
    {
        paramName = null;
        if (binding.ValueKind != JsonValueKind.Object) return false;
        if (!binding.TryGetProperty("value", out var valueEl)) return false;
        if (valueEl.ValueKind != JsonValueKind.String) return false;

        var value = valueEl.GetString();
        if (value is null) return false;

        var match = Regex.Match(value, @"^\[parameters\('([^']+)'\)\]$");
        if (!match.Success) return false;

        paramName = match.Groups[1].Value;
        return true;
    }

    /// <summary>
    /// Parses an UndefinedPolicyParameter Azure error and returns the policy GUID and the
    /// parameter names that should be stripped on the next attempt.
    /// Example: "attempting to assign the parameter(s) 'maxPort,minPort' which are not defined
    ///           in the policy definition '82985f06-...' using version '7.0.0'"
    /// </summary>
    private static (string? PolicyShortId, List<string> ParamNames) ParseUndefinedPolicyParameterError(string stderr)
    {
        if (!stderr.Contains("UndefinedPolicyParameter"))
            return (null, []);

        var policyMatch = Regex.Match(stderr, @"policy definition '([0-9a-f\-]{36})'", RegexOptions.IgnoreCase);
        var paramMatch = Regex.Match(stderr, @"parameter\(s\) '([^']+)'");

        if (!policyMatch.Success || !paramMatch.Success)
            return (null, []);

        var policyId = policyMatch.Groups[1].Value;
        var paramNames = paramMatch.Groups[1].Value
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        return (policyId, paramNames);
    }

    /// <summary>Extracts the resource ID from the Azure CLI JSON output.</summary>
    private static string? ExtractResourceId(string jsonOutput)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts a display name to a valid Azure resource name
    /// (alphanumeric and hyphens only).
    /// </summary>
    private static string SanitizeName(string displayName, int maxLength = 128)
    {
        var sanitized = new string(displayName
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray());

        return sanitized.Length > maxLength ? sanitized[..maxLength] : sanitized;
    }

    private async Task<(bool Success, string StdOut, string StdErr)> RunAzCliAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        // See AzurePolicyService for explanation: on Windows use cmd.exe /c az.
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("az");
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "az",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            return (process.ExitCode == 0, stdOut, stdErr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing az CLI deployment");
            return (false, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Creates a policy assignment so that the initiative appears in
    /// Microsoft Defender for Cloud → Regulatory Compliance → Manage compliance standards.
    /// </summary>
    private async Task<(bool Success, string? AssignmentId, string? ErrorMessage)> CreateAssignmentAsync(
        string definitionResourceId,
        MergeRequest request,
        CancellationToken cancellationToken)
    {
        var scope = BuildScope(request);
        var assignmentName = SanitizeName(request.OutputDisplayName, maxLength: 24);

        var args = new List<string>
        {
            "policy", "assignment", "create",
            "--name", assignmentName,
            "--display-name", request.OutputDisplayName,
            "--policy-set-definition", definitionResourceId,
            "--scope", scope,
            "--output", "json"
        };

        var result = await RunAzCliAsync([.. args], cancellationToken);
        if (!result.Success)
        {
            return (false, null, result.StdErr.Trim());
        }

        var assignmentId = ExtractResourceId(result.StdOut);
        return (true, assignmentId, null);
    }

    /// <summary>
    /// Builds the Azure scope path for the policy assignment.
    /// Management group takes precedence over subscription.
    /// </summary>
    private static string BuildScope(MergeRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ManagementGroupId))
            return $"/providers/Microsoft.Management/managementGroups/{request.ManagementGroupId}";

        return $"/subscriptions/{request.SubscriptionId}";
    }

    private void TryDeleteTempFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Temporary file could not be deleted: {Path}", path); }
    }
}
