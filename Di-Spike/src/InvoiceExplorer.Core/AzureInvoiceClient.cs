using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace InvoiceExplorer.Core;

public interface IInvoiceAnalyzer
{
    Task<string> AnalyzeAsync(byte[] content, string extension, AnalysisRequest request, Action<string> onOperation, CancellationToken token);
    Task<string> PollAsync(string operationUrl, CancellationToken token);
}
public sealed class RequestBudgetException() : Exception("The batch analyze-request budget has been reached. Increase MaxAnalyzeRequests and resume the same batch.");
public sealed class AzureRequestException(HttpStatusCode status, string code) : HttpRequestException($"Azure HTTP {(int)status}. Code: {code}. Check resource permissions, feature support, and quota.", null, status);

/// <summary>Shared by all workers so concurrency never bypasses the configured request rate.</summary>
public sealed class RequestPacer(double requestsPerSecond)
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private DateTimeOffset next = DateTimeOffset.MinValue;
    public async Task WaitAsync(CancellationToken token)
    {
        await mutex.WaitAsync(token);
        try
        {
            var delay = next - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            next = DateTimeOffset.UtcNow.AddSeconds(1 / requestsPerSecond);
        }
        finally { mutex.Release(); }
    }
}

/// <summary>Uses the stable REST API and preserves the original response, including unrecognized fields.</summary>
public sealed class AzureInvoiceClient(HttpClient http, Settings settings, RequestPacer? pacer = null, Func<bool>? reserveRequest = null) : IInvoiceAnalyzer
{
    public Task<string> AnalyzeAsync(byte[] content, string extension, Action<string> onOperation, CancellationToken cancellationToken = default)
        => AnalyzeAsync(content, extension, new("Original", settings.ModelId, ["keyValuePairs"], [], settings.PageRange, settings.DocumentIntelligence.Locale), onOperation, cancellationToken);

    public async Task<string> AnalyzeAsync(byte[] content, string extension, AnalysisRequest request, Action<string> onOperation, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var endpoint = new Uri(settings.Endpoint.TrimEnd('/') + "/");
        using var response = await SendAsync(HttpMethod.Post, BuildUri(settings, request), content, extension, timeout.Token);
        if (!response.Headers.TryGetValues("Operation-Location", out var locations)) throw new InvalidDataException("Azure did not return Operation-Location. The submission outcome is ambiguous; it must not be retried automatically.");
        var operation = new Uri(endpoint, locations.First()); ValidateOperation(endpoint, operation);
        // Persist before the first poll so cancellation can resume this operation without another POST.
        onOperation(operation.AbsoluteUri);
        return await PollCoreAsync(operation, RetryDelay(response), timeout.Token);
    }
    public async Task<string> PollAsync(string operationUrl, CancellationToken token)
    {
        var uri = new Uri(operationUrl); ValidateOperation(new Uri(settings.Endpoint), uri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        return await PollCoreAsync(uri, TimeSpan.Zero, timeout.Token);
    }
    private async Task<string> PollCoreAsync(Uri operation, TimeSpan delay, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(delay, token);
            using var poll = await SendAsync(HttpMethod.Get, operation, null, ".pdf", token);
            var json = await poll.Content.ReadAsStringAsync(token);
            using var result = JsonDocument.Parse(json);
            var status = result.RootElement.GetProperty("status").GetString()?.ToLowerInvariant();
            if (status == "succeeded") return json;
            if (status is "failed" or "canceled" or "cancelled") throw new InvalidOperationException($"Azure finished with status {status}. Code: {ErrorCode(json)}.");
            if (status is not ("running" or "notstarted")) throw new InvalidDataException($"Unrecognized Azure status: {status}");
            delay = RetryDelay(poll);
        }
    }
    public static Uri BuildUri(Settings settings, AnalysisRequest request)
    {
        var model = Uri.EscapeDataString(request.ModelId);
        var path = settings.IsContentUnderstanding ? $"contentunderstanding/analyzers/{model}:analyzeBinary" : $"documentintelligence/documentModels/{model}:analyze";
        var query = new List<string> { "api-version=" + Uri.EscapeDataString(settings.ApiVersion) };
        if (!settings.IsContentUnderstanding)
        {
            if (request.Features.Length > 0) query.Add("features=" + string.Join(',', request.Features.Select(Uri.EscapeDataString)));
            if (request.QueryFields.Length > 0) query.Add("queryFields=" + string.Join(',', request.QueryFields.Select(Uri.EscapeDataString)));
            if (request.Pages.Length > 0) query.Add("pages=" + Uri.EscapeDataString(request.Pages));
            if (request.Locale.Length > 0) query.Add("locale=" + Uri.EscapeDataString(request.Locale));
        }
        return new Uri(new Uri(settings.Endpoint.TrimEnd('/') + "/"), path + "?" + string.Join('&', query));
    }
    public static void ValidateOperation(Uri endpoint, Uri operation)
    {
        if (operation.Scheme != "https" || !string.IsNullOrEmpty(operation.UserInfo) || !operation.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) || operation.Port != endpoint.Port)
            throw new InvalidDataException("Operation-Location points outside the configured endpoint.");
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, byte[]? bytes, string extension, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (pacer is not null) await pacer.WaitAsync(token);
            if (method == HttpMethod.Post && reserveRequest is not null && !reserveRequest()) throw new RequestBudgetException();
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Add("Ocp-Apim-Subscription-Key", settings.ApiKey);
            if (bytes is not null)
            {
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(extension.ToLowerInvariant() switch { ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".tif" or ".tiff" => "image/tiff", _ => "application/octet-stream" });
            }
            var response = await http.SendAsync(request, token);
            // 429 explicitly rejects the request. Timeouts, transport failures and POST 5xx are ambiguous.
            // Retrying those POSTs can duplicate processing and billing, so only GET 5xx is retried.
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || method == HttpMethod.Get && (int)response.StatusCode >= 500) && attempt < settings.DocumentIntelligence.MaxRetries)
            {
                var delay = RetryDelay(response, attempt); response.Dispose(); await Task.Delay(delay, token); continue;
            }
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode; var body = await response.Content.ReadAsStringAsync(token); response.Dispose();
            throw new AzureRequestException(status, ErrorCode(body));
        }
    }
    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt = 0)
    {
        var retry = response.Headers.RetryAfter;
        var fallback = Math.Max(settings.DocumentIntelligence.PollIntervalMilliseconds / 1000d, Math.Pow(2, attempt));
        var seconds = retry?.Delta?.TotalSeconds ?? (retry?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? fallback;
        return TimeSpan.FromSeconds(Math.Max(0.01, seconds));
    }
    private static string ErrorCode(string json)
    {
        try { using var doc = JsonDocument.Parse(json); if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c)) return c.GetString() ?? "Unknown"; }
        catch (JsonException) { }
        return "Unknown";
    }
}
