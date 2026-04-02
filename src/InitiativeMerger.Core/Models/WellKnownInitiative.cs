namespace InitiativeMerger.Core.Models;

/// <summary>
/// Catalogue of well-known Azure Policy built-in initiatives with their verified resource IDs.
/// IDs have been verified via: az policy set-definition list
/// Policy counts are indicative — Microsoft updates these periodically.
/// </summary>
public static class WellKnownInitiative
{
    public static readonly IReadOnlyList<KnownInitiativeEntry> All =
    [
        new(
            Key: "MCSB",
            DisplayName: "Microsoft Cloud Security Benchmark",
            Description: "Microsoft's own security best practices for Azure (~223 policies). " +
                         "Default baseline in Microsoft Defender for Cloud.",
            ResourceId: "/providers/Microsoft.Authorization/policySetDefinitions/1f3afdf9-d0c9-4c3d-847f-89da613e70a8",
            Version: "57.56.0"
        ),
        new(
            Key: "CIS",
            DisplayName: "CIS Microsoft Azure Foundations Benchmark v2.0.0",
            Description: "Center for Internet Security (CIS) Azure Foundations recommendations (~108 policies).",
            ResourceId: "/providers/Microsoft.Authorization/policySetDefinitions/06f19060-9e68-4070-92ca-f15cc126059e",
            Version: "v2.0.0"
        ),
        new(
            Key: "ISO27001",
            DisplayName: "ISO 27001:2013",
            Description: "International standard for information security management (~450 policies).",
            ResourceId: "/providers/Microsoft.Authorization/policySetDefinitions/89c6cddc-1c73-4ac1-b19c-54d1a15a42f2",
            Version: "8.8.0"
        ),
        new(
            Key: "NIST",
            DisplayName: "NIST SP 800-53 Rev. 5",
            Description: "National Institute of Standards and Technology security controls (~696 policies).",
            ResourceId: "/providers/Microsoft.Authorization/policySetDefinitions/179d1daa-458f-4e47-8086-2a68d0d6c38f",
            Version: "14.19.0"
        ),
        new(
            Key: "BIO",
            DisplayName: "NL BIO Cloud Theme V2",
            Description: "Baseline Information Security Government (BIO) v2 for Dutch government organisations (~282 policies).",
            ResourceId: "/providers/Microsoft.Authorization/policySetDefinitions/d8b2ffbe-c6a8-4622-965d-4ade11d1d2ee",
            Version: "v2"
        )
    ];

    /// <summary>Look up a known initiative by key (case-insensitive).</summary>
    public static KnownInitiativeEntry? FindByKey(string key) =>
        All.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Immutable record for a known initiative entry.</summary>
public record KnownInitiativeEntry(
    string Key,
    string DisplayName,
    string Description,
    string ResourceId,
    string Version
);
