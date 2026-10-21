using System.Globalization;
using System.Text;
using System.Text.Json;

namespace InvoiceExplorer.Core;

/// <summary>Measures predictions against separately reviewed labels. Never trains or tunes on the Test split.</summary>
public static class Evaluation
{
    public static EvaluationReport Run(InvoiceRepository repo, string split = "Test", bool demo = false)
    {
        var latest = repo.ListExecutions(demo: demo).ToDictionary(e => e.Hash);
        var labels = repo.GetLabels().Where(l => l.Split == split).ToArray();
        var rows = new List<EvaluationRow>();
        foreach (var group in labels.GroupBy(l => l.Hash))
        {
            if (!latest.TryGetValue(group.Key, out var summary)) continue;
            var execution = repo.GetExecution(summary.Id)!;
            // Failed latest runs remain in the denominator as missing predictions, preventing survivorship bias.
            var original = repo.GetAttempts(summary.Id).FirstOrDefault(a => a.Strategy == "Original" && a.Status == "Succeeded");
            var baseline = original is null ? [] : QualityEngine.ReadCandidates(repo.GetRun(original.RunId)!.RawJson, original.RunId, "Original", group.Select(l => l.Field));
            foreach (var label in group)
            {
                var field = execution.Decision?.Fields.FirstOrDefault(f => f.Name == label.Field);
                var native = baseline.FirstOrDefault(f => f.Name == label.Field && f.DocumentIndex == 0)?.Value;
                bool Correct(string? value) => label.Present ? value is not null && QualityEngine.Normalize(label.Field, value) == QualityEngine.Normalize(label.Field, label.ExpectedValue) : string.IsNullOrWhiteSpace(value);
                rows.Add(new(group.Key, label.Field, label.ExpectedValue, field?.Value, native, label.Present, Correct(field?.Value), Correct(native), field?.State == "Accepted" && execution.Status == "Ready", field?.Confidence));
            }
        }
        var present = rows.Count(r => r.ExpectedPresent); var correct = rows.Count(r => r.ExpectedPresent && r.Correct);
        var baselineCorrect = rows.Count(r => r.ExpectedPresent && r.BaselineCorrect);
        var accepted = rows.Count(r => r.Accepted); var acceptedCorrect = rows.Count(r => r.Accepted && r.Correct);
        return new(split, rows.Count, rows.Select(r => r.Hash).Distinct().Count(), present, correct, baselineCorrect,
            rows.Count(r => !r.ExpectedPresent && r.Correct), rows.Count(r => !r.ExpectedPresent), accepted, acceptedCorrect,
            Ratio(correct, present), Ratio(baselineCorrect, present), Ratio(acceptedCorrect, accepted), WilsonLower(correct, present), rows);
    }
    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : numerator / (double)denominator;
    public static double? WilsonLower(int successes, int total)
    {
        if (total == 0) return null;
        const double z = 1.96; var p = successes / (double)total;
        return (p + z * z / (2 * total) - z * Math.Sqrt(p * (1 - p) / total + z * z / (4 * total * total))) / (1 + z * z / total);
    }
}

public static class BusinessExport
{
    /// <summary>One row per invoice for business spreadsheets; the long export retains field-level evidence.</summary>
    public static string WideCsv(InvoiceRepository repo, string? batch = null, bool demo = false)
    {
        var rows = repo.ListExecutions(batch, demo: demo).Select(s => (Summary: s, Execution: repo.GetExecution(s.Id)!)).ToArray();
        var names = rows.SelectMany(r => r.Execution.Decision?.Fields.Select(f => f.Name) ?? []).Distinct().Order(StringComparer.Ordinal).ToArray();
        var header = new[] { "ExecutionId", "BatchId", "SourceFile", "Status", "CoveragePercent", "MeanAzureConfidencePercent", "Error" }
            .Concat(names.SelectMany(n => new[] { n, n + "_ConfidencePercent", n + "_State" }));
        var result = new StringBuilder().AppendJoin(',', header.Select(CsvExport.Escape)).Append("\r\n");
        foreach (var (s, e) in rows)
        {
            var cells = new List<string> { s.Id.ToString(CultureInfo.InvariantCulture), s.BatchId, s.SourcePath, s.Status, Percent(s.Coverage), Percent(s.MeanConfidence), e.Error ?? "" };
            foreach (var name in names)
            { var f = e.Decision?.Fields.FirstOrDefault(f => f.Name == name); cells.Add(f?.Value ?? ""); cells.Add(Percent(f?.Confidence)); cells.Add(f?.State ?? "No result"); }
            result.AppendJoin(',', cells.Select(CsvExport.Escape)).Append("\r\n");
        }
        return result.ToString();
    }
    public static string Csv(InvoiceRepository repo, string? batch = null, bool demo = false)
    {
        var builder = new StringBuilder("ExecutionId,BatchId,SourceFile,Status,Field,Value,AzureConfidencePercent,FieldState,Required,SelectedRunId,CoveragePercent,MeanAzureConfidencePercent,Reason\r\n");
        foreach (var summary in repo.ListExecutions(batch, demo: demo))
        {
            var execution = repo.GetExecution(summary.Id)!;
            var fields = execution.Decision?.Fields ?? [new FieldDecision("", null, null, execution.Status, false, 0, null, execution.Error ?? "Processing has not completed.", [])];
            foreach (var field in fields)
                builder.AppendJoin(',', new[] { execution.Id.ToString(CultureInfo.InvariantCulture), execution.BatchId, execution.SourcePath, execution.Status, field.Name, field.Value ?? "", Percent(field.Confidence), field.State, field.Required.ToString(), field.SelectedRunId?.ToString(CultureInfo.InvariantCulture) ?? "", Percent(summary.Coverage), Percent(summary.MeanConfidence), field.Reason }.Select(CsvExport.Escape)).Append("\r\n");
        }
        return builder.ToString();
    }
    public static string EvaluationCsv(EvaluationReport report)
    {
        var builder = new StringBuilder("Hash,Field,Expected,Selected,Baseline,ExpectedPresent,Correct,BaselineCorrect,Accepted,AzureConfidencePercent\r\n");
        foreach (var row in report.Rows)
            builder.AppendJoin(',', new[] { row.Hash, row.Field, row.Expected ?? "", row.Actual ?? "", row.Baseline ?? "", row.ExpectedPresent.ToString(), row.Correct.ToString(), row.BaselineCorrect.ToString(), row.Accepted.ToString(), Percent(row.Confidence) }.Select(CsvExport.Escape)).Append("\r\n");
        return builder.ToString();
    }
    private static string Percent(double? value) => value.HasValue ? (value.Value * 100).ToString("0.####", CultureInfo.InvariantCulture) : "";
}
