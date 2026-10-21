# Client VM runbook

## Transfer and first launch

Transfer the clean portable ZIP or source ZIP through the client's approved process. Do not copy this development machine's invoice folder, database, credentials, raw responses, logs, or screenshots. No data needs to travel back from the VM.

Extract the Windows x64 portable ZIP to a writable local folder such as `C:\Tools\InvoiceExplorer`. An SDK is not required for the self-contained executables. Native PDFium, Skia, SQLite, and .NET runtime files are included; do not move only the EXE. Corporate application controls may require the client to approve locally built binaries. The source ZIP is provided if the client prefers building inside its environment.

Copy `appsettings.local.example.json` to `appsettings.local.json`. Configure the resource endpoint/key and actual Azure tier inside the VM. Keep the API version at `2024-11-30` unless testing a deliberate compatibility change. QueryFields and TargetFields serve different purposes; keep the 15 target names unless the agreed business schema changes.

The example S0 override enables paid query/high-resolution passes and allows a large batch, but the root configuration defaults to F0/one file. Explicitly review the example's limits before using it. Environment variables override JSON. Use `doctor` to confirm the resolved paths and whether credentials are configured; it never prints the API key or calls Azure.

```powershell
.\Run-Console.cmd doctor
.\Run-Console.cmd plan --limit 10
.\Run-Console.cmd extract --limit 10
.\Start-Review.cmd
```

`doctor` tests the PDF reader and native renderer on the first input if it has at most two pages. With an empty input folder it reports that native rendering could not be exercised. A successful local check does not verify Azure authentication or feature availability. The first real extraction is the integration smoke test.

The review application listens on `http://localhost:5180` inside the VM. Access it using a browser in that VM. Do not configure a public binding or reverse proxy: there is no multi-user authentication, and non-loopback requests are rejected. Close its terminal or press Ctrl+C to stop it.

## Pilot, then batch

Use a representative pilot and inspect field evidence before processing everything. F0 deliberately processes one PDF and only its first two pages. S0 is needed by this application for large batches and paid add-ons. The Azure resource tier must actually match the local setting.

```powershell
.\Run-Console.cmd plan --all
.\Run-Console.cmd extract --all
```

`plan` prints a conservative volume bound before duplicate skipping. It is not a price quote or an exact count of billed pages. Actual usage depends on fallbacks, retries, page selection, and the resource's pricing. The submission limit can stop a batch before every document finishes. Request pacing applies across all workers. Begin with low concurrency and rate values, then adjust using observed Azure quota behavior.

Input discovery is recursive and deterministic. Reparse-point directories are skipped. Identical bytes within a batch are deduplicated; previously completed hashes under the same policy are skipped unless `--rerun` is set. “Completed” includes Review, because extraction did finish. A new model/policy fingerprint automatically permits a new execution. Changing filenames does not create new invoice identities.

## Recovery

The console prints the durable batch ID before processing. Keep that ID, or run `batches` to retrieve it.

```powershell
.\Run-Console.cmd resume BATCH_ID
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
.\Run-Console.cmd report --batch BATCH_ID
.\Run-Console.cmd evaluate --split Test
```

The report command writes long business CSV, wide invoice-per-row CSV, and field metrics JSON into `exports`. The UI exports the current batch/workspace scope. Search/status filtering in the register is for navigation; export uses the batch/workspace scope, not the text/status filter. The raw history page downloads each individual run's complete CSV and JSON.

The dashboard's Test benchmark uses the latest execution per source across the selected real/demo workspace, regardless of the displayed batch filter. It states this explicitly. Save evaluation reports after each experiment before starting the next. Use the separately reviewed labels described in `EVALUATION.md`; do not interpret fictional demo numbers as a test result.

## Backup and storage

Stop the CLI and web process before copying the workspace's result directories. Copy the database and any existing `-wal`/`-shm` companions together, plus source/variant artifacts and the configuration used to produce them. `exports` is reproducible from retained raw/decision data, but keeping the files helps preserve an experiment's exact report snapshot. Keep credentials in the client's approved secret handling process.

Keep the same absolute workspace location when restoring results: persisted source snapshots and some source selections use absolute paths. The portable application itself can be placed anywhere before the first run. Moving a populated database alone will not move its PDF artifacts. Place the database on local storage; SQLite WAL on a network share is unsupported by this runbook.

There is no automatic retention purge. Local source PDFs, raw JSON, labels, CSV, and logs require the client's normal data retention rules. The application does not encrypt SQLite by itself. Do not upload client results to this development machine for troubleshooting; reproduce with synthetic inputs or diagnose in the VM.

## Source build in the VM

Install an approved .NET 10 SDK compatible with `global.json`, restore the included package locks, and build the `.slnx` solution. Initial package restore and portable publication require approved NuGet/runtime access; running the already-published package does not require NuGet.

```powershell
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
dotnet restore InvoiceExplorer.slnx --locked-mode
dotnet build InvoiceExplorer.slnx -c Release --no-restore
dotnet test tests/InvoiceExplorer.Tests -c Release --no-build
dotnet run --project src/InvoiceExplorer.Cli -c Release -- doctor
dotnet run --project src/InvoiceExplorer.Web -c Release --urls http://localhost:5180
```

`Publish-Portable.ps1` creates fresh timestamped output directories; it does not overwrite a prior package. It copies only an explicit allowlist of source/configuration/documentation and published runtime files. Local settings, invoices, databases, exports, and screenshots are excluded. SHA-256 files accompany both ZIPs for transfer-integrity verification.
