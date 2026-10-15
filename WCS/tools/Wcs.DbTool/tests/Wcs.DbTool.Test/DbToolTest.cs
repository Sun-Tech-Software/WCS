using Microsoft.Data.Sqlite;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.DbTool.Test;

/// <summary>
/// The four promises the backup/restore guide makes about Wcs.DbTool, each on a real <c>wcs.db</c>
/// (created and migrated by <see cref="WcsDatabase.Open"/>, in WAL mode like the host's).
/// </summary>
public sealed class DbToolTest
{
    [Fact]
    public async Task ABackupTakenWhileAWriterKeepsCommittingIsAConsistentSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var data = Path.Combine(temp.Path, "Data");
        var backup = Path.Combine(temp.Path, "backup");
        CreateOperationalDatabase(data);

        // Every transaction of the writer keeps one invariant: Counter.Value = COUNT(*) FROM Probe.
        // A copy that mixed pages from before and after a commit would break it.
        using var writer = Open(Path.Combine(data, WcsDatabase.OperationalFileName));
        Execute(writer, "CREATE TABLE Probe (N INTEGER PRIMARY KEY, Payload BLOB); CREATE TABLE Counter (Value INTEGER); INSERT INTO Counter VALUES (0);");
        // About 20 MB, so that copying takes a while.
        Execute(writer, "WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM s WHERE n < 2000) " +
                        "INSERT INTO Probe (Payload) SELECT randomblob(10000) FROM s; UPDATE Counter SET Value = 2000;");

