using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace InvoiceExplorer.Core;

/// <summary>Additive schema: existing invoices and raw extraction runs remain unchanged.</summary>
public sealed partial class InvoiceRepository
{
    private static void InitializePipeline(SqliteConnection db)
    {
        using var version = Command(db, null, "PRAGMA user_version;");
        if (Convert.ToInt32(version.ExecuteScalar()) > 2) throw new InvalidOperationException("This database was created by a newer application. Use that version; no downgrade was performed.");
        Execute(db, null, """
        CREATE TABLE IF NOT EXISTS Batches(
          Id TEXT PRIMARY KEY, StartedUtc TEXT NOT NULL, FinishedUtc TEXT, Status TEXT NOT NULL,
          Fingerprint TEXT NOT NULL, ConfigJson TEXT NOT NULL, Requests INTEGER NOT NULL DEFAULT 0, IsDemo INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS PipelineExecutions(
          Id INTEGER PRIMARY KEY, BatchId TEXT NOT NULL REFERENCES Batches(Id), Hash TEXT NOT NULL,
          SourcePath TEXT NOT NULL, ArtifactPath TEXT NOT NULL DEFAULT '', StartedUtc TEXT NOT NULL,
          Status TEXT NOT NULL, Fingerprint TEXT NOT NULL, IsDemo INTEGER NOT NULL DEFAULT 0,
          ProfileJson TEXT, DecisionJson TEXT, Error TEXT, MeanConfidence REAL, Coverage REAL NOT NULL DEFAULT 0,
          AcceptedFields INTEGER NOT NULL DEFAULT 0, TargetCount INTEGER NOT NULL DEFAULT 0, Vendor TEXT NOT NULL DEFAULT '',
          UNIQUE(BatchId,Hash));
        CREATE INDEX IF NOT EXISTS IX_Executions_Hash ON PipelineExecutions(Hash,Id);
        CREATE INDEX IF NOT EXISTS IX_Executions_Batch ON PipelineExecutions(BatchId,Status);
        CREATE TABLE IF NOT EXISTS PipelineAttempts(
          RunId INTEGER PRIMARY KEY REFERENCES ExtractionRuns(Id), ExecutionId INTEGER NOT NULL REFERENCES PipelineExecutions(Id),
          Strategy TEXT NOT NULL, RequestJson TEXT NOT NULL, UNIQUE(ExecutionId,Strategy));
        CREATE TABLE IF NOT EXISTS PipelineFields(
          ExecutionId INTEGER NOT NULL REFERENCES PipelineExecutions(Id), Name TEXT NOT NULL, Value TEXT, Confidence REAL,
          State TEXT NOT NULL, Required INTEGER NOT NULL, SelectedRunId INTEGER, PRIMARY KEY(ExecutionId,Name));
        CREATE TABLE IF NOT EXISTS BatchSkips(BatchId TEXT NOT NULL REFERENCES Batches(Id), SourcePath TEXT NOT NULL, Reason TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ReviewLabels(
          Id INTEGER PRIMARY KEY, Hash TEXT NOT NULL, Field TEXT NOT NULL, ExpectedValue TEXT, Present INTEGER NOT NULL,
          Reviewer TEXT NOT NULL, Split TEXT NOT NULL, Notes TEXT NOT NULL, CreatedUtc TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_Labels_HashField ON ReviewLabels(Hash,Field,Id);
        PRAGMA user_version=2;
        """);
    }

