using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Apre i file mensili del registro dei messaggi dei PLC e ne crea i contesti: uno per il mese di
/// adesso, e uno in sola lettura per ogni mese ancora in linea.
/// </summary>
/// <remarks>
/// E' il gemello di <see cref="WcsHistoryDatabase"/> — stessa rotazione mensile, stesso
/// riversamento del <c>-wal</c> alla rotazione e nel <c>Dispose</c>, stessa ritenzione per file —
/// su un file suo, <c>wcs-plc-AAAA-MM.db</c>, nella stessa cartella <c>Data</c>. Sta a parte per
/// la voce <c>D183</c>: finche' i due registri condividevano il file condividevano anche la coda
/// di scrittura, e sotto pieno cadevano le righe piu' vecchie senza guardare di quale registro
/// fossero; i messaggi dei PLC sono molti di piu' del registro del navigatore, quindi a cadere
/// erano soprattutto righe dell'altro. Separati, un'ondata di telegrammi puo' far cadere solo
/// telegrammi.
/// <para>
/// La copia non e' una svista: la rotazione dello storico e' codice che regge l'impianto da
/// prima, e questa voce non e' il momento per generalizzarla in una base comune ai due (la
/// generalizzazione toccherebbe il percorso dello storico, che qui non si vuole muovere). La
/// duplicazione e' dichiarata nel rapporto della voce.
/// </para>
/// </remarks>
public sealed partial class WcsPlcLogDatabase : IDisposable
{
    public const string FileNamePrefix = "wcs-plc-";

    /// <summary>
    /// Quanti mesi di file restano in linea di serie: il mese corrente e quello prima.
    /// </summary>
    /// <remarks>
    /// Non e' la ritenzione di questo registro — quella e' di sette giorni e la fa
    /// <c>Wcs.Engine.ORM.PlcMessageLog.ClearOlderThan</c>, una volta al giorno, sul solo file del
    /// mese corrente. Due mesi sono il minimo che non la contraddice: nei primi giorni del mese la
    /// finestra di sette giorni entra nel mese prima, e quel file deve essere ancora leggibile.
    /// Tenerne di piu' sarebbe tenere in linea, per mesi, i residui che la potatura non raggiunge
    /// piu' (un file che diventa passato porta con se' i suoi ultimi sette giorni di telegrammi,
    /// XML compreso, e nessuno lo pota piu'): il contrario di quello che la ritenzione dichiara.
    /// Lo storico ne tiene dodici perche' li' il file del mese <i>e'</i> la ritenzione.
    /// </remarks>
    public const int DefaultMonthsToKeep = 2;

    private readonly string? _directory;
    private readonly Func<DateTime> _clock;
    private readonly SqliteConnection? _inMemoryConnection;
    private readonly object _rotation = new();

    private (int Year, int Month) _currentMonth;
    private DbContextOptions<WcsPlcLogDbContext>? _currentOptions;

    private WcsPlcLogDatabase(string? directory, int monthsToKeep, Func<DateTime> clock, SqliteConnection? inMemoryConnection)
    {
        _directory = directory;
        MonthsToKeep = monthsToKeep;
        _clock = clock;
        _inMemoryConnection = inMemoryConnection;
    }

    /// <summary>Quanti mesi di file restano in linea, compreso quello corrente.</summary>
    public int MonthsToKeep { get; }

    /// <summary>Il file del mese corrente, per i log e la diagnostica.</summary>
    public string CurrentDescription => _directory == null ? ":memory:" : FilePath(_directory, _clock());

    /// <summary>
    /// Apre il registro dei messaggi nella cartella indicata: il file del mese corrente viene
    /// creato o portato all'ultima migrazione, i file oltre la ritenzione cancellati.
    /// </summary>
    public static WcsPlcLogDatabase Open(string directory, int monthsToKeep = DefaultMonthsToKeep, Func<DateTime>? clock = null)
    {
        Directory.CreateDirectory(directory);
        var database = new WcsPlcLogDatabase(directory, Math.Max(1, monthsToKeep), clock ?? (() => DateTime.Now), null);
        database.EnsureCurrentMonth();
        return database;
    }

