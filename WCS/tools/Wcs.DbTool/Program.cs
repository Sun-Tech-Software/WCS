using Wcs.DbTool;

// Backup, verification and restore of the WCS databases (wcs.db, wcs-history-YYYY-MM.db,
// wcs-plc-YYYY-MM.db). The guide: docs/integration/backup-restore-and-disaster-recovery.md.

const string Usage = """
    Wcs.DbTool - backup, verify and restore the databases of the WCS host

      backup  --data <data folder> --out <new or empty folder>
              Online backup with the SQLite Online Backup API: the host may keep running.
      verify  <database file | data folder | backup folder>
              PRAGMA integrity_check and the schema against this build; on a backup folder also
              the SHA-256 of every file against wcs-backup.json. Never writes a database.
      restore --from <backup folder> --data <data folder> [--assume-stopped]
              Only with the host stopped: refuses a backup that does not verify and a data folder
              whose databases are open; moves the replaced files to <data folder>.replaced-<time>.
              --assume-stopped: needed outside Windows, where the open-file check is not available.

    Exit code: 0 done, 1 refused or failed (see the output), 2 wrong arguments.
    """;

try
{
    var code = args switch
    {
        ["backup", .. var rest] when Options(rest, ["--data", "--out"], []) is { } o
            => BackupCommand.Run(o["--data"], o["--out"], Console.Out),
        ["verify", var path] => VerifyCommand.Run(path, Console.Out),
        ["restore", .. var rest] when Options(rest, ["--from", "--data"], ["--assume-stopped"]) is { } o
            => RestoreCommand.Run(o["--from"], o["--data"], Console.Out, o.ContainsKey("--assume-stopped")),
        _ => -1
    };
    if (code >= 0) return code;

    Console.Error.WriteLine(Usage);
    return 2;
}
catch (Exception failure)
{
    Console.Error.WriteLine($"Failed: {failure.Message}");
    Console.Error.WriteLine(failure);
    return 1;
}

// "--name value" pairs, every required one present once, plus optional switches; null otherwise.
static Dictionary<string, string>? Options(string[] arguments, string[] required, string[] switches)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < arguments.Length; i++)
    {
        var name = arguments[i];
        if (switches.Contains(name, StringComparer.OrdinalIgnoreCase) && options.TryAdd(name, "")) continue;
        if (!required.Contains(name, StringComparer.OrdinalIgnoreCase) || i + 1 >= arguments.Length) return null;
        if (!options.TryAdd(name, arguments[++i])) return null;
    }
    return required.All(options.ContainsKey) ? options : null;
}
