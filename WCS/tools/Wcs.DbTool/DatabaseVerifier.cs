using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence;

namespace Wcs.DbTool;

/// <summary>Where the schema of a database stands against the migrations of this build.</summary>
internal enum SchemaState
{
    /// <summary>Every migration of this build is applied, and no other.</summary>
    Current,

    /// <summary>
    /// Some migrations of this build are not applied yet. The host applies them to <c>wcs.db</c> and
    /// to the current month's files when it opens them; it reads past months as they are.
    /// </summary>
    Older,

    /// <summary>The file has migrations this build does not know: it was written by a later version of the WCS.</summary>
    Newer,

    /// <summary>No migration history could be read.</summary>
    Unknown
}

internal sealed record VerifyResult(
    string Path,
    DatabaseKind Kind,
    string Integrity,
    SchemaState Schema,
    string? LastApplied,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Unknown,
    string? Error,
    string? Contents = null)
{
    public bool IntegrityOk => Error == null && Integrity == "ok";

    /// <summary>Sound, and openable by the host of this build (which migrates an older schema forward).</summary>
    public bool Ok => IntegrityOk && Schema is SchemaState.Current or SchemaState.Older;

    public string Describe() =>
        Contents == null ? DescribeState() : $"{DescribeState()}{Environment.NewLine}{"",27}contents: {Contents}";

    private string DescribeState()
    {
        if (Error != null) return $"FAILED   {Error}";
        if (!IntegrityOk) return $"FAILED   integrity_check: {Integrity}";
        return Schema switch
        {
            SchemaState.Current => $"ok       schema current ({LastApplied})",
            SchemaState.Older => $"ok       schema older: {Pending.Count} migration(s) pending, applied when the host opens it for writing (last applied {LastApplied ?? "none"})",
            SchemaState.Newer => $"FAILED   schema newer than this build: {Unknown.Count} unknown migration(s), first {Unknown[0]}; written by a later WCS version, do not open it with these binaries",
            _ => "FAILED   no migration history: not a WCS database"
        };
    }
}

/// <summary>
/// Checks one database file without changing it: <c>PRAGMA integrity_check</c>, then the applied
/// migrations against those of this build.
/// </summary>
internal static class DatabaseVerifier
{
    public static VerifyResult Verify(string path)
    {
        var kind = WcsDatabaseFiles.KindOf(path)
            ?? throw new ArgumentException($"{System.IO.Path.GetFileName(path)} is not a WCS database name (wcs.db, wcs-history-YYYY-MM.db, wcs-plc-YYYY-MM.db)", nameof(path));

        if (!File.Exists(path))
            return new VerifyResult(path, kind, "", SchemaState.Unknown, null, [], [], "file not found");

        try
        {
            using var connection = OpenReadOnly(path);

            var problems = new List<string>();
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA integrity_check;";
                using var reader = check.ExecuteReader();
                while (reader.Read()) problems.Add(reader.GetString(0));
            }
            var integrity = problems.Count == 1 && problems[0] == "ok" ? "ok" : string.Join("; ", problems.Take(5));

            var applied = new List<string>();
            try
            {
                using var history = connection.CreateCommand();
                history.CommandText = "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY MigrationId;";
                using var reader = history.ExecuteReader();
                while (reader.Read()) applied.Add(reader.GetString(0));
            }
            catch (SqliteException)
            {
                return new VerifyResult(path, kind, integrity, SchemaState.Unknown, null, [], [], null);
            }

            var known = KnownMigrations(kind);
            var pending = known.Except(applied, StringComparer.Ordinal).ToList();
            var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
            var state = unknown.Count > 0 ? SchemaState.Newer
                : applied.Count == 0 ? SchemaState.Unknown
                : pending.Count > 0 ? SchemaState.Older
                : SchemaState.Current;

            var contents = kind == DatabaseKind.Operational && integrity == "ok" ? Summarize(connection) : null;
            return new VerifyResult(path, kind, integrity, state, applied.LastOrDefault(), pending, unknown, null, contents);
        }
        catch (SqliteException failure)
        {
            return new VerifyResult(path, kind, "", SchemaState.Unknown, null, [], [], $"SQLite error {failure.SqliteErrorCode}: {failure.Message}");
        }
    }

    /// <summary>
    /// What <c>wcs.db</c> holds, for the checks after a restore: the plant, the missions, and the
    /// durable queues towards the WMS with their parked rows (refused for good, waiting for a person).
    /// A table this schema does not have yet is left out.
    /// </summary>
    private static string Summarize(SqliteConnection connection)
    {
        // A parked transition waits until DateTimeOffset.MaxValue (PendingWarehouseTransitionStore.ParkedUntil), stored as UTC ticks.
        var parkedUntil = DateTimeOffset.MaxValue.UtcTicks;
        (string Label, string Sql)[] counts =
        [
            ("plant items", "SELECT COUNT(*) FROM PlantItems"),
            ("missions", "SELECT COUNT(*) FROM Missions"),
            ("extraction queue", "SELECT COUNT(*) FROM ExtractionQueue"),
            ("transitions to the WMS", "SELECT COUNT(*) FROM PendingWarehouseTransitions"),
            ("of which parked", $"SELECT COUNT(*) FROM PendingWarehouseTransitions WHERE NextAttemptAtUtc = {parkedUntil}"),
            ("extraction outcomes to send", "SELECT COUNT(*) FROM PendingExtractionOutcomes"),
            ("picked-extraction reports", "SELECT COUNT(*) FROM PendingPickedExtractionReports"),
            ("of which parked", "SELECT COUNT(*) FROM PendingPickedExtractionReports WHERE Parked = 1"),
            ("cell give-backs", "SELECT COUNT(*) FROM PendingCellGiveBacks"),
            ("of which parked", "SELECT COUNT(*) FROM PendingCellGiveBacks WHERE Parked = 1")
        ];

        var parts = new List<string>();
        foreach (var (label, sql) in counts)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                parts.Add($"{label} {command.ExecuteScalar()}");
            }
            catch (SqliteException)
            {
                // The table comes with a later migration.
            }
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// A read-only connection outside the pool, so that the file is released when it is disposed.
    /// On a database in WAL mode SQLite still needs to create the <c>-shm</c> next to it: the folder
    /// must be writable, the database file is not touched.
    /// </summary>
    internal static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>The migrations this build applies to a database of that kind, oldest first.</summary>
    internal static IReadOnlyList<string> KnownMigrations(DatabaseKind kind)
    {
        const string nowhere = "Data Source=:memory:";
        using DbContext context = kind switch
        {
            DatabaseKind.Operational => new WcsDbContext(new DbContextOptionsBuilder<WcsDbContext>().UseSqlite(nowhere).Options),
            DatabaseKind.History => new WcsHistoryDbContext(new DbContextOptionsBuilder<WcsHistoryDbContext>().UseSqlite(nowhere).Options),
            _ => new WcsPlcLogDbContext(new DbContextOptionsBuilder<WcsPlcLogDbContext>().UseSqlite(nowhere).Options)
        };
        return context.Database.GetMigrations().ToList();
    }
}
