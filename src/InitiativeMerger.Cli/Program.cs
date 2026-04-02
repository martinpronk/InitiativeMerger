using InitiativeMerger.Core.Models;
using InitiativeMerger.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// CLI entry point for the Initiative Merger tool.
/// Usage: initiative-merger [options]
///
/// Examples:
///   initiative-merger --keys MCSB CIS --name "My Initiative" --output merged.json
///   initiative-merger --keys MCSB ISO27001 BIO --deploy --subscription xxxxxxxx-xxxx
///   initiative-merger --ids /providers/... --conflict-strategy MostRestrictive
/// </summary>

// --- Set up DI container ---
var services = new ServiceCollection();

services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Warning); // Only warnings/errors in CLI mode
});

services.AddScoped<IAzurePolicyService, AzurePolicyService>();
services.AddScoped<IConflictResolutionService, ConflictResolutionService>();
services.AddScoped<IDeploymentService, DeploymentService>();
services.AddScoped<IInitiativeMergerService, InitiativeMergerService>();

using var provider = services.BuildServiceProvider();

// --- Parse arguments ---
var args_list = args.ToList();

if (args_list.Contains("--help") || args_list.Contains("-h") || args_list.Count == 0)
{
    PrintHelp();
    return 0;
}

if (args_list.Contains("--list-known"))
{
    PrintKnownInitiatives();
    return 0;
}

// Read required or optional parameters
var keys = GetMultipleArgs(args_list, "--keys");
var customIds = GetMultipleArgs(args_list, "--ids");
var outputName = GetArg(args_list, "--name") ?? "Merged Compliance Initiative";
var outputDescription = GetArg(args_list, "--description") ?? string.Empty;
var outputCategory = GetArg(args_list, "--category") ?? "Regulatory Compliance";
var outputFile = GetArg(args_list, "--output") ?? "merged-initiative.json";
var conflictStrategyStr = GetArg(args_list, "--conflict-strategy") ?? "PreferFirst";
var subscriptionId = GetArg(args_list, "--subscription");
var managementGroupId = GetArg(args_list, "--management-group");
var deploy = args_list.Contains("--deploy");
var verbose = args_list.Contains("--verbose") || args_list.Contains("-v");

if (verbose)
{
    // Increase logging level for verbose mode
    Console.WriteLine("[VERBOSE] Verbose mode enabled");
}

if (keys.Count == 0 && customIds.Count == 0)
{
    Console.Error.WriteLine("Error: Use --keys and/or --ids to select initiatives.");
    Console.Error.WriteLine("Use --help for documentation, --list-known for available frameworks.");
    return 1;
}

if (!Enum.TryParse<ConflictResolutionStrategy>(conflictStrategyStr, true, out var conflictStrategy))
{
    Console.Error.WriteLine($"Error: Unknown conflict strategy '{conflictStrategyStr}'.");
    Console.Error.WriteLine($"Valid options: {string.Join(", ", Enum.GetNames<ConflictResolutionStrategy>())}");
    return 1;
}

// --- Execute the merge ---
Console.WriteLine("Initiative Merger");
Console.WriteLine("=================");

if (keys.Count > 0)
    Console.WriteLine($"Frameworks:    {string.Join(", ", keys)}");
if (customIds.Count > 0)
    Console.WriteLine($"Custom IDs:    {string.Join(", ", customIds)}");
Console.WriteLine($"Output name:   {outputName}");
Console.WriteLine($"Strategy:      {conflictStrategy}");
Console.WriteLine($"Output file:   {outputFile}");
if (deploy) Console.WriteLine($"Deployment:    {(subscriptionId is not null ? $"Subscription {subscriptionId}" : $"Management Group {managementGroupId}")}");
Console.WriteLine();

// Check Azure CLI status
using var scope = provider.CreateScope();
var azureService = scope.ServiceProvider.GetRequiredService<IAzurePolicyService>();
var merger = scope.ServiceProvider.GetRequiredService<IInitiativeMergerService>();

Console.Write("Checking Azure CLI status... ");
var status = await azureService.CheckAzureCliStatusAsync();

if (!status.IsAvailable)
{
    Console.Error.WriteLine($"\nFout: {status.ErrorMessage}");
    return 1;
}

if (!status.IsLoggedIn)
{
    Console.Error.WriteLine($"\nFout: {status.ErrorMessage}");
    return 1;
}

Console.WriteLine($"OK (logged in as {status.AccountName})");

// Execute the merge
Console.Write("Fetching and merging initiatives... ");

var request = new MergeRequest
{
    WellKnownKeys = keys,
    CustomInitiativeIds = customIds,
    OutputDisplayName = outputName,
    OutputDescription = outputDescription,
    OutputCategory = outputCategory,
    ConflictResolution = conflictStrategy,
    DeployToAzure = deploy,
    SubscriptionId = subscriptionId,
    ManagementGroupId = managementGroupId
};

var result = await merger.MergeAsync(request);

if (!result.Success)
{
    Console.Error.WriteLine($"\nFout: {result.ErrorMessage}");
    return 1;
}

