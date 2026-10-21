using System.Text.Json.Serialization;

namespace InvoiceExplorer.Core;

public sealed record AnalysisRequest(string Strategy, string ModelId, string[] Features, string[] QueryFields, string Pages, string Locale);
public sealed record DocumentProfile(int Pages, int TextCharacters, int ImageCount, string Kind, string[] Warnings);
public sealed record FieldCandidate(string Name, string Value, string Type, double? Confidence, long RunId,
    string Strategy, int DocumentIndex, string Evidence, string Content, bool Derived = false);
public sealed record FieldDecision(string Name, string? Value, double? Confidence, string State, bool Required,
    double Threshold, long? SelectedRunId, string Reason, IReadOnlyList<FieldCandidate> Candidates);
public sealed record QualityIssue(string Code, string Severity, string Message);
public sealed record QualityDecision(IReadOnlyList<FieldDecision> Fields, IReadOnlyList<QualityIssue> Issues, string Status)
{
    public double? MeanConfidence => Fields.Where(f => f.Value is not null && f.Confidence.HasValue).Select(f => f.Confidence).DefaultIfEmpty(null).Average();
    public double Coverage => Fields.Count == 0 ? 0 : Fields.Count(f => f.Value is not null) / (double)Fields.Count;
    public int AcceptedFields => Fields.Count(f => f.State == "Accepted");
    public int MissingFields => Fields.Count(f => f.Value is null);
}
public sealed record PipelineAttempt(long RunId, long ExecutionId, string Strategy, string RequestJson, string Status, string? OperationUrl, string? Error);
public sealed record PipelineExecution(long Id, string BatchId, string Hash, string SourcePath, string ArtifactPath,
    string StartedUtc, string Status, string Fingerprint, bool IsDemo, DocumentProfile? Profile, QualityDecision? Decision, string? Error);
public sealed record BatchSummary(string Id, string StartedUtc, string? FinishedUtc, string Status, string Fingerprint,
    int Total, int Completed, int Review, int Failed, int Pending, int Requests, bool IsDemo);
public sealed record ExecutionSummary(long Id, string BatchId, string Hash, string SourcePath, string StartedUtc,
    string Status, double? MeanConfidence, double Coverage, int AcceptedFields, int TargetCount, string Vendor, bool IsDemo, int Attempts);
public sealed record FieldMetric(string Name, int Invoices, int Present, int Accepted, int Conflicts, double? MeanConfidence, int Scored);
public sealed record ReviewLabel(long Id, string Hash, string Field, string? ExpectedValue, bool Present, string Reviewer, string Split, string Notes, string CreatedUtc);
public sealed record LabelEntry(string Hash, string Field, string? ExpectedValue, bool Present, string Reviewer, string Split, string Notes);
public sealed record EvaluationRow(string Hash, string Field, string? Expected, string? Actual, string? Baseline,
    bool ExpectedPresent, bool Correct, bool BaselineCorrect, bool Accepted, double? Confidence);
public sealed record EvaluationReport(string Split, int LabeledFields, int LabeledInvoices, int ExpectedPresent,
    int CorrectPresent, int BaselineCorrectPresent, int CorrectAbsent, int AbsentLabels, int AcceptedLabeled, int AcceptedCorrect,
    double? Accuracy, double? BaselineAccuracy, double? AcceptedPrecision, double? WilsonLower95, IReadOnlyList<EvaluationRow> Rows);

/// <summary>One vocabulary for persisted statuses. Review is a completed extraction, not an API failure.</summary>
public static class PipelineStatus
{
    public static bool IsCompleted(string status) => status is "Ready" or "Review";
    public static bool IsTerminal(string status) => IsCompleted(status) || status is "Failed" or "Ambiguous";
}
