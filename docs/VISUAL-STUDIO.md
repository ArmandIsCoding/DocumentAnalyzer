# Configure and debug in Visual Studio

Open `InvoiceExplorer.slnx` with Visual Studio and the .NET 10 SDK specified in `global.json`. Select the **Debug** build configuration. No terminal is required for the steps below.

## Shared configuration

The solution's **Configuration** folder contains `appsettings.json` and `appsettings.local.example.json`. The CLI and Web projects also expose these same files through linked **Configuration** items. They are references to files at the repository root, not separate copies.

1. Open `Configuration/appsettings.local.example.json`.
2. Use **File > Save As** to save a copy named `appsettings.local.json` in the repository root, beside `InvoiceExplorer.slnx`. If that local file already exists, open and edit it instead.
3. Enter `DocumentIntelligence.Endpoint` and `ApiKey`. These are the only required values. Place PDFs in the root `invoices` folder; subfolders are included.
4. Save the file. Reload the solution if the new local file does not appear under the CLI/Web project's **Configuration** folder.

The example contains only endpoint and key. Model, query fields, all-page selection, and pipeline settings have internal defaults. The console processes the full folder by default. To use another input location, optionally add `"Batch": { "InputDirectory": "C:/Invoices" }`. Query fields and other experiment settings can also be overridden, but are not required to start.

If upgrading an old local file, replace its content with the current two-value example and keep your endpoint/key. Old `Tier`, `ApiVersion`, and `AllowPaidFeatures` entries are ignored; explicit `MaxDocuments`, `MaxAnalyzeRequests`, and `Pages` overrides still apply until removed.

`appsettings.local.json` is ignored by Git and is not copied into build output. Do not put credentials in the tracked template, shared defaults, or `launchSettings.json`. The Web project's own `appsettings.json` controls ASP.NET logging; it is not the shared extraction configuration.

The applications locate the repository root from `invoice-workspace.marker`. The working directory does not need a machine-specific override. If an existing `INVOICE_ROOT` environment variable points to another checkout, remove it or correct it. Other supported environment variables still override JSON.

## Debug the console with F5

Right-click **InvoiceExplorer.Cli** and select **Set as Startup Project**. Select a profile beside the green Start button:

| Profile | Command | Behavior |
| --- | --- | --- |
| Validate configuration | `doctor` | Checks resolved settings and local PDF processing. No Azure submission. This is the initial profile. |
| Preview batch | `plan` | Inspects the configured selection and estimates the maximum number of analysis passes. No Azure submission. |
| Extract configured batch | `extract` | Processes all discovered inputs by default, with duplicate skipping. Submits documents to Azure. |
| Extract all invoices | `extract --all` | Overrides an optional document-count limit. Submits documents to Azure. |

For extraction debugging, select **Extract configured batch**, set a breakpoint in `src/InvoiceExplorer.Cli/Program.cs` or `src/InvoiceExplorer.Core/BatchRunner.cs`, and press **F5**. No tier or API version selection is needed. `Extract all invoices` bypasses an optional document-count setting but retains any explicitly configured request budget.

For other commands, open the project's **Properties > Debug > Open debug launch profiles UI** and edit **Command line arguments** in a dedicated profile. For example, `extract --rerun` creates another execution for already processed inputs; `resume BATCH_ID` resumes the saved batch. Do not change arguments in shared profiles unintentionally when committing.

## Debug the web application

Set **InvoiceExplorer.Web** as the startup project, select **Web review**, and press **F5**. Visual Studio opens `http://localhost:5180`. Both projects use the same root configuration and SQLite database.

To debug both processes, start one project, then use the other project's **Debug > Start New Instance** command. The CLI uses its selected profile. Refresh the web page after the console persists results. Stop a previously running instance if it already owns port 5180; only one extraction process may hold the database runner lock.

Saving configuration does not reload a running pipeline or web process. Stop and restart the relevant debugging session after changing settings. On a fresh clone, the web UI remains empty until an extraction is recorded.

Visual Studio stores these launch profiles in each startup project's `Properties/launchSettings.json`. See [Microsoft's .NET debug configuration reference](https://learn.microsoft.com/en-us/visualstudio/debugger/project-settings-for-csharp-debug-configurations-dotnetcore) for the profile editor and debugger options.
