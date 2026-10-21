using InvoiceExplorer.Core;

namespace InvoiceExplorer.Tests;

public class RepositoryAndConfigTests
{
    [Fact]
    public void LegacySyntheticRecordsAreExcludedFromOperationalListsAndEvaluation()
    {
        using var w = new Workspace();
        var (realBatch, _) = w.Execution();
        var realRun = w.Repo.StartRun("actual-hash", "actual.pdf", w.Settings);
        var payload = SyntheticInvoiceResponse.Json(1);
        w.Repo.Complete(realRun, "actual.pdf", payload, ExtractionParser.Parse(payload));

        // Older databases may still contain this flag. Such records must not enter operator reports.
        var syntheticRun = w.Repo.StartRun("synthetic-hash", "synthetic.pdf", w.Settings, demo: true);
        w.Repo.Complete(syntheticRun, "synthetic.pdf", payload, ExtractionParser.Parse(payload));
        var mixedRun = w.Repo.StartRun("actual-hash", "synthetic-copy.pdf", w.Settings, demo: true);
        var syntheticBatch = w.Repo.CreateBatch(w.Settings, demo: true);
        var syntheticExecution = w.Repo.AddExecution(syntheticBatch, "synthetic-hash", "synthetic.pdf", w.Settings.Fingerprint(), demo: true);
        w.Repo.SaveDecision(syntheticExecution, QualityEngine.Decide(QualityEngine.ReadCandidates(payload, syntheticRun, "Original", ["InvoiceId"]), w.Settings.Pipeline));
        w.Repo.AddLabel("synthetic-hash", "InvoiceId", "DEMO-001", true, "Test fixture", "Test", "");

        var invoice = Assert.Single(w.Repo.ListInvoices());
        Assert.Equal("actual-hash", invoice.Hash);
        Assert.Equal(1, invoice.RunCount);
        Assert.Equal(realRun, Assert.Single(w.Repo.ListRuns(invoice.Id)).Id);
        Assert.Equal(realBatch, Assert.Single(w.Repo.ListBatches()).Id);
        Assert.Equal("hash", Assert.Single(w.Repo.ListExecutions()).Hash);
        Assert.DoesNotContain("synthetic.pdf", BusinessExport.Csv(w.Repo));
        Assert.DoesNotContain("synthetic.pdf", BusinessExport.WideCsv(w.Repo));
        Assert.Null(Evaluation.Run(w.Repo).Accuracy);
        Assert.Empty(Evaluation.Run(w.Repo).Rows);
        Assert.NotNull(w.Repo.GetRun(mixedRun)); // Historical storage is preserved; display queries exclude it.
    }

