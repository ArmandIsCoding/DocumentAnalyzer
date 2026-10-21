using Microsoft.Data.Sqlite;

namespace InvoiceExplorer.Core;

public sealed partial class InvoiceRepository(string databasePath)
{
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, ForeignKeys = true, DefaultTimeout = 30
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Invoices (
                Id INTEGER PRIMARY KEY, Hash TEXT NOT NULL UNIQUE,
                FileName TEXT NOT NULL, IsDemo INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS ExtractionRuns (
                Id INTEGER PRIMARY KEY, InvoiceId INTEGER NOT NULL REFERENCES Invoices(Id),
                FileName TEXT NOT NULL, StartedUtc TEXT NOT NULL, FinishedUtc TEXT,
                Status TEXT NOT NULL, Provider TEXT NOT NULL, ModelId TEXT NOT NULL,
                ApiVersion TEXT NOT NULL, IsDemo INTEGER NOT NULL, Pages TEXT NOT NULL,
                OverallConfidence REAL, DocumentConfidence REAL,
                ConfidenceCount INTEGER NOT NULL DEFAULT 0, LeafCount INTEGER NOT NULL DEFAULT 0,
                Error TEXT, OperationUrl TEXT, RawJson TEXT, FullText TEXT, Csv TEXT);
            CREATE INDEX IF NOT EXISTS IX_Runs_Invoice ON ExtractionRuns(InvoiceId, StartedUtc, Id);
            CREATE TABLE IF NOT EXISTS ExtractedFields (
                Id INTEGER PRIMARY KEY, RunId INTEGER NOT NULL REFERENCES ExtractionRuns(Id),
                Category TEXT NOT NULL, Path TEXT NOT NULL, Type TEXT NOT NULL,
                Value TEXT NOT NULL, Content TEXT NOT NULL, Confidence REAL,
                IsLeaf INTEGER NOT NULL, Source TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Fields_Run ON ExtractedFields(RunId);
            """;
        cmd.ExecuteNonQuery();
        InitializePipeline(db);
    }

    public long StartRun(string hash, string fileName, Settings settings, bool demo = false)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        Execute(db, tx, "INSERT INTO Invoices(Hash,FileName,IsDemo) VALUES($hash,$file,$demo) ON CONFLICT(Hash) DO NOTHING;",
            ("$hash", hash), ("$file", fileName), ("$demo", demo));
        using var cmd = Command(db, tx, """
            INSERT INTO ExtractionRuns(InvoiceId,FileName,StartedUtc,Status,Provider,ModelId,ApiVersion,IsDemo,Pages)
            VALUES((SELECT Id FROM Invoices WHERE Hash=$hash),$file,$time,'Running',$provider,$model,$api,$demo,$pages)
            RETURNING Id;
            """, ("$hash", hash), ("$file", fileName), ("$time", DateTimeOffset.UtcNow.ToString("O")),
            ("$provider", demo ? "Demo" : settings.Provider), ("$model", settings.ModelId),
            ("$api", settings.ApiVersion), ("$demo", demo), ("$pages", settings.PageRange));
        var id = (long)cmd.ExecuteScalar()!;
        tx.Commit();
        return id;
    }

    public void SetOperation(long id, string url)
    {
        using var db = Open();
        Execute(db, null, "UPDATE ExtractionRuns SET OperationUrl=$url WHERE Id=$id;", ("$url", url), ("$id", id));
    }

    public string Complete(long id, string fileName, string rawJson, ParsedExtraction parsed)
    {
        var csv = CsvExport.Create(id, fileName, parsed);
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var field in parsed.Fields)
            Execute(db, tx, """
                INSERT INTO ExtractedFields(RunId,Category,Path,Type,Value,Content,Confidence,IsLeaf,Source)
                VALUES($run,$category,$path,$type,$value,$content,$confidence,$leaf,$source);
                """, ("$run", id), ("$category", field.Category), ("$path", field.Path), ("$type", field.Type),
                ("$value", field.Value), ("$content", field.Content), ("$confidence", field.Confidence),
                ("$leaf", field.IsLeaf), ("$source", field.Source));
        Execute(db, tx, """
            UPDATE ExtractionRuns SET Status='Succeeded', FinishedUtc=$time, RawJson=$json, FullText=$text,
            Csv=$csv, OverallConfidence=$overall, DocumentConfidence=$document,
            ConfidenceCount=$count, LeafCount=$leaves WHERE Id=$id;
            """, ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$json", rawJson), ("$text", parsed.FullText),
            ("$csv", csv), ("$overall", parsed.OverallConfidence), ("$document", parsed.DocumentConfidence),
            ("$count", parsed.ConfidenceCount), ("$leaves", parsed.LeafCount), ("$id", id));
        tx.Commit();
        return csv;
    }

    public void Fail(long id, string message, string status = "Failed")
    {
        using var db = Open();
        Execute(db, null, "UPDATE ExtractionRuns SET Status=$status, Error=$error, FinishedUtc=$time WHERE Id=$id;",
            ("$id", id), ("$error", message), ("$status", status), ("$time", DateTimeOffset.UtcNow.ToString("O")));
    }

    public IReadOnlyList<InvoiceSummary> ListInvoices()
    {
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT i.Id,i.FileName,i.Hash,i.IsDemo,
                (SELECT COUNT(*) FROM ExtractionRuns r WHERE r.InvoiceId=i.Id),
                latest.StartedUtc, latest.Status, latest.OverallConfidence
            FROM Invoices i JOIN ExtractionRuns latest ON latest.Id=(
                SELECT r.Id FROM ExtractionRuns r WHERE r.InvoiceId=i.Id ORDER BY r.StartedUtc DESC,r.Id DESC LIMIT 1)
            ORDER BY latest.StartedUtc DESC,latest.Id DESC;
            """);
        using var reader = cmd.ExecuteReader();
        var list = new List<InvoiceSummary>();
        while (reader.Read())
            list.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetString(6), Number(reader, 7)));
        return list;
    }

    public IReadOnlyList<ExtractionRun> ListRuns(long invoiceId)
    {
        using var db = Open();
        using var cmd = Command(db, null, $"SELECT {RunColumns} FROM ExtractionRuns WHERE InvoiceId=$id ORDER BY StartedUtc,Id;", ("$id", invoiceId));
        using var reader = cmd.ExecuteReader();
        var list = new List<ExtractionRun>();
        while (reader.Read()) list.Add(ReadRun(reader));
        return list;
    }

    public RunDetails? GetRun(long id)
    {
        using var db = Open();
        ExtractionRun run;
        string text, json, csv;
        using (var cmd = Command(db, null, $"SELECT {RunColumns},FullText,RawJson,Csv FROM ExtractionRuns WHERE Id=$id;", ("$id", id)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return null;
            run = ReadRun(reader);
            text = String(reader, 17) ?? "";
            json = String(reader, 18) ?? "";
            csv = String(reader, 19) ?? "";
        }
        using var fieldsCmd = Command(db, null, """
            SELECT Category,Path,Type,Value,Content,Confidence,IsLeaf,Source FROM ExtractedFields WHERE RunId=$id ORDER BY Id;
            """, ("$id", id));
        using var fieldsReader = fieldsCmd.ExecuteReader();
        var fields = new List<ExtractedField>();
        while (fieldsReader.Read())
            fields.Add(new(fieldsReader.GetString(0), fieldsReader.GetString(1), fieldsReader.GetString(2),
                fieldsReader.GetString(3), fieldsReader.GetString(4), Number(fieldsReader, 5),
                fieldsReader.GetBoolean(6), fieldsReader.GetString(7)));
        return new(run, fields, text, json, csv);
    }

    private const string RunColumns = """
        Id,InvoiceId,FileName,StartedUtc,FinishedUtc,Status,Provider,ModelId,ApiVersion,
        IsDemo,Pages,OverallConfidence,DocumentConfidence,ConfidenceCount,LeafCount,Error,OperationUrl
        """;
    private static ExtractionRun ReadRun(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2),
        r.GetString(3), String(r, 4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
        r.GetBoolean(9), r.GetString(10), Number(r, 11), Number(r, 12), r.GetInt32(13), r.GetInt32(14), String(r, 15), String(r, 16));
    private static double? Number(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetDouble(index);
    private static string? String(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        using var cmd = Command(db, tx, sql, values);
        cmd.ExecuteNonQuery();
    }
}
