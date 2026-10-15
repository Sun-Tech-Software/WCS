using System.Text.RegularExpressions;
using Wcs.Engine.Persistence;

namespace Wcs.DbTool;

/// <summary>The three kinds of SQLite file the host keeps in <c>Wcs:DatabaseDirectory</c>.</summary>
internal enum DatabaseKind
{
    /// <summary><c>wcs.db</c>: plant, missions, commands, durable queues towards the WMS.</summary>
    Operational,

    /// <summary><c>wcs-history-YYYY-MM.db</c>: handled load units and the operators' log, one per month.</summary>
    History,

    /// <summary><c>wcs-plc-YYYY-MM.db</c>: the field driver's message log, one per month.</summary>
    PlcLog
}

/// <summary>Which files of a data folder are WCS databases, and which files travel with them.</summary>
internal static class WcsDatabaseFiles
{
    /// <summary>
    /// The files SQLite keeps next to a database. A <c>-wal</c> holds committed transactions not yet
    /// copied into the <c>.db</c>: it belongs to that <c>.db</c> and to no other.
    /// </summary>
    public static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    /// <summary>The kind of a database from its file name, or <c>null</c> for any other file.</summary>
    public static DatabaseKind? KindOf(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, WcsDatabase.OperationalFileName, StringComparison.OrdinalIgnoreCase))
            return DatabaseKind.Operational;
        if (MonthlyPattern(WcsHistoryDatabase.FileNamePrefix).IsMatch(name)) return DatabaseKind.History;
        if (MonthlyPattern(WcsPlcLogDatabase.FileNamePrefix).IsMatch(name)) return DatabaseKind.PlcLog;
        return null;
    }

    /// <summary>The WCS databases in a folder (not their sidecars), <c>wcs.db</c> first, then by name.</summary>
    public static IReadOnlyList<string> Find(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.db")
                .Where(f => KindOf(f) != null)
                .OrderBy(f => KindOf(f))
                .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    /// <summary>A database and the sidecars of it that exist.</summary>
    public static IEnumerable<string> WithSidecars(string database)
    {
        yield return database;
        foreach (var suffix in SidecarSuffixes)
            if (File.Exists(database + suffix))
                yield return database + suffix;
    }

    private static Regex MonthlyPattern(string prefix) =>
        new($"^{Regex.Escape(prefix)}\\d{{4}}-\\d{{2}}\\.db$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
