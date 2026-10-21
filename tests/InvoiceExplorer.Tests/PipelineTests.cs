using System.Text.Json;
using InvoiceExplorer.Core;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Content;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace InvoiceExplorer.Tests;

// Every fixture is fictional and isolated. These tests never contact Azure or read client invoices.
public sealed class Workspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "invoice-pipeline-test-" + Guid.NewGuid().ToString("N"));
    public Settings Settings { get; }
    public InvoiceRepository Repo { get; }
    public Workspace()
    {
        Directory.CreateDirectory(Root);
        Settings = new() { RootDirectory = Root, InputDirectory = Path.Combine(Root, "invoices"), DatabasePath = Path.Combine(Root, "data", "test.db"), ExportDirectory = Path.Combine(Root, "exports") };
        Settings.Storage.ArtifactDirectory = Path.Combine(Root, "artifacts");
        Settings.Batch.MaxDocuments = 100; Settings.Pipeline.TargetFields = ["InvoiceId"]; Settings.Pipeline.RequiredFields = ["InvoiceId"];
        Settings.DocumentIntelligence.QueryFields = ["InvoiceId"]; Settings.Pipeline.HighResolutionFallback = false;
        Directory.CreateDirectory(Settings.InputDirectory); Repo = new(Settings.DatabasePath); Repo.Initialize();
    }
    public string Pdf(string name = "invoice.pdf", int pages = 1)
    {
        var builder = new PdfDocumentBuilder(); var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 0; i < pages; i++)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText($"FICTIONAL INVOICE INV-001 / PAGE {i + 1}", 16, new PdfPoint(50, 700), font);
            page.AddText("Invoice total: USD 1100.00 / Tax: 100.00", 12, new PdfPoint(50, 650), font);
        }
        var path = Path.Combine(Settings.InputDirectory, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, builder.Build()); return path;
    }
    public (string Batch, long Id) Execution(string hash = "hash")
    { var batch = Repo.CreateBatch(Settings); return (batch, Repo.AddExecution(batch, hash, "invoice.pdf", Settings.Fingerprint())); }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    public static string Response(string value = "INV-001", double score = .99, int pages = 1) => JsonSerializer.Serialize(new { status = "succeeded", analyzeResult = new { pages = Enumerable.Range(1, pages).Select(n => new { pageNumber = n }), documents = new[] { new { fields = new { InvoiceId = new { type = "string", valueString = value, confidence = score } } } } } });
}

public sealed class FakeAnalyzer : IInvoiceAnalyzer
{
    public int Posts, Polls;
    public List<AnalysisRequest> Requests { get; } = [];
    public Func<AnalysisRequest, Action<string>, string> Analyze { get; set; } = (_, callback) => { callback("https://example.com/operation/1"); return Workspace.Response(); };
    public Func<string> Poll { get; set; } = () => Workspace.Response();
    public Task<string> AnalyzeAsync(byte[] content, string extension, AnalysisRequest request, Action<string> onOperation, CancellationToken token)
    { Posts++; Requests.Add(request); return Task.FromResult(Analyze(request, onOperation)); }
    public Task<string> PollAsync(string operationUrl, CancellationToken token) { Polls++; return Task.FromResult(Poll()); }
}

