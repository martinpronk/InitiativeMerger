using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using InitiativeMerger.Core.Models;
using Microsoft.Extensions.Logging;

namespace InitiativeMerger.Core.Services;

/// <summary>
/// Implementation of IAzurePolicyService via Azure CLI calls.
/// Uses az policy set-definition show to retrieve initiative JSON.
/// All process calls are parameterized (no string interpolation in the command line)
/// to prevent command injection.
/// </summary>
public sealed class AzurePolicyService : IAzurePolicyService
{
    private readonly ILogger<AzurePolicyService> _logger;


    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    public AzurePolicyService(ILogger<AzurePolicyService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PolicyInitiative?> GetInitiativeAsync(
        string initiativeIdOrName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initiativeIdOrName);

        // Validate input: only accept resource IDs and alphanumeric names
        if (!IsValidInitiativeIdentifier(initiativeIdOrName))
        {
            _logger.LogWarning("Invalid initiative identifier rejected: {Id}", initiativeIdOrName);
            return null;
        }

        // az policy set-definition show --name expects the short name or GUID, not the full
        // resource ID. Extract the last segment of /providers/.../policySetDefinitions/{guid}.
        var shortName = ExtractShortName(initiativeIdOrName);

        _logger.LogInformation("Fetching initiative: {Short} (from: {Id})", shortName, initiativeIdOrName);

        var (success, stdOut, stdErr) = await RunAzCliAsync(
            ["policy", "set-definition", "show", "--name", shortName],
            cancellationToken);

        if (!success)
        {
            var errorDetail = string.IsNullOrWhiteSpace(stdErr) ? "(no stderr)" : stdErr.Trim();
            _logger.LogWarning("Fetch failed for {Short}: {Error}", shortName, errorDetail);
            // Throw an exception so the caller can display the real error
            throw new InvalidOperationException(
                $"Initiative '{shortName}' could not be retrieved.\naz stderr: {errorDetail}");
        }

        try
        {
            return JsonSerializer.Deserialize<PolicyInitiative>(stdOut, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parse error for {Short}. Output (first 500 chars): {Out}",
                shortName, stdOut[..Math.Min(500, stdOut.Length)]);
            throw new InvalidOperationException(
                $"JSON parse error for initiative '{shortName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts the short name/GUID from a full Azure resource ID.
    /// /providers/Microsoft.Authorization/policySetDefinitions/1f3a... → 1f3a...
    /// Returns the input unchanged if it is not a resource ID.
    /// </summary>
    private static string ExtractShortName(string identifier) =>
        identifier.StartsWith('/') ? identifier.Split('/').Last() : identifier;

    /// <inheritdoc />
    public async Task<(IReadOnlyList<PolicyInitiative> Initiatives, IReadOnlyList<string> Errors)> GetInitiativesAsync(
        IEnumerable<string> initiativeIdsOrNames,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PolicyInitiative>();
        var errors = new List<string>();

        foreach (var id in initiativeIdsOrNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var initiative = await GetInitiativeAsync(id, cancellationToken);
                if (initiative is not null)
                    results.Add(initiative);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initiative skipped: {Id}", id);
                errors.Add(ex.Message);
            }
        }

        return (results.AsReadOnly(), errors.AsReadOnly());
    }

    /// <inheritdoc />
    public async Task<AzureCliStatus> CheckAzureCliStatusAsync(CancellationToken cancellationToken = default)
    {
        // Combine the availability and login check into a single call (az account show).
        // On Windows RunAzCliAsync uses cmd.exe /c az, so cmd.exe is always found.
        // If az is not in PATH, the call returns exit code != 0 with an error message in stderr.
        var (success, stdOut, stdErr) = await RunAzCliAsync(
            ["account", "show", "--output", "json"], cancellationToken);

        if (!success)
        {
            // Distinguish: az not found vs. not logged in
            var isNotFound = stdErr.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
                          || stdErr.Contains("not found", StringComparison.OrdinalIgnoreCase)
                          || stdErr.Contains("cannot find", StringComparison.OrdinalIgnoreCase);

            return isNotFound
                ? new AzureCliStatus(IsAvailable: false, IsLoggedIn: false, TenantId: null, AccountName: null,
                    ErrorMessage: "Azure CLI (az) not found. Make sure az is in PATH and restart the application.")
                : new AzureCliStatus(IsAvailable: true, IsLoggedIn: false, TenantId: null, AccountName: null,
                    ErrorMessage: "Not logged in to Azure. Run 'az login' and click Refresh.");
        }

        try
        {
            using var doc = JsonDocument.Parse(stdOut);
            var root = doc.RootElement;
            var tenantId = root.TryGetProperty("tenantId", out var t) ? t.GetString() : null;
            var name = root.TryGetProperty("user", out var u)
                && u.TryGetProperty("name", out var n) ? n.GetString() : null;

            return new AzureCliStatus(IsAvailable: true, IsLoggedIn: true, TenantId: tenantId, AccountName: name, ErrorMessage: null);
        }
        catch
        {
            _logger.LogWarning("Could not parse az account show output. StdOut: {Out}", stdOut[..Math.Min(200, stdOut.Length)]);
            return new AzureCliStatus(IsAvailable: true, IsLoggedIn: false, TenantId: null, AccountName: null,
                ErrorMessage: "Could not process account information. Please try again.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetPolicyDisplayNamesAsync(
        IEnumerable<string> policyDefinitionIds,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ids = policyDefinitionIds.ToList();
        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;

        // Max 15 concurrent az CLI calls — higher values overload the az CLI process pool
        var semaphore = new SemaphoreSlim(15, 15);

        var tasks = ids.Select(async id =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var shortName = id.StartsWith('/') ? id.Split('/').Last() : id;
                var (success, stdOut, _) = await RunAzCliAsync(
                    ["policy", "definition", "show", "--name", shortName], cancellationToken);

                if (success && !string.IsNullOrWhiteSpace(stdOut))
                {
                    using var doc = JsonDocument.Parse(stdOut);
                    var root = doc.RootElement;
                    // az CLI flattens properties; defensively also try properties.displayName
                    var displayName =
                        (root.TryGetProperty("displayName", out var dn) ? dn.GetString() : null)
                        ?? (root.TryGetProperty("properties", out var props)
                            && props.TryGetProperty("displayName", out var pdn) ? pdn.GetString() : null);

                    if (displayName is not null)
                        result[id] = displayName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "displayName not available for {Id}", id);
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                progress?.Report(done);
            }
        });

        await Task.WhenAll(tasks);
        return result;
    }

    /// <summary>
    /// Executes an Azure CLI command as a child process.
    ///
    /// On Windows 'az' is a batch wrapper (.cmd file). A .NET Process with
    /// UseShellExecute=false cannot start .cmd files directly — including az.cmd.
    /// The solution is to use cmd.exe /c az [...args] as a wrapper.
    /// On Linux/macOS 'az' is invoked directly.
    ///
    /// Arguments are passed via ArgumentList (never as a string) to prevent command injection.
    /// </summary>
    private async Task<(bool Success, string StdOut, string StdErr)> RunAzCliAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo;

        if (OperatingSystem.IsWindows())
        {
            // cmd.exe /c az [arg1] [arg2] ...
            // ArgumentList quotes each argument safely — cmd.exe /c accepts this correctly.
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
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            // Read stdout and stderr in parallel to prevent deadlocks on large output
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            _logger.LogDebug("az exit={Exit} stdout={Out} stderr={Err}",
                process.ExitCode, stdOut.Length, stdErr.Length);

            return (process.ExitCode == 0, stdOut, stdErr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing az CLI command");
            return (false, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Validates that an initiative identifier is safe for use as a CLI argument.
    /// Accepts: full resource IDs (/providers/...), GUIDs, and display names without special characters.
    /// </summary>
    private static bool IsValidInitiativeIdentifier(string identifier)
    {
        // Full resource ID: must start with /providers/ or /subscriptions/
        if (identifier.StartsWith('/'))
        {
            return identifier.StartsWith("/providers/Microsoft.Authorization/policySetDefinitions/", StringComparison.OrdinalIgnoreCase)
                || identifier.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
                || identifier.StartsWith("/providers/Microsoft.Management/managementGroups/", StringComparison.OrdinalIgnoreCase);
        }

        // GUID or display name: only alphanumeric, spaces, hyphens, parentheses and dots
        return identifier.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')');
    }
}
