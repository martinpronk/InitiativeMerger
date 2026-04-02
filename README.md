# Azure Initiative Merger

A .NET 10 tool to merge multiple Azure Policy compliance initiatives into a single initiative — without duplicate policy definitions.

## Problem

Many organisations are required to follow multiple compliance frameworks simultaneously: MCSB as the Microsoft Defender for Cloud baseline, CIS as a technical hardening guide, ISO 27001 for certification, NIST SP 800-53 for government customers and BIO for Dutch government organisations. The obvious first step is to assign all of these initiatives to your subscription or management group.

However, Azure Policy has no built-in deduplication mechanism. Each initiative is evaluated independently, even when the underlying policy definitions overlap.

**What goes wrong in practice:**

| Situation | Consequence |
|---|---|
| The same `policyDefinitionId` appears in both MCSB and CIS | The policy is evaluated twice and counted twice in the compliance score |
| NIST (~696 policies) and MCSB (~223 policies) overlap significantly | Your compliance dashboard shows hundreds of duplicate findings |
| A resource is non-compliant with five initiatives at once | The actual impact is unclear — everything shows red |
| Each evaluation round runs per initiative | Extra compute costs and longer refresh cycles |

**Concrete example:**

```
MCSB      ~223 policies  ┐
CIS       ~108 policies  │
ISO 27001 ~450 policies  ├── Naïve total: ~1,759+ policies
NIST      ~696 policies  │   After deduplication: significantly fewer unique policies
BIO       ~282 policies  ┘   (exact overlap depends on current versions)
```

The result: a compliance dashboard that does not show *how well* you are doing, only *how often* each problem has been counted.

## Solution

Initiative Merger combines multiple initiatives into one, removes duplicates on `policyDefinitionId`, resolves parameter conflicts and generates a ready-to-deploy custom initiative JSON.

**The result:**
- One initiative with only unique policy definitions
- A fair, readable compliance score
- Parameter conflicts (e.g. `effect: "Audit"` vs `effect: "Deny"`) surfaced and resolved
- Controls selectable after the merge: choose exactly which control domains to include
- Direct deployment to Azure via the tool — no manual copy-pasting
- Assignment to scope so the initiative immediately appears in Defender for Cloud → Regulatory Compliance

---

## Architecture

```
src/
├── InitiativeMerger.Core/          # Domain logic (no external dependencies)
│   ├── Models/
│   │   ├── WellKnownInitiative.cs  # Catalogue of known framework IDs
│   │   ├── PolicyInitiative.cs     # ARM model for a policySetDefinition
│   │   ├── PolicyDefinitionReference.cs
│   │   ├── MergeRequest.cs         # Input model with configuration options
│   │   ├── MergeResult.cs          # Output model with statistics
│   │   └── ConflictReport.cs       # Report of parameter conflicts
│   └── Services/
│       ├── AzurePolicyService.cs   # Fetches initiatives via Azure CLI
│       ├── InitiativeMergerService.cs  # Core merge logic
│       ├── ConflictResolutionService.cs # Detects and resolves conflicts
│       └── DeploymentService.cs    # Deploys initiative via Azure CLI
│
├── InitiativeMerger.Web/           # Blazor Web App (Interactive Server) + REST API
│   ├── App.razor                   # HTML root document
│   ├── Routes.razor                # Router component
│   ├── Pages/
│   │   ├── Index.razor             # Selection UI with checkboxes
│   │   └── MergeResultPage.razor   # Result with statistics and JSON
│   └── Controllers/
│       └── InitiativeController.cs # REST API for programmatic access
│
└── InitiativeMerger.Cli/           # Standalone CLI tool
    └── Program.cs                  # Top-level statements, argument parsing
```

### Data flow

```
User selects frameworks
        ↓
AzurePolicyService.GetInitiativeAsync()
  → az policy set-definition show --name <id>
        ↓
ConflictResolutionService.DetectAndResolve()
  → Compare parameters by name
  → Resolve via strategy (PreferFirst / MostRestrictive / etc.)
        ↓
InitiativeMergerService.GenerateInitiativeJson()
  → Deduplicate on policyDefinitionId (HashSet)
  → Merge parameters
  → Group on policyDefinitionGroups
        ↓
[Optional] InitiativeMergerService.FilterByGroups()
  → Select a subset of control groups
  → Remove orphaned initiative parameters
        ↓
[Optional] DeploymentService.DeployAsync()
  → az policy set-definition create --definitions @tempfile.json
  → [AssignToScope] az policy assignment create → visible in Defender for Cloud
        ↓
JSON output / deployment confirmation
```

---

## Installation and requirements

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli) (`az` command available in PATH)
- Azure account with read access to policy definitions
- For deployment: **Policy Contributor** or **Owner** role on the target scope

### Clone and build

```bash
git clone https://github.com/your-org/initiative-merger.git
cd initiative-merger
dotnet build
```

---

## Usage: CLI

```bash
# List available frameworks
dotnet run --project src/InitiativeMerger.Cli -- --list-known

# Merge MCSB and CIS
dotnet run --project src/InitiativeMerger.Cli -- \
  --keys MCSB CIS \
  --name "My Merged Initiative" \
  --output merged.json

# Combine all five frameworks, deploy directly to a subscription
dotnet run --project src/InitiativeMerger.Cli -- \
  --keys MCSB CIS ISO27001 NIST BIO \
  --name "Full Compliance Framework" \
  --description "Combined initiative for all compliance requirements" \
  --conflict-strategy MostRestrictive \
  --deploy \
  --subscription xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx

# Add custom initiative IDs
dotnet run --project src/InitiativeMerger.Cli -- \
  --keys MCSB \
  --ids /providers/Microsoft.Authorization/policySetDefinitions/custom-id \
  --output result.json
```

