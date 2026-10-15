using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Un comando verso un dispositivo che non esiste si chiude in errore, non come riuscito.
/// </summary>
/// <remarks>
/// I gestori <c>HandleItem*</c> di <c>CommandToDo</c> hanno lo stesso scheletro — cerca il
/// dispositivo, se c'e' agisci — e sei su sette scrivevano <c>Done</c> fuori dall'<c>if</c>: un
/// <c>ItemCommandId</c> vuoto, di un dispositivo cancellato o di una fotografia vecchia
/// dell'impianto risultava eseguito con successo senza aver fatto niente (voce <c>C38</c>, riparata
/// prima in un solo gestore).
/// </remarks>
public sealed class CommandOnMissingDeviceTest : IAsyncLifetime
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

    private static Command NewCommand(Command.CommandTypes type) =>
        new(loadingFromDatabase: true)
        {
            Date = DateTime.Now,
            CommandType = type,
            Status = Command.StatusTypes.ToDo,
            WorkstationId = null,
            ItemCommandId = Guid.NewGuid() // nessun dispositivo con questo identificativo
        };

    [Theory]
    [InlineData(Command.CommandTypes.ItemExecuteStep)]
    [InlineData(Command.CommandTypes.ItemSetOffLine)]
    [InlineData(Command.CommandTypes.ItemSetOnLine)]
    [InlineData(Command.CommandTypes.ItemStepDone)]
    [InlineData(Command.CommandTypes.ItemRejectLu)]
    [InlineData(Command.CommandTypes.ItemUnloadLu)]
    public async Task ACommandTowardsAMissingDeviceClosesInError(Command.CommandTypes type)
    {
        var command = NewCommand(type);
        await ORM.CommandToDo.Update(command);

        await CommandToDo.Instance.Execute().WaitAsync(TimeSpan.FromSeconds(30));

        var executed = Assert.Single(CommandToDo.Instance.List, c => c.IdCommandToDo == command.IdCommandToDo);
        Assert.Equal(Command.StatusTypes.Error, executed.Status);
        Assert.Contains(command.ItemCommandId.ToString(), executed.ErrorDescription);
    }
}
