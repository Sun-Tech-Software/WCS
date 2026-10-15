using System.Reflection;
using System.Text;
using Log.Interface;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Correzione della revisione al compito 6: un blob del disegno malformato non deve impedire
/// l'apertura dei database del motore. La prima versione lasciava sollevare
/// <c>GeometryMigration.RunOnceAsync</c> dentro <c>Navigator.OpenDatabases</c>: gli scrittori a
/// coda gia' creati (con il loro ciclo di sfondo) e i tre database precedenti restavano senza
/// essere ne' fermati ne' disposti, e <c>_databasesOpen</c> restava falso, cosicche' ogni
/// ritentativo di <c>RetryEngineLoadAsync</c> (voce D145) ne abbandonava un altro paio.
/// </summary>
/// <remarks>
/// Il test chiama <c>OpenDatabases</c> per riflessione, non <c>StartAsync</c>: basta il metodo
/// corretto, e cosi' non serve avviare anche <c>LoadNavigatorData</c> (magazzino, dispositivi,
/// cicli di fondo) che non c'entra con questo difetto.
/// </remarks>
public sealed class GeometryMigrationFailureDoesNotBlockStartupTest : IDisposable
{
    private readonly string _previousDirectory = Navigator.DatabaseDirectory;
    private readonly IWriter? _previousWriter = Navigator.EventLogWriter;
    private readonly bool _previousDebugLog = Navigator.ActivateDebugLog;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wcs-geometry-{Guid.NewGuid():N}");

    public GeometryMigrationFailureDoesNotBlockStartupTest()
    {
        Directory.CreateDirectory(_root);
        Navigator.DatabaseDirectory = _root;
    }

    public void Dispose()
    {
        Navigator.Dispose();
        Navigator.DatabaseDirectory = _previousDirectory;
        Navigator.SetEventLogWriter(_previousWriter);
        Navigator.ActivateDebugLog = _previousDebugLog;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* file ancora aperto: pazienza */ }
    }

    private static void CallOpenDatabases()
    {
        var method = typeof(Navigator).GetMethod("OpenDatabases", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        try
        {
            method!.Invoke(null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    [Fact]
    public async Task AMalformedBlobDoesNotPreventTheDatabasesFromOpening()
    {
        Navigator.ActivateDebugLog = true;
        var writer = new RecordingWriter();
        Navigator.SetEventLogWriter(writer);

        // Un wcs.db con il disegno gia' presente, ma illeggibile: il primo OpenDatabases lo trova
        // gia' li' e prova a migrarlo.
        using (var seed = WcsDatabase.Open(_root))
        {
            await using var context = seed.CreateContext();
            var diagram = await PlantStore.GetOrCreateDiagramAsync(context);
            diagram.DiagramXml = Encoding.UTF8.GetBytes("<non e' xml valido");
            await context.SaveChangesAsync();
        }

        // Prima della correzione questa chiamata sollevava e non tornava mai: la si vuole vedere
        // tornare pulita.
        CallOpenDatabases();

        Assert.True(Navigator.IsConfigured);
        Assert.NotNull(Navigator.StateWriter);
        Assert.NotNull(Navigator.HistoryWriter);
        Assert.NotNull(Navigator.PlcLogWriter);

        await using (var context = Navigator.Store!.CreateContext())
        {
            var diagram = await PlantStore.LoadDiagramAsync(context);
            Assert.NotNull(diagram);
            Assert.Null(diagram!.GeometryMigratedAt);
        }

        var loggedError = await writer.WaitForEntryAsync(
            e => e.Type == LogEntryType.Error && e.Description.Contains("geometria", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5));
        Assert.True(loggedError, "Nessuna riga di errore per la migrazione fallita.");
    }

    /// <summary>
    /// Non marcare vuol dire anche questo: al giro successivo (blob ancora malformato) si riprova
    /// e si arriva comunque in fondo, invece di restare bloccati per sempre sul primo fallimento.
    /// </summary>
    [Fact]
    public async Task ARetryAfterTheSameFailureStillReachesTheEnd()
    {
        using (var seed = WcsDatabase.Open(_root))
        {
            await using var context = seed.CreateContext();
            var diagram = await PlantStore.GetOrCreateDiagramAsync(context);
            diagram.DiagramXml = Encoding.UTF8.GetBytes("<ancora non xml");
            await context.SaveChangesAsync();
        }

        CallOpenDatabases();
        Assert.True(Navigator.IsConfigured);

        // Un secondo giro come farebbe RetryEngineLoadAsync: Dispose riporta _databasesOpen a
        // falso, come fa Navigator.StopAsync fra un tentativo e l'altro.
        Navigator.Dispose();
        CallOpenDatabases();

        Assert.True(Navigator.IsConfigured);
        Assert.NotNull(Navigator.StateWriter);
    }

    private sealed class RecordingWriter : IWriter
    {
        private readonly List<(string Description, LogEntryType Type)> _entries = new();
        private readonly SemaphoreSlim _signal = new(0);

        public void WriteLog(string description, LogEntryType logType)
        {
            lock (_entries) _entries.Add((description, logType));
            _signal.Release();
        }

        public async Task<bool> WaitForEntryAsync(
            Func<(string Description, LogEntryType Type), bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_entries)
                    if (_entries.Any(predicate)) return true;

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                await _signal.WaitAsync(remaining > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : remaining);
            }

            lock (_entries)
                return _entries.Any(predicate);
        }
    }
}