### CLI options

| Option | Description | Default |
|---|---|---|
| `--keys` | Well-known framework keys (MCSB, CIS, ISO27001, NIST, BIO) | — |
| `--ids` | Additional resource IDs or display names | — |
| `--name` | Name of the new initiative | Merged Compliance Initiative |
| `--description` | Description | Auto-generated |
| `--category` | Category in Azure Portal | Regulatory Compliance |
| `--output` | Output JSON file | merged-initiative.json |
| `--conflict-strategy` | See table below | PreferFirst |
| `--deploy` | Deploy after generation | false |
| `--subscription` | Subscription ID for deployment | — |
| `--management-group` | Management Group ID for deployment | — |
| `--verbose` | Verbose logging | false |

### Conflict strategies

| Strategy | Behaviour |
|---|---|
| `PreferFirst` | Use the value from the first selected initiative |
| `MostRestrictive` | Use the lowest numeric value, false over true |
| `UseDefault` | Use the Azure Policy default value (parameter omitted) |
| `FailOnConflict` | Abort on conflicts — requires manual action |

---

## Usage: Web UI

```bash
dotnet run --project src/InitiativeMerger.Web
```

Open your browser at `http://localhost:5000`:

1. **Step 1**: Check the desired frameworks (MCSB, CIS, ISO27001, NIST, BIO)
2. **Step 2**: Optionally add additional initiative IDs
3. **Step 3**: Configure name, description and conflict strategy
   - Optional: check **Visible in Defender for Cloud → Security policies** (`ASC: true` metadata)
   - Optional: choose **Assign to scope** to make the initiative immediately visible in Regulatory Compliance
4. Click **Start merge**
5. Review statistics and conflicts
6. Use **Filter controls** to include or exclude specific control domains per framework
7. Download the (filtered) JSON or deploy directly to Azure

---

## Usage: REST API

The web application also exposes a REST API:

```bash
# List known frameworks
GET /api/initiative/known

# Merge via API (useful for CI/CD)
POST /api/initiative/merge
Content-Type: application/json

{
  "wellKnownKeys": ["MCSB", "CIS"],
  "customInitiativeIds": [],
  "outputDisplayName": "My Initiative",
  "outputDescription": "",
  "outputCategory": "Regulatory Compliance",
  "conflictResolution": "PreferFirst",
  "deployToAzure": false
}

# Azure CLI status
GET /api/initiative/azure-status
```

---

## Deploying the generated JSON manually

If you prefer not to deploy automatically, you can deploy the generated JSON yourself:

```bash
# Via Azure CLI
az policy set-definition create \
  --name "my-merged-initiative" \
  --display-name "My Merged Initiative" \
  --definitions @merged-initiative.json \
  --subscription xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx

# Or to a Management Group
az policy set-definition create \
  --name "my-merged-initiative" \
  --display-name "My Merged Initiative" \
  --definitions @merged-initiative.json \
  --management-group mg-my-organisation

# Assign to a subscription
az policy assignment create \
  --name "merged-compliance-assignment" \
  --policy-set-definition "my-merged-initiative" \
  --scope /subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

---

## Known frameworks

| Key | Framework | Policies |
|---|---|---|
| `MCSB` | Microsoft Cloud Security Benchmark | ~223 |
| `CIS` | CIS Microsoft Azure Foundations Benchmark v2.0.0 | ~108 |
| `ISO27001` | ISO 27001:2013 | ~450 |
| `NIST` | NIST SP 800-53 Rev. 5 | ~696 |
| `BIO` | NL BIO Cloud Theme V2 | ~282 |

Additional initiatives can be added via **Step 2** in the UI or `--ids` in the CLI using any Azure policy set definition resource ID or display name.

---

## Potential extensions (roadmap)

### High priority
- **Initiative versioning**: Track which Microsoft built-in version was used. Automatically detect when a built-in has been updated and generate a diff report.
- **Automatic update notifications**: Periodic Azure Function or GitHub Action that compares built-in initiative versions and opens a PR on changes.
- **Parameter conflict UI**: Interactively choose the desired value per conflict in the web UI.

### Medium priority
- **Policy effect overrides**: Ability to override the effect (Audit/Deny/DeployIfNotExists) per policy.
- **Control mapping export**: Export a CSV/Excel with the policies under each control (useful for auditors).
- **Azure DevOps / GitHub Actions integration**: Pipeline task that updates the merged initiative on every build.
- **Multi-user web app**: Replace the in-memory cache with real session state using IMemoryCache.

### Low priority
- **Policy definition preview**: Show the full definition of a policy in the UI (effect, conditions).
- **Compliance score simulation**: Calculate the expected compliance score based on current resources.
- **Terraform provider output**: Generate an `azurerm_policy_set_definition` Terraform resource alongside the ARM JSON.

---

## Security (Security by Design)

- **Command injection prevention**: Azure CLI calls use `ProcessStartInfo.ArgumentList` (no string interpolation)
- **Input validation**: Initiative IDs are validated via whitelist pattern before use as CLI arguments
- **Temporary files**: Deployment JSON is written to a GUID-named temp file and deleted immediately after use
- **Security headers**: The web app sends `X-Content-Type-Options`, `X-Frame-Options` and `Referrer-Policy` headers
- **No secrets in code**: Subscription IDs and tenant IDs are never logged at DEBUG level

---

## Contributing

Pull requests are welcome. Please open an issue first to discuss major changes.

```bash
# Run tests (once a test project has been added)
dotnet test

# Build for all platforms
dotnet publish src/InitiativeMerger.Cli -r linux-x64 --self-contained -o publish/linux
dotnet publish src/InitiativeMerger.Cli -r win-x64 --self-contained -o publish/windows
```

## License

MIT