public class BatchPipelineTests
{
    [Fact]
    public async Task ReadyOriginalStopsEarlyAndPreservesEvidence()
    {
        using var w = new Workspace(); var file = w.Pdf(); var original = File.ReadAllBytes(file); var api = new FakeAnalyzer();
        var runner = new BatchRunner(w.Settings, w.Repo, api); var batch = await runner.CreateAsync([file], false, default); await runner.RunAsync(batch, default);
        var e = w.Repo.GetExecution(Assert.Single(w.Repo.ListExecutions()).Id)!;
        Assert.Equal("Ready", e.Status); Assert.Equal(1, api.Posts); Assert.Equal(original, File.ReadAllBytes(e.ArtifactPath));
        Assert.Single(w.Repo.GetAttempts(e.Id)); Assert.Equal(1, e.Decision!.Coverage); Assert.Contains("INV-001", BusinessExport.Csv(w.Repo));
    }
    [Fact]
    public async Task TargetedFallbackRetainsScoresAndBothRawRuns()
    {
        using var w = new Workspace(); var file = w.Pdf();
        var api = new FakeAnalyzer { Analyze = (r, cb) => { cb("https://example.com/operation/1"); return Workspace.Response(score: r.Strategy == "Original" ? .74 : .99); } };
        var runner = new BatchRunner(w.Settings, w.Repo, api); var batch = await runner.CreateAsync([file], false, default); await runner.RunAsync(batch, default);
        var e = w.Repo.GetExecution(w.Repo.ListExecutions().Single().Id)!;
        Assert.Equal("Ready", e.Status); Assert.Equal(2, api.Posts); Assert.Equal("prebuilt-layout", api.Requests[1].ModelId);
        Assert.Equal(new[] { "InvoiceId" }, api.Requests[1].QueryFields); Assert.Equal(.99, e.Decision!.MeanConfidence);
        Assert.Equal(new[] { .74, .99 }, e.Decision.Fields.Single().Candidates.Select(c => c.Confidence!.Value));
    }
    [Fact]
    public async Task DefaultFallbacksRunWithoutTierOrFeatureGate()
    {
        using var w = new Workspace(); var file = w.Pdf();
        w.Settings.Pipeline.HighResolutionFallback = new PipelineOptions().HighResolutionFallback;
        var api = new FakeAnalyzer { Analyze = (r, cb) => { cb("https://example.com/operation/1"); return Workspace.Response(score: r.Strategy == "HighResolution" ? .99 : .74); } };
        var runner = new BatchRunner(w.Settings, w.Repo, api); var batch = await runner.CreateAsync([file], false, default); await runner.RunAsync(batch, default);
        var e = w.Repo.GetExecution(w.Repo.ListExecutions().Single().Id)!;
        Assert.Equal("Ready", e.Status);
        Assert.Equal(new[] { "Original", "QueryFields", "HighResolution" }, api.Requests.Select(r => r.Strategy));
        Assert.Contains("ocrHighResolution", api.Requests[2].Features);
        Assert.Equal(3, w.Repo.GetAttempts(e.Id).Count);
        Assert.Equal(new[] { .74, .74, .99 }, e.Decision!.Fields.Single().Candidates.Select(c => c.Confidence!.Value));
    }
    [Fact]
    public async Task FallbackConflictCannotAutoApprove()
    {
        using var w = new Workspace(); var file = w.Pdf();
        var api = new FakeAnalyzer { Analyze = (r, cb) => { cb("https://example.com/operation/1"); return r.Strategy == "Original" ? Workspace.Response("A", .74) : Workspace.Response("B", .999); } };
        var runner = new BatchRunner(w.Settings, w.Repo, api); var batch = await runner.CreateAsync([file], false, default); await runner.RunAsync(batch, default);
        var e = w.Repo.GetExecution(w.Repo.ListExecutions().Single().Id)!;
        Assert.Equal("Review", e.Status); Assert.Equal("Conflict", e.Decision!.Fields.Single().State);
    }
    [Fact]
    public async Task CancellationAfterSubmissionResumesWithGetOnly()
    {
        using var w = new Workspace(); var api = new FakeAnalyzer { Analyze = (_, cb) => { cb("https://example.com/operation/1"); throw new OperationCanceledException(); } };
        var runner = new BatchRunner(w.Settings, w.Repo, api); var batch = await runner.CreateAsync([w.Pdf()], false, default); await runner.RunAsync(batch, default);
        Assert.Equal("Interrupted", w.Repo.ListBatches().Single().Status);
        await runner.RunAsync(batch, default);
        Assert.Equal(1, api.Posts); Assert.Equal(1, api.Polls); Assert.Equal("Ready", w.Repo.ListExecutions().Single().Status);
    }
    [Fact]
    public async Task UnconfirmedSubmissionNeverPostsAgainOnResume()
    {
        using var w = new Workspace(); var api = new FakeAnalyzer { Analyze = (_, _) => throw new OperationCanceledException() };
        var runner = new BatchRunner(w.Settings, w.Repo, api); var batch = await runner.CreateAsync([w.Pdf()], false, default);
        await runner.RunAsync(batch, default); await runner.RunAsync(batch, default); await runner.RunAsync(batch, default);
        Assert.Equal(1, api.Posts); Assert.Equal("Ambiguous", w.Repo.ListExecutions().Single().Status);
    }
    [Fact]
    public async Task TransportFailureWhilePollingRemainsResumable()
    {
        using var w = new Workspace(); var api = new FakeAnalyzer { Analyze = (_, cb) => { cb("https://example.com/operation/1"); throw new HttpRequestException("Disconnected"); } };
        var runner = new BatchRunner(w.Settings, w.Repo, api); var batch = await runner.CreateAsync([w.Pdf()], false, default);
        await runner.RunAsync(batch, default); await runner.RunAsync(batch, default);
        Assert.Equal(1, api.Posts); Assert.Equal(1, api.Polls); Assert.Equal("Ready", w.Repo.ListExecutions().Single().Status);
    }
    [Fact]
    public async Task BudgetBeforeSubmissionIsSafelyResumable()
    {
        using var w = new Workspace(); var api = new FakeAnalyzer { Analyze = (_, _) => throw new RequestBudgetException() };
        var runner = new BatchRunner(w.Settings, w.Repo, api); var batch = await runner.CreateAsync([w.Pdf()], false, default);
        await runner.RunAsync(batch, default); Assert.Equal("BudgetExceeded", w.Repo.ListExecutions().Single().Status);
        api.Analyze = (_, cb) => { cb("https://example.com/operation/1"); return Workspace.Response(); };
        w.Settings.Batch.MaxAnalyzeRequests = 200; await runner.RunAsync(batch, default); Assert.Equal("Ready", w.Repo.ListExecutions().Single().Status);
    }
    [Fact]
    public async Task ContentDeduplicationSupportsExplicitRerunAndChronology()
    {
        using var w = new Workspace(); var one = w.Pdf(); var two = Path.Combine(w.Settings.InputDirectory, "renamed.pdf"); File.Copy(one, two);
        var api = new FakeAnalyzer(); var runner = new BatchRunner(w.Settings, w.Repo, api);
        var first = await runner.CreateAsync([one, two], false, default); await runner.RunAsync(first, default); Assert.Equal(1, w.Repo.SkipCount(first));
        var second = await runner.CreateAsync([two], false, default); Assert.Equal(1, w.Repo.SkipCount(second));
        var third = await runner.CreateAsync([two], true, default); await runner.RunAsync(third, default);
        Assert.Equal(2, api.Posts); Assert.Equal(2, w.Repo.ListExecutions(latestOnly: false).Count); Assert.Single(w.Repo.ListExecutions());
    }
    [Fact]
    public async Task ChangedPlannedSourceFailsWithoutAzureCall()
    {
        using var w = new Workspace(); var file = w.Pdf(); var api = new FakeAnalyzer(); var runner = new BatchRunner(w.Settings, w.Repo, api);
        var batch = await runner.CreateAsync([file], false, default); File.AppendAllText(file, "changed"); await runner.RunAsync(batch, default);
        Assert.Equal(0, api.Posts); Assert.Equal("Failed", w.Repo.ListExecutions().Single().Status);
    }
    [Fact]
    public async Task ChangedPolicyBlocksResume()
    {
        using var w = new Workspace(); var runner = new BatchRunner(w.Settings, w.Repo, new FakeAnalyzer()); var batch = await runner.CreateAsync([w.Pdf()], false, default);
        w.Settings.Pipeline.AcceptanceConfidence = .9;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(batch, default));
    }
    [Fact]
    public void DefaultDiscoveryIncludesAllInputsAndRespectsExplicitLimits()
    {
        using var w = new Workspace();
        for (var i = 0; i < 12; i++) w.Pdf($"subfolder/invoice-{i}.pdf");
        var settings = Settings.Load(w.Root);
        Assert.Equal(12, BatchRunner.Discover(settings).Length);
        Assert.Single(BatchRunner.Discover(settings, 1));
        settings.Batch.MaxDocuments = 3;
        Assert.Equal(3, BatchRunner.Discover(settings).Length);
        Assert.Equal(12, BatchRunner.Discover(settings, int.MaxValue).Length);
    }
    [Theory]
    [InlineData(3, "Ready")]
    [InlineData(2, "Review")]
    public async Task RequestsAllPagesAndDetectsServiceTruncation(int returnedPages, string expectedStatus)
    {
        using var w = new Workspace(); var file = w.Pdf(pages: 3);
        var api = new FakeAnalyzer { Analyze = (_, cb) => { cb("https://example.com/operation/1"); return Workspace.Response(pages: returnedPages); } };
        var runner = new BatchRunner(w.Settings, w.Repo, api); var batch = await runner.CreateAsync([file], false, default); await runner.RunAsync(batch, default);
        var e = w.Repo.GetExecution(w.Repo.ListExecutions().Single().Id)!;
        Assert.Equal(expectedStatus, e.Status);
        Assert.Equal(returnedPages < 3, e.Decision!.Issues.Any(i => i.Code == "PartialDocument"));
        Assert.Equal("", api.Requests.Single().Pages);
    }
    [Fact]
    public async Task ExplicitPageSelectionRemainsMarkedIncomplete()
    {
        using var w = new Workspace(); var file = w.Pdf(pages: 3); w.Settings.DocumentIntelligence.Pages = "1-2";
        var api = new FakeAnalyzer { Analyze = (_, cb) => { cb("https://example.com/operation/1"); return Workspace.Response(pages: 2); } };
        var runner = new BatchRunner(w.Settings, w.Repo, api); var batch = await runner.CreateAsync([file], false, default); await runner.RunAsync(batch, default);
        var e = w.Repo.GetExecution(w.Repo.ListExecutions().Single().Id)!;
        Assert.Equal("Review", e.Status); Assert.Single(e.Decision!.Issues, i => i.Code == "PartialDocument");
        Assert.Equal("1-2", api.Requests.Single().Pages);
    }
    [Fact]
    public async Task NativeRasterPreservesPagesGeometryAndSourceBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var w = new Workspace(); var path = w.Pdf(pages: 2); var bytes = File.ReadAllBytes(path); var hash = PdfPreparation.Hash(bytes);
        var raster = await PdfPreparation.RasterizeAsync(bytes, new() { RasterDpi = 150, RasterGrayscale = true }, default);
        var profile = PdfPreparation.Inspect(raster); Assert.Equal(2, profile.Pages); Assert.Equal("Scanned", profile.Kind); Assert.Equal(2, profile.ImageCount);
        using var original = UglyToad.PdfPig.PdfDocument.Open(bytes); using var variant = UglyToad.PdfPig.PdfDocument.Open(raster);
        Assert.Equal(original.GetPage(1).Width, variant.GetPage(1).Width, 1); Assert.Equal(original.GetPage(1).Height, variant.GetPage(1).Height, 1);
        Assert.Equal(hash, PdfPreparation.Hash(File.ReadAllBytes(path)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PdfPreparation.RasterizeAsync(bytes, new() { MaxRasterPages = 1 }, default));
        // Optional local visual QA artifacts; disabled during normal CI and never contain real documents.
        if (Environment.GetEnvironmentVariable("INVOICE_TEST_VISUAL_OUTPUT") is { Length: > 0 } output)
        { Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(output, "fictional-original.pdf"), bytes); File.WriteAllBytes(Path.Combine(output, "fictional-raster.pdf"), raster); }
    }
}
