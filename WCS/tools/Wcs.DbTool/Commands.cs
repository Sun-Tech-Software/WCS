using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wcs.Engine.Persistence;

namespace Wcs.DbTool;

/// <summary>
/// <c>backup</c>: copies every WCS database of a data folder with the SQLite Online Backup API, so
/// that the host can keep running and writing. Each copy is a consistent snapshot of its database
/// (one read transaction for the whole copy), written as one self-contained file (rollback journal
/// mode, no <c>-wal</c>), verified, and listed with its SHA-256 in <c>wcs-backup.json</c>.
/// </summary>
/// <remarks>
/// The files are copied one after the other, so <c>wcs.db</c> and the monthly files are snapshots
/// of slightly different instants: the history and the PLC log are append-only journals, and no
/// row of <c>wcs.db</c> points into them, so that difference costs at most the last lines of a log.
/// </remarks>
internal static class BackupCommand
{
    public static int Run(string dataDirectory, string outputDirectory, TextWriter output)
    {
        dataDirectory = Path.GetFullPath(dataDirectory);
        outputDirectory = Path.GetFullPath(outputDirectory);

        var databases = WcsDatabaseFiles.Find(dataDirectory);
        if (databases.Count == 0)
            return Fail(output, $"no WCS database in {dataDirectory} (expected wcs.db, wcs-history-YYYY-MM.db, wcs-plc-YYYY-MM.db)");
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            return Fail(output, $"{outputDirectory} is not empty: a backup goes into a new or empty folder");

        Directory.CreateDirectory(outputDirectory);
        output.WriteLine($"Backup of {dataDirectory}");
        output.WriteLine($"     into {outputDirectory}");

        var manifest = new BackupManifest
        {
            CreatedUtc = DateTime.UtcNow,
            Source = dataDirectory,
            Machine = Environment.MachineName
        };
        var allOk = true;
        foreach (var database in databases)
        {
            var name = Path.GetFileName(database);
            var target = Path.Combine(outputDirectory, name);
            var watch = Stopwatch.StartNew();
            try
            {
                Copy(database, target);
            }
            catch (Exception failure) when (failure is SqliteException or IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"  {name,-24} FAILED   {failure.Message}");
                allOk = false;
                continue;
            }

            var result = DatabaseVerifier.Verify(target);
            var bytes = new FileInfo(target).Length;
            manifest.Files.Add(new BackupManifestFile
            {
                Name = name,
                Kind = result.Kind.ToString(),
                Bytes = bytes,
                Sha256 = Sha256(target),
                LastMigration = result.LastApplied
            });
            allOk &= result.Ok;
            output.WriteLine($"  {name,-24} {Size(bytes),10} {watch.ElapsedMilliseconds,6} ms  {result.Describe()}");
        }

        manifest.Write(outputDirectory);
        output.WriteLine(allOk
            ? $"Backup complete: {manifest.Files.Count} file(s), listed in {BackupManifest.FileName}."
            : "Backup INCOMPLETE or not usable by this build: see the FAILED lines above.");
        return allOk ? 0 : 1;
    }

    /// <summary>
    /// One database into one file. The source is opened read-only and is never changed; the copy is
    /// written under a temporary name and renamed only once it is complete.
    /// </summary>
    private static void Copy(string database, string target)
    {
        var partial = target + ".partial";
        using (var source = DatabaseVerifier.OpenReadOnly(database))
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = partial, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
               }.ToString()))
        {
            destination.Open();
            // sqlite3_backup_step(-1): every page in one step, under one read transaction on the
            // source. In WAL mode that transaction does not stop the host's writers, and it sees
            // the database as of its first read: commits made during the copy are not in it.
            source.BackupDatabase(destination);

            // The copy carries the WAL flag of its source; back to a rollback journal, so that the
            // backup is one file. The host puts it in WAL mode again when it opens it.
            using var journal = destination.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode = DELETE;";
            journal.ExecuteScalar();
        }
        File.Move(partial, target);
    }

    internal static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.0} MB")
    };

    internal static int Fail(TextWriter output, string message)
    {
        output.WriteLine($"Refused: {message}");
        return 1;
    }
}

