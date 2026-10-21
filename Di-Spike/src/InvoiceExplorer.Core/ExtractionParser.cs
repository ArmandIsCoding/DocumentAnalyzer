using System.Text.Json;

namespace InvoiceExplorer.Core;

public static class ExtractionParser
{
    public static ParsedExtraction Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var fields = new List<ExtractedField>();
        var texts = new List<string>();
        var documentScores = new List<double>();
        if (root.TryGetProperty("analyzeResult", out var analysis))
        {
            texts.Add(Text(analysis, "content"));
            if (analysis.TryGetProperty("documents", out var documents))
            {
                var i = 0;
                foreach (var item in documents.EnumerateArray())
                {
                    if (Confidence(item) is { } score) documentScores.Add(score);
                    AddFields(item, $"Documents[{i++}]", fields);
                }
            }
            AddDetails(analysis, "Analysis", fields);
        }
        else if (root.TryGetProperty("result", out var result) && result.TryGetProperty("contents", out var contents))
        {
            var i = 0;
            foreach (var item in contents.EnumerateArray())
            {
                var prefix = $"Contents[{i++}]";
                texts.Add(Text(item, "markdown"));
                AddFields(item, prefix, fields);
                AddDetails(item, prefix, fields);
            }
        }
        else throw new InvalidDataException("The response contains neither analyzeResult nor result.contents.");
        return new(fields, string.Join("\n\n", texts), documentScores.Count == 0 ? null : documentScores.Average());
    }

    private static void AddFields(JsonElement item, string prefix, List<ExtractedField> fields)
    {
        if (item.TryGetProperty("fields", out var values))
            foreach (var prop in values.EnumerateObject()) Walk(prop.Value, $"{prefix}.{prop.Name}", fields);
    }

    private static void Walk(JsonElement node, string path, List<ExtractedField> fields)
    {
        var type = Text(node, "type");
        var value = node.EnumerateObject().FirstOrDefault(p => p.Name.StartsWith("value", StringComparison.Ordinal));
        var container = value.Name is "valueObject" or "valueArray";
        fields.Add(new("Field", path, type, value.Value.ValueKind == JsonValueKind.Undefined ? Text(node, "content") : Display(value.Value),
            Text(node, "content"), Confidence(node), !container, Source(node)));
        if (value.Name == "valueObject")
            foreach (var prop in value.Value.EnumerateObject()) Walk(prop.Value, $"{path}.{prop.Name}", fields);
        if (value.Name == "valueArray")
        {
            var i = 0;
            foreach (var item in value.Value.EnumerateArray()) Walk(item, $"{path}[{i++}]", fields);
        }
    }

    private static void AddDetails(JsonElement result, string prefix, List<ExtractedField> fields)
    {
        if (result.TryGetProperty("keyValuePairs", out var pairs))
        {
            var i = 0;
            foreach (var pair in pairs.EnumerateArray())
            {
                var key = pair.TryGetProperty("key", out var k) ? Text(k, "content") : "";
                var val = pair.TryGetProperty("value", out var v) ? Text(v, "content") : "";
                fields.Add(new("KeyValue", $"{prefix}.KeyValuePairs[{i++}].{key}", "string", val, val, Confidence(pair), true, Source(pair)));
            }
        }
        if (result.TryGetProperty("tables", out var tables))
        {
            var i = 0;
            foreach (var table in tables.EnumerateArray())
            {
                if (table.TryGetProperty("cells", out var cells))
                    foreach (var cell in cells.EnumerateArray())
                        fields.Add(new("Table", $"{prefix}.Tables[{i}][{Text(cell, "rowIndex")},{Text(cell, "columnIndex")}]",
                            "string", Text(cell, "content"), Text(cell, "content"), Confidence(cell), true, Source(cell)));
                i++;
            }
        }
        if (result.TryGetProperty("pages", out var pages))
            foreach (var page in pages.EnumerateArray())
            {
                var pageNumber = Text(page, "pageNumber");
                foreach (var collection in new[] { "words", "selectionMarks", "barcodes", "formulas" })
                    if (page.TryGetProperty(collection, out var words))
                    {
                        var i = 0;
                        foreach (var word in words.EnumerateArray())
                        {
                            var text = Text(word, "content");
                            if (text.Length == 0) text = Text(word, "value");
                            if (text.Length == 0) text = Text(word, "state");
                            fields.Add(new("OCR", $"{prefix}.Pages[{pageNumber}].{collection}[{i++}]", collection,
                                text, text, Confidence(word), true, Source(word)));
                        }
                    }
            }
    }

    private static string Source(JsonElement node)
    {
        foreach (var name in new[] { "source", "boundingRegions", "polygon", "spans" })
            if (node.TryGetProperty(name, out var source)) return Display(source);
        return "";
    }
    private static double? Confidence(JsonElement node) =>
        node.TryGetProperty("confidence", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var score) &&
        double.IsFinite(score) && score is >= 0 and <= 1 ? score : null;
    private static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => value.GetRawText()
    };
    private static string Text(JsonElement node, string name) => node.TryGetProperty(name, out var value) ? Display(value) : "";
}

