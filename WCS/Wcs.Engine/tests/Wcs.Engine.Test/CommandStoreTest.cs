using System.Collections.Concurrent;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// I comandi in ingresso sul database del WCS: la chiave che torna a chi scrive, la coda di quelli
/// da eseguire nell'ordine di arrivo, lo stato che li toglie dalla coda.
/// </summary>
public sealed class CommandStoreTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    public CommandStoreTest()
    {
        Navigator.Store = _database;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        _database.Dispose();
    }

    private static Command NewCommand(Command.CommandTypes type, Command.StatusTypes status = Command.StatusTypes.ToDo) =>
        new(loadingFromDatabase: true)
        {
            Date = new DateTime(2026, 9, 2, 12, 0, 0),
            CommandType = type,
            Status = status,
            WorkstationId = "PC-01",
            LuId = 77,
            PickUpDoorCode = "P01A",
            BufferCellId = 5,
            ItemCommandId = Guid.NewGuid()
        };

    [Fact]
    public async Task ANewCommandGetsItsKeyBackAndComesOutOfTheQueueInOrder()
    {
        var first = NewCommand(Command.CommandTypes.ItemExecuteStep);
        var second = NewCommand(Command.CommandTypes.MoveLuManual);

        await ORM.CommandToDo.Update(first);
        await ORM.CommandToDo.Update(second);

        Assert.True(first.IdCommandToDo > 0);
        Assert.True(second.IdCommandToDo > first.IdCommandToDo);

        var queue = new ConcurrentQueue<Command>();
        await ORM.CommandToDo.LoadFromDatabase(queue);

        var loaded = queue.ToArray();
        Assert.Equal([first.IdCommandToDo, second.IdCommandToDo], loaded.Select(c => c.IdCommandToDo));
        Assert.Equal(Command.CommandTypes.ItemExecuteStep, loaded[0].CommandType);
        Assert.Equal("PC-01", loaded[0].WorkstationId);
        Assert.Equal(77, loaded[0].LuId);
        Assert.Equal("P01A", loaded[0].PickUpDoorCode);
        Assert.Equal(5, loaded[0].BufferCellId);
        Assert.Equal(first.ItemCommandId, loaded[0].ItemCommandId);
        Assert.Equal(first.Date, loaded[0].Date);
    }

    [Fact]
    public async Task ACommandThatIsRunningOrDoneIsNotLoadedAgain()
    {
        var waiting = NewCommand(Command.CommandTypes.ItemExecuteStep);
        var running = NewCommand(Command.CommandTypes.ItemExecuteStep, Command.StatusTypes.Running);
        var done = NewCommand(Command.CommandTypes.ItemExecuteStep, Command.StatusTypes.Done);

        await ORM.CommandToDo.Update(waiting);
        await ORM.CommandToDo.Update(running);
        await ORM.CommandToDo.Update(done);

        var queue = new ConcurrentQueue<Command>();
        await ORM.CommandToDo.LoadFromDatabase(queue);

        Assert.Equal([waiting.IdCommandToDo], queue.Select(c => c.IdCommandToDo));
    }

    [Fact]
    public async Task SettingTheStatusWithAnErrorTakesTheCommandOutOfTheQueueAndKeepsTheReason()
    {
        var command = NewCommand(Command.CommandTypes.ItemExecuteStep);
        await ORM.CommandToDo.Update(command);

        await ORM.CommandToDo.SetStatus(command.IdCommandToDo, Command.StatusTypes.Error, "dispositivo occupato");

        var queue = new ConcurrentQueue<Command>();
        await ORM.CommandToDo.LoadFromDatabase(queue);
        Assert.Empty(queue);

        await using var context = _database.CreateContext();
        var row = Assert.Single(context.Commands);
        Assert.Equal((short)Command.StatusTypes.Error, row.Status);
        Assert.Equal("dispositivo occupato", row.ErrorDescription);
        Assert.Equal(0, row.ShowStatus);
    }

    [Fact]
    public async Task UpdatingAnExistingCommandKeepsTheColumnsTheEngineDoesNotGovern()
    {
        var command = NewCommand(Command.CommandTypes.ItemExecuteStep);
        await ORM.CommandToDo.Update(command);

        // Chi ha scritto il comando da fuori ha messo utente e provenienza: il motore non li tocca.
        await using (var context = _database.CreateContext())
        {
            var row = context.Commands.Single();
            row.UserId = "operatore";
            row.FromApi = true;
            row.ShowStatus = 1;
            await context.SaveChangesAsync();
        }

        command.Info = "aggiornato dal motore";
        await ORM.CommandToDo.Update(command);

        await using var reading = _database.CreateContext();
        var updated = Assert.Single(reading.Commands);
        Assert.Equal("aggiornato dal motore", updated.Info);
        Assert.Equal("operatore", updated.UserId);
        Assert.True(updated.FromApi);
        Assert.Equal(1, updated.ShowStatus);
    }
}
