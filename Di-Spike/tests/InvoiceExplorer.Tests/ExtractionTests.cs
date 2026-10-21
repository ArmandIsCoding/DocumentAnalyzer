using InvoiceExplorer.Core;
using Microsoft.Data.Sqlite;

namespace InvoiceExplorer.Tests;

public class ExtractionTests
{
    [Fact]
    public void ParserPreservesNestedFieldsAndExcludesOcrContainersAndMissingConfidence()
    {
        var parsed = ExtractionParser.Parse(DemoData.Json(1));
        Assert.Equal(9, parsed.LeafCount);
        Assert.Equal(8, parsed.ConfidenceCount);
        Assert.Equal(.9125, parsed.OverallConfidence!.Value, 6);
        Assert.Equal(.99, parsed.DocumentConfidence);
        Assert.Contains(parsed.Fields, f => f.Path == "Documents[0].Items[0].Quantity" && f.Value == "2");
        Assert.Contains(parsed.Fields, f => f.Category == "OCR" && f.Confidence == .995);
        Assert.Null(parsed.Fields.Single(f => f.Path.EndsWith("PaymentTerm")).Confidence);
    }

    [Fact]
    public void ContentUnderstandingSupportsAllContentsAndUnknownTypes()
    {
        const string json = """
            {"result":{"contents":[{"markdown":"First","fields":{
            "Items":{"type":"array","valueArray":[{"type":"object","valueObject":{
            "Total":{"type":"number","valueNumber":123.45,"confidence":0.8},
            "Metadata":{"type":"json","valueJson":{"custom":"value"}}}}]}}},
            {"markdown":"Second","fields":{"InvoiceId":{"type":"string","valueString":"B","confidence":0.6}}}]}}
            """;
        var parsed = ExtractionParser.Parse(json);
        Assert.Equal(3, parsed.LeafCount);
        Assert.Equal(.7, parsed.OverallConfidence!.Value, 6);
        Assert.Contains("First\n\nSecond", parsed.FullText);
        Assert.Contains(parsed.Fields, f => f.Path == "Contents[1].InvoiceId" && f.Value == "B");
        Assert.Contains(parsed.Fields, f => f.Type == "json" && f.Value.Contains("custom"));
    }

    [Fact]
    public void MissingAndInvalidConfidenceDoNotBecomeZero()
    {
        var parsed = ExtractionParser.Parse("""
            {"analyzeResult":{"documents":[{"fields":{
            "A":{"type":"string","valueString":"a"},
            "B":{"type":"string","valueString":"b","confidence":null},
            "C":{"type":"string","valueString":"c","confidence":1.5}}}]}}
            """);
        Assert.Null(parsed.OverallConfidence);
        Assert.Equal(0, parsed.ConfidenceCount);
        Assert.Equal(3, parsed.LeafCount);
    }

    [Fact]
    public void ParserIncludesTablesKeyValuePairsAndZeroConfidence()
    {
        var parsed = ExtractionParser.Parse("""
            {"analyzeResult":{"documents":[{"fields":{"Total":{"type":"number","valueNumber":0,"confidence":0}}}],
            "tables":[{"cells":[{"rowIndex":0,"columnIndex":1,"content":"A, B"}]}],
            "keyValuePairs":[{"key":{"content":"Reference"},"value":{"content":"abc"},"confidence":0.75}]}}
            """);
        Assert.Equal(0, parsed.OverallConfidence);
        Assert.Equal(1, parsed.ConfidenceCount);
        Assert.Contains(parsed.Fields, f => f.Category == "Table" && f.Value == "A, B" && f.Path.EndsWith("[0,1]"));
        Assert.Contains(parsed.Fields, f => f.Category == "KeyValue" && f.Value == "abc" && f.Confidence == .75);
    }

    [Fact]
    public void CsvEscapesQuotesNewlinesAndSpreadsheetFormulas()
    {
        Assert.Equal("\"a,\"\"b\"\"\nc\"", CsvExport.Escape("a,\"b\"\nc"));
        Assert.Equal("\"'=SUM(A1)\"", CsvExport.Escape("=SUM(A1)"));
        Assert.Equal("\"'  +cmd\"", CsvExport.Escape("  +cmd"));
        Assert.Equal("\"José\"", CsvExport.Escape("José"));
        var parsed = ExtractionParser.Parse(DemoData.Json(1));
        var csv = CsvExport.Create(42, "a,b.pdf", parsed);
        Assert.Contains("\"42\",\"a,b.pdf\"", csv);
        Assert.Contains("\"99\"", csv);
    }

    [Fact]
    public void RepositoryRetainsChronologicalRunsCsvJsonErrorsAndRenamedDuplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"invoice-test-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new InvoiceRepository(path);
            repository.Initialize();
            var one = repository.StartRun("same-hash", "original.pdf", new());
            var json = DemoData.Json(1);
            var csv = repository.Complete(one, "original.pdf", json, ExtractionParser.Parse(json));
            var two = repository.StartRun("same-hash", "renamed.pdf", new());
            repository.Fail(two, "Azure HTTP 429");
            var invoice = Assert.Single(repository.ListInvoices());
            Assert.Equal(2, invoice.RunCount);
            var runs = repository.ListRuns(invoice.Id);
            Assert.Equal(new[] { one, two }, runs.Select(r => r.Id));
            Assert.Equal("Failed", runs[1].Status);
            Assert.Equal("renamed.pdf", runs[1].FileName);
            var details = repository.GetRun(one)!;
            Assert.Equal(json, details.RawJson);
            Assert.Equal(csv, details.Csv);
            Assert.Contains("FICTIONAL INVOICE", details.FullText);
            Assert.Equal(12, details.Fields.Count);
            Assert.Null(repository.GetRun(long.MaxValue));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
