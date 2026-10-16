using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Apre i file mensili dello storico del WCS e ne crea i contesti: uno per il mese di adesso, e
/// uno in sola lettura per ogni mese ancora in linea.
/// </summary>
/// <remarks>
/// Ogni mese ha il proprio file, <c>wcs-history-AAAA-MM.db</c>. Chi chiede un contesto lo
/// riceve sul file del mese corrente: al cambio di mese il file nuovo viene creato e portato
/// all'ultima migrazione, e i file piu' vecchi di <see cref="MonthsToKeep"/> mesi vengono
/// cancellati. I mesi passati si leggono con <see cref="CreateReadContext"/>, in sola lettura;
/// la scrittura resta sul mese corrente. La ritenzione e' quindi cancellare un file, e il backup copiarne uno. L'orologio e'
/// iniettabile: i test provano il cambio di mese senza aspettarlo, e l'importatore interno degli impianti (non pubblicato) scrive lo storico
/// di un impianto migrato aprendo un mese passato alla volta. Nei test la variante in memoria ha un solo file e non ruota.
/// <para>
/// I file sono in WAL e quello che si scrive vive nel <c>-wal</c> finche' non viene riversato:
/// lo storico lo riversa da se' (<c>PRAGMA wal_checkpoint(TRUNCATE)</c>) alla rotazione del mese,
/// sul file che diventa passato, e nel <see cref="Dispose"/>, sul mese corrente. Il backup di un
/// mese passato e' quindi copiare il suo <c>.db</c>; chi copia a caldo il mese corrente deve
/// copiare anche il suo <c>-wal</c>, o porta via uno stub. Voce D84.
/// </para>
/// </remarks>
public sealed partial class WcsHistoryDatabase : IDisposable
{
    public const string FileNamePrefix = "wcs-history-";

    private readonly string? _directory;
    private readonly Func<DateTime> _clock;
    private readonly SqliteConnection? _inMemoryConnection;
    private readonly object _rotation = new();

    private (int Year, int Month) _currentMonth;
    private Action<IReadOnlyCollection<Entities.RetiredHandledReference>>? _keepReferences;
    private DbContextOptions<WcsHistoryDbContext>? _currentOptions;

    private WcsHistoryDatabase(string? directory, int monthsToKeep, Func<DateTime> clock, SqliteConnection? inMemoryConnection)
    {
        _directory = directory;
        MonthsToKeep = monthsToKeep;
        _clock = clock;
        _inMemoryConnection = inMemoryConnection;
    }

    /// <summary>Quanti mesi di storico restano in linea, compreso quello corrente.</summary>
    public int MonthsToKeep { get; }

    /// <summary>Il file del mese corrente, per i log e la diagnostica.</summary>
    public string CurrentDescription => _directory == null ? ":memory:" : FilePath(_directory, _clock());

    /// <summary>
    /// Apre lo storico nella cartella indicata: il file del mese corrente viene creato o portato
    /// all'ultima migrazione, i file oltre la ritenzione cancellati.
    /// </summary>
    /// <param name="keepReferences">
    /// Item D103: called with the references of a month before its file leaves the retention, to keep
    /// them where they do not rotate (<c>wcs.db</c>). If it raises, the file stays and the next change of
    /// month tries again. Null: the file goes without them, as before.
    /// </param>
    public static WcsHistoryDatabase Open(string directory, int monthsToKeep = 12, Func<DateTime>? clock = null,
        Action<IReadOnlyCollection<Entities.RetiredHandledReference>>? keepReferences = null)
    {
        Directory.CreateDirectory(directory);
        var database = new WcsHistoryDatabase(directory, Math.Max(1, monthsToKeep), clock ?? (() => DateTime.Now), null)
        {
            _keepReferences = keepReferences
        };
        database.EnsureCurrentMonth();
        return database;
    }

    /// <summary>Uno storico in memoria con lo schema corrente: per i test. Non ruota.</summary>
    public static WcsHistoryDatabase InMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var database = new WcsHistoryDatabase(null, 1, () => DateTime.Now, connection)
        {
            _currentOptions = new DbContextOptionsBuilder<WcsHistoryDbContext>().UseSqlite(connection).Options
        };