    public string CreateBatch(Settings settings, bool demo = false)
    {
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        using var db = Open();
        Execute(db, null, "INSERT INTO Batches(Id,StartedUtc,Status,Fingerprint,ConfigJson,IsDemo) VALUES($id,$time,'Planning',$fp,$config,$demo);",
            ("$id", id), ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$fp", settings.Fingerprint()), ("$config", settings.PublicJson()), ("$demo", demo));
        return id;
    }
    public long AddExecution(string batch, string hash, string sourcePath, string fingerprint, bool demo = false)
    {
        using var db = Open();
        using var cmd = Command(db, null, """
            INSERT INTO PipelineExecutions(BatchId,Hash,SourcePath,StartedUtc,Status,Fingerprint,IsDemo)
            VALUES($batch,$hash,$path,$time,'Pending',$fp,$demo) RETURNING Id;
            """, ("$batch", batch), ("$hash", hash), ("$path", sourcePath), ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$fp", fingerprint), ("$demo", demo));
        return (long)cmd.ExecuteScalar()!;
    }
    public void AddSkip(string batch, string path, string reason)
    { using var db = Open(); Execute(db, null, "INSERT INTO BatchSkips VALUES($batch,$path,$reason);", ("$batch", batch), ("$path", path), ("$reason", reason)); }
    public int SkipCount(string batch)
    { using var db = Open(); using var cmd = Command(db, null, "SELECT COUNT(*) FROM BatchSkips WHERE BatchId=$id;", ("$id", batch)); return Convert.ToInt32(cmd.ExecuteScalar()); }
    public bool WasCompleted(string hash, string fingerprint)
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT COUNT(*) FROM PipelineExecutions WHERE Hash=$hash AND Fingerprint=$fp AND Status IN ('Ready','Review') AND IsDemo=0;", ("$hash", hash), ("$fp", fingerprint));
        return (long)cmd.ExecuteScalar()! > 0;
    }
    public void SetBatchStatus(string id, string status)
    {
        using var db = Open(); Execute(db, null, "UPDATE Batches SET Status=$status,FinishedUtc=$time WHERE Id=$id;",
            ("$id", id), ("$status", status), ("$time", status == "Running" ? null : DateTimeOffset.UtcNow.ToString("O")));
    }
    public bool ReserveRequest(string batch, int limit)
    {
        using var db = Open(); using var cmd = Command(db, null, "UPDATE Batches SET Requests=Requests+1 WHERE Id=$id AND Requests<$limit;", ("$id", batch), ("$limit", limit));
        return cmd.ExecuteNonQuery() == 1;
    }
    public long StartAttempt(PipelineExecution execution, AnalysisRequest request, Settings settings)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        Execute(db, tx, "INSERT INTO Invoices(Hash,FileName,IsDemo) VALUES($hash,$file,$demo) ON CONFLICT(Hash) DO NOTHING;", ("$hash", execution.Hash), ("$file", execution.SourcePath), ("$demo", execution.IsDemo));
        using var cmd = Command(db, tx, """
            INSERT INTO ExtractionRuns(InvoiceId,FileName,StartedUtc,Status,Provider,ModelId,ApiVersion,IsDemo,Pages)
            VALUES((SELECT Id FROM Invoices WHERE Hash=$hash),$file,$time,'Prepared','DocumentIntelligence',$model,$api,$demo,$pages) RETURNING Id;
            """, ("$hash", execution.Hash), ("$file", execution.SourcePath), ("$time", DateTimeOffset.UtcNow.ToString("O")),
            ("$model", request.ModelId), ("$api", settings.ApiVersion), ("$demo", execution.IsDemo), ("$pages", request.Pages));
        var id = (long)cmd.ExecuteScalar()!;
        Execute(db, tx, "INSERT INTO PipelineAttempts VALUES($run,$execution,$strategy,$request);", ("$run", id), ("$execution", execution.Id), ("$strategy", request.Strategy), ("$request", JsonSerializer.Serialize(request)));
        tx.Commit(); return id;
    }
    public void MarkSubmitting(long runId)
    { using var db = Open(); Execute(db, null, "UPDATE ExtractionRuns SET Status='Running',Error=NULL,FinishedUtc=NULL WHERE Id=$id;", ("$id", runId)); }
    public void SetExecutionProfile(long id, DocumentProfile profile, string artifact)
    {
        using var db = Open(); Execute(db, null, "UPDATE PipelineExecutions SET ProfileJson=$profile,ArtifactPath=$path WHERE Id=$id;", ("$profile", JsonSerializer.Serialize(profile)), ("$path", artifact), ("$id", id));
    }
    public void SetExecutionStatus(long id, string status, string? error = null)
    { using var db = Open(); Execute(db, null, "UPDATE PipelineExecutions SET Status=$status,Error=$error WHERE Id=$id;", ("$id", id), ("$status", status), ("$error", error)); }
    public void SaveDecision(long id, QualityDecision decision)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        Execute(db, tx, "DELETE FROM PipelineFields WHERE ExecutionId=$id;", ("$id", id));
        foreach (var f in decision.Fields)
            Execute(db, tx, "INSERT INTO PipelineFields VALUES($id,$name,$value,$confidence,$state,$required,$run);", ("$id", id), ("$name", f.Name), ("$value", f.Value), ("$confidence", f.Confidence), ("$state", f.State), ("$required", f.Required), ("$run", f.SelectedRunId));
        Execute(db, tx, """
            UPDATE PipelineExecutions SET Status=$status,DecisionJson=$json,MeanConfidence=$mean,Coverage=$coverage,
            AcceptedFields=$accepted,TargetCount=$count,Vendor=$vendor,Error=NULL WHERE Id=$id;
            """, ("$id", id), ("$status", decision.Status), ("$json", JsonSerializer.Serialize(decision)), ("$mean", decision.MeanConfidence), ("$coverage", decision.Coverage),
            ("$accepted", decision.AcceptedFields), ("$count", decision.Fields.Count), ("$vendor", decision.Fields.FirstOrDefault(f => f.Name == "VendorName")?.Value ?? ""));
        tx.Commit();
    }
    public PipelineExecution? GetExecution(long id)
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT Id,BatchId,Hash,SourcePath,ArtifactPath,StartedUtc,Status,Fingerprint,IsDemo,ProfileJson,DecisionJson,Error FROM PipelineExecutions WHERE Id=$id;", ("$id", id));
        using var r = cmd.ExecuteReader();
        return !r.Read() ? null : new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetBoolean(8),
            r.IsDBNull(9) ? null : JsonSerializer.Deserialize<DocumentProfile>(r.GetString(9)), r.IsDBNull(10) ? null : JsonSerializer.Deserialize<QualityDecision>(r.GetString(10)), String(r, 11));
    }
    public IReadOnlyList<PipelineAttempt> GetAttempts(long executionId)
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT a.RunId,a.ExecutionId,a.Strategy,a.RequestJson,r.Status,r.OperationUrl,r.Error FROM PipelineAttempts a JOIN ExtractionRuns r ON r.Id=a.RunId WHERE a.ExecutionId=$id ORDER BY a.RunId;", ("$id", executionId));
        using var r = cmd.ExecuteReader(); var list = new List<PipelineAttempt>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), String(r, 5), String(r, 6)));
        return list;
    }
    public IReadOnlyList<ExecutionSummary> ListExecutions(string? batch = null, bool latestOnly = true, bool demo = false)
    {
        using var db = Open(); using var cmd = Command(db, null, """
            SELECT e.Id,e.BatchId,e.Hash,e.SourcePath,e.StartedUtc,e.Status,e.MeanConfidence,e.Coverage,e.AcceptedFields,e.TargetCount,e.Vendor,e.IsDemo,
              (SELECT COUNT(*) FROM PipelineAttempts a WHERE a.ExecutionId=e.Id)
            FROM PipelineExecutions e WHERE ($batch IS NULL OR e.BatchId=$batch) AND e.IsDemo=$demo
              AND ($latest=0 OR e.Id=(SELECT MAX(x.Id) FROM PipelineExecutions x WHERE x.Hash=e.Hash AND x.IsDemo=e.IsDemo AND ($batch IS NULL OR x.BatchId=$batch)))
            ORDER BY e.Id DESC;
            """, ("$batch", batch), ("$latest", latestOnly), ("$demo", demo));
        using var r = cmd.ExecuteReader(); var list = new List<ExecutionSummary>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Number(r, 6), r.GetDouble(7), r.GetInt32(8), r.GetInt32(9), r.GetString(10), r.GetBoolean(11), r.GetInt32(12)));
        return list;
    }
    public IReadOnlyList<BatchSummary> ListBatches()
    {
        using var db = Open(); using var cmd = Command(db, null, """
            SELECT b.Id,b.StartedUtc,b.FinishedUtc,b.Status,b.Fingerprint,COUNT(e.Id),
              SUM(CASE WHEN e.Status='Ready' THEN 1 ELSE 0 END), SUM(CASE WHEN e.Status='Review' THEN 1 ELSE 0 END),
              SUM(CASE WHEN e.Status IN ('Failed','Ambiguous') THEN 1 ELSE 0 END),
              SUM(CASE WHEN e.Status IN ('Pending','Processing','Interrupted','BudgetExceeded') THEN 1 ELSE 0 END),b.Requests,b.IsDemo
            FROM Batches b LEFT JOIN PipelineExecutions e ON e.BatchId=b.Id AND e.IsDemo=0
            WHERE b.IsDemo=0 GROUP BY b.Id ORDER BY b.StartedUtc DESC;
            """);
        using var r = cmd.ExecuteReader(); var list = new List<BatchSummary>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), String(r, 2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetBoolean(11)));
        return list;
    }
    public IReadOnlyList<FieldMetric> FieldMetrics(string? batch, bool demo)
    {
        using var db = Open(); using var cmd = Command(db, null, """
            SELECT f.Name,COUNT(*),SUM(CASE WHEN f.Value IS NOT NULL THEN 1 ELSE 0 END),
              SUM(CASE WHEN f.State='Accepted' THEN 1 ELSE 0 END),SUM(CASE WHEN f.State='Conflict' THEN 1 ELSE 0 END),AVG(f.Confidence),COUNT(f.Confidence)
            FROM PipelineFields f JOIN PipelineExecutions e ON e.Id=f.ExecutionId WHERE e.IsDemo=$demo AND ($batch IS NULL OR e.BatchId=$batch)
              AND e.Id=(SELECT MAX(x.Id) FROM PipelineExecutions x WHERE x.Hash=e.Hash AND x.IsDemo=e.IsDemo AND ($batch IS NULL OR x.BatchId=$batch))
            GROUP BY f.Name ORDER BY f.Name;
            """, ("$batch", batch), ("$demo", demo));
        using var r = cmd.ExecuteReader(); var list = new List<FieldMetric>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), Number(r, 5), r.GetInt32(6)));
        return list;
    }
    public void AddLabel(string hash, string field, string? expected, bool present, string reviewer, string split, string notes)
        => ImportLabels([new(hash, field, expected, present, reviewer, split, notes)]);

    /// <summary>Validate the entire import in one transaction. An invoice cannot leak across dataset splits.</summary>
    public void ImportLabels(IEnumerable<LabelEntry> entries)
    {
        var labels = entries.ToArray();
        if (labels.GroupBy(l => (l.Hash, l.Field)).Any(g => g.Count() > 1)) throw new ArgumentException("An import cannot contain duplicate hash/field labels.");
        using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var l in labels)
        {
            if (string.IsNullOrWhiteSpace(l.Reviewer)) throw new ArgumentException("Enter the reviewer's name before saving a label.");
            if (string.IsNullOrWhiteSpace(l.Field)) throw new ArgumentException("Select a target field before saving a label.");
            if (l.Split is not ("Calibration" or "Test")) throw new ArgumentException("Dataset split must be Calibration or Test.");
            if (l.Present && (string.IsNullOrWhiteSpace(l.ExpectedValue) || !QualityEngine.IsValid(l.Field, l.ExpectedValue)))
                throw new ArgumentException($"Enter a valid verified value for {l.Field}. Use ISO dates, decimal amounts, and uppercase currency codes.");
            using var known = Command(db, tx, "SELECT b.ConfigJson FROM PipelineExecutions e JOIN Batches b ON e.BatchId=b.Id WHERE e.Hash=$hash ORDER BY e.Id DESC LIMIT 1;", ("$hash", l.Hash));
            var config = known.ExecuteScalar() as string;
            if (config is null || !JsonSerializer.Deserialize<Settings>(config, Settings.JsonOptions)!.Pipeline.TargetFields.Contains(l.Field)) throw new ArgumentException("Label hash and field must belong to a recorded extraction.");
            using var split = Command(db, tx, "SELECT COUNT(*) FROM ReviewLabels WHERE Hash=$hash AND Split<>$split;", ("$hash", l.Hash), ("$split", l.Split));
            if (Convert.ToInt32(split.ExecuteScalar()) > 0) throw new ArgumentException("This invoice already belongs to a different dataset split. Keep every field and execution of one invoice in the same split.");
            Execute(db, tx, "INSERT INTO ReviewLabels(Hash,Field,ExpectedValue,Present,Reviewer,Split,Notes,CreatedUtc) VALUES($hash,$field,$value,$present,$reviewer,$split,$notes,$time);",
                ("$hash", l.Hash), ("$field", l.Field), ("$value", l.Present ? l.ExpectedValue : null), ("$present", l.Present), ("$reviewer", l.Reviewer.Trim()), ("$split", l.Split), ("$notes", l.Notes), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        }
        tx.Commit();
    }
    public IReadOnlyList<ReviewLabel> GetLabels(string? hash = null, bool history = false)
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT Id,Hash,Field,ExpectedValue,Present,Reviewer,Split,Notes,CreatedUtc FROM ReviewLabels l WHERE ($hash IS NULL OR Hash=$hash) AND ($history=1 OR Id=(SELECT MAX(x.Id) FROM ReviewLabels x WHERE x.Hash=l.Hash AND x.Field=l.Field)) ORDER BY Id DESC;", ("$hash", hash), ("$history", history));
        using var r = cmd.ExecuteReader(); var list = new List<ReviewLabel>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), String(r, 3), r.GetBoolean(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8)));
        return list;
    }
}
