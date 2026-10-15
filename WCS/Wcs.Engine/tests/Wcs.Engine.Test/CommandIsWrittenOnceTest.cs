using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Un comando costruito e scritto una volta lascia <b>una</b> riga in <c>Commands</c>.
/// </summary>
/// <remarks>
/// Ogni setter di <see cref="Command"/> con <c>updateDb: true</c> lanciava un <c>Task.Run</c> non
/// atteso che scriveva il comando: chi ne costruisce uno ne imposta otto o nove proprieta' di fila,
/// e tutte le istantanee partivano con <c>IdCommandToDo == 0</c>, quindi <c>UpsertAsync</c> le
/// inseriva come righe distinte. Nel database di collaudo si vedevano quattro gruppi da nove righe
/// identiche al tick; alla ripresa <c>LoadFromDatabase</c> avrebbe accodato nove volte lo stesso
/// comando pendente.
/// </remarks>
public sealed class CommandIsWrittenOnceTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    public CommandIsWrittenOnceTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
    }

    public void Dispose()
    {
        Navigator.StateWriter = null;
        Navigator.Store = null;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ACommandBuiltWithEightPropertiesLeavesOneRow()
    {
        await using var writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(_database).ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) });
        Navigator.StateWriter = writer;

        // Il costruttore normale, non quello del caricamento: e' il gesto di chi crea un comando
        // (Navigator.WriteAlert, AutomationDevice.AddMissionToPickUp, i quattro del gestore di
        // connessione), che imposta le proprieta' e poi chiama Update una volta sola.
        var command = new Command
        {
            Date = new DateTime(2026, 9, 10, 15, 39, 49),
            CommandType = Command.CommandTypes.MoveLuAutomatic,
            LuId = 869284,
            Status = Command.StatusTypes.Done,
            WorkstationId = "PC-01",
            BufferCellId = 5,
            PickUpDoorCode = "P01A",
            StorageDoorCode = "S01A"
        };

        // La barriera dimostra che i setter di un comando senza chiave non hanno accodato nulla.
        await writer.FlushAsync();
        Assert.Equal(0, await CountAsync());

        await ORM.CommandToDo.Update(command);

        Assert.Equal(1, await CountAsync());
        Assert.True(command.IdCommandToDo > 0);
    }

    [Fact]
    public async Task APropertyChangeOnAPersistedCommandIsQueuedBeforeTheSetterReturns()
    {
        await using var writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(_database).ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) });
        Navigator.StateWriter = writer;
        var command = new Command { Status = Command.StatusTypes.Running };
        await ORM.CommandToDo.Update(command);
        Assert.True(command.IdCommandToDo > 0);

        command.Status = Command.StatusTypes.Error;
        await writer.FlushAsync();

        await using var context = _database.CreateContext();
        var stored = Assert.Single(await context.Commands.AsNoTracking().ToListAsync());
        Assert.Equal((short)Command.StatusTypes.Error, stored.Status);
    }

    private async Task<int> CountAsync()
    {
        await using var context = _database.CreateContext();
        return await context.Commands.AsNoTracking().CountAsync();
    }
}
