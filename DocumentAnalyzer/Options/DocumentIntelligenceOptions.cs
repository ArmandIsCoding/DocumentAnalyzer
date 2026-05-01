namespace DocumentAnalyzer.Options;

public sealed class DocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";

    public string Endpoint { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string ModelId { get; set; } = "prebuilt-invoice";

    public int MaxFileSizeMb { get; set; } = 10;
}

