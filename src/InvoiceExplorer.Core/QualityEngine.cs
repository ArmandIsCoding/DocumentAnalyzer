using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InvoiceExplorer.Core;

/// <summary>Evidence-preserving extraction and conservative reconciliation. Never manufactures confidence.</summary>
public static class QualityEngine
{
    private static readonly HashSet<string> Amounts = new(StringComparer.OrdinalIgnoreCase) { "SubTotal", "TotalTax", "InvoiceTotal", "AmountDue" };
    public static IReadOnlyList<FieldCandidate> ReadCandidates(string json, long runId, string strategy, IEnumerable<string> targets)
    {
        using var doc = JsonDocument.Parse(json);
        var candidates = new List<FieldCandidate>();
        if (!doc.RootElement.TryGetProperty("analyzeResult", out var result) || !result.TryGetProperty("documents", out var documents)) return candidates;
        var index = 0;
        foreach (var document in documents.EnumerateArray())
        {
            if (!document.TryGetProperty("fields", out var fields)) { index++; continue; }
            foreach (var name in targets)
            {
                var actualName = name == "PaymentTerms" && !fields.TryGetProperty(name, out _) ? "PaymentTerm" : name;
                if (fields.TryGetProperty(actualName, out var field))
                {
                    var value = Value(field);
                    if (!string.IsNullOrWhiteSpace(value)) candidates.Add(new(name, value, Text(field, "type"), Score(field), runId, strategy, index, Evidence(field), Text(field, "content")));
                }
                // A currency code is an explicit structured attribute, not a separate model prediction.
                // Its confidence stays null; do not borrow the amount's confidence as a currency score.
                else if (name == "Currency")
                    foreach (var moneyName in new[] { "InvoiceTotal", "AmountDue", "SubTotal" })
                        if (fields.TryGetProperty(moneyName, out var money) && money.TryGetProperty("valueCurrency", out var currency) && Text(currency, "currencyCode") is { Length: > 0 } code)
                        { candidates.Add(new(name, code, "currencyCode", null, runId, strategy, index, Evidence(money), Text(money, "content"), true)); break; }
            }
            index++;
        }
        return candidates;
    }
    public static int DocumentCount(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("analyzeResult").TryGetProperty("documents", out var documents) ? documents.GetArrayLength() : 0;
    }
    public static double? OcrConfidence(string json)
    {
        var parsed = ExtractionParser.Parse(json);
        return parsed.Fields.Where(f => f.Category == "OCR" && f.Confidence.HasValue).Select(f => f.Confidence).DefaultIfEmpty(null).Average();
    }
    public static QualityDecision Decide(IEnumerable<FieldCandidate> source, PipelineOptions options, IEnumerable<QualityIssue>? extraIssues = null)
    {
        var all = source.ToList();
        var issues = extraIssues?.ToList() ?? [];
        var decisions = new List<FieldDecision>();
        if (all.Any(c => c.DocumentIndex > 0)) issues.Add(new("MultipleDocuments", "Error", "Multiple invoices were detected. Values from different document indexes are never merged. Split the source document and rerun."));
        foreach (var field in options.TargetFields)
        {
            var candidates = all.Where(c => c.Name == field && c.DocumentIndex == 0).ToList();
            var required = options.RequiredFields.Contains(field, StringComparer.OrdinalIgnoreCase);
            var threshold = options.FieldThresholds.GetValueOrDefault(field, options.AcceptanceConfidence);
            var valid = candidates.Where(c => IsValid(field, c.Value)).ToList();
            var selected = valid.OrderBy(c => c.Derived).ThenByDescending(c => c.Confidence ?? -1).ThenBy(c => c.RunId).FirstOrDefault()
                ?? candidates.FirstOrDefault();
            var distinct = valid.Select(c => Normalize(field, c.Value)).Distinct(StringComparer.Ordinal).Count();
            string state, reason;
            if (selected is null) { state = required ? "Missing" : "NotFound"; reason = required ? "Required field was not extracted." : "Optional field not found; absence has not been verified."; }
            else if (!IsValid(field, selected.Value)) { state = "Invalid"; reason = "The value does not satisfy the field's format rule."; }
            else if (distinct > 1) { state = "Conflict"; reason = "Passes returned different values. The highest-confidence candidate is shown for review; disagreement blocks acceptance."; }
            else if (!selected.Confidence.HasValue) { state = "Unscored"; reason = selected.Derived ? "Explicit currency attribute; Azure did not provide a separate confidence score." : "Azure did not return field confidence."; }
            else if (selected.Confidence < threshold) { state = "LowConfidence"; reason = "Azure confidence is below the configured field threshold."; }
            else { state = "Accepted"; reason = "Meets the configured threshold and format rules. This is not measured accuracy."; }
            decisions.Add(new(field, selected?.Value, selected?.Confidence, state, required, threshold, selected?.RunId, reason, candidates));
        }
        string? Val(string key) => decisions.FirstOrDefault(f => f.Name == key)?.Value;
        if (options.CheckTotals && TryAmount(Val("SubTotal"), out var subtotal) && TryAmount(Val("TotalTax"), out var tax) && TryAmount(Val("InvoiceTotal"), out var total) && Math.Abs(subtotal + tax - total) > options.AmountTolerance)
            issues.Add(new("TotalMismatch", "Warning", "Subtotal + tax differs from invoice total. Discounts, shipping, or other adjustments may explain the difference; verify the source."));
        if (options.CheckDateOrder && DateOnly.TryParseExact(Val("InvoiceDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued) && DateOnly.TryParseExact(Val("DueDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due) && due < issued)
            issues.Add(new("DateOrder", "Warning", "Due date precedes invoice date. Verify the dates against the source."));
        // No zero-filled averages: missing values remain explicit, and coverage is a separate metric.
        var needsReview = decisions.Any(f => f.State is not ("Accepted" or "NotFound")) || issues.Any(i => i.Severity is "Warning" or "Error");
        return new(decisions, issues.Distinct().ToArray(), needsReview ? "Review" : "Ready");
    }
    public static string Normalize(string field, string? value)
    {
        if (value is null) return "";
        if (Amounts.Contains(field) && TryAmount(value, out var amount)) return amount.ToString("G29", CultureInfo.InvariantCulture);
        return Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();
    }
    public static bool IsValid(string field, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (Amounts.Contains(field)) return TryAmount(value, out _);
        if (field is "InvoiceDate" or "DueDate") return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        if (field == "Currency") return Regex.IsMatch(value, "^[A-Z]{3}$");
        return true;
    }
    public static bool TryAmount(string? value, out decimal amount)
    {
        amount = 0;
        // Ambiguous locale-specific strings are reviewed, not silently interpreted as money.
        return value is not null && Regex.IsMatch(value.Trim(), @"^-?\d+(\.\d+)?$") && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }
    private static string Value(JsonElement field)
    {
        if (field.TryGetProperty("valueCurrency", out var money) && money.TryGetProperty("amount", out var amount)) return amount.ToString();
        foreach (var key in new[] { "valueString", "valueDate", "valueNumber", "valueInteger", "valueTime", "valuePhoneNumber", "valueCountryRegion" })
            if (field.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return value.ToString();
        // For addresses, keep the actual document text; serializing the address object would hurt comparison.
        return Text(field, "content");
    }
    private static string Evidence(JsonElement field) => JsonSerializer.Serialize(new { boundingRegions = field.TryGetProperty("boundingRegions", out var r) ? (object)r : null, spans = field.TryGetProperty("spans", out var s) ? (object)s : null });
    private static double? Score(JsonElement f) => f.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var n) && n is >= 0 and <= 1 ? n : null;
    private static string Text(JsonElement f, string key) => f.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}