    [Fact]
    public void DefaultsAndLocalOverridesSupportCommentsAndStandardEnvironmentArrays()
    {
        using var w = new Workspace();
        File.WriteAllText(Path.Combine(w.Root, "appsettings.json"), """
            { // English comments are valid
              "DocumentIntelligence":{"Tier":"S0"}, "Batch":{"MaxDocuments":20}}
            """);
        File.WriteAllText(Path.Combine(w.Root, "appsettings.local.json"), """{"batch":{"Concurrency":3}}""");
        var names = new[] { "DocumentIntelligence__QueryFields", "DocumentIntelligence__ApiKey", "Pipeline__TargetFields__0" };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable(names[0], "[\"InvoiceId\",\"Currency\"]"); Environment.SetEnvironmentVariable(names[1], "123456789"); Environment.SetEnvironmentVariable(names[2], "CustomField");
            var s = Settings.Load(w.Root); Assert.Equal(20, s.MaxDocuments); Assert.Equal(3, s.Batch.Concurrency); Assert.Equal("123456789", s.ApiKey);
            Assert.Equal(new[] { "InvoiceId", "Currency" }, s.DocumentIntelligence.QueryFields); Assert.Equal("CustomField", s.Pipeline.TargetFields[0]); Assert.Equal(Path.Combine(w.Root, "data", "invoices.db"), s.DatabasePath);
        }
        finally { for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], previous[i]); }
    }
    [Fact]
    public void FingerprintIgnoresSecretsBudgetAndPathsButTracksQualityPolicy()
    {
        var s = new Settings { ApiKey = "private-test-key" }; var fp = s.Fingerprint(); Assert.DoesNotContain(s.ApiKey, s.PublicJson());
        s.ApiKey = "rotated"; s.Endpoint = "https://new.example.com"; s.DatabasePath = "different.db"; s.Batch.MaxAnalyzeRequests = 999; Assert.Equal(fp, s.Fingerprint());
        s.Pipeline.AcceptanceConfidence = .97; Assert.NotEqual(fp, s.Fingerprint());
    }
    [Fact]
    public void ValidationRejectsUnsafeOrInconsistentOptions()
    {
        var s = new Settings(); s.Validate(); s.Pipeline.AllowPaidFeatures = true; Assert.Throws<InvalidOperationException>(s.Validate);
        s.Pipeline.AllowPaidFeatures = false; s.DocumentIntelligence.MaxRetries = -1; Assert.Throws<InvalidOperationException>(s.Validate);
        s.DocumentIntelligence.MaxRetries = 5; s.Pipeline.RequiredFields = ["Unknown"]; Assert.Throws<InvalidOperationException>(s.Validate);
        s.Pipeline.RequiredFields = []; s.Endpoint = "http://insecure.example"; s.ApiKey = "x"; Assert.Throws<InvalidOperationException>(s.ValidateAzure);
    }
    [Fact]
    public async Task RequestBudgetIsAtomicUnderConcurrentReservations()
    {
        using var w = new Workspace(); var batch = w.Repo.CreateBatch(w.Settings); var granted = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, 100), async (_, _) => { if (w.Repo.ReserveRequest(batch, 17)) Interlocked.Increment(ref granted); await Task.CompletedTask; });
        Assert.Equal(17, granted); Assert.Equal(17, w.Repo.ListBatches().Single().Requests);
    }
    [Fact]
    public void LabelImportIsAtomicAndPreservesAuditAndDatasetBoundaries()
    {
        using var w = new Workspace(); w.Execution();
        w.Repo.AddLabel("hash", "InvoiceId", "A", true, "Reviewer", "Test", "First review");
        w.Repo.AddLabel("hash", "InvoiceId", "B", true, "Reviewer", "Test", "Correction");
        Assert.Equal(2, w.Repo.GetLabels(history: true).Count); Assert.Equal("B", w.Repo.GetLabels().Single().ExpectedValue);
        Assert.Throws<ArgumentException>(() => w.Repo.AddLabel("hash", "InvoiceId", "C", true, "Reviewer", "Calibration", ""));
        Assert.Throws<ArgumentException>(() => w.Repo.ImportLabels([new("hash", "InvoiceId", "C", true, "Reviewer", "Test", ""), new("unknown", "InvoiceId", "X", true, "Reviewer", "Test", "")]));
        Assert.Equal(2, w.Repo.GetLabels(history: true).Count); Assert.Equal("B", w.Repo.GetLabels().Single().ExpectedValue);
    }
    [Fact]
    public void EvaluationCountsMissingPredictionsAndFailedLatestExecutions()
    {
        using var w = new Workspace(); var (batch, id) = w.Execution();
        var json = Workspace.Response(); var run = w.Repo.StartAttempt(w.Repo.GetExecution(id)!, new("Original", "prebuilt-invoice", [], [], "", ""), w.Settings);
        w.Repo.Complete(run, "invoice.pdf", json, ExtractionParser.Parse(json));
        w.Repo.SaveDecision(id, QualityEngine.Decide(QualityEngine.ReadCandidates(json, run, "Original", ["InvoiceId"]), w.Settings.Pipeline));
        w.Repo.AddLabel("hash", "InvoiceId", "INV-001", true, "Reviewer", "Test", "");
        var first = Evaluation.Run(w.Repo); Assert.Equal(1, first.Accuracy); Assert.Equal(1, first.BaselineAccuracy); Assert.Equal(1, first.AcceptedPrecision);
        var (_, latest) = w.Execution(); w.Repo.SetExecutionStatus(latest, "Failed", "Test failure");
        var second = Evaluation.Run(w.Repo); Assert.Equal(1, second.ExpectedPresent); Assert.Equal(0, second.Accuracy); Assert.Null(second.AcceptedPrecision);
        Assert.Contains("Failed", BusinessExport.Csv(w.Repo)); Assert.Single(w.Repo.ListExecutions());
    }
    [Fact]
    public void AbsenceIsMeasuredSeparatelyAndSmallSamplesDoNotProve98Percent()
    {
        using var w = new Workspace(); var (_, id) = w.Execution();
        w.Repo.SaveDecision(id, QualityEngine.Decide([], w.Settings.Pipeline)); w.Repo.AddLabel("hash", "InvoiceId", null, false, "Reviewer", "Test", "Verified absent");
        var report = Evaluation.Run(w.Repo); Assert.Null(report.Accuracy); Assert.Equal(1, report.CorrectAbsent); Assert.Equal(1, report.AbsentLabels);
        Assert.Null(Evaluation.WilsonLower(0, 0)); Assert.True(Evaluation.WilsonLower(100, 100) < .98);
    }
    [Fact]
    public void QueryFieldsRequestIncludesFeatureAndEscapesParameters()
    {
        var s = new Settings { Endpoint = "https://example.com", Tier = "S0" };
        var url = AzureInvoiceClient.BuildUri(s, new("QueryFields", "prebuilt-layout", ["queryFields"], ["VendorTaxId", "PaymentTerms"], "1-3,5", "en-US")).AbsoluteUri;
        Assert.Contains("features=queryFields", url); Assert.Contains("queryFields=VendorTaxId,PaymentTerms", url); Assert.Contains("pages=1-3%2C5", url); Assert.Contains("locale=en-US", url);
    }
}
