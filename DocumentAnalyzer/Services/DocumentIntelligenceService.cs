using System.Text.Json;
using Azure;
using Azure.AI.DocumentIntelligence;
using DocumentAnalyzer.Options;
using Microsoft.Extensions.Options;
using System.Linq;

namespace DocumentAnalyzer.Services;

public sealed class DocumentIntelligenceService
{
    private readonly DocumentIntelligenceOptions _options;

    public DocumentIntelligenceService(IOptions<DocumentIntelligenceOptions> options)
    {
        _options = options.Value;
    }

    public long MaxFileSizeBytes => _options.MaxFileSizeMb * 1024L * 1024L;

    public async Task<DocumentAnalysisUiResult> AnalyzePdfAsync(Stream pdfStream, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(_options.Key))
        {
            throw new InvalidOperationException(
                "Falta configurar DocumentIntelligence: define Endpoint y Key en configuración o user-secrets.");
        }

        var client = new DocumentIntelligenceClient(new Uri(_options.Endpoint), new AzureKeyCredential(_options.Key));
        BinaryData payload = await BinaryData.FromStreamAsync(pdfStream, cancellationToken).ConfigureAwait(false);

        Operation<AnalyzeResult> operation = await client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            _options.ModelId,
            payload,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        AnalyzeResult result = operation.Value;

        string rawJson = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        IReadOnlyList<DocumentPagePreview> pages = result.Pages
            .Select(page => new DocumentPagePreview(
                page.PageNumber,
                page.Width ?? 1,
                page.Height ?? 1,
                page.Words
                    .Where(word => word.Polygon.Count >= 8)
                    .Select(word => new DocumentWordPreview(word.Content, word.Polygon.ToArray()))
                    .ToList()))
            .ToList();

        List<DocumentFieldOverlay> fields = new();

        if (result.Documents.Count > 0)
        {
            foreach ((string fieldName, DocumentField field) in result.Documents[0].Fields.OrderBy(x => x.Key))
            {
                foreach (BoundingRegion region in field.BoundingRegions.Where(region => region.Polygon.Count >= 8))
                {
                    fields.Add(new DocumentFieldOverlay(
                        fieldName,
                        field.Content,
                        field.Confidence,
                        region.PageNumber,
                        region.Polygon.ToArray()));
                }
            }
        }

        return new DocumentAnalysisUiResult(rawJson, pages, fields);
    }
}

