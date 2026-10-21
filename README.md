# Document Analyzer · Invoice Explorer

A Windows .NET 10 console pipeline and Blazor Server review workspace for Azure AI Document Intelligence. It preserves original PDFs, every extraction attempt, Azure confidence, source evidence, quality decisions, CSV, and verified reference labels in a local SQLite database. The solution lives directly at the repository root; open `InvoiceExplorer.slnx` in Visual Studio. The project and executable names remain `InvoiceExplorer`.

**Confidence and accuracy are distinct quantities.** Azure returns confidence scores for individual predictions. Accuracy is estimated by comparing those predictions with independently reviewed reference values. A threshold of 0.98 is an acceptance rule, not evidence of 98% accuracy. No real Azure extraction has been validated in this development environment; resource credentials and client data must be tested inside the client VM.

## Repository layout

```text
InvoiceExplorer.slnx                 .NET 10 solution
src/InvoiceExplorer.Core/            Extraction, reconciliation, SQLite, and reporting
src/InvoiceExplorer.Cli/             Batch processing and operator commands
src/InvoiceExplorer.Web/             Blazor Server dashboard and review workspace
tests/InvoiceExplorer.Tests/         Automated pipeline and integration tests
docs/                               Architecture, evaluation, validation, and VM runbook
appsettings.json                    Shared defaults, with no credentials
appsettings.local.example.json      S0 batch configuration template
invoice-workspace.marker            Workspace root discovery
```

Local `invoices`, `data`, `exports`, `artifacts`, `tmp`, and `dist` directories are excluded from Git. `appsettings.local.json` is also excluded. Package lock files are versioned. Git history preserves the retired prototypes; the working tree contains only this solution.

## Clone and run

Deployment uses GitHub and a source checkout on the client VM. Install Git and the Windows x64 .NET 10 SDK specified by `global.json` (10.0.402, with latest-patch roll-forward). Clone the repository using your approved repository URL:

```powershell
git clone <repository-url> DocumentAnalyzer
cd DocumentAnalyzer
```

For Visual Studio, open `InvoiceExplorer.slnx`, expand **Configuration**, and follow the [Visual Studio setup and F5 debugging guide](docs/VISUAL-STUDIO.md). The console includes profiles for configuration validation, input inspection, and extraction. Set **InvoiceExplorer.Web** as the startup project to debug the review interface.

Run these commands **from the repository root**:

```powershell
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
dotnet restore InvoiceExplorer.slnx --locked-mode
dotnet build InvoiceExplorer.slnx -c Release --no-restore
dotnet run --project src/InvoiceExplorer.Web -c Release --no-build --urls http://localhost:5180
```

Open `http://localhost:5180`. A new workspace contains no extraction records. Counts are zero, confidence and coverage are unavailable, and accuracy is not measured until reference labels are supplied. Starting the web app does not call Azure or generate sample records. Stop the web process with Ctrl+C.

For real extraction, copy `appsettings.local.example.json` to `appsettings.local.json`, enter the approved Azure resource settings locally, and create the `invoices` directory for your PDFs. Run:

```powershell
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- doctor
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- plan
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- extract
```

The S0 example selects up to **10,000 PDFs** with a **30,000-request analyze budget**, including extra passes and retries. Both are configurable. Without the local override, the checked-in defaults remain F0 and one PDF. There is no fixed ten-invoice cap. The configured tier must match the actual Azure resource; editing the file does not provision or change that resource.

Keep `INVOICE_ROOT` unset when using automatic root discovery, or point it to the current repository root. A variable left pointing to a previous checkout will select that workspace instead.

## Batch execution on the VM

Configure `appsettings.local.json` and keep invoice inputs and results inside the VM. After building, inspect the input selection and run the console from the repository root:

```powershell
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- plan --all
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- extract --all
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- batches
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- resume BATCH_ID
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- report --batch BATCH_ID
```

`plan` inspects local PDFs without sending them to Azure. `extract` sends selected documents to the configured Azure endpoint. Additional analysis passes and retries may consume quota and incur charges. `MaxAnalyzeRequests` is a submission-count limit, not a monetary budget. `--all` bypasses the document-count limit in S0; the request budget still applies. An explicit `--limit N` can be used for an initial sample. `resume` reuses saved operations. `--rerun` creates another execution for previously processed content.

There is no sample-data generator in the application. Synthetic responses are restricted to automated tests and temporary databases.

See [the VM runbook](docs/VM-RUNBOOK.md) for clone, configuration, execution, updates, and recovery; [the evaluation protocol](docs/EVALUATION.md) for accuracy measurement; and [the architecture](docs/ARCHITECTURE.md) for implementation details.

