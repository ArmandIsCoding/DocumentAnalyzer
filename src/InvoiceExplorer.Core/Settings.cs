using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InvoiceExplorer.Core;

/// <summary>Portable, shared configuration. Environment overrides use standard Section__Property names.</summary>
public sealed class Settings
{
    public DocumentIntelligenceOptions DocumentIntelligence { get; set; } = new();
    public BatchOptions Batch { get; set; } = new();
    public PipelineOptions Pipeline { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    [JsonIgnore] public string RootDirectory { get; set; } = Environment.CurrentDirectory;
    // Compatibility properties keep the original extraction history and parser usable.
    [JsonIgnore] public string Provider { get; set; } = "DocumentIntelligence";
    [JsonIgnore] public string Endpoint { get => DocumentIntelligence.Endpoint; set => DocumentIntelligence.Endpoint = value; }
    [JsonIgnore] public string ApiKey { get => DocumentIntelligence.ApiKey; set => DocumentIntelligence.ApiKey = value; }
    [JsonIgnore] public string ModelId { get => DocumentIntelligence.ModelId; set => DocumentIntelligence.ModelId = value; }
    [JsonIgnore] public string InputDirectory { get => Batch.InputDirectory; set => Batch.InputDirectory = value; }
    [JsonIgnore] public string FilePattern { get => Batch.FilePattern; set => Batch.FilePattern = value; }
    [JsonIgnore] public string DatabasePath { get => Storage.DatabasePath; set => Storage.DatabasePath = value; }
    [JsonIgnore] public string ExportDirectory { get => Storage.ExportDirectory; set => Storage.ExportDirectory = value; }
    [JsonIgnore] public int MaxDocuments { get => Batch.MaxDocuments; set => Batch.MaxDocuments = value; }
    [JsonIgnore] public int TimeoutSeconds { get => DocumentIntelligence.TimeoutSeconds; set => DocumentIntelligence.TimeoutSeconds = value; }
    [JsonIgnore] public bool IsContentUnderstanding => Provider.Equals("ContentUnderstanding", StringComparison.OrdinalIgnoreCase);
    // The REST contract is an implementation detail, not an operator setting.
    [JsonIgnore] public string ApiVersion => IsContentUnderstanding ? "2025-11-01" : "2024-11-30";
    [JsonIgnore] public string PageRange => DocumentIntelligence.Pages;
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string FindRoot()
    {
        if (Environment.GetEnvironmentVariable("INVOICE_ROOT") is { Length: > 0 } path) return Path.GetFullPath(path);
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "invoice-workspace.marker")) || File.Exists(Path.Combine(dir.FullName, "invoice-settings.json"))) return dir.FullName;
        throw new InvalidOperationException("Set INVOICE_ROOT to the workspace containing invoice-workspace.marker and appsettings.json.");
    }
    public static Settings Load(string root)
    {
        // Start with typed defaults so numeric-looking secrets remain strings in environment overrides.
        var json = JsonSerializer.SerializeToNode(new Settings(), JsonOptions)!.AsObject();
        foreach (var name in new[] { "appsettings.json", "appsettings.local.json" })
            if (File.Exists(Path.Combine(root, name))) Merge(json, JsonNode.Parse(File.ReadAllText(Path.Combine(root, name)), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject());
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().OrderBy(e => ((string)e.Key).Count(c => c == '_')).ThenBy(e => (string)e.Key, StringComparer.OrdinalIgnoreCase))
        {
            var key = (string)entry.Key;
            if (new[] { "DocumentIntelligence__", "Batch__", "Pipeline__", "Storage__" }.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                SetEnvironment(json, key.Split("__"), entry.Value?.ToString() ?? "");
        }
        var result = json.Deserialize<Settings>(JsonOptions) ?? new();
        // Migrate an existing local spike configuration only when no new local override exists.
        var legacy = Path.Combine(root, "invoice-settings.local.json");
        if (!File.Exists(Path.Combine(root, "appsettings.local.json")) && File.Exists(legacy))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
            if (Environment.GetEnvironmentVariable("DocumentIntelligence__Endpoint") is null && doc.RootElement.TryGetProperty("Endpoint", out var e)) result.Endpoint = e.GetString() ?? "";
            if (Environment.GetEnvironmentVariable("DocumentIntelligence__ApiKey") is null && doc.RootElement.TryGetProperty("ApiKey", out var k)) result.ApiKey = k.GetString() ?? "";
        }
        result.Endpoint = Environment.GetEnvironmentVariable("INVOICE_AZURE_ENDPOINT") ?? result.Endpoint;
        result.ApiKey = Environment.GetEnvironmentVariable("INVOICE_AZURE_KEY") ?? result.ApiKey;
        result.DatabasePath = Path.GetFullPath(Environment.GetEnvironmentVariable("INVOICE_DB") ?? result.DatabasePath, root);
        result.InputDirectory = Path.GetFullPath(result.InputDirectory, root);
        result.ExportDirectory = Path.GetFullPath(result.ExportDirectory, root);
        result.Storage.ArtifactDirectory = Path.GetFullPath(result.Storage.ArtifactDirectory, root);
        result.RootDirectory = root;
        return result;
    }
    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var p in source)
        {
            var key = target.Select(x => x.Key).FirstOrDefault(k => k.Equals(p.Key, StringComparison.OrdinalIgnoreCase)) ?? p.Key;
            if (p.Value is JsonObject child && target[key] is JsonObject existing) Merge(existing, child);
            else target[key] = p.Value?.DeepClone();
        }
    }
    private static void SetEnvironment(JsonObject root, string[] parts, string value)
    {
        JsonObject node = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var key = node.Select(p => p.Key).FirstOrDefault(k => k.Equals(parts[i], StringComparison.OrdinalIgnoreCase)) ?? parts[i];
            if (int.TryParse(parts[i + 1], out var index))
            {
                if (index is < 0 or > 99 || i + 2 != parts.Length) throw new InvalidOperationException("Invalid environment array index.");
                if (node[key] is not JsonArray) node[key] = new JsonArray();
                var array = node[key]!.AsArray();
                while (array.Count <= index) array.Add(null);
                array[index] = value; return;
            }
            node[key] ??= new JsonObject(); node = node[key]!.AsObject();
        }
        var last = node.Select(p => p.Key).FirstOrDefault(k => k.Equals(parts[^1], StringComparison.OrdinalIgnoreCase)) ?? parts[^1];
        if (node[last] is JsonValue old && old.TryGetValue<string>(out _)) node[last] = value;
        else try { node[last] = JsonNode.Parse(value); } catch (JsonException) { node[last] = value; }
    }
    public void ValidateAzure()
    {
        Validate();
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Set DocumentIntelligence:Endpoint to the HTTPS endpoint of your Azure resource.");
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new InvalidOperationException("Set DocumentIntelligence:ApiKey in appsettings.local.json or DocumentIntelligence__ApiKey. No Azure credential is configured.");
    }
    public void Validate()
    {
        if (DocumentIntelligence is null || Batch is null || Pipeline is null || Storage is null || Pipeline.TargetFields is null || Pipeline.RequiredFields is null || Pipeline.FieldThresholds is null || DocumentIntelligence.QueryFields is null)
            throw new InvalidOperationException("Configuration sections and field lists cannot be null.");
        if (TimeoutSeconds < 10 || MaxDocuments < 1 || string.IsNullOrWhiteSpace(ModelId)) throw new InvalidOperationException("Check timeout, maximum documents, and model ID.");
        if (DocumentIntelligence.MaxRetries is < 0 or > 10 || DocumentIntelligence.PollIntervalMilliseconds is < 100 or > 60000) throw new InvalidOperationException("MaxRetries must be 0-10; PollIntervalMilliseconds must be 100-60000.");
        if (Pipeline.TargetFields.Any(string.IsNullOrWhiteSpace) || Pipeline.RequiredFields.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Field names cannot be blank.");
        if (Pipeline.FieldThresholds.Keys.Except(Pipeline.TargetFields).Any()) throw new InvalidOperationException("FieldThresholds must use exact TargetFields names.");
        if (Pipeline.MinimumOcrConfidence is < 0 or > 1) throw new InvalidOperationException("MinimumOcrConfidence must be between 0 and 1.");
        if (DocumentIntelligence.Pages is null || !System.Text.RegularExpressions.Regex.IsMatch(DocumentIntelligence.Pages, @"^$|^[1-9]\d*(?:-[1-9]\d*)?(?:,[1-9]\d*(?:-[1-9]\d*)?)*$")) throw new InvalidOperationException("Pages must be empty or a range such as 1-3,5.");
        if (Batch.Concurrency is < 1 or > 16 || Batch.RequestsPerSecond is <= 0 or > 50 || Batch.MaxAnalyzeRequests < 1) throw new InvalidOperationException("Invalid batch concurrency, rate limit, or analyze request budget.");
        if (Pipeline.MaxPasses is < 1 or > 5 || Pipeline.TargetFields.Length == 0 || Pipeline.TargetFields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Pipeline.TargetFields.Length) throw new InvalidOperationException("Target fields must be unique; MaxPasses must be 1-5.");
        if (DocumentIntelligence.QueryFields.Length > 20 || DocumentIntelligence.QueryFields.Any(f => string.IsNullOrWhiteSpace(f) || f.Contains(','))) throw new InvalidOperationException("QueryFields supports up to 20 nonempty names without commas.");
        if (Pipeline.RequiredFields.Except(Pipeline.TargetFields, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidOperationException("RequiredFields must be included in TargetFields.");
        if (Pipeline.AcceptanceConfidence is <= 0 or > 1 || Pipeline.FieldThresholds.Values.Any(v => v is <= 0 or > 1)) throw new InvalidOperationException("Confidence thresholds must be in (0,1].");
        if (Pipeline.RasterDpi is < 150 or > 400 || Pipeline.MaxRasterPages is < 1 or > 50 || Pipeline.MaxRasterPixels is < 1000000 or > 40000000) throw new InvalidOperationException("Invalid raster safety limits.");
        if (Pipeline.QueryMode is not ("Off" or "Fallback" or "Always")) throw new InvalidOperationException("QueryMode must be Off, Fallback, or Always.");
        if (Pipeline.AmountTolerance < 0) throw new InvalidOperationException("AmountTolerance cannot be negative.");
    }
    // Exclude credentials and machine paths. Key rotation and relocating the workspace preserve resumption.
    public string Fingerprint()
    {
        var node = JsonSerializer.SerializeToNode(DocumentIntelligence, JsonOptions)!.AsObject(); node.Remove("ApiKey"); node.Remove("Endpoint");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pipeline-v3|" + ApiVersion + "|" + node.ToJsonString() + JsonSerializer.Serialize(Pipeline, JsonOptions))));
    }
    public string PublicJson()
    {
        var node = JsonSerializer.SerializeToNode(this, JsonOptions)!; node["DocumentIntelligence"]!["ApiKey"] = "[REDACTED]";
        return node.ToJsonString(JsonOptions);
    }
}
public sealed class DocumentIntelligenceOptions
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ModelId { get; set; } = "prebuilt-invoice";
    public string CustomModelId { get; set; } = "";
    public string Locale { get; set; } = "";
    public string Pages { get; set; } = "";
    public string[] QueryFields { get; set; } = ["VendorName", "VendorAddress", "VendorTaxId", "CustomerName", "CustomerAddress", "InvoiceId", "PurchaseOrder", "InvoiceDate", "DueDate", "SubTotal", "TotalTax", "InvoiceTotal", "AmountDue", "Currency", "PaymentTerms"];
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; } = 5;
    public int PollIntervalMilliseconds { get; set; } = 2000;
}
public sealed class BatchOptions
{
    public string InputDirectory { get; set; } = "invoices";
    public string FilePattern { get; set; } = "*.pdf";
    public bool Recursive { get; set; } = true;
    // Process the full folder unless the operator explicitly selects a smaller experiment.
    public int MaxDocuments { get; set; } = int.MaxValue;
    public int Concurrency { get; set; } = 2;
    public double RequestsPerSecond { get; set; } = 2;
    // Optional per-batch submission ceiling; no small pilot budget is imposed by default.
    public int MaxAnalyzeRequests { get; set; } = int.MaxValue;
    public bool SkipPreviouslyCompleted { get; set; } = true;
    public bool ContinueOnFileError { get; set; } = true;
}
public sealed class PipelineOptions
{
    public string[] TargetFields { get; set; } = new DocumentIntelligenceOptions().QueryFields;
    public string[] RequiredFields { get; set; } = ["VendorName", "InvoiceId", "InvoiceDate", "InvoiceTotal", "Currency"];
    public double AcceptanceConfidence { get; set; } = .98;
    public Dictionary<string, double> FieldThresholds { get; set; } = new();
    public string QueryMode { get; set; } = "Fallback";
    public bool HighResolutionFallback { get; set; } = true;
    public bool RasterFallback { get; set; }
    public bool RasterGrayscale { get; set; }
    public int RasterDpi { get; set; } = 300;
    public int MaxRasterPages { get; set; } = 10;
    public int MaxRasterPixels { get; set; } = 25000000;
    public int MaxPasses { get; set; } = 3;
    public double MinimumOcrConfidence { get; set; } = .85;
    public decimal AmountTolerance { get; set; } = .02m;
    public bool CheckTotals { get; set; } = true;
    public bool CheckDateOrder { get; set; } = true;
}
public sealed class StorageOptions
{
    public string DatabasePath { get; set; } = "data/invoices.db";
    public string ExportDirectory { get; set; } = "exports";
    public string ArtifactDirectory { get; set; } = "data/artifacts";
}
