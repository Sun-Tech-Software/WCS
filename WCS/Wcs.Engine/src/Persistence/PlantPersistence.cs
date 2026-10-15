using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Quello che l'Editor puo' fare sul database del WCS per il diagramma: caricare e salvare il
/// modello disegnato, togliere dispositivi e giunzioni che non esistono piu'.
/// </summary>
/// <remarks>
/// L'Editor gira nello stesso processo del motore e prima scriveva le tabelle del diagramma con
/// SQL proprio, su una connessione propria. Ora passa da qui: le scritture vanno dallo scrittore
/// di stato e vengono attese, perche' chi salva il diagramma vuole sapere che e' salvato. Senza
/// scrittore si scrive direttamente sullo store; senza store non c'e' nulla su cui scrivere e i
/// metodi non fanno niente.
/// </remarks>
public static class PlantPersistence
{
    /// <summary>Il modello Syncfusion salvato in XML, o nullo se non e' mai stato salvato.</summary>
    public static async Task<byte[]?> LoadDiagramXmlAsync()
    {
        var store = Navigator.Store;
        if (store == null) return null;

        await using var context = store.CreateContext();
        var diagram = await PlantStore.LoadDiagramAsync(context);
        return diagram?.DiagramXml;
    }

    /// <summary>Salva il modello nei due formati, binario e XML.</summary>
    public static Task SaveDiagramAsync(byte[] binary, byte[] xml) =>
        WriteAsync(async context =>
        {
            var diagram = await PlantStore.GetOrCreateDiagramAsync(context);
            diagram.Diagram = binary;
            diagram.DiagramXml = xml;
        });

    /// <summary>Toglie un dispositivo e le giunzioni che lo toccano.</summary>
    public static Task RemoveItemAsync(Guid itemId) =>
        WriteAsync(context => PlantStore.DeleteItemAsync(context, itemId));

    /// <summary>Toglie una giunzione.</summary>
    public static Task RemoveJunctionAsync(Guid junctionId) =>
        WriteAsync(context => PlantStore.DeleteJunctionAsync(context, junctionId));

    /// <summary>
    /// Toglie dal database i dispositivi che non sono fra quelli indicati: e' la pulizia che
    /// l'Editor fa al salvataggio, con i dispositivi del diagramma in memoria.
    /// </summary>
    public static async Task RemoveItemsNotInAsync(IReadOnlyCollection<Guid> keptItemIds)
    {
        var store = Navigator.Store;
        if (store == null) return;

        List<Guid> stored;
        await using (var context = store.CreateContext())
            stored = await PlantStore.LoadItemIdsAsync(context);

        foreach (var itemId in stored.Where(id => !keptItemIds.Contains(id)))
            await RemoveItemAsync(itemId);
    }

    /// <summary>Lo stato di compattazione, letto dalla riga del diagramma.</summary>
    internal static async Task<short> LoadCompactingStatusAsync()
    {
        var store = Navigator.Store;
        if (store == null) return 0;

        await using var context = store.CreateContext();
        var diagram = await PlantStore.LoadDiagramAsync(context);
        return diagram?.CompactingStatus ?? 0;
    }

    internal static Task SaveCompactingStatusAsync(short value) =>
        WriteAsync(async context => (await PlantStore.GetOrCreateDiagramAsync(context)).CompactingStatus = value);

    /// <summary>
    /// I due contatori che il motore rinnova a ogni giro: chi legge la riga da fuori vede che il
    /// motore e' vivo. Il contatore di stato si rinnova solo quando lo stato e' cambiato.
    /// </summary>
    /// <remarks>
    /// Aggiornamento mirato sulle due colonne, non <c>GetOrCreateDiagramAsync</c>: quella caricava
    /// l'entita' intera - <c>DiagramXml</c> compreso, 27,7 MB sul database di collaudo - una volta
    /// al secondo per cambiare due <c>int</c>, e il costo si pagava dentro il lotto dell'unico
    /// thread che scrive missioni, dispositivi e comandi. I contatori non hanno lettori nel WCS:
    /// restano per compatibilita' con chi legge <c>DiagramDto</c>.
    /// </remarks>
    internal static Task SaveCountersAsync(int checkCounter, int? statusCounter) =>
        WriteAsync(async context =>
        {
            // Se la riga e' gia' tracciata in questo lotto (un salvataggio del diagramma dell'Editor
            // nello stesso lotto), il SaveChanges che segue riscriverebbe i contatori come li aveva
            // letti: in quel caso si scrive sull'entita', non sotto di essa.
            var tracked = context.ChangeTracker.Entries<PlantDiagram>().Select(e => e.Entity)
                .FirstOrDefault(d => d.Id == PlantDiagram.DefaultId);
            if (tracked != null)
            {
                tracked.CheckCounter = checkCounter;
                if (statusCounter.HasValue) tracked.StatusCounter = statusCounter.Value;
                return;
            }

            var rows = context.PlantDiagrams.Where(d => d.Id == PlantDiagram.DefaultId);
            var updated = statusCounter.HasValue
                ? await rows.ExecuteUpdateAsync(set => set
                    .SetProperty(d => d.CheckCounter, checkCounter)
                    .SetProperty(d => d.StatusCounter, statusCounter.Value))
                : await rows.ExecuteUpdateAsync(set => set.SetProperty(d => d.CheckCounter, checkCounter));
            if (updated > 0) return;

            // La riga non c'e' ancora: e' il primo giro del motore su un database nuovo.
            var created = await PlantStore.GetOrCreateDiagramAsync(context);
            created.CheckCounter = checkCounter;
            if (statusCounter.HasValue) created.StatusCounter = statusCounter.Value;
        });

    /// <summary>Le righe della mappa porte di un dispositivo, nell'ordine in cui sono state scritte.</summary>
    public static async Task<List<DoorMapping>> LoadDoorMappingsAsync(Guid itemId)
    {
        var store = Navigator.Store;
        if (store == null) return [];

        await using var context = store.CreateContext();
        return await DoorMappingStore.ByItemAsync(context, itemId);
    }

    /// <summary>
    /// Sostituisce la mappa porte di un dispositivo con le righe date: e' il salvataggio della
    /// finestra di collegamento alle celle. I due stati delle porte che restano non si perdono.
    /// </summary>
    public static Task SaveDoorMappingsAsync(Guid itemId, IReadOnlyCollection<DoorMapping> mappings) =>
        WriteAsync(context => DoorMappingStore.ReplaceForItemAsync(context, itemId, mappings));

    /// <summary>
    /// Collega un dispositivo a una sola cella polmone dalla porta A, come faceva l'Editor con
    /// una cancellazione e un inserimento.
    /// </summary>
    public static Task LinkBufferCellAsync(Guid itemId, string doorCode, decimal cellId) =>
        SaveDoorMappingsAsync(itemId, [new DoorMapping { ItemId = itemId, DoorCode = doorCode, CellId = cellId, Side = "A" }]);

    private static async Task WriteAsync(WcsWrite write)
    {
        if (Navigator.StateWriter is { } writer)
        {
            await writer.EnqueueAndWaitAsync(write);
            return;
        }

        var store = Navigator.Store;
        if (store == null) return;

        await using var context = store.CreateContext();
        await write(context);
        await context.SaveChangesAsync();
    }
}
