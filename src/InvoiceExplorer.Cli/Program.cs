using System.Text;
using System.Text.Json;
using InvoiceExplorer.Core;

ApplicationCulture.UseEnglish();
Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); Console.WriteLine("Stopping. Saved operations can be resumed without submitting them again."); };
try
{
    var command = args.FirstOrDefault() ?? "help";
    if (command is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
            Invoice Explorer | Azure Document Intelligence batch processing
              doctor                         Validate local configuration and native PDF rendering availability.
              plan [--limit N|--all] [--file PATH]  Inspect local PDFs and estimate the maximum number of passes. No Azure calls.
              extract [--limit N|--all] [--file PATH] [--rerun]
              resume BATCH_ID                Resume a saved queue and poll known Azure operations.
              batches                        List batch status and request counts.
              report [--batch BATCH_ID]       Export business CSV and field metrics to exports/.
              evaluate [--split Test|Calibration]  Compare the latest predictions with reviewed labels.
              labels-template PATH           Export a blank JSON labeling template for the latest real results.
              labels-import PATH             Import completed JSON labels (append-only audit history).
              list                           List raw invoice history (legacy view).
            Shared configuration: appsettings.json + appsettings.local.json + Section__Property environment variables.
            Set DocumentIntelligence.Endpoint and ApiKey. Defaults process invoices/ recursively with all pages.
            Confidence is not measured accuracy. A 0.98 threshold does not establish 98% correctness.
            """); return 0;
    }
    ValidateArguments(args);
    var settings = Settings.Load(Settings.FindRoot()); settings.Validate();
    var repo = new InvoiceRepository(settings.DatabasePath); repo.Initialize();
    string? Option(string name) { var i = Array.IndexOf(args, name); return i < 0 ? null : i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Missing value after {name}."); }
    if (command == "doctor")
    {
        Console.WriteLine($"Workspace: {settings.RootDirectory}\nEndpoint configured: {!string.IsNullOrWhiteSpace(settings.Endpoint)}\nAPI key configured: {!string.IsNullOrWhiteSpace(settings.ApiKey)}\nInput: {settings.InputDirectory}\nSQLite: {settings.DatabasePath}");
        var sample = BatchRunner.Discover(settings, 1).FirstOrDefault();
        if (sample is not null)
        {
            var bytes = await File.ReadAllBytesAsync(sample); var profile = PdfPreparation.Inspect(bytes);
            Console.WriteLine($"PDF reader: OK ({profile.Pages} pages, {profile.Kind})");
            // Rendering verifies the native library load without changing any input file or calling Azure.
            var rasterOptions = new PipelineOptions { RasterDpi = 150, MaxRasterPages = 2 };
            if (profile.Pages <= 2) { _ = await PdfPreparation.RasterizeAsync(bytes, rasterOptions, cancellation.Token); Console.WriteLine("Native PDF renderer: OK"); }
            else Console.WriteLine("Native renderer check skipped: the first input exceeds two pages.");
        }
        else Console.WriteLine("No local PDF available for native renderer verification.");
        Console.WriteLine("No network request was made. Credentials and remaining quota have not been verified."); return 0;
    }
    if (command == "list") { foreach (var i in repo.ListInvoices()) Console.WriteLine($"#{i.Id} {i.FileName} | {i.RunCount} raw runs | {i.LastStatus}"); return 0; }
    if (command == "batches") { foreach (var b in repo.ListBatches()) Console.WriteLine($"{b.Id} | {b.Status} | {b.Total} invoices | Ready {b.Completed} / Review {b.Review} / Failed {b.Failed} / Pending {b.Pending} | {b.Requests} analyze submissions | Skipped {repo.SkipCount(b.Id)}"); return 0; }
    Directory.CreateDirectory(settings.ExportDirectory);
    if (command == "report")
    {
        var batch = Option("--batch"); if (batch is not null && !repo.ListBatches().Any(b => b.Id == batch)) throw new ArgumentException("Batch not found.");
        var path = Path.Combine(settings.ExportDirectory, batch is null ? "business-latest.csv" : $"business-{batch}.csv");
        await File.WriteAllTextAsync(path, BusinessExport.Csv(repo, batch), new UTF8Encoding(true));
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".wide.csv"), BusinessExport.WideCsv(repo, batch), new UTF8Encoding(true));
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".metrics.json"), JsonSerializer.Serialize(repo.FieldMetrics(batch, false), Settings.JsonOptions));
        Console.WriteLine($"Report: {path}"); return 0;
    }
    if (command == "evaluate")
    {
        var split = Option("--split") ?? "Test"; if (split is not ("Test" or "Calibration")) throw new ArgumentException("Split must be Test or Calibration.");
        var report = Evaluation.Run(repo, split); var prefix = Path.Combine(settings.ExportDirectory, $"evaluation-{split.ToLowerInvariant()}");
        await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(report, Settings.JsonOptions));
        await File.WriteAllTextAsync(prefix + ".csv", BusinessExport.EvaluationCsv(report), new UTF8Encoding(true));
        Console.WriteLine($"{report.LabeledInvoices} labeled invoices / {report.LabeledFields} labeled fields.\nValue accuracy: {report.Accuracy?.ToString("P2") ?? "Not measured"}; original baseline: {report.BaselineAccuracy?.ToString("P2") ?? "Not measured"}.\n95% Wilson lower bound: {report.WilsonLower95?.ToString("P2") ?? "Not measured"}.\nReport: {prefix}.json"); return 0;
    }
    if (command == "labels-template")
    {
        var path = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Specify an output JSON path.");
        var labels = repo.ListExecutions().SelectMany(e => settings.Pipeline.TargetFields.Select(field => new { e.Hash, Field = field, Present = (bool?)null, ExpectedValue = (string?)null, Reviewer = "", Split = "Test", Notes = "Review the original source. Do not copy predictions without checking." }));
        if (File.Exists(path)) throw new IOException("Label template already exists. Choose another path to protect completed annotations.");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(labels, Settings.JsonOptions)); Console.WriteLine($"Blank label template: {path}"); return 0;
    }
    if (command == "labels-import")
    {
        var path = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Specify a reviewed JSON label file.");
        var labels = JsonSerializer.Deserialize<List<LabelInput>>(await File.ReadAllTextAsync(path), Settings.JsonOptions) ?? [];
        var hashes = repo.ListExecutions().Select(e => e.Hash).ToHashSet();
        foreach (var l in labels)
            if (!hashes.Contains(l.Hash) || !settings.Pipeline.TargetFields.Contains(l.Field) || l.Present is null || l.Present == true && string.IsNullOrWhiteSpace(l.ExpectedValue) || string.IsNullOrWhiteSpace(l.Reviewer) || l.Split is not ("Test" or "Calibration")) throw new InvalidDataException("Every label needs a known hash/field, Present, reviewer, valid split, and expected value when present.");
        if (labels.GroupBy(l => (l.Hash, l.Field)).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate hash/field labels in import.");
        repo.ImportLabels(labels.Select(l => new LabelEntry(l.Hash, l.Field, l.ExpectedValue, l.Present!.Value, l.Reviewer, l.Split, l.Notes)));
        Console.WriteLine($"Imported {labels.Count} reviewed labels."); return 0;
    }
    if (command is not ("plan" or "extract" or "resume")) throw new ArgumentException($"Unknown command: {command}. Use --help.");
    int? limit = null;
    if (Option("--limit") is { } value) limit = int.TryParse(value, out var number) && number > 0 ? number : throw new ArgumentException("--limit must be positive.");
    if (args.Contains("--all")) limit = int.MaxValue;
    var files = command == "resume" ? [] : BatchRunner.Discover(settings, limit, Option("--file"));
    if (command == "plan")
    {
        var pages = 0; var invalid = 0; var digital = 0; var scanned = 0;
        foreach (var path in files)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            try { if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new InvalidDataException("Input exceeds the application memory limit of 64 MiB."); var profile = PdfPreparation.Inspect(await File.ReadAllBytesAsync(path, cancellation.Token)); pages += profile.Pages; if (profile.Kind == "Digital") digital++; else scanned++; }
            catch (Exception ex) when (ex is not OperationCanceledException) { invalid++; Console.WriteLine($"Input issue: {Path.GetFileName(path)}: {ex.GetType().Name}"); }
        }
        var budget = settings.Batch.MaxAnalyzeRequests == int.MaxValue ? "not configured" : settings.Batch.MaxAnalyzeRequests.ToString();
        Console.WriteLine($"Plan: {files.Length} PDFs; {pages} source pages; {digital} digital, {scanned} scanned/mixed, {invalid} unreadable.\nUpper bound before duplicate skipping: {(long)(files.Length - invalid) * settings.Pipeline.MaxPasses} analyze operations / {(long)pages * settings.Pipeline.MaxPasses} analyzed pages.\nAnalyze-request budget: {budget}.\nThis is a volume bound, not a price quote. No files were submitted to Azure."); return 0;
    }
    settings.ValidateAzure();
    // A process lock prevents two consoles from independently claiming the same durable work queue.
    using var lease = new FileStream(settings.DatabasePath + ".runner.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    string? batchId = command == "resume" ? args.ElementAtOrDefault(1) ?? throw new ArgumentException("Specify a batch ID.") : null;
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var client = new AzureInvoiceClient(http, settings, new RequestPacer(settings.Batch.RequestsPerSecond), () => repo.ReserveRequest(batchId!, settings.Batch.MaxAnalyzeRequests));
    var runner = new BatchRunner(settings, repo, client, Console.WriteLine);
    batchId ??= await runner.CreateAsync(files, args.Contains("--rerun"), cancellation.Token);
    Console.WriteLine($"Batch: {batchId}\nResume command: resume {batchId}");
    await runner.RunAsync(batchId, cancellation.Token);
    var final = repo.ListBatches().Single(b => b.Id == batchId);
    await File.WriteAllTextAsync(Path.Combine(settings.ExportDirectory, $"business-{batchId}.csv"), BusinessExport.Csv(repo, batchId), new UTF8Encoding(true));
    Console.WriteLine($"{final.Status}: {final.Completed} ready, {final.Review} review, {final.Failed} failed, {final.Pending} pending, {repo.SkipCount(batchId)} skipped.");
    return cancellation.IsCancellationRequested ? 130 : final.Pending > 0 || final.Failed > 0 ? 1 : 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled. Saved results are retained."); return 130; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

// Reject typos and conflicting switches before an accidental large submission can occur.
static void ValidateArguments(string[] arguments)
{
    var command = arguments[0];
    var valued = command switch { "extract" or "plan" => new[] { "--limit", "--file" }, "report" => ["--batch"], "evaluate" => ["--split"], _ => [] };
    var flags = command switch { "extract" => new[] { "--all", "--rerun" }, "plan" => ["--all"], _ => [] };
    var positional = command is "resume" or "labels-template" or "labels-import";
    var seen = new HashSet<string>();
    for (var i = 1; i < arguments.Length; i++)
    {
        var arg = arguments[i];
        if (positional && i == 1 && !arg.StartsWith("--")) continue;
        if (!seen.Add(arg)) throw new ArgumentException($"Duplicate argument: {arg}");
        if (valued.Contains(arg)) { if (++i >= arguments.Length || arguments[i].StartsWith("--")) throw new ArgumentException($"Missing value after {arg}"); }
        else if (!flags.Contains(arg)) throw new ArgumentException($"Unexpected argument: {arg}. Use --help.");
    }
    if (seen.Contains("--all") && seen.Contains("--limit") || seen.Contains("--file") && (seen.Contains("--all") || seen.Contains("--limit"))) throw new ArgumentException("Use only one of --all, --limit, or --file.");
}

internal sealed record LabelInput(string Hash, string Field, bool? Present, string? ExpectedValue, string Reviewer, string Split, string Notes);