/// <summary>
/// <c>verify</c>: one database file, a data folder, or a backup folder (then also every file
/// against the SHA-256 recorded in <c>wcs-backup.json</c>). Never writes a database: on one in WAL
/// mode (a data folder, a copy of one) SQLite still creates the <c>-shm</c> and an empty <c>-wal</c>
/// beside it, as any reader does.
/// </summary>
internal static class VerifyCommand
{
    public static int Run(string path, TextWriter output)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path))
        {
            if (WcsDatabaseFiles.KindOf(path) == null)
                return BackupCommand.Fail(output, $"{Path.GetFileName(path)} is not a WCS database name (wcs.db, wcs-history-YYYY-MM.db, wcs-plc-YYYY-MM.db)");
            var single = DatabaseVerifier.Verify(path);
            output.WriteLine($"  {Path.GetFileName(path),-24} {single.Describe()}");
            return single.Ok ? 0 : 1;
        }

        if (!Directory.Exists(path)) return BackupCommand.Fail(output, $"{path} does not exist");

        var databases = WcsDatabaseFiles.Find(path);
        if (databases.Count == 0) return BackupCommand.Fail(output, $"no WCS database in {path}");

        output.WriteLine($"Verify {path}");
        var allOk = true;
        var manifest = BackupManifest.TryRead(path);
        if (manifest?.Problem() is { } problem)
            return BackupCommand.Fail(output, $"{BackupManifest.FileName}: {problem}");
        if (manifest != null)
            allOk &= CheckManifest(manifest, path, output);

        foreach (var database in databases)
        {
            var result = DatabaseVerifier.Verify(database);
            allOk &= result.Ok;
            output.WriteLine($"  {Path.GetFileName(database),-24} {result.Describe()}");
        }

        output.WriteLine(allOk ? "All verified." : "Verification FAILED: see the lines above.");
        return allOk ? 0 : 1;
    }

    /// <summary>Every file the manifest lists is there, with the same bytes; and no other database is.</summary>
    internal static bool CheckManifest(BackupManifest manifest, string directory, TextWriter output)
    {
        var ok = true;
        output.WriteLine($"  {BackupManifest.FileName}: {manifest.Files.Count} file(s), taken {manifest.CreatedUtc:yyyy-MM-dd HH:mm:ss} UTC from {manifest.Source} on {manifest.Machine}");
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(directory, file.Name);
            if (!File.Exists(path))
            {
                output.WriteLine($"  {file.Name,-24} FAILED   listed in the manifest, missing");
                ok = false;
            }
            else if (BackupCommand.Sha256(path) != file.Sha256)
            {
                output.WriteLine($"  {file.Name,-24} FAILED   sha256 differs from the manifest: the file changed after the backup");
                ok = false;
            }
        }

        foreach (var database in WcsDatabaseFiles.Find(directory))
            if (manifest.Files.All(f => !string.Equals(f.Name, Path.GetFileName(database), StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine($"  {Path.GetFileName(database),-24} FAILED   not listed in the manifest");
                ok = false;
            }
        return ok;
    }
}

