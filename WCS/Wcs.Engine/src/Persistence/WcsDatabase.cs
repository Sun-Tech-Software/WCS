using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Apre il database operativo del WCS e ne crea i contesti.
/// </summary>
/// <remarks>
/// In esercizio e' un file, <c>wcs.db</c>, in una cartella scelta dalla configurazione; lo schema
/// viene portato all'ultima migrazione all'apertura, e il file viene messo in modalita' WAL con
/// <c>synchronous = NORMAL</c>: i lettori non bloccano lo scrittore, e una caduta di corrente perde
/// al piu' l'ultimo lotto, mai la coerenza. Quello che si scrive vive nel <c>-wal</c> finche' non
/// viene riversato, e con le connessioni nel pool nessuno lo riversa alla chiusura: lo fa lo
/// store da se' nel <see cref="Dispose"/> (<c>PRAGMA wal_checkpoint(TRUNCATE)</c>), quindi il
/// backup a host fermo e' copiare <c>wcs.db</c>; chi lo copia a caldo, o dopo una chiusura
/// sporca, deve copiare anche il suo <c>-wal</c>, o porta via uno stub (voce D84). Nei test e' un
/// database in memoria con lo stesso schema, condiviso per nome fra le connessioni dei contesti:
/// SQLite lo distrugge alla chiusura dell'ultima connessione, quindi una connessione tenuta
/// aperta lo fa vivere quanto questo oggetto (si veda <see cref="InMemory"/>).
/// </remarks>
public sealed class WcsDatabase : IDisposable
{
    public const string OperationalFileName = "wcs.db";

    private readonly DbContextOptions<WcsDbContext> _options;
    private readonly SqliteConnection? _inMemoryKeepAlive;

    private WcsDatabase(DbContextOptions<WcsDbContext> options, SqliteConnection? inMemoryKeepAlive, string description)
    {
        _options = options;
        _inMemoryKeepAlive = inMemoryKeepAlive;
        Description = description;
    }

    /// <summary>Dove sta il database, per i log e la diagnostica.</summary>
    public string Description { get; }

