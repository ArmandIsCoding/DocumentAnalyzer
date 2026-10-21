using System.Globalization;
using System.Text;

namespace InvoiceExplorer.Core;

public static class CsvExport
{
    public static string Create(long runId, string fileName, ParsedExtraction result)
    {
        var csv = new StringBuilder("RunId,FileName,Category,Path,Type,Value,Content,ConfidencePercent,IsLeaf,Source\r\n");
        foreach (var f in result.Fields)
        {
            var cells = new[] { runId.ToString(CultureInfo.InvariantCulture), fileName, f.Category, f.Path, f.Type,
                f.Value, f.Content, f.Confidence.HasValue ? (f.Confidence.Value * 100).ToString("0.####", CultureInfo.InvariantCulture) : "",
                f.IsLeaf ? "true" : "false", f.Source };
            csv.AppendJoin(',', cells.Select(Escape)).Append("\r\n");
        }
        return csv.ToString();
    }

    public static string Escape(string value)
    {
        // Preserve the original in SQLite/JSON; prevent spreadsheet formula execution in exports.
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || value.StartsWith('\t') || value.StartsWith('\r'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