## Configuration

Both applications read the same workspace configuration in this order: built-in defaults, `appsettings.json`, `appsettings.local.json`, then standard `Section__Property` environment variables. JSON files allow comments and trailing commas. Paths are relative to the workspace marker unless absolute. `INVOICE_ROOT` explicitly selects a workspace.

```powershell
$env:DocumentIntelligence__Endpoint = 'https://YOUR-RESOURCE.cognitiveservices.azure.com/'
$env:DocumentIntelligence__ApiKey = 'SET-LOCALLY-ON-THE-CLIENT-VM'
$env:DocumentIntelligence__QueryFields = '["VendorName","VendorAddress","VendorTaxId","CustomerName","CustomerAddress","InvoiceId","PurchaseOrder","InvoiceDate","DueDate","SubTotal","TotalTax","InvoiceTotal","AmountDue","Currency","PaymentTerms"]'
```

The JSON array form replaces the whole list. Indexed variables such as `DocumentIntelligence__QueryFields__0` override individual entries; use the full JSON form to shorten a list. Historical `INVOICE_AZURE_ENDPOINT`, `INVOICE_AZURE_KEY`, `INVOICE_TIER`, and `INVOICE_DB` aliases remain supported and take final precedence. Remove old aliases when adopting standard settings. Old `invoice-settings.local.json` endpoint/key values are read only if the new local file is absent and the corresponding standard environment override is absent.

| Setting | Purpose |
| --- | --- |
| `DocumentIntelligence.Endpoint`, `ApiKey` | Approved Azure resource credentials. Never distributed in the package. |
| `ApiVersion`, `ModelId` | Stable REST version `2024-11-30`; original model `prebuilt-invoice`. |
| `Tier` | Actual `F0` or `S0` resource tier. Changing this string does not change Azure billing or provision a resource. |
| `Locale` | Optional Azure locale hint, for example `en-US`; leave empty for mixed documents unless evaluated. |
| `Pages` | Optional S0 subset, such as `1-3,5`. Any explicit subset blocks automatic readiness because completeness is unknown. |
| `QueryFields` | At most 20 fields for the dedicated query pass. Not a replacement for `TargetFields`. |
| `CustomModelId` | Optional already-trained custom model in the same resource. Training is performed separately in the client environment. |
| `Batch.MaxDocuments` | Default selection count; overridden by `--limit` or `--all` in S0. |
| `Concurrency`, `RequestsPerSecond` | Bounded workers and shared request pacing, including polling. |
| `MaxAnalyzeRequests` | Durable per-batch ceiling on POST attempts, including retries and fallback passes. Increase and resume when exhausted. |
| `SkipPreviouslyCompleted` | Skip the same SHA-256 content under the same extraction-policy fingerprint. `--rerun` overrides this. |
| `Pipeline.TargetFields` | Business fields included in coverage, decisions, and reports. All raw Azure fields are retained separately. |
| `RequiredFields` | Missing values block readiness. Missing optional fields remain visible but do not independently block readiness. |
| `AcceptanceConfidence`, `FieldThresholds` | Per-field acceptance policy. Setting `0.98` does not produce or prove 98% accuracy. |
| `AllowPaidFeatures` | Explicit gate for query-field and high-resolution add-ons. Requires S0. |
| `QueryMode` | `Off`, `Fallback` for unresolved configured fields, or `Always` for a controlled comparison. |
| `HighResolutionFallback` | High-resolution OCR on the original PDF when review is still needed. Requires the paid-feature gate. |
| `RasterFallback`, `RasterGrayscale` | Optional local render to lossless image-backed PDF. Off by default; validate benefit first. |
| `RasterDpi`, `MaxRasterPages`, `MaxRasterPixels` | Rendering quality and memory bounds, checked before rendering each page. |
| `MaxPasses` | Maximum strategies per invoice, 1–5. Order: Original, QueryFields, HighResolution, Raster, CustomModel. Disabled/inapplicable strategies do not consume a pass. |
| `CheckTotals`, `AmountTolerance`, `CheckDateOrder` | Conservative consistency checks that flag issues without changing values. |
| `Storage.*` | SQLite, CSV, and immutable source/variant directories. Use local writable storage. |

The default configuration is conservative F0: **one PDF, first two pages, no paid add-ons**. A document with more than two pages is flagged as incomplete. The application caps F0 files at 4 MiB and S0 files at 64 MiB to bound memory use. Large merged PDFs should be split into individual invoices before processing. One logical invoice per PDF is expected; multiple detected documents block readiness and their fields are never combined.

For F0, a local override can contain only credentials:

