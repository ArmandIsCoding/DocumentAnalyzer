using System.Text.Json;

namespace InvoiceExplorer.Core;

/// <summary>Deterministic fictional data for UI demonstrations. Never presented as measured extraction quality.</summary>
public static class PipelineDemo
{
    public static void Seed(InvoiceRepository repo, Settings settings)
    {
        if (repo.ListBatches().Any(b => b.IsDemo)) return;
        var batch = repo.CreateBatch(settings, true);
        for (var i = 0; i < 12; i++)
        {
            var hash = "demo-pipeline-" + i;
            var id = repo.AddExecution(batch, hash, $"Fictional / invoice-{2026001 + i}.pdf", settings.Fingerprint(), true);
            var execution = repo.GetExecution(id)!;
            repo.SetExecutionProfile(id, new(1, 720, 0, "Digital", ["Fictional demonstration; no source PDF was submitted."]), "");
            var candidates = new List<FieldCandidate>();
            for (var pass = 0; pass < (i % 3 == 0 ? 2 : 1); pass++)
            {
                var strategy = pass == 0 ? "Original" : "QueryFields";
                var request = new AnalysisRequest(strategy, pass == 0 ? "prebuilt-invoice" : "prebuilt-layout", [], [], "", "");
                var run = repo.StartAttempt(execution, request, settings);
                var json = Json(i, pass == 1);
                repo.Complete(run, execution.SourcePath, json, ExtractionParser.Parse(json));
                candidates.AddRange(QualityEngine.ReadCandidates(json, run, strategy, settings.Pipeline.TargetFields));
            }
            repo.SaveDecision(id, QualityEngine.Decide(candidates, settings.Pipeline));
            foreach (var candidate in QualityEngine.ReadCandidates(Json(i, false), 0, "Truth", settings.Pipeline.TargetFields))
                repo.AddLabel(hash, candidate.Name, candidate.Value, true, "Demo reviewer (simulated)", "Test", "Fictional benchmark label.");
        }
        repo.SetBatchStatus(batch, "Completed");
    }
    public static string Json(int index, bool alternative)
    {
        var vendor = new[] { "Northstar Office Supply", "Atlas Design Studio", "Willow Logistics", "Harbor Technology" }[index % 4];
        var values = new Dictionary<string, (string Type, object Value)>
        {
            ["VendorName"] = ("string", vendor), ["VendorAddress"] = ("string", "120 Market Street, Boston, MA"),
            ["VendorTaxId"] = ("string", "US-DEMO-1024"), ["CustomerName"] = ("string", "Riverside Workshop"),
            ["CustomerAddress"] = ("string", "85 Cedar Avenue, Seattle, WA"), ["InvoiceId"] = ("string", "INV-" + (2026001 + index)),
            ["PurchaseOrder"] = ("string", "PO-" + (4100 + index)), ["InvoiceDate"] = ("date", "2026-09-10"),
            ["DueDate"] = ("date", "2026-10-10"), ["SubTotal"] = ("number", 1000 + index * 100),
            ["TotalTax"] = ("number", 100 + index * 10), ["InvoiceTotal"] = ("number", 1100 + index * 110 + (alternative && index == 3 ? 25 : 0)),
            ["AmountDue"] = ("number", 1100 + index * 110), ["Currency"] = ("string", "USD"), ["PaymentTerms"] = ("string", "Net 30")
        };
        var fields = new Dictionary<string, object>();
        foreach (var (name, value) in values)
        {
            if (index == 5 && name == "InvoiceId" || index == 8 && name == "PurchaseOrder") continue;
            var score = name == "TotalTax" && index % 4 == 2 ? .78 : index % 3 == 0 && !alternative ? .91 : .991;
            fields[name] = new Dictionary<string, object> { ["type"] = value.Type, ["value" + char.ToUpperInvariant(value.Type[0]) + value.Type[1..]] = value.Value,
                ["content"] = value.Value.ToString()!, ["confidence"] = score, ["boundingRegions"] = new[] { new { pageNumber = 1, polygon = new[] { 1d, 1d, 3d, 1d, 3d, 1.3, 1d, 1.3 } } } };
        }
        return JsonSerializer.Serialize(new { status = "succeeded", analyzeResult = new { content = "FICTIONAL INVOICE - SIMULATED AZURE RESPONSE", documents = new[] { new { docType = "invoice", confidence = .99, fields } }, pages = new[] { new { pageNumber = 1, words = new[] { new { content = "INVOICE", confidence = .99 } } } } } });
    }
}
