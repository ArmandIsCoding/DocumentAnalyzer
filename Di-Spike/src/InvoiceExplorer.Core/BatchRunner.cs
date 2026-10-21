using System.Net;
using System.Text;
using System.Text.Json;

namespace InvoiceExplorer.Core;

/// <summary>Durable work queue. Each source, request, response, and decision has an independent audit record.</summary>
public sealed class BatchRunner(Settings settings, InvoiceRepository repository, IInvoiceAnalyzer analyzer, Action<string>? log = null)
{
    public static string[] Discover(Settings settings, int? limit = null, string? file = null)
    {
        if (file is not null) return [Path.GetFullPath(file)];
        if (!Directory.Exists(settings.InputDirectory)) throw new DirectoryNotFoundException("The configured input directory does not exist.");
        var options = new EnumerationOptions { RecurseSubdirectories = settings.Batch.Recursive, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive };
        return Directory.EnumerateFiles(settings.InputDirectory, settings.FilePattern, options)
            .Where(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).Take(settings.IsFree ? 1 : limit ?? settings.MaxDocuments).ToArray();
    }

    public async Task<string> CreateAsync(string[] files, bool rerun, CancellationToken token)
    {
        if (files.Length == 0) throw new InvalidOperationException("No PDF files match the configured input pattern.");
        var batch = repository.CreateBatch(settings); var seen = new HashSet<string>();
        try
        {
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var bytes = await ReadSourceAsync(path, token); var hash = PdfPreparation.Hash(bytes);
                    if (!seen.Add(hash)) { repository.AddSkip(batch, path, "Duplicate content in this batch."); continue; }
                    if (!rerun && settings.Batch.SkipPreviouslyCompleted && repository.WasCompleted(hash, settings.Fingerprint()))
                    { repository.AddSkip(batch, path, "Already completed under this pipeline configuration. Use --rerun for a new execution."); continue; }
                    var relative = Path.GetRelativePath(settings.InputDirectory, path);
                    // Store an absolute path only when --file deliberately selects a source outside the input directory.
                    repository.AddExecution(batch, hash, relative.StartsWith("..") ? Path.GetFullPath(path) : relative, settings.Fingerprint());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    var id = repository.AddExecution(batch, "unreadable-" + Guid.NewGuid().ToString("N"), path, settings.Fingerprint());
                    repository.SetExecutionStatus(id, "Failed", ex.Message);
                }
            }
            repository.SetBatchStatus(batch, "Planned");
            return batch;
        }
        catch { repository.SetBatchStatus(batch, "PlanningInterrupted"); throw; }
    }
    public async Task RunAsync(string batch, CancellationToken token)
    {
        var summary = repository.ListBatches().SingleOrDefault(b => b.Id == batch) ?? throw new ArgumentException("Batch not found.");
        if (summary.Fingerprint != settings.Fingerprint()) throw new InvalidOperationException("The extraction configuration changed. Restore it to resume, or start a new batch with --rerun.");
        if (summary.IsDemo) throw new InvalidOperationException("Demo batches cannot be submitted to Azure.");
        if (summary.Status == "PlanningInterrupted") throw new InvalidOperationException("Planning was interrupted; no requests were submitted. Start a new batch to build a complete manifest.");
        repository.SetBatchStatus(batch, "Running");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var executions = repository.ListExecutions(batch, false).Where(e => !PipelineStatus.IsTerminal(e.Status)).ToArray();
        try
        {
            await Parallel.ForEachAsync(executions, new ParallelOptions { MaxDegreeOfParallelism = settings.IsFree ? 1 : settings.Batch.Concurrency, CancellationToken = stop.Token }, async (row, ct) =>
            {
                try { await ProcessAsync(repository.GetExecution(row.Id)!, ct); }
                catch (OperationCanceledException) { repository.SetExecutionStatus(row.Id, "Interrupted", "Processing interrupted. Resume this batch to poll any saved operation."); throw; }
                catch (RequestBudgetException ex) { repository.SetExecutionStatus(row.Id, "BudgetExceeded", ex.Message); stop.Cancel(); }
                catch (AzureRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                { repository.SetExecutionStatus(row.Id, "Interrupted", ex.Message); stop.Cancel(); }
                catch (AmbiguousSubmissionException ex) { repository.SetExecutionStatus(row.Id, "Ambiguous", ex.Message); }
                catch (Exception ex)
                {
                    repository.SetExecutionStatus(row.Id, "Failed", ex.Message); log?.Invoke($"Execution #{row.Id}: failed - {ex.Message}");
                    if (!settings.Batch.ContinueOnFileError) stop.Cancel();
                }
            });
        }
        catch (OperationCanceledException) { /* The durable queue, not a process exit, is the source of truth. */ }
        var current = repository.ListBatches().Single(b => b.Id == batch);
        repository.SetBatchStatus(batch, current.Pending > 0 ? "Interrupted" : current.Failed > 0 ? "CompletedWithErrors" : "Completed");
    }

    public async Task ProcessAsync(PipelineExecution execution, CancellationToken token)
    {
        repository.SetExecutionStatus(execution.Id, "Processing");
        var path = Path.IsPathRooted(execution.SourcePath) ? execution.SourcePath : Path.Combine(settings.InputDirectory, execution.SourcePath);
        var bytes = await ReadSourceAsync(File.Exists(execution.ArtifactPath) ? execution.ArtifactPath : path, token);
        if (PdfPreparation.Hash(bytes) != execution.Hash) throw new InvalidDataException("The source changed after planning. Start a new batch; previous results remain attached to the original hash.");
        var profile = PdfPreparation.Inspect(bytes);
        var artifact = await PdfPreparation.SnapshotAsync(bytes, execution.Hash, settings, token);
        repository.SetExecutionProfile(execution.Id, profile, artifact);
        var candidates = new List<FieldCandidate>(); var issues = new List<QualityIssue>();
        if (settings.IsFree && profile.Pages > 2 || settings.DocumentIntelligence.Pages.Length > 0)
            issues.Add(new("PartialDocument", "Error", "Only the configured page range was analyzed. Completeness of the invoice is not established."));
        foreach (var warning in profile.Warnings) issues.Add(new("InputQuality", "Warning", warning));
        double? ocr = null; var count = 0;
        QualityDecision Decision() => QualityEngine.Decide(candidates, settings.Pipeline, issues);
        async Task Pass(AnalysisRequest request, byte[] input)
        {
            if (++count > settings.Pipeline.MaxPasses) return;
            token.ThrowIfCancellationRequested();
            var existing = repository.GetAttempts(execution.Id).SingleOrDefault(a => a.Strategy == request.Strategy);
            var id = existing?.RunId ?? repository.StartAttempt(execution, request, settings);
            if (existing is not null) request = JsonSerializer.Deserialize<AnalysisRequest>(existing.RequestJson)!;
            string json;
            try
            {
                if (existing?.Status == "Succeeded") json = repository.GetRun(id)!.RawJson;
                else if (existing?.Status == "Ambiguous") throw new AmbiguousSubmissionException();
                else if (existing?.Status == "Failed" && request.Strategy == "Original") throw new InvalidOperationException(existing.Error ?? "The original extraction failed.");
                else if (existing?.Status == "Failed")
                { issues.Add(new("PassFailed", "Warning", $"{request.Strategy}: {existing.Error}")); return; }
                else if (existing?.OperationUrl is { Length: > 0 } operation)
                { json = await analyzer.PollAsync(operation, token); repository.Complete(id, execution.SourcePath, json, ExtractionParser.Parse(json)); }
                else
                {
                    if (existing is not null && existing.Status != "Prepared") throw new AmbiguousSubmissionException();
                    repository.MarkSubmitting(id);
                    json = await analyzer.AnalyzeAsync(input, ".pdf", request, url => repository.SetOperation(id, url), token);
                    repository.Complete(id, execution.SourcePath, json, ExtractionParser.Parse(json));
                }
            }
            catch (RequestBudgetException) { repository.Fail(id, "Analyze request budget reached before submission.", "Prepared"); throw; }
            catch (OperationCanceledException)
            {
                var attempt = repository.GetAttempts(execution.Id).Single(a => a.RunId == id);
                repository.Fail(id, "Request interrupted; resume the saved operation if available.", attempt.OperationUrl is null ? "Ambiguous" : "TimedOut");
                throw;
            }
            catch (AzureRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            { repository.Fail(id, ex.Message, "Prepared"); throw; }
            catch (HttpRequestException ex) when (repository.GetAttempts(execution.Id).Single(a => a.RunId == id).OperationUrl is not null && (ex is not AzureRequestException || (int)ex.StatusCode! >= 500))
            {
                repository.Fail(id, "Polling was interrupted. The saved operation can be resumed.", "TimedOut");
                throw new OperationCanceledException("Polling interrupted; resume the saved operation.", ex);
            }
            catch (Exception ex)
            {
                var attempt = repository.GetAttempts(execution.Id).Single(a => a.RunId == id);
                var ambiguous = attempt.OperationUrl is null && (ex is HttpRequestException && ex is not AzureRequestException || ex is AzureRequestException azure && (int)azure.StatusCode! >= 500 || ex is InvalidDataException || ex is AmbiguousSubmissionException);
                repository.Fail(id, ex.Message, ambiguous ? "Ambiguous" : "Failed");
                if (ambiguous) throw new AmbiguousSubmissionException();
                if (request.Strategy == "Original") throw;
                issues.Add(new("PassFailed", "Warning", $"{request.Strategy}: {ex.Message}")); return;
            }
            candidates.AddRange(QualityEngine.ReadCandidates(json, id, request.Strategy, settings.Pipeline.TargetFields));
            if (QualityEngine.DocumentCount(json) != 1) issues.Add(new("DocumentCount", "Error", "Expected exactly one invoice per PDF. No automatic approval is possible when zero or multiple documents are detected."));
            if (request.Strategy == "Original") ocr = QualityEngine.OcrConfidence(json);
            var csv = repository.GetRun(id)!.Csv; Directory.CreateDirectory(settings.ExportDirectory);
            await File.WriteAllTextAsync(Path.Combine(settings.ExportDirectory, $"run-{id:D6}.csv"), csv, new UTF8Encoding(true), token);
            log?.Invoke($"Execution #{execution.Id} / {request.Strategy}: {Decision().Status}, {Decision().Coverage:P0} target coverage.");
        }
        AnalysisRequest Request(string strategy, string model, string[] features, string[]? fields = null) => new(strategy, model, features, fields ?? [], settings.IsFree && profile.Pages == 1 ? "1" : settings.PageRange, settings.DocumentIntelligence.Locale);
        await Pass(Request("Original", settings.ModelId, ["keyValuePairs"]), bytes);
        var paid = settings.Pipeline.AllowPaidFeatures && !settings.IsFree;
        var queryFields = settings.DocumentIntelligence.QueryFields.Where(name => settings.Pipeline.QueryMode == "Always" || Decision().Fields.Any(f => f.Name == name && f.State != "Accepted")).ToArray();
        if (paid && settings.Pipeline.QueryMode != "Off" && queryFields.Length > 0)
            await Pass(Request("QueryFields", "prebuilt-layout", ["queryFields"], queryFields), bytes);
        if (Decision().Status == "Review" && paid && settings.Pipeline.HighResolutionFallback)
            await Pass(Request("HighResolution", settings.ModelId, ["keyValuePairs", "ocrHighResolution"]), bytes);
        if (Decision().Status == "Review" && settings.Pipeline.RasterFallback && count < settings.Pipeline.MaxPasses && (profile.Kind != "Digital" || ocr is null || ocr < settings.Pipeline.MinimumOcrConfidence))
        {
            try
            {
                var variant = await PdfPreparation.RasterizeAsync(bytes, settings.Pipeline, token);
                if (variant.Length > (settings.IsFree ? 4L : 500L) * 1024 * 1024) throw new InvalidOperationException("Rasterized PDF exceeds the resource file-size limit.");
                var variantDirectory = Path.Combine(settings.Storage.ArtifactDirectory, "variants"); Directory.CreateDirectory(variantDirectory);
                await File.WriteAllBytesAsync(Path.Combine(variantDirectory, $"execution-{execution.Id}-raster.pdf"), variant, token);
                await Pass(Request("Raster", settings.ModelId, ["keyValuePairs"]), variant);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or RequestBudgetException or AmbiguousSubmissionException or AzureRequestException))
            { issues.Add(new("RasterUnavailable", "Warning", ex.Message)); }
        }
        if (Decision().Status == "Review" && settings.DocumentIntelligence.CustomModelId.Length > 0)
            await Pass(Request("CustomModel", settings.DocumentIntelligence.CustomModelId, []), bytes);
        if (!paid && settings.Pipeline.QueryMode != "Off") issues.Add(new("PaidFeaturesDisabled", "Info", "Query fields and high-resolution fallback were skipped. Paid features require S0 and AllowPaidFeatures=true."));
        if (ocr is not null && ocr < settings.Pipeline.MinimumOcrConfidence) issues.Add(new("LowOcr", "Warning", $"Original OCR mean confidence is {ocr:P1}. Inspect image quality before relying on extracted values."));
        repository.SaveDecision(execution.Id, Decision());
    }
    private async Task<byte[]> ReadSourceAsync(string path, CancellationToken token)
    {
        var size = new FileInfo(path).Length;
        // Bound concurrent memory use in the portable spike. Azure's service maximum is higher on S0.
        if (size > (settings.IsFree ? 4L : 64L) * 1024 * 1024) throw new InvalidDataException("PDF exceeds the application size limit (F0: 4 MiB; S0: 64 MiB). Split oversized input before processing.");
        return await File.ReadAllBytesAsync(path, token);
    }
}
public sealed class AmbiguousSubmissionException() : Exception("An analyze submission has no confirmed outcome and no saved operation URL. It will not be resubmitted automatically. Inspect Azure usage and explicitly start a new batch with --rerun if appropriate.");
