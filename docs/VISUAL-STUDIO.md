# Configure and debug in Visual Studio

Open `InvoiceExplorer.slnx` with Visual Studio and the .NET 10 SDK specified in `global.json`. Select the **Debug** build configuration. No terminal is required for the steps below.

## Shared configuration

The solution's **Configuration** folder contains `appsettings.json` and `appsettings.local.example.json`. The CLI and Web projects also expose these same files through linked **Configuration** items. They are references to files at the repository root, not separate copies.

1. Open `Configuration/appsettings.local.example.json`.
2. Use **File > Save As** to save a copy named `appsettings.local.json` in the repository root, beside `InvoiceExplorer.slnx`. If that local file already exists, open and edit it instead.
3. Enter the approved `DocumentIntelligence.Endpoint`, `ApiKey`, and actual `Tier`. Set `Batch.InputDirectory` to the invoice folder, using an absolute path or a path relative to the repository root.
4. Save the file. Reload the solution if the new local file does not appear under the CLI/Web project's **Configuration** folder.

The example uses S0, enables query/high-resolution fallback passes, and sets limits of 10,000 documents and 30,000 analysis submissions. Adjust these values to the intended experiment. `appsettings.json` provides settings omitted from the local file, including `QueryFields`, target fields, and validation policy. Query fields can also be overridden in the local file.

`appsettings.local.json` is ignored by Git and is not copied into build output. Do not put credentials in the tracked template, shared defaults, or `launchSettings.json`. The Web project's own `appsettings.json` controls ASP.NET logging; it is not the shared extraction configuration.

The applications locate the repository root from `invoice-workspace.marker`. The working directory does not need a machine-specific override. If an existing `INVOICE_ROOT` environment variable points to another checkout, remove it or correct it. Other supported environment variables still override JSON.

## Debug the console with F5

Right-click **InvoiceExplorer.Cli** and select **Set as Startup Project**. Select a profile beside the green Start button:

| Profile | Command | Behavior |
| --- | --- | --- |
| Validate configuration | `doctor` | Checks resolved settings and local PDF processing. No Azure submission. This is the initial profile. |
| Preview batch | `plan` | Inspects the configured selection and estimates the maximum number of analysis passes. No Azure submission. |
| Extract configured batch | `extract` | Processes the selection determined by `Batch.MaxDocuments` and the remaining settings. Submits documents to Azure. |
| Extract all invoices | `extract --all` | Processes all discovered inputs in S0, subject to the request budget and duplicate skipping. Submits documents to Azure. |

For extraction debugging, select **Extract configured batch**, set a breakpoint in `src/InvoiceExplorer.Cli/Program.cs` or `src/InvoiceExplorer.Core/BatchRunner.cs`, and press **F5**. There is no additional document limit imposed by this profile. `Extract all invoices` bypasses the document-count setting but retains the request budget. F0 still limits the application to one PDF and two pages.

For other commands, open the project's **Properties > Debug > Open debug launch profiles UI** and edit **Command line arguments** in a dedicated profile. For example, `extract --rerun` creates another execution for already processed inputs; `resume BATCH_ID` resumes the saved batch. Do not change arguments in shared profiles unintentionally when committing.

## Debug the web application

Set **InvoiceExplorer.Web** as the startup project, select **Web review**, and press **F5**. Visual Studio opens `http://localhost:5180`. Both projects use the same root configuration and SQLite database.

To debug both processes, start one project, then use the other project's **Debug > Start New Instance** command. The CLI uses its selected profile. Refresh the web page after the console persists results. Stop a previously running instance if it already owns port 5180; only one extraction process may hold the database runner lock.

Saving configuration does not reload a running pipeline or web process. Stop and restart the relevant debugging session after changing settings. On a fresh clone, the web UI remains empty until an extraction is recorded.

Visual Studio stores these launch profiles in each startup project's `Properties/launchSettings.json`. See [Microsoft's .NET debug configuration reference](https://learn.microsoft.com/en-us/visualstudio/debugger/project-settings-for-csharp-debug-configurations-dotnetcore) for the profile editor and debugger options.