        using var context = new WcsHistoryDbContext(database._currentOptions);
        context.Database.Migrate();
        return database;
    }

    /// <summary>Un contesto sul file del mese di adesso. Chi lo chiede lo dispone.</summary>
    public WcsHistoryDbContext CreateContext()
    {
        EnsureCurrentMonth();
        return new WcsHistoryDbContext(_currentOptions!);
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
    /// I file mensili sono in WAL (<c>PRAGMA journal_mode = WAL</c> in <see cref="EnsureCurrentMonth"/>),
    /// e finche' nessuno riversa il contenuto vive nel <c>-wal</c>: chi si ferma sporco lascia il file
    /// del mese passato con il suo <c>-wal</c> pieno e senza <c>-shm</c> attendibile. La sola lettura
    /// regge lo stesso, e non e' una svista: <c>SQLITE_OPEN_READONLY</c> vale per il file del database,
    /// non per i file di servizio, quindi SQLite ricrea il <c>-shm</c>, ricostruisce l'indice del WAL e
    /// legge le pagine dal <c>-wal</c> senza toccare il <c>.db</c>. Quello che serve e' che la cartella
    /// dello storico sia scrivibile, perche' e' li' che il <c>-shm</c> viene creato. Lo tiene fermo
    /// <c>HistoryMonthsTest.AMonthLeftWithItsWalIsStillReadable</c>, che ricostruisce la cartella come
    /// la lascia un arresto sporco e verifica che il <c>-shm</c> ricompaia.
    /// </remarks>
    public WcsHistoryDbContext CreateReadContext(int year, int month)
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
        var options = new DbContextOptionsBuilder<WcsHistoryDbContext>().UseSqlite(connectionString).Options;
        return new WcsHistoryDbContext(options);
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
    /// Riversa il <c>-wal</c> del file indicato nel suo <c>.db</c> e lo tronca, cosi' che il
    /// <c>.db</c> da solo contenga tutto (<c>PRAGMA wal_checkpoint(TRUNCATE)</c>). Un riversamento
    /// che non si completa — un'altra connessione che lo tiene occupato, un errore di SQLite — non
    /// solleva: il contenuto resta nel <c>-wal</c>, leggibile come prima, e lo si dice a
    /// <see cref="Notice"/>, perche' un backup fatto dopo prenderebbe uno stub.
    /// </summary>
    private static void Checkpoint(DbContextOptions<WcsHistoryDbContext> options, string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var context = new WcsHistoryDbContext(options);
            var connection = context.Database.GetDbConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = command.ExecuteReader();

            // Tre colonne: busy, pagine nel -wal, pagine riversate. busy diverso da zero vuol dire
            // che il riversamento non e' arrivato in fondo.
            if (reader.Read() && reader.GetInt64(0) != 0)
                Report($"storico: il riversamento del -wal di {name} non e' completo, un'altra connessione lo tiene occupato; il contenuto resta nel -wal");
        }
        catch (Exception failure) when (failure is SqliteException or IOException or InvalidOperationException)
        {
            Report($"storico: il riversamento del -wal di {name} e' fallito ({failure.Message}); il contenuto resta nel -wal");
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

            var options = new DbContextOptionsBuilder<WcsHistoryDbContext>().UseSqlite(connectionString).Options;

            using (var context = new WcsHistoryDbContext(options))
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

            // Item D103: what the month says about processes stays in wcs.db before the file goes; if
            // it cannot be kept, the file stays too - a guard on picks must not lose its answer.
            if (_keepReferences is { } keep)
            {
                try
                {
                    using (var context = CreateReadContext(month.Year, month.Month))
                        keep(RetiredHandledReferences.Of(context));
                }
                catch (Exception failure)
                {
                    Report($"storico: {name} e' oltre la ritenzione di {MonthsToKeep} mesi ma i riferimenti delle " +
                           $"sue lavorazioni non si sono potuti conservare ({failure.Message}); resta in linea e si " +
                           "riprova al prossimo cambio di mese");
                    continue;
                }
            }

            try
            {
                SqliteConnection.ClearAllPools();
                File.Delete(file);
                foreach (var side in new[] { file + "-wal", file + "-shm" })
                    if (File.Exists(side)) File.Delete(side);
                deleted++;
                Report($"storico: cancellato {name}, oltre la ritenzione di {MonthsToKeep} mesi");
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Si riprova al prossimo giro, ma non in silenzio: un mese che doveva sparire e non
                // e' sparito e' il solo segno che la ritenzione non sta girando come crede chi legge
                // la configurazione.
                Report($"storico: {name} e' oltre la ritenzione di {MonthsToKeep} mesi ma non si e' " +
                       $"lasciato cancellare ({failure.Message}); resta in linea e si riprova al " +
                       "prossimo cambio di mese");
            }
        }

        if (deleted > 0)
            Report($"storico: {deleted} file mensili cancellati perche' oltre la ritenzione di {MonthsToKeep} mesi");
    }

    /// <summary>
    /// A chi la ritenzione racconta i file mensili che cancella. L'host ci collega il proprio
    /// registro all'avvio (<c>EngineHostedService</c>), accanto a <c>Navigator.HostErrorLog</c>;
    /// senza registro collegato la notizia va sull'errore standard. Cancellare un file di storico
    /// non deve succedere in silenzio: e' un dato che se ne va, e su un impianto appena importato
    /// con la ritenzione predefinita sono gli anni che l'importazione ha appena scritto.
    /// </summary>
    public static Action<string>? Notice { get; set; }

    /// <summary>
    /// Consegna la notizia al registro collegato, o sull'errore standard se non c'e'. Non solleva se
    /// il registro solleva a sua volta — <c>ILogger.Log</c> rilancia il guasto di un provider come
    /// <c>AggregateException</c> — perche' la ritenzione non puo' cadere per colpa di chi la
    /// racconta: e' la stessa regola di <c>Navigator.ReportError</c>.
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

    [GeneratedRegex(@"^wcs-history-(\d{4})-(\d{2})\.db$")]
    private static partial Regex MonthPattern();
}
