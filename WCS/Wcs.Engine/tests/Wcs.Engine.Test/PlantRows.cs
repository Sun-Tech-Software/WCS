using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Test;

/// <summary>
/// Mette sullo store la riga di <c>PlantItems</c> di un dispositivo, senza passare dal motore.
/// </summary>
/// <remarks>
/// Serve da quando <c>DoorMapping.ItemId</c> ha una chiave esterna verso <c>PlantItems</c>: una
/// mappa porte su un identificativo che nessuna riga rappresenta non si scrive piu' - il database la
/// rifiuta con «FOREIGN KEY constraint failed», che e' proprio il difetto che la chiave esterna e'
/// li' a impedire. I test a cui serve soltanto «un dispositivo qualunque a cui appendere una porta»
/// chiamano questo, invece di costruire un <c>AutomationDevice</c> vero che non userebbero.
/// <para>
/// La riga va sullo store e <b>non</b> in <c>PathDevices</c>: un dispositivo che esiste sul database
/// ma non nel diagramma in memoria e' uno stato vero, ed e' quello che qualche test di qui vuole.
/// </para>
/// </remarks>
internal static class PlantRows
{
    public static async Task EnsureAsync(params Guid[] itemIds)
    {
        var store = Navigator.Store
            ?? throw new InvalidOperationException("Navigator.Store non e' impostato: la riga del dispositivo non ha dove andare.");

        await using var context = store.CreateContext();
        foreach (var itemId in itemIds)
            if (!await context.PlantItems.AnyAsync(i => i.ItemId == itemId))
                context.PlantItems.Add(new PlantItem { ItemId = itemId });

        await context.SaveChangesAsync();
    }
}