    /// <summary>
    /// Apre (o crea) <c>wcs.db</c> nella cartella indicata e porta lo schema all'ultima migrazione.
    /// </summary>
    public static WcsDatabase Open(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, OperationalFileName);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        var options = BuildOptions(connectionString);
        var database = new WcsDatabase(options, null, path);
        database.Prepare(walMode: true);
        return database;
    }

    /// <summary>
    /// Un database in memoria con lo schema corrente: per i test. Vive finche' non viene disposto.
    /// </summary>
    /// <remarks>
    /// Ha la stessa forma del file: ogni contesto apre la propria connessione, e chi trova il
    /// database occupato aspetta (Microsoft.Data.Sqlite riprova fino al timeout del comando),
    /// come sul file. Era una sola connessione condivisa da tutti i contesti, e due contesti che
    /// ci lavoravano nello stesso momento la facevano cadere: nella suite dell'host arrivava
    /// come un 500 a intermittenza, perche' i cicli di sfondo del motore scrivono sullo store
    /// mentre i test fanno le loro richieste. Il database e' condiviso fra le connessioni per
    /// nome (cache condivisa), con un nome nuovo per ogni store cosi' che due store non si
    /// vedano; SQLite lo distrugge alla chiusura dell'ultima connessione, e quella tenuta qui vive
    /// quanto questo oggetto. Il pool e' spento perche' una connessione trattenuta dal pool
    /// terrebbe in vita il database oltre <see cref="Dispose"/>.
    /// </remarks>
    public static WcsDatabase InMemory()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"wcs-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();

        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();

        var database = new WcsDatabase(BuildOptions(connectionString), keepAlive, ":memory:");
        database.Prepare(walMode: false);
        return database;
    }

    /// <summary>Un contesto nuovo: chi lo chiede lo dispone.</summary>
    public WcsDbContext CreateContext() => new(_options);

    public void Dispose()
    {
        // Il file smette di essere scritto qui: e' il momento di riversare il -wal, perche' con le
        // connessioni nel pool il riversamento alla chiusura dell'ultima non arriva mai. E' il
        // gemello del riversamento dello storico (WcsHistoryDatabase), sul file della prova dal
        // vivo del 2026-09-10. Voce D84.
        if (_inMemoryKeepAlive == null)
            Checkpoint();

        _inMemoryKeepAlive?.Dispose();
    }

    /// <summary>
    /// Riversa il <c>-wal</c> di <c>wcs.db</c> nel suo <c>.db</c> e lo tronca, cosi' che il
    /// <c>.db</c> da solo contenga tutto (<c>PRAGMA wal_checkpoint(TRUNCATE)</c>). Un riversamento
    /// che non si completa — un'altra connessione che lo tiene occupato, un errore di SQLite — non
    /// solleva: il contenuto resta nel <c>-wal</c>, leggibile come prima, e lo si dice al registro
    /// del motore, perche' un backup fatto dopo prenderebbe uno stub.
    /// </summary>
    private void Checkpoint()
    {
        try
        {
            using var context = CreateContext();
            var connection = context.Database.GetDbConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = command.ExecuteReader();

            // Tre colonne: busy, pagine nel -wal, pagine riversate. busy diverso da zero vuol dire
            // che il riversamento non e' arrivato in fondo.
            if (reader.Read() && reader.GetInt64(0) != 0)
                Navigator.WriteLog(
                    $"store: il riversamento del -wal di {Description} non e' completo, un'altra connessione lo tiene occupato; il contenuto resta nel -wal",
                    Log.Interface.LogEntryType.Warning);
        }
        catch (Exception failure) when (failure is SqliteException or IOException or InvalidOperationException)
        {
            Navigator.WriteLog(
                $"store: il riversamento del -wal di {Description} e' fallito ({failure.Message}); il contenuto resta nel -wal",
                Log.Interface.LogEntryType.Warning);
        }
    }

    private static DbContextOptions<WcsDbContext> BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder<WcsDbContext>().UseSqlite(connectionString).Options;

    private void Prepare(bool walMode)
    {
        using var context = CreateContext();
        ReportOrphanDoorMappings(context);
        context.Database.Migrate();

        if (!walMode) return;

        context.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
        context.Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;");
    }

    /// <summary>La migrazione che porta la chiave esterna di <c>DoorMappings.ItemId</c>, e con essa la cancellazione delle righe orfane.</summary>
    internal const string DoorMappingForeignKeyMigration = "20260918135742_AddDoorMappingItemForeignKey";

    /// <summary>
    /// Scrive nel registro del motore quello che <see cref="DescribeOrphanDoorMappings"/> ha trovato,
    /// se ha trovato qualcosa da dire.
    /// </summary>
    private static void ReportOrphanDoorMappings(WcsDbContext context)
    {
        if (DescribeOrphanDoorMappings(context) is { } message)
            Navigator.WriteLog(message, Log.Interface.LogEntryType.Warning);
    }

    /// <summary>
    /// Quante righe della mappa porte puntano a un dispositivo che non c'e', e su quali
    /// identificativi, <b>prima</b> che <see cref="DoorMappingForeignKeyMigration"/> le cancelli per
    /// poter mettere la chiave esterna. <c>null</c> quando non c'e' niente da dire.
    /// </summary>
    /// <remarks>
    /// Guarda solo quando quella migrazione e' fra quelle in sospeso: su un database gia' migrato non
    /// c'e' niente da dire (il vincolo impedisce alle orfane di nascere), e su un database appena
    /// creato la tabella non esiste ancora - per questo la guardia e' «qualche migrazione e' gia'
    /// applicata», non «questa migrazione e' in sospeso» da sola. Un guasto nel guardare non ferma
    /// l'avvio: la migrazione fara' comunque il suo lavoro, e quello che si perde e' la riga di
    /// registro, che viene detta.
    /// <para>
    /// Risponde con il testo invece di scriverlo lei perche' <c>Navigator.WriteLog</c> manda la riga
    /// da dentro un <c>Task.Run</c>: una prova che volesse dimostrare che <b>non</b> c'e' niente da
    /// dire non avrebbe niente da aspettare, e sarebbe verde anche un istante prima che la riga
    /// arrivi.
    /// </para>
    /// </remarks>
    internal static string? DescribeOrphanDoorMappings(WcsDbContext context)
    {
        try
        {
            if (!context.Database.GetAppliedMigrations().Any()) return null;
            if (!context.Database.GetPendingMigrations().Contains(DoorMappingForeignKeyMigration)) return null;

            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT ItemId, COUNT(*) FROM DoorMappings " +
                "WHERE ItemId NOT IN (SELECT ItemId FROM PlantItems) GROUP BY ItemId ORDER BY ItemId;";

            var perItem = new List<string>();
            var rows = 0L;
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var count = reader.GetInt64(1);
                    rows += count;

                    // Normalizzato: SQLite tiene i Guid come testo, e la forma con cui ci sono
                    // finiti dipende da chi ha scritto la riga - un identificativo in maiuscolo qui
                    // e in minuscolo altrove manderebbe chi cerca a mani vuote.
                    var stored = reader.GetValue(0);
                    perItem.Add(stored is string text && Guid.TryParse(text, out var itemId)
                        ? $"{itemId} ({count})"
                        : $"{stored} ({count})");
                }

            if (rows == 0) return null;

            return $"store: la migrazione {DoorMappingForeignKeyMigration} sta per cancellare {rows} riga/e della mappa porte " +
                   $"che puntano a dispositivi non piu' presenti - identificativo (righe): {string.Join(", ", perItem)}";
        }
        catch (Exception failure) when (failure is SqliteException or InvalidOperationException or IOException)
        {
            return $"store: non sono riuscito a contare le righe orfane della mappa porte prima della migrazione ({failure.Message}); " +
                   "la migrazione procede lo stesso e quelle righe, se ci sono, verranno cancellate senza essere elencate";
        }
    }
}