Console.WriteLine("Done!");

// --- Show statistics ---
Console.WriteLine();
Console.WriteLine("Statistics:");
Console.WriteLine($"  Policies before merge:  {result.Statistics.TotalPoliciesBeforeMerge}");
Console.WriteLine($"  Unique policies:        {result.Statistics.UniquePoliciesAfterMerge}");
Console.WriteLine($"  Duplicates removed:     {result.Statistics.DuplicatesRemoved}");
Console.WriteLine($"  Parameter conflicts:    {result.Statistics.ParameterConflictsFound} ({result.Statistics.ParameterConflictsResolved} resolved)");

// --- Report conflicts ---
if (result.Conflicts.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"Parameter conflicts ({result.Conflicts.Count}):");
    foreach (var conflict in result.Conflicts)
    {
        var status_icon = conflict.IsAutoResolved ? "+" : "!";
        Console.WriteLine($"  [{status_icon}] {conflict.ParameterName} ({conflict.Type})");
        if (verbose)
        {
            Console.WriteLine($"      {conflict.Description}");
            Console.WriteLine($"      Resolution: {conflict.ResolutionDescription}");
        }
    }
}

// --- Save JSON ---
if (result.GeneratedJson is not null)
{
    await File.WriteAllTextAsync(outputFile, result.GeneratedJson);
    Console.WriteLine();
    Console.WriteLine($"Initiative JSON saved: {Path.GetFullPath(outputFile)}");
}

// --- Deployment result ---
if (result.Deployment is not null)
{
    Console.WriteLine();
    if (result.Deployment.Success)
    {
        Console.WriteLine($"Deployment succeeded!");
        Console.WriteLine($"  Scope:       {result.Deployment.Scope}");
        Console.WriteLine($"  Resource ID: {result.Deployment.DeployedResourceId}");
    }
    else
    {
        Console.Error.WriteLine($"Deployment failed: {result.Deployment.ErrorMessage}");
        return 1;
    }
}

// --- Show Azure Portal link ---
Console.WriteLine();
Console.WriteLine("Tip: View your compliance results in the Azure Portal:");
Console.WriteLine("  https://portal.azure.com/#blade/Microsoft_Azure_Policy/PolicyMenuBlade/Compliance");

return 0;

// --- Helper functions ---

static string? GetArg(List<string> args, string flag)
{
    var idx = args.IndexOf(flag);
    return idx >= 0 && idx + 1 < args.Count ? args[idx + 1] : null;
}

static List<string> GetMultipleArgs(List<string> args, string flag)
{
    var result = new List<string>();
    var idx = args.IndexOf(flag);
    if (idx < 0) return result;

    for (int i = idx + 1; i < args.Count && !args[i].StartsWith("--"); i++)
        result.Add(args[i]);

    return result;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Initiative Merger - Azure Policy Initiative Combinator
        =======================================================

        USAGE:
          initiative-merger [options]

        OPTIONS:
          --keys <key1> [key2] ...     Well-known framework keys (see --list-known)
          --ids <id1> [id2] ...        Additional initiative resource IDs or names
          --name <name>                Name of the new initiative (default: "Merged Compliance Initiative")
          --description <text>         Description of the new initiative
          --category <category>        Category in Azure Portal (default: "Regulatory Compliance")
          --output <file>              Output JSON file (default: merged-initiative.json)
          --conflict-strategy <opt>    PreferFirst | MostRestrictive | UseDefault | FailOnConflict
                                       (default: PreferFirst)
          --deploy                     Deploy the initiative directly to Azure after generation
          --subscription <id>          Subscription ID for deployment
          --management-group <id>      Management Group ID for deployment (alternative)
          --list-known                 Show all known framework keys
          --verbose, -v                Verbose output
          --help, -h                   This help text

        EXAMPLES:
          # Combine MCSB and CIS, save as JSON
          initiative-merger --keys MCSB CIS --name "My Initiative" --output merged.json

          # Combine five frameworks and deploy to a subscription
          initiative-merger --keys MCSB CIS ISO27001 NIST BIO \
            --name "Full Compliance Framework" \
            --deploy --subscription xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx

          # Use a custom initiative ID alongside known frameworks
          initiative-merger --keys MCSB \
            --ids /providers/Microsoft.Authorization/policySetDefinitions/custom-id \
            --conflict-strategy MostRestrictive

        REQUIREMENTS:
          - Azure CLI (az) installed
          - az login executed
          - For deployment: Policy Contributor or Owner role on the target scope
        """);
}

static void PrintKnownInitiatives()
{
    Console.WriteLine("Known frameworks (--keys options):");
    Console.WriteLine();
    foreach (var i in WellKnownInitiative.All)
    {
        Console.WriteLine($"  {i.Key,-12} {i.DisplayName}");
        Console.WriteLine($"  {string.Empty,-12} {i.Description}");
        Console.WriteLine($"  {string.Empty,-12} ID: {i.ResourceId}");
        Console.WriteLine();
    }
}
