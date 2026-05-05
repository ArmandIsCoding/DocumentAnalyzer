namespace DocumentAnalyzerLLM.Services;

public sealed record DocumentAnalysisUiResult(
    string RawJson,
    IReadOnlyList<DocumentPagePreview> Pages,
    IReadOnlyList<DocumentFieldOverlay> Fields);

public sealed record DocumentPagePreview(
    int PageNumber,
    double Width,
    double Height,
    IReadOnlyList<DocumentWordPreview> Words);

public sealed record DocumentWordPreview(
    string Content,
    IReadOnlyList<float> Polygon);

public sealed record DocumentFieldOverlay(
    string Name,
    string? Content,
    float? Confidence,
    int PageNumber,
    IReadOnlyList<float> Polygon);

