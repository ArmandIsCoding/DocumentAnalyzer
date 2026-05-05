using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentAnalyzerLLM.Options;
using Microsoft.Extensions.Options;

namespace DocumentAnalyzerLLM.Services;

public sealed class AzureLlmDocumentService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AzureLlmOptions _options;

    public AzureLlmDocumentService(HttpClient httpClient, IOptions<AzureLlmOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public long MaxFileSizeBytes => _options.MaxFileSizeMb * 1024L * 1024L;

    public async Task<DocumentAnalysisUiResult> AnalyzePdfAsync(Stream pdfStream, CancellationToken cancellationToken = default)
    {
        ValidateSettings();

        byte[] pdfBytes = await ReadPdfBytesAsync(pdfStream, cancellationToken).ConfigureAwait(false);
        string modelJson = await RequestJsonFromLlmAsync(pdfBytes, cancellationToken).ConfigureAwait(false);

        LlmInvoiceResponse response = DeserializeJson(modelJson);
        string prettyRawJson = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });

        List<DocumentPagePreview> pages = response.Pages
            .Select(page => new DocumentPagePreview(
                page.PageNumber <= 0 ? 1 : page.PageNumber,
                page.Width <= 0 ? 1 : page.Width,
                page.Height <= 0 ? 1 : page.Height,
                []))
            .ToList();

        if (pages.Count == 0)
        {
            pages.Add(new DocumentPagePreview(1, 1, 1, []));
        }

        List<DocumentFieldOverlay> fields = response.Fields
            .Where(field => field.Polygon.Count >= 8)
            .Select(field => new DocumentFieldOverlay(
                string.IsNullOrWhiteSpace(field.Name) ? "Campo" : field.Name,
                field.Content,
                field.Confidence,
                field.PageNumber <= 0 ? 1 : field.PageNumber,
                field.Polygon))
            .ToList();

        return new DocumentAnalysisUiResult(prettyRawJson, pages, fields);
    }

    private void ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint) ||
            string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.Deployment))
        {
            throw new InvalidOperationException(
                "Falta configurar AzureLlm: define Endpoint, ApiKey y Deployment en configuración o user-secrets.");
        }
    }

    private async Task<byte[]> ReadPdfBytesAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await pdfStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (buffer.Length == 0)
        {
            throw new InvalidOperationException("El PDF está vacío.");
        }

        if (buffer.Length > MaxFileSizeBytes)
        {
            throw new IOException($"El archivo supera el límite permitido ({_options.MaxFileSizeMb} MB).");
        }

        return buffer.ToArray();
    }

    private async Task<string> RequestJsonFromLlmAsync(byte[] pdfBytes, CancellationToken cancellationToken)
    {
        string endpoint = _options.Endpoint.TrimEnd('/');
        string requestUrl =
            $"{endpoint}/openai/responses?api-version={Uri.EscapeDataString(_options.ApiVersion)}";

        string base64Pdf = Convert.ToBase64String(pdfBytes);

        var requestBody = new
        {
            model = _options.Deployment,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = _options.Prompt },
                        new
                        {
                            type = "input_file",
                            filename = "invoice.pdf",
                            file_data = $"data:application/pdf;base64,{base64Pdf}"
                        }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("api-key", _options.ApiKey);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure LLM devolvió {(int)response.StatusCode}: {responseBody}");
        }

        using JsonDocument json = JsonDocument.Parse(responseBody);
        string outputText = ExtractOutputText(json.RootElement);

        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException("El modelo no devolvió texto para procesar.");
        }

        return ExtractJsonPayload(outputText);
    }

    private static string ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out JsonElement outputTextElement) &&
            outputTextElement.ValueKind == JsonValueKind.String)
        {
            return outputTextElement.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out JsonElement outputArray) ||
            outputArray.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (JsonElement message in outputArray.EnumerateArray())
        {
            if (!message.TryGetProperty("content", out JsonElement contentArray) ||
                contentArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement content in contentArray.EnumerateArray())
            {
                if (!content.TryGetProperty("type", out JsonElement type) || type.GetString() != "output_text")
                {
                    continue;
                }

                if (content.TryGetProperty("text", out JsonElement textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    builder.AppendLine(textElement.GetString());
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static string ExtractJsonPayload(string output)
    {
        string trimmed = output.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            int firstLineBreak = trimmed.IndexOf('\n');
            if (firstLineBreak >= 0)
            {
                trimmed = trimmed[(firstLineBreak + 1)..];
            }

            int closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                trimmed = trimmed[..closingFence];
            }

            trimmed = trimmed.Trim();
        }

        int objectStart = trimmed.IndexOf('{');
        int objectEnd = trimmed.LastIndexOf('}');

        if (objectStart >= 0 && objectEnd > objectStart)
        {
            return trimmed.Substring(objectStart, objectEnd - objectStart + 1);
        }

        throw new InvalidOperationException("No se encontró un JSON válido en la respuesta del modelo.");
    }

    private static LlmInvoiceResponse DeserializeJson(string json)
    {
        LlmInvoiceResponse? parsed = JsonSerializer.Deserialize<LlmInvoiceResponse>(json, JsonOptions);

        if (parsed is null)
        {
            throw new InvalidOperationException("No se pudo deserializar el JSON generado por el modelo.");
        }

        return parsed;
    }

    private sealed class LlmInvoiceResponse
    {
        public List<LlmPage> Pages { get; init; } = [];
        public List<LlmField> Fields { get; init; } = [];
    }

    private sealed class LlmPage
    {
        public int PageNumber { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
    }

    private sealed class LlmField
    {
        public string Name { get; init; } = string.Empty;
        public string? Content { get; init; }
        public float? Confidence { get; init; }
        public int PageNumber { get; init; }
        public List<float> Polygon { get; init; } = [];
    }
}


