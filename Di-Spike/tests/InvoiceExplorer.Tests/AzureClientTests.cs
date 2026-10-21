using System.Net;
using InvoiceExplorer.Core;

namespace InvoiceExplorer.Tests;

public class AzureClientTests
{
    [Theory]
    [InlineData(false, "documentintelligence/documentModels/prebuilt-invoice:analyze", "2024-11-30")]
    [InlineData(true, "contentunderstanding/analyzers/prebuilt-invoice:analyzeBinary", "2025-11-01")]
    public async Task SubmitsBinaryAndPollsOperation(bool cu, string path, string apiVersion)
    {
        var calls = new List<(string Method, string Url, byte[] Body)>();
        var handler = new FakeHandler(async request =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri,
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync()));
            Assert.Equal("test-key", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Add("Operation-Location", "https://example.cognitiveservices.azure.com/results/123");
                return response;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(DemoData.Json(1)) };
        });
        using var http = new HttpClient(handler);
        var settings = new Settings { Endpoint = "https://example.cognitiveservices.azure.com", ApiKey = "test-key",
            Provider = cu ? "ContentUnderstanding" : "DocumentIntelligence" };
        var client = new AzureInvoiceClient(http, settings);
        string? operation = null;
        var json = await client.AnalyzeAsync([1, 2, 3], ".pdf", url => operation = url);
        Assert.Equal(2, calls.Count);
        Assert.Contains(path, calls[0].Url);
        Assert.Contains(apiVersion, calls[0].Url);
        Assert.Equal(new byte[] { 1, 2, 3 }, calls[0].Body);
        Assert.Equal(!cu, calls[0].Url.Contains("pages=1-2"));
        Assert.Equal(operation, calls[1].Url);
        Assert.Contains("analyzeResult", json);
    }

    [Fact]
    public async Task RetriesThrottlingAndSurfacesFailedOperation()
    {
        var posts = 0;
        using var http = new HttpClient(new FakeHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                if (++posts == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
                accepted.Headers.Add("Operation-Location", "https://example.com/result/1");
                return Task.FromResult(accepted);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"failed","error":{"code":"InvalidContent","message":"private details"}}""")
            });
        }));
        var client = new AzureInvoiceClient(http, new Settings { Endpoint = "https://example.com", ApiKey = "test" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AnalyzeAsync([1], ".pdf", _ => { }));
        Assert.Equal(2, posts);
        Assert.Contains("InvalidContent", error.Message);
        Assert.DoesNotContain("private details", error.Message);
    }

    [Fact]
    public async Task DoesNotRetryAmbiguousPostFailure()
    {
        var count = 0;
        using var http = new HttpClient(new FakeHandler(_ =>
        {
            count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }));
        var client = new AzureInvoiceClient(http, new Settings { Endpoint = "https://example.com", ApiKey = "test" });
        await Assert.ThrowsAsync<AzureRequestException>(() => client.AnalyzeAsync([1], ".pdf", _ => { }));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("https://attacker.example/result")]
    [InlineData("http://resource.example/result")]
    [InlineData("https://resource.example:444/result")]
    public void RejectsPollingOutsideCredentialDestination(string url)
    {
        Assert.Throws<InvalidDataException>(() => AzureInvoiceClient.ValidateOperation(new("https://resource.example"), new(url)));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
