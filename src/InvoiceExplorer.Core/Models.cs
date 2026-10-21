namespace InvoiceExplorer.Core;

public sealed record ExtractedField(
    string Category, string Path, string Type, string Value, string Content,
    double? Confidence, bool IsLeaf, string Source);

public sealed record ParsedExtraction(
    IReadOnlyList<ExtractedField> Fields, string FullText, double? DocumentConfidence)
{
    // Containers, OCR and document classification must not inflate field confidence.
    public IEnumerable<ExtractedField> SemanticLeaves => Fields.Where(f => f.Category == "Field" && f.IsLeaf);
    public int ConfidenceCount => SemanticLeaves.Count(f => f.Confidence.HasValue);
    public int LeafCount => SemanticLeaves.Count();
    public double? OverallConfidence => SemanticLeaves.Where(f => f.Confidence.HasValue)
        .Select(f => f.Confidence).DefaultIfEmpty(null).Average();
}

public sealed record InvoiceSummary(long Id, string FileName, string Hash, bool IsDemo,
    int RunCount, string LastStarted, string LastStatus, double? OverallConfidence);

public sealed record ExtractionRun(long Id, long InvoiceId, string FileName, string StartedUtc,
    string? FinishedUtc, string Status, string Provider, string ModelId, string ApiVersion,
    bool IsDemo, string Pages, double? OverallConfidence, double? DocumentConfidence,
    int ConfidenceCount, int LeafCount, string? Error, string? OperationUrl);

public sealed record RunDetails(ExtractionRun Run, IReadOnlyList<ExtractedField> Fields,
    string FullText, string RawJson, string Csv);