        long committed = 0;
        using var stop = new CancellationTokenSource();
        var writing = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Execute(writer, "BEGIN IMMEDIATE; INSERT INTO Probe (Payload) VALUES (randomblob(4000)); UPDATE Counter SET Value = Value + 1; COMMIT;");
                Interlocked.Increment(ref committed);
            }
        });

        while (Interlocked.Read(ref committed) < 20) await Task.Delay(5);
        var before = Interlocked.Read(ref committed);
        var output = new StringWriter();
        var code = BackupCommand.Run(data, backup, output);
        var after = Interlocked.Read(ref committed);
        await stop.CancelAsync();
        await writing;

        Assert.True(code == 0, output.ToString());
        Assert.True(after > before, $"the writer was blocked for the whole backup ({before} commits before, {after} after)");

        var copy = Path.Combine(backup, WcsDatabase.OperationalFileName);
        Assert.True(File.Exists(Path.Combine(backup, BackupManifest.FileName)));
        Assert.False(File.Exists(copy + "-wal"), "the copy must be one self-contained file");
        using var restored = Open(copy, SqliteOpenMode.ReadOnly);
        Assert.Equal("delete", Scalar<string>(restored, "PRAGMA journal_mode;"));
        Assert.Equal("ok", Scalar<string>(restored, "PRAGMA integrity_check;"));
        var rows = Scalar<long>(restored, "SELECT COUNT(*) FROM Probe;");
        Assert.Equal(rows, Scalar<long>(restored, "SELECT Value FROM Counter;"));
        Assert.True(rows > 2000, $"the snapshot has {rows} rows: it was taken before the writer started");
    }

    [Fact]
    public void VerifyFailsOnACorruptedFileAndOnABackupWhoseFileChanged()
    {
        using var temp = new TemporaryDirectory();
        var data = Path.Combine(temp.Path, "Data");
        var backup = Path.Combine(temp.Path, "backup");
        CreateOperationalDatabase(data, rows: 2000);
        Assert.Equal(0, BackupCommand.Run(data, backup, new StringWriter()));
        Assert.Equal(0, VerifyCommand.Run(backup, new StringWriter()));

        // One page well inside the Probe table overwritten, as a bad sector or a torn copy would.
        var copy = Path.Combine(backup, WcsDatabase.OperationalFileName);
        var bytes = File.ReadAllBytes(copy);
        var pageSize = 4096;
        var page = bytes.Length / pageSize * 9 / 10;
        Array.Fill(bytes, (byte)0x5A, page * pageSize, pageSize);
        File.WriteAllBytes(copy, bytes);

        var single = new StringWriter();
        Assert.Equal(1, VerifyCommand.Run(copy, single));
        Assert.Contains("FAILED", single.ToString());

        var folder = new StringWriter();
        Assert.Equal(1, VerifyCommand.Run(backup, folder));
        Assert.Contains("sha256", folder.ToString());
    }

    [Fact]
    public void RestoreRefusesADataFolderWhoseDatabaseIsOpen()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source");
        var backup = Path.Combine(temp.Path, "backup");
        var data = Path.Combine(temp.Path, "Data");
        CreateOperationalDatabase(source, marker: "backup");
        Assert.Equal(0, BackupCommand.Run(source, backup, new StringWriter()));
        CreateOperationalDatabase(data, marker: "live");

        // A host that is running holds its databases open, as this connection does.
        using var host = Open(Path.Combine(data, WcsDatabase.OperationalFileName));
        Assert.Equal("live", Scalar<string>(host, "SELECT Text FROM Marker;"));
        var before = Snapshot(data);

        var output = new StringWriter();
        var code = RestoreCommand.Run(backup, data, output, clock: () => new DateTime(2026, 10, 15, 12, 0, 0));

        Assert.Equal(1, code);
        Assert.Contains("in use", output.ToString());
        Assert.Equal(before, Snapshot(data));
        Assert.False(Directory.Exists(data + ".replaced-20261015-120000"));
        Assert.Equal("live", Scalar<string>(host, "SELECT Text FROM Marker;"));
    }

    [Fact]
    public void RestoreMovesEveryReplacedFileAsideUnchangedAndPutsTheBackupInPlace()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source");
        var backup = Path.Combine(temp.Path, "backup");
        var data = Path.Combine(temp.Path, "Data");
        CreateOperationalDatabase(source, marker: "backup");
        Assert.Equal(0, BackupCommand.Run(source, backup, new StringWriter()));

        // The data folder as a host killed mid-write leaves it: wcs.db with a -wal holding the last
        // commits. Copied while the writer still has them open, so the pair is the real thing.
        var crashed = Path.Combine(temp.Path, "crashed");
        CreateOperationalDatabase(crashed, marker: "old");
        Directory.CreateDirectory(data);
        using (var writer = Open(Path.Combine(crashed, WcsDatabase.OperationalFileName)))
        {
            Execute(writer, "PRAGMA wal_autocheckpoint = 0; UPDATE Marker SET Text = 'old, in the wal';");
            foreach (var file in WcsDatabaseFiles.WithSidecars(Path.Combine(crashed, WcsDatabase.OperationalFileName)))
                CopyShared(file, Path.Combine(data, Path.GetFileName(file)));
        }
        Assert.True(new FileInfo(Path.Combine(data, "wcs.db-wal")).Length > 0);
        File.WriteAllText(Path.Combine(data, "wcs-history-2026-11.db"), "a month the backup does not have");
        File.WriteAllText(Path.Combine(data, "notes.txt"), "not a database");
        var before = Snapshot(data);

        var output = new StringWriter();
        var code = RestoreCommand.Run(backup, data, output, clock: () => new DateTime(2026, 10, 15, 12, 0, 0));

        Assert.True(code == 0, output.ToString());
        var aside = data + ".replaced-20261015-120000";
        Assert.True(Directory.Exists(aside), output.ToString());
        foreach (var (name, content) in before.Where(f => f.Key != "notes.txt"))
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(aside, name)));

        Assert.Equal(["notes.txt", "wcs.db"], Directory.GetFiles(data).Select(f => Path.GetFileName(f)!).Order().ToArray());
        using (var restored = Open(Path.Combine(data, WcsDatabase.OperationalFileName), SqliteOpenMode.ReadOnly))
            Assert.Equal("backup", Scalar<string>(restored, "SELECT Text FROM Marker;"));

        // The pair kept aside is still a working database, last commits included.
        using var old = Open(Path.Combine(aside, WcsDatabase.OperationalFileName), SqliteOpenMode.ReadOnly);
        Assert.Equal("old, in the wal", Scalar<string>(old, "SELECT Text FROM Marker;"));
    }

    [Fact]
    public void RestoreRefusesABackupWrittenByALaterVersion()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source");
        var backup = Path.Combine(temp.Path, "backup");
        var data = Path.Combine(temp.Path, "Data");
        CreateOperationalDatabase(source, marker: "from the future");
        using (var connection = Open(Path.Combine(source, WcsDatabase.OperationalFileName)))
            Execute(connection, "INSERT INTO \"__EFMigrationsHistory\" VALUES ('99991231000000_FromALaterVersion', '99.0.0');");

        var backupOutput = new StringWriter();
        Assert.Equal(1, BackupCommand.Run(source, backup, backupOutput));
        Assert.Contains("newer", backupOutput.ToString());

        var output = new StringWriter();
        Assert.Equal(1, RestoreCommand.Run(backup, data, output));
        Assert.Contains("newer", output.ToString());
        Assert.Empty(WcsDatabaseFiles.Find(data));
    }

    private static void CreateOperationalDatabase(string directory, string marker = "", int rows = 0)
    {
        using (WcsDatabase.Open(directory)) { }
        SqliteConnection.ClearAllPools();
        using var connection = Open(Path.Combine(directory, WcsDatabase.OperationalFileName));
        Execute(connection, $"CREATE TABLE Marker (Text TEXT); INSERT INTO Marker VALUES ('{marker}');");
        if (rows > 0)
            Execute(connection, "CREATE TABLE Probe (N INTEGER PRIMARY KEY, Payload BLOB); " +
                                $"WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM s WHERE n < {rows}) " +
                                "INSERT INTO Probe (Payload) SELECT randomblob(1000) FROM s;");
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static void CopyShared(string from, string to)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(to);
        input.CopyTo(output);
    }

    private static Dictionary<string, byte[]> Snapshot(string directory) =>
        Directory.GetFiles(directory).ToDictionary(f => Path.GetFileName(f)!, f =>
        {
            using var input = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            input.CopyTo(copy);
            return copy.ToArray();
        });
}