/// <summary>
/// <c>restore</c>: puts a backup in place of the databases of a data folder, with the host stopped.
/// Refuses when the backup does not verify or when a database of the folder is open; moves every
/// replaced file (databases and their <c>-wal</c>/<c>-shm</c>) into a sibling folder
/// <c>&lt;data&gt;.replaced-YYYYMMDD-HHMMSS</c>, unchanged.
/// </summary>
internal static class RestoreCommand
{
    public static int Run(string backupDirectory, string dataDirectory, TextWriter output,
        bool assumeStopped = false, Func<DateTime>? clock = null)
    {
        backupDirectory = Path.GetFullPath(backupDirectory);
        dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        clock ??= () => DateTime.Now;

        // 1. The backup: complete, unchanged since it was taken, sound, and openable by this build.
        var manifest = BackupManifest.TryRead(backupDirectory);
        if (manifest == null)
            return BackupCommand.Fail(output, $"{backupDirectory} has no {BackupManifest.FileName}: restore takes a folder written by 'backup'");
        if (manifest.Problem() is { } problem)
            return BackupCommand.Fail(output, $"{BackupManifest.FileName}: {problem}; nothing was changed");
        if (manifest.Files.All(f => !string.Equals(f.Name, WcsDatabase.OperationalFileName, StringComparison.OrdinalIgnoreCase)))
            return BackupCommand.Fail(output, $"the backup has no {WcsDatabase.OperationalFileName}");

        output.WriteLine($"Restore {backupDirectory}");
        output.WriteLine($"   into {dataDirectory}");
        var sound = VerifyCommand.CheckManifest(manifest, backupDirectory, output);
        foreach (var file in manifest.Files)
        {
            var result = DatabaseVerifier.Verify(Path.Combine(backupDirectory, file.Name));
            sound &= result.Ok;
            output.WriteLine($"  {file.Name,-24} {result.Describe()}");
        }
        if (!sound) return BackupCommand.Fail(output, "the backup does not verify; nothing was changed");

        // 2. The data folder: nobody may have its databases open.
        var replaced = ReplacedFiles(dataDirectory);
        if (!OperatingSystem.IsWindows() && !assumeStopped)
            return BackupCommand.Fail(output, "the check that no process has the databases open works on Windows only: " +
                                              "stop the host, then run again with --assume-stopped");
        var inUse = replaced.Where(IsOpenElsewhere).ToList();
        if (inUse.Count > 0)
            return BackupCommand.Fail(output, "in use by another process (stop the host first): " +
                                              string.Join(", ", inUse.Select(Path.GetFileName)) + "; nothing was changed");

        // 3. The current files aside, then the backup in place; on any failure, back as it was.
        var aside = AsideDirectory(dataDirectory, clock());
        var moved = new List<(string From, string To)>();
        var copied = new List<string>();
        try
        {
            if (replaced.Count > 0) Directory.CreateDirectory(aside);
            foreach (var file in replaced)
            {
                var to = Path.Combine(aside, Path.GetFileName(file));
                File.Move(file, to);
                moved.Add((file, to));
            }

            Directory.CreateDirectory(dataDirectory);
            foreach (var file in manifest.Files)
            {
                var target = Path.Combine(dataDirectory, file.Name);
                var partial = target + ".restoring";
                File.Copy(Path.Combine(backupDirectory, file.Name), partial, overwrite: true);
                copied.Add(partial);
                if (BackupCommand.Sha256(partial) != file.Sha256)
                    throw new IOException($"{file.Name}: the copy in the data folder differs from the backup");
                File.Move(partial, target);
                copied[^1] = target;
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            foreach (var file in copied) File.Delete(file);
            foreach (var (from, to) in Enumerable.Reverse(moved)) File.Move(to, from);
            if (Directory.Exists(aside) && !Directory.EnumerateFileSystemEntries(aside).Any()) Directory.Delete(aside);
            return BackupCommand.Fail(output, $"{failure.Message}; the data folder was put back as it was");
        }

        output.WriteLine($"Restored {manifest.Files.Count} file(s), taken {manifest.CreatedUtc:yyyy-MM-dd HH:mm:ss} UTC.");
        output.WriteLine(moved.Count > 0
            ? $"The {moved.Count} file(s) replaced are kept, unchanged, in {aside}"
            : "The data folder had no WCS database: nothing replaced.");
        return 0;
    }

    /// <summary>The WCS databases of the folder with every sidecar, and sidecars whose database is gone.</summary>
    private static List<string> ReplacedFiles(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory)) return [];
        return Directory.GetFiles(dataDirectory)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                var suffix = WcsDatabaseFiles.SidecarSuffixes.FirstOrDefault(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
                return WcsDatabaseFiles.KindOf(suffix == null ? name : name[..^suffix.Length]) != null;
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Whether some process has the file open. SQLite on Windows opens its files sharing read and
    /// write; asking for the file with no sharing at all fails while any such handle exists, a
    /// connection idle in a pool included.
    /// </summary>
    private static bool IsOpenElsewhere(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string AsideDirectory(string dataDirectory, DateTime now)
    {
        var stem = $"{dataDirectory}.replaced-{now:yyyyMMdd-HHmmss}";
        var candidate = stem;
        for (var n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++) candidate = $"{stem}-{n}";
        return candidate;
    }
}

/// <summary>What a backup folder holds: written by <c>backup</c>, read by <c>verify</c> and <c>restore</c>.</summary>
internal sealed class BackupManifest
{
    public const string FileName = "wcs-backup.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Format { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public string Source { get; set; } = "";
    public string Machine { get; set; } = "";
    public List<BackupManifestFile> Files { get; set; } = [];

    public void Write(string directory) =>
        File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(this, Json));

    public static BackupManifest? TryRead(string directory)
    {
        var path = Path.Combine(directory, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), Json) : null;
    }

    /// <summary>
    /// Why the manifest cannot be trusted, or <c>null</c>. Its file names become paths in the backup
    /// and in the data folder: each one must be the bare name of a WCS database, listed once, so that
    /// a manifest written by hand cannot reach a file outside those two folders.
    /// </summary>
    public string? Problem()
    {
        if (Format != 1) return $"format {Format} is not known to this tool (expected 1)";
        if (Files is null || Files.Count == 0) return "it lists no file";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Name)) return "a file has no name";
            if (Path.GetFileName(file.Name) != file.Name || file.Name.IndexOfAny(['/', '\\', ':']) >= 0)
                return $"'{file.Name}' is not a bare file name";
            if (WcsDatabaseFiles.KindOf(file.Name) == null) return $"'{file.Name}' is not a WCS database name";
            if (!seen.Add(file.Name)) return $"'{file.Name}' is listed twice";
            if (string.IsNullOrWhiteSpace(file.Sha256)) return $"'{file.Name}' has no sha256";
        }
        return null;
    }
}

internal sealed class BackupManifestFile
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? LastMigration { get; set; }
}