    /// <summary>Un registro in memoria con lo schema corrente: per i test. Non ruota.</summary>
    public static WcsPlcLogDatabase InMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var database = new WcsPlcLogDatabase(null, 1, () => DateTime.Now, connection)
        {
            _currentOptions = new DbContextOptionsBuilder<WcsPlcLogDbContext>().UseSqlite(connection).Options
        };

        using var context = new WcsPlcLogDbContext(database._currentOptions);
        context.Database.Migrate();
        return database;
    }

    /// <summary>Un contesto sul file del mese di adesso. Chi lo chiede lo dispone.</summary>
    public WcsPlcLogDbContext CreateContext()
    {
        EnsureCurrentMonth();
        return new WcsPlcLogDbContext(_currentOptions!);
    }

    /// <summary>I file mensili presenti nella cartella, dal piu' vecchio.</summary>
    public IReadOnlyList<string> Files()
    {
        if (_directory == null) return [];
        return Directory.GetFiles(_directory, FileNamePrefix + "*.db")
            .Where(f => MonthOf(f) != null)
            .OrderBy(f => MonthOf(f))
            .ToList();
    }

    /// <summary>I mesi presenti su disco, dal piu' vecchio. In memoria: solo il mese di adesso.</summary>
    public IReadOnlyList<(int Year, int Month)> MonthsAvailable()
    {
        if (_directory == null)
        {
            var now = _clock();
            return [(now.Year, now.Month)];
        }
        // Come CreateContext: assicura prima il file del mese corrente, cosi' il mese di adesso
        // compare nell'elenco anche subito dopo un cambio di mese, prima di qualunque scrittura.
        EnsureCurrentMonth();
        return Files().Select(f => MonthOf(f)!.Value).ToList();
    }

    /// <summary>
    /// Un contesto in sola lettura sul file di un mese presente. Per il mese di adesso e' lo stesso
    /// contesto di <see cref="CreateContext"/>; per un mese assente solleva
    /// <see cref="FileNotFoundException"/>: chi interroga ha gia' controllato <see cref="MonthsAvailable"/>.
    /// </summary>
    /// <remarks>
    /// Vale quello che vale per lo storico: i file sono in WAL e la sola lettura funziona anche su
    /// un file lasciato con il suo <c>-wal</c> pieno, perche' <c>SQLITE_OPEN_READONLY</c> riguarda
    /// il file del database e non i file di servizio (si veda
    /// <see cref="WcsHistoryDatabase.CreateReadContext"/>).
    /// </remarks>
    public WcsPlcLogDbContext CreateReadContext(int year, int month)
    {
        var now = _clock();
        if (_directory == null || (year, month) == (now.Year, now.Month)) return CreateContext();

        var path = FilePath(_directory, new DateTime(year, month, 1));
        if (!File.Exists(path)) throw new FileNotFoundException("Il file del mese non c'e'", path);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true
        }.ToString();
        var options = new DbContextOptionsBuilder<WcsPlcLogDbContext>().UseSqlite(connectionString).Options;
        return new WcsPlcLogDbContext(options);
    }

    public void Dispose()
    {
        // Il mese corrente smette di essere scritto qui: e' il momento di riversare il -wal, perche'
        // con le connessioni nel pool il riversamento alla chiusura dell'ultima non arriva mai.
        if (_directory != null && _currentOptions != null)
            Checkpoint(_currentOptions, FilePath(_directory, new DateTime(_currentMonth.Year, _currentMonth.Month, 1)));

        _inMemoryConnection?.Dispose();
    }

    /// <summary>
    /// Riversa il <c>-wal</c> del file indicato nel suo <c>.db</c> e lo tronca
    /// (<c>PRAGMA wal_checkpoint(TRUNCATE)</c>), cosi' che il <c>.db</c> da solo contenga tutto. Un
    /// riversamento che non si completa non solleva: il contenuto resta nel <c>-wal</c>, leggibile
    /// come prima, e lo si dice a <see cref="Notice"/>.
    /// </summary>
    private static void Checkpoint(DbContextOptions<WcsPlcLogDbContext> options, string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var context = new WcsPlcLogDbContext(options);
            var connection = context.Database.GetDbConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = command.ExecuteReader();

            // Tre colonne: busy, pagine nel -wal, pagine riversate. busy diverso da zero vuol dire
            // che il riversamento non e' arrivato in fondo.
            if (reader.Read() && reader.GetInt64(0) != 0)
                Report($"registro dei messaggi PLC: il riversamento del -wal di {name} non e' completo, un'altra connessione lo tiene occupato; il contenuto resta nel -wal");
        }
        catch (Exception failure) when (failure is SqliteException or IOException or InvalidOperationException)
        {
            Report($"registro dei messaggi PLC: il riversamento del -wal di {name} e' fallito ({failure.Message}); il contenuto resta nel -wal");
        }
    }

    /// <summary>Il nome del file di un mese.</summary>
    public static string FilePath(string directory, DateTime month) =>
        Path.Combine(directory, $"{FileNamePrefix}{month:yyyy-MM}.db");

    private static (int Year, int Month)? MonthOf(string path)
    {
        var match = MonthPattern().Match(Path.GetFileName(path));
        if (!match.Success) return null;
        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private void EnsureCurrentMonth()
    {
        if (_directory == null) return;

        var now = _clock();
        var month = (now.Year, now.Month);
        if (_currentOptions != null && month == _currentMonth) return;

        lock (_rotation)
        {
            if (_currentOptions != null && month == _currentMonth) return;

            // Il mese che diventa passato non si scrive piu': si riversa il suo -wal adesso, cosi'
            // il suo .db e' completo da solo e chi lo copia porta via tutto. Voce D84.
            if (_currentOptions != null)
                Checkpoint(_currentOptions, FilePath(_directory, new DateTime(_currentMonth.Year, _currentMonth.Month, 1)));

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = FilePath(_directory, now),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true
            }.ToString();

            var options = new DbContextOptionsBuilder<WcsPlcLogDbContext>().UseSqlite(connectionString).Options;

            using (var context = new WcsPlcLogDbContext(options))
            {
                context.Database.Migrate();
                context.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
                context.Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;");
            }

            _currentOptions = options;
            _currentMonth = month;

            ApplyRetention(now);
        }
    }

    /// <summary>
    /// Cancella i file dei mesi oltre la ritenzione, dicendolo a <see cref="Notice"/>: un file che
    /// non si lascia cancellare resta, si riprova al prossimo cambio di mese, e anche quello si dice.
    /// </summary>
    private void ApplyRetention(DateTime now)
    {
        var oldest = new DateTime(now.Year, now.Month, 1).AddMonths(-(MonthsToKeep - 1));
        var deleted = 0;

        foreach (var file in Files())
        {
            var month = MonthOf(file)!.Value;
            if (new DateTime(month.Year, month.Month, 1) >= oldest) continue;

            var name = Path.GetFileName(file);
            try
            {
                SqliteConnection.ClearAllPools();
                File.Delete(file);
                foreach (var side in new[] { file + "-wal", file + "-shm" })
                    if (File.Exists(side)) File.Delete(side);
                deleted++;
                Report($"registro dei messaggi PLC: cancellato {name}, oltre la ritenzione di {MonthsToKeep} mesi");
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                Report($"registro dei messaggi PLC: {name} e' oltre la ritenzione di {MonthsToKeep} mesi ma non si e' " +
                       $"lasciato cancellare ({failure.Message}); resta in linea e si riprova al " +
                       "prossimo cambio di mese");
            }
        }

        if (deleted > 0)
            Report($"registro dei messaggi PLC: {deleted} file mensili cancellati perche' oltre la ritenzione di {MonthsToKeep} mesi");
    }

    /// <summary>
    /// A chi la ritenzione racconta i file mensili che cancella. L'host ci collega il proprio
    /// registro all'avvio (<c>EngineHostedService</c>), accanto a <c>WcsHistoryDatabase.Notice</c>;
    /// senza registro collegato la notizia va sull'errore standard.
    /// </summary>
    public static Action<string>? Notice { get; set; }

    /// <summary>
    /// Consegna la notizia al registro collegato, o sull'errore standard se non c'e'. Non solleva
    /// se il registro solleva a sua volta: e' la stessa regola di <c>WcsHistoryDatabase.Report</c>.
    /// </summary>
    private static void Report(string message)
    {
        try
        {
            if (Notice is { } notice)
            {
                notice(message);
                return;
            }
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure.ToString());
        }

        Console.Error.WriteLine(message);
    }

    [GeneratedRegex(@"^wcs-plc-(\d{4})-(\d{2})\.db$")]
    private static partial Regex MonthPattern();
}
