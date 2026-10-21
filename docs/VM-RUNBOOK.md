# Client VM runbook

## Clone and build

Use GitHub as the source distribution mechanism. The VM requires Git, Windows x64, and the .NET 10 SDK specified in `global.json` (10.0.402, with latest-patch roll-forward). Package restore requires access to the approved NuGet sources. Native SQLite, PDFium, and Skia dependencies are restored with the projects.

```powershell
git clone <repository-url> DocumentAnalyzer
cd DocumentAnalyzer
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
dotnet restore InvoiceExplorer.slnx --locked-mode
dotnet build InvoiceExplorer.slnx -c Release --no-restore
dotnet test tests/InvoiceExplorer.Tests -c Release --no-build
```

Run all following commands from this repository root. For an IDE workflow, open `InvoiceExplorer.slnx` and use the [Visual Studio configuration and F5 guide](VISUAL-STUDIO.md). Shared settings are visible in **Configuration**, and the CLI/Web projects include launch profiles. Do not transfer this development machine's invoice folder, databases, credentials, responses, logs, or screenshots.

## Configure and start

Copy `appsettings.local.example.json` to `appsettings.local.json`. Enter only the approved resource endpoint and API key locally in the VM. The API version and model have internal defaults. Optional `QueryFields` overrides control the query pass; `TargetFields` controls decisions and reporting.

Defaults process all discovered PDFs and request all pages, with query/high-resolution fallbacks when needed. There is no small document-count or submission-count budget unless explicitly configured. Environment variables override JSON. Keep `INVOICE_ROOT` unset for automatic discovery or set it to this checkout's absolute path. When upgrading from an older configuration, replace the local file with the minimal example to remove old pilot limits; no tier selector is used.

Create `invoices` locally and place PDFs there, including subfolders, or set `Batch.InputDirectory` to the approved input location. Then run:

```powershell
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- doctor
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- plan
dotnet run --project src/InvoiceExplorer.Web -c Release --no-build --urls http://localhost:5180
```

The web app starts with no records and does not call Azure. Open `http://localhost:5180` in a browser inside the VM. Use a second terminal at the repository root for extraction commands while the web process runs. Stop the web process with Ctrl+C. Non-loopback requests are rejected; this application has no multi-user authentication.

`doctor` checks resolved paths, configured credential presence, and local PDF processing. It does not print the API key or call Azure. Native rendering is exercised only if the first input has at most two pages. A successful check does not establish Azure authentication, quota, or feature availability.

## Pilot, then batch

Use `--limit N` for a representative pilot when needed. Otherwise, `extract` processes the full folder. Azure determines resource quotas and feature availability; the application does not ask for a tier or infer it from a local setting.

```powershell
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- plan --all
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- extract --all
```

`plan` prints a conservative volume bound before duplicate skipping. It is not a price quote or an exact count of billed pages. Actual usage depends on fallbacks, retries, page selection, and the resource's pricing. An optional `Batch.MaxAnalyzeRequests` limit can stop a batch before every document finishes. Request pacing applies across all workers. Begin with the default concurrency/rate values, then adjust using observed Azure quota behavior.

Input discovery is recursive and deterministic. Reparse-point directories are skipped. Identical bytes within a batch are deduplicated; previously completed hashes under the same policy are skipped unless `--rerun` is set. “Completed” includes Review, because extraction did finish. A new model/policy fingerprint automatically permits a new execution. Changing filenames does not create new invoice identities.

## Recovery

The console prints the durable batch ID before processing. Keep that ID, or run `batches` to retrieve it.

```powershell
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- resume BATCH_ID
```

| Condition | Action |
| --- | --- |
| Ctrl+C, process interruption, or polling timeout | Resume the same batch. Saved operation URLs are polled without another POST. |
| BudgetExceeded | Increase `Batch.MaxAnalyzeRequests`, then resume. The existing count is retained. |
| HTTP 401/403 | Correct the resource credential/access inside the VM, then resume. |
| Repeated HTTP 429 | Reduce request rate/concurrency or resolve quota, then resume. |
| Ambiguous | Inspect Azure usage/diagnostics. Do not keep resuming expecting a new submission. If appropriate, explicitly create a new extraction using `extract --file PATH --rerun`. |
| Failed | Inspect the retained error and raw attempts. Correct the input/configuration and explicitly start a new execution. |
| PlanningInterrupted | Start a new batch. The interrupted manifest was incomplete and no requests from it were submitted. |
| Configuration fingerprint changed | Restore the extraction policy used by that batch, or start a new batch. Worker/rate/budget/key changes are allowed during resume. |
| File changed after planning | Start a new batch for the new bytes. The old hash cannot be silently reassigned. |
| Source PDF exceeds limits or contains multiple invoices | Split into logical invoices in the client environment and rerun; this application does not guess document boundaries. |

Azure operation results have a service retention window. Resume promptly. An expired operation that returns a definitive error cannot be reconstructed from its URL; review quota/cost and deliberately submit a new execution if needed.

Exit codes: 0 for successful commands or a finished batch containing Ready/Review results; 1 for failure or unfinished work; 130 for user cancellation. Review is not a process error. All persisted results survive a nonzero exit code. Only one extractor process can use a database at a time; stop an existing console before starting another.

## Reporting and benchmark

```powershell
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- report --batch BATCH_ID
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- evaluate --split Test
```

The report command writes long business CSV, wide invoice-per-row CSV, and field metrics JSON into `exports`. The UI exports the current batch/workspace scope. Search/status filtering in the register is for navigation; export uses the batch/workspace scope, not the text/status filter. The raw history page downloads each individual run's complete CSV and JSON.

The dashboard's Test evaluation uses the latest execution per source across all batches, regardless of the displayed batch filter. It states this explicitly. Save evaluation reports after each experiment before starting the next. Use the separately reviewed labels described in `EVALUATION.md`. The application starts with no results and contains no sample-data generator. Synthetic responses are restricted to automated tests.

## Backup and storage

Stop the CLI and web process before copying the workspace's result directories. Copy the database and any existing `-wal`/`-shm` companions together, plus source/variant artifacts and the configuration used to produce them. `exports` is reproducible from retained raw/decision data, but keeping the files helps preserve an experiment's exact report snapshot. Keep credentials in the client's approved secret handling process.

Keep the same absolute workspace location when restoring results: persisted source snapshots and some source selections use absolute paths. Choose the checkout location before the first extraction. Moving a populated database alone will not move its PDF artifacts. Place the database on local storage; SQLite WAL on a network share is unsupported by this runbook.

There is no automatic retention purge. Local source PDFs, raw JSON, labels, CSV, and logs require the client's normal data retention rules. The application does not encrypt SQLite by itself. Do not upload client results to this development machine for troubleshooting; reproduce with synthetic inputs or diagnose in the VM.

## Update code in the VM

Stop the web app and any extraction process before rebuilding. Back up local results as described above. Review local source changes with `git status`; do not discard them when updating.

```powershell
git pull --ff-only
dotnet restore InvoiceExplorer.slnx --locked-mode
dotnet build InvoiceExplorer.slnx -c Release --no-restore
dotnet test tests/InvoiceExplorer.Tests -c Release --no-build
dotnet run --project src/InvoiceExplorer.Web -c Release --no-build --urls http://localhost:5180
```

A fast-forward pull stops if the local branch diverged; resolve that state before rebuilding. The ignored local configuration, invoices, database, artifacts, and exports stay in the VM. Record the deployed revision with `git rev-parse HEAD` when documenting an extraction experiment. No ZIP distribution is used.
