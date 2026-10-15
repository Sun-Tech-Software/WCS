using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// La rete sotto tutti i comandi: il <c>try/catch</c> per comando dentro
/// <see cref="CommandToDo.Execute"/>. Un gestore che solleva lasciava il comando «in esecuzione»
/// per sempre — la ricarica della coda pesca solo gli stati minori di <c>Running</c>, quindi quella
/// riga non tornava mai — e l'errore finiva solo nel registro di diagnostica, spento per
/// impostazione. Ora il comando si chiude in <c>Error</c> con il messaggio, e la passata prosegue
/// con i comandi seguenti.
/// </summary>
/// <remarks>
/// Il gestore che solleva davvero, e senza il WMS: <c>DeleteLuFromExtractionQueue</c> con una
/// <c>LuId</c> diversa da zero chiede al confine la descrizione dell'unita' di carico, e
/// <c>Navigator.RequireWarehouse</c> solleva <see cref="InvalidOperationException"/> perche' in
/// questa classe il magazzino non e' cablato. (Fino al compito 16 la stessa riga leggeva
/// <c>Navigator.Database</c>, la connessione a SQL Server, e sollevava un
/// <see cref="NullReferenceException"/>: il motore non ha piu' quella connessione.) Non e' un
/// rifiuto scritto nel gestore: su quella strada il gestore non tocca <c>Status</c>, quindi
/// <c>Error</c> e la descrizione possono venire solo dal <c>catch</c> del ciclo.
/// Come in <see cref="TestRouteCommandTest"/>, ogni setter di <see cref="Command"/> scrive sul
/// database: serve lo scrittore a coda, altrimenti SQLite in memoria risponde «database is locked»
/// alle scritture concorrenti. Le verifiche stanno sugli oggetti in memoria e non sulle righe,
/// perche' quelle scritture partono in sottofondo e attenderle sarebbe una prova a tempo.
/// </remarks>
public sealed class CommandExecutionErrorTest : IAsyncLifetime
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private WcsDatabase _store = null!;
    private QueuedDatabaseWriter<WcsWrite> _writer = null!;

    public Task InitializeAsync()
    {
        _store = WcsDatabase.InMemory();
        _writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(_store).ExecuteAsync,
            new QueuedDatabaseWriterOptions
                { DropOldestWhenFull = false, MaxBatchSize = 100, FlushInterval = TimeSpan.FromMilliseconds(10) });

        Navigator.Store = _store;
        Navigator.StateWriter = _writer;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        // La coda dei comandi e' un singleton di processo: un'altra classe di test potrebbe averci
        // lasciato dentro dei comandi, e la lista letta alla fine non sarebbe piu' solo questa passata.
        CommandToDo.Instance.Clear();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        CommandToDo.Instance.Clear();
        Navigator.NavStatus = _previousStatus;
        Navigator.StateWriter = null;
        Navigator.Store = null;
        await _writer.StopAsync();
        _store.Dispose();
    }

    /// <summary>
    /// Nessuna postazione: la riga di registro porta l'identificativo della postazione cosi' com'e'
    /// (nullo qui). Fino al compito 16 <c>GetDescPostazione</c> ne cercava nome e dominio su SQL
    /// Server, e quella lettura sta fuori dal <c>try</c> per comando: un suo errore fermava
    /// l'intera passata invece del solo comando. Adesso non c'e' piu' nessuna lettura da fare.
    /// </summary>
    private static Command NewCommand(Command.CommandTypes type, decimal luId = 0) =>
        new(loadingFromDatabase: true)
        {
            Date = DateTime.Now,
            CommandType = type,
            Status = Command.StatusTypes.ToDo,
            WorkstationId = null,
            LuId = luId
        };

    [Fact]
    public async Task AHandlerThatThrowsClosesItsCommandInErrorAndTheNextOneIsStillExecuted()
    {
        // Il presupposto del caso: senza magazzino la lettura del codice UDC solleva. Se un giorno
        // questa classe cablasse un gestionale finto, il gestore risponderebbe invece di sollevare
        // e il test andrebbe riscritto: meglio vederlo qui che scoprirlo da un'asserzione oscura
        // piu' sotto. La suite gira in serie (DisableTestParallelization), quindi nessun'altra
        // classe puo' avere un magazzino cablato mentre questa gira.
        Assert.Null(Navigator.Warehouse);

        // Primo in coda: il gestore solleva. Secondo: MoveLuAutomatic, che lo switch chiude in Done.
        var throwing = NewCommand(Command.CommandTypes.DeleteLuFromExtractionQueue, luId: 77);
        var following = NewCommand(Command.CommandTypes.MoveLuAutomatic);

        await ORM.CommandToDo.Update(throwing);
        await ORM.CommandToDo.Update(following);

        // La coda esce nell'ordine di arrivo: il comando che solleva e' il primo della passata.
        Assert.True(following.IdCommandToDo > throwing.IdCommandToDo);

        await CommandToDo.Instance.Execute().WaitAsync(TimeSpan.FromSeconds(30));

        var executed = CommandToDo.Instance.List;
        var failed = Assert.Single(executed, c => c.IdCommandToDo == throwing.IdCommandToDo);
        var next = Assert.Single(executed, c => c.IdCommandToDo == following.IdCommandToDo);

        Assert.Equal(Command.StatusTypes.Error, failed.Status);
        // Il messaggio e' quello dell'eccezione, che qui e' quella di RequireWarehouse; non lo si
        // fissa alla lettera perche' il punto e' che il catch lo riporti, non quale sia.
        Assert.False(string.IsNullOrWhiteSpace(failed.ErrorDescription));

        // Il punto del rimedio: l'eccezione chiude il suo comando, non la passata.
        Assert.Equal(Command.StatusTypes.Done, next.Status);
    }
}