```json
{
  "DocumentIntelligence": {
    "Endpoint": "https://YOUR-RESOURCE.cognitiveservices.azure.com/",
    "ApiKey": "SET-ONLY-INSIDE-THE-VM",
    "Tier": "F0"
  }
}
```

## What the pipeline does

1. Discover PDFs recursively, create a durable manifest, and deduplicate by SHA-256. A renamed file retains its extraction history.
2. Inspect text/image/page characteristics locally. Copy and verify the original PDF into the artifact store.
3. Analyze the **original** with `prebuilt-invoice`, retaining the complete raw response and all extracted fields, tables, key-value pairs, OCR, and available evidence.
4. Escalate unresolved fields through configured strategies, subject to the pass limit and request budget. A dedicated `prebuilt-layout` query pass provides another candidate; it does not magically improve native field accuracy. High-resolution OCR and rasterization are different experiments.
5. Compare candidates, check formats, totals, dates, document count, and page completeness. Different valid values trigger a conflict. Agreement does not inflate scores. A derived currency code keeps a null confidence because Azure did not supply a separate field score.
6. Store every pass and final decision in SQLite. Export long CSV plus a business-oriented wide CSV. Review original PDFs, raw history, target-field evidence, and execution timelines in Blazor.
7. Record verified labels without overwriting predictions. Measure the original-pass baseline and final selection on exactly the same reviewed fields.

PDF-to-image conversion cannot recover information that was never captured, and it can discard a useful text layer. The pipeline does not indiscriminately flatten, sharpen, crop, translate, or rewrite every invoice. When OCR is already good but semantics are wrong, representative custom-model training is usually a more relevant experiment than image manipulation.

## Results and terminology

| Metric/status | Meaning |
| --- | --- |
| Mean field confidence | Arithmetic mean of selected Azure scores among scored target values. Missing/unscored values are disclosed separately. This is not an overall probability of correctness. |
| Target coverage | Present target values / all configured targets, including optional targets. Dashboard field metrics cover completed decisions; failed/pending invoices remain in Operations and invoice-level readiness denominators. |
| Ready | Required values present, extracted target fields meet policy, and no blocking quality issue. An automated policy result, not human approval or proven accuracy. |
| Review | Extraction completed with missing, invalid, uncertain, unscored, or conflicting information or quality warnings. |
| Failed | Processing failed. Raw data already persisted remains available. |
| Ambiguous | A POST may have reached Azure but no operation URL was durably confirmed. Never automatically resubmitted. |
| Accuracy | Exact normalized matches / reviewed fields labeled as present. Missing predictions count as wrong. Verified absence is measured separately. |
| Ready-invoice field precision | Correct reviewed fields among accepted fields on Ready invoices. Always consider how many invoices were rejected for review. |

The legacy history view retains its original **raw leaf-field mean**, which may include line-item fields and therefore differs from the dashboard's target-field mean. Both retain the returned Azure scores. No score is fabricated for a missing value.

## Validation

After restoring and building from the repository root as shown above, run the tests. SDK version is pinned in `global.json`; NuGet lock files are included. There is no Azure SDK dependency: a small REST client exposes request persistence and retry decisions explicitly.

```powershell
dotnet test tests/InvoiceExplorer.Tests -c Release --no-build
```

Tests use fictional PDFs, isolated SQLite databases, and fake HTTP/analyzer implementations. They cover reconciliation, missing values, no invented confidence, request boundaries, resume, ambiguous submissions, duplicates, batch budgets, label audit, evaluation, and native multipage rendering. See [validation notes](docs/VALIDATION.md).

## Data boundaries

The web app serves loopback clients only and has no external fonts, analytics, or model calls. It is a single-operator local review tool, not an authenticated multi-user service. PDF bytes are sent to Azure only by an explicit extraction command. Original documents, raw JSON, CSV, labels, and SQLite remain sensitive local business data. Keep them inside the client environment. Only source code and configuration templates are versioned. Invoice inputs, credentials, databases, extraction artifacts, and exports remain excluded from Git. Fetch code updates in the VM; do not send client data back to this development machine.

## Microsoft references

- [Confidence and accuracy](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/concept/accuracy-confidence?view=doc-intel-4.0.0)
- [Query fields, key-value pairs, and high-resolution OCR](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/concept/add-on-capabilities?view=doc-intel-4.0.0)
- [Prebuilt invoice model](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/prebuilt/invoice?view=doc-intel-4.0.0)
- [Service limits](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/service-limits?view=doc-intel-4.0.0)

Read service limits and pricing for the actual resource before a production run. Local `doctor` verifies configuration and native libraries; it cannot verify credentials, remaining quota, regional feature support, or live model accuracy.
