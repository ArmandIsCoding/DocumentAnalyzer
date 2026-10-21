# Repository guidance

## Scope and language

- This repository contains the Invoice Explorer solution directly at its root: `InvoiceExplorer.slnx`.
- All application UI, code comments, documentation, reports, and command output must be in English.
- Use precise technical terminology. Titles must describe data or methods; avoid slogans, promotional claims, and unsupported performance statements.
- Do not seed synthetic extraction records in application workspaces or add demonstration modes. Synthetic fixtures belong only in automated tests and temporary test databases.
- The retired DocumentAnalyzer and DocumentAnalyzerLLM prototypes are no longer part of the working tree. Do not restore their Identity, EF Core, upload UI, or Azure SDK architecture into this solution.

## Architecture

- `src/InvoiceExplorer.Core`: shared configuration, Azure REST client, PDF inspection/rendering, batch orchestration, field reconciliation, SQLite repositories, CSV reports, and evaluation.
- `src/InvoiceExplorer.Cli/Program.cs`: operator commands, request budgets, and batch execution.
- `src/InvoiceExplorer.Web`: Blazor Server review dashboard, execution history, field evidence, labels, and downloads. It serves loopback clients only.
- `tests/InvoiceExplorer.Tests`: fictional-document, temporary-database, and fake-analyzer tests. Tests must not submit documents to Azure.
- Read `docs/ARCHITECTURE.md` before changing orchestration or persistence and `docs/EVALUATION.md` before changing quality metrics.

## Local workflows

Use Windows x64 and the .NET SDK specified in `global.json`. Run commands from the repository root:

```powershell
dotnet restore InvoiceExplorer.slnx --locked-mode
dotnet build InvoiceExplorer.slnx -c Release --no-restore
dotnet test tests/InvoiceExplorer.Tests -c Release --no-build
dotnet run --project src/InvoiceExplorer.Web -c Release --no-build --urls http://localhost:5180
```

`doctor`, `plan`, and starting the web application do not call Azure. `extract` submits documents and may incur charges. Deployment uses GitHub and a source clone in the VM. Do not generate ZIP deliverables or restore the retired packaging scripts.

Visual Studio users configure the root files through linked **Configuration** items and use the committed CLI/Web launch profiles. Keep profiles free of credentials and machine-specific paths. `Validate configuration` is the first CLI profile; extraction profiles must remain explicit. See `docs/VISUAL-STUDIO.md`.

## Configuration and data

- `invoice-workspace.marker` identifies the workspace. Shared configuration comes from `appsettings.json`, `appsettings.local.json`, and supported environment overrides. `INVOICE_ROOT` can explicitly select another workspace; never hardcode a developer checkout path.
- Keep credentials in the ignored local settings file or the client's approved secret mechanism. Never print keys or commit populated credentials.
- Keep Endpoint, ApiKey, ModelId, and Locale explicit in both root configuration templates. ModelId defaults to prebuilt-invoice; an empty Locale omits the language hint. Keep the API version internal; do not reintroduce tier selectors, paid-feature gates, or small pilot limits. Defaults process the full folder and request all pages. Experiment limits and strategy overrides remain optional.
- Local invoices, SQLite databases and sidecars, extraction artifacts, exports, screenshots, build outputs, temporary files, and portable packages are ignored. Do not include them in commits or source packages.
- SQLite schema changes must be additive and preserve raw history, attempts, labels, and resumable state. Existing stored artifact paths can be absolute; see the VM runbook before relocating populated results.

## Extraction and review invariants

- Preserve the original PDF, raw Azure response, field evidence, and scores for each pass. Do not inflate confidence when candidates agree or fabricate confidence for derived values.
- Confidence is not measured accuracy. Evaluate predictions against independently reviewed labels. Missing observations must not become zero confidence or fabricated performance values.
- Contradictory candidates and incomplete documents require review. Keep predictions immutable when adding reference labels.
- Preserve request budgets, rate limits, saved operation URLs, and the rule against automatically resubmitting ambiguous POST requests.
- Keep documents from different detected invoices separate. PDF rasterization is an optional evaluated fallback, not a mandatory preprocessing step.
- The review app is a local single-operator tool. Do not make it publicly accessible without an explicit authentication and hosting design.

## Delivery

- Maintain README instructions, the VM runbook, and tests when changing operator-visible behavior.
- Preserve secret/data exclusions, dependency locks, and native dependency compatibility. Document clone, restore, build, and run steps; use Git for code delivery.
- Do not claim live Azure accuracy or throughput validation from fake-analyzer tests or fictional demo data.
