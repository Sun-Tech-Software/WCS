using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Model;

/// <summary>
/// Il grafo dell'impianto come lo vede l'Editor web: dispositivi, giunzioni e il registro dei tipi
/// che il motore dichiara.
/// </summary>
/// <remarks>
/// Costruibile vuoto - lo riempie <see cref="LoadAsync"/> da un host vero - oppure gia' pieno, per i
/// test che non hanno un host contro cui parlare.
/// </remarks>
public sealed class PlantGraph
{
    public IReadOnlyList<PlantItemDto> Items { get; private set; } = [];
    public IReadOnlyList<JunctionDto> Junctions { get; private set; } = [];
    public IReadOnlyList<DeviceTypeDto> Types { get; private set; } = [];

    /// <summary>
    /// Il tipo di un dispositivo, per il codice che porta <see cref="PlantItemDto.DeviceType"/>.
    /// La chiave e' <see cref="DeviceTypeDto.NumericCode"/> letto come lo stesso enum, non
    /// <see cref="DeviceTypeDto.Code"/>: quest'ultimo e' una stringa che nessun dispositivo porta -
    /// il solo legame diretto fra un item e il proprio tipo e' il valore numerico di transizione.
    /// </summary>
    public IReadOnlyDictionary<DeviceTypes, DeviceTypeDto> TypesByDeviceType { get; private set; } =
        new Dictionary<DeviceTypes, DeviceTypeDto>();

    /// <summary>
    /// Cresce a ogni mutazione (<see cref="Fill"/>, <see cref="AddItem"/>, <see cref="RemoveItem"/>,
    /// <see cref="ApplyGeometry"/>, <see cref="AddJunction"/>, <see cref="RemoveJunction"/>,
    /// <see cref="RemoveJunctionsTouching"/>): il modo per chi tiene un risultato calcolato sul grafo
    /// (<c>ChecksPanel</c>, compito 10) di accorgersi che non e' piu' fresco senza confrontare
    /// <see cref="Items"/>/<see cref="Junctions"/> per riferimento - ogni mutatore li sostituisce con
    /// una lista nuova, ma un vecchio riferimento tenuto altrove non lo direbbe da solo.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>
    /// Come <see cref="Revision"/>, ma cresce solo per le mutazioni che possono cambiare l'ESITO di
    /// "Check Junctions" - quali dispositivi sono isolati, quali giunzioni sono scucite - non per uno
    /// spostamento (correzione: prima di questa correzione l'evidenziazione rossa sulla tela
    /// (<c>PlantCanvas.HighlightedItemIds</c>) si spegneva anche al primo trascinamento dopo una
    /// verifica, perche' confrontava con <see cref="Revision"/>, che <see cref="ApplyGeometry"/> fa
    /// crescere anche lei: il gesto naturale dopo aver trovato i problemi - avvicinare i dispositivi
    /// isolati - cancellava l'elenco di che cosa c'era ancora da fare, prima che chi guarda avesse
    /// finito di sistemare tutto). <see cref="ApplyGeometry"/> non tocca questo contatore: una
    /// geometria non cambia ne' quali dispositivi esistono ne' quali giunzioni li collegano.
    /// </summary>
    public int TopologyRevision { get; private set; }

    /// <summary>
    /// Cresce SOLO quando <see cref="Fill"/> sostituisce in blocco <see cref="Items"/>,
    /// <see cref="Junctions"/> e <see cref="Types"/> - il costruttore che riempie subito (usato dai
    /// test) e <see cref="LoadAsync"/> (la lettura vera dall'host, anche dopo un'importazione). NON
    /// cresce per nessuna mutazione incrementale - <see cref="AddItem"/>, <see cref="RemoveItem"/>,
    /// <see cref="AddJunction"/>, <see cref="RemoveJunction"/>, <see cref="RemoveJunctionsTouching"/>,
    /// <see cref="ApplyGeometry"/> - anche se alcune di loro fanno crescere
    /// <see cref="TopologyRevision"/>. E' il segnale per chi deve distinguere una rilettura VERA
    /// dell'impianto da una singola modifica dell'operatore: <c>PlantCanvas</c> lo usa per decidere
    /// quando rifare <c>ViewBox.FitTo</c>, si veda il commento del suo campo
    /// <c>_viewBoxFittedFillRevision</c> - <see cref="TopologyRevision"/> da solo non basta, perche'
    /// creare un dispositivo o disegnare una giunzione lo fa crescere anche lui.
    /// </summary>
    public int FillRevision { get; private set; }

    /// <summary>
    /// Queue item 21: how big each device's load unit is drawn on the live canvas, by
    /// <see cref="PlantItemDto.ItemId"/> - the one thing of a device's configuration the live frame
    /// needs, and <see cref="PlantItemDto"/> does not carry it. Read with the plant
    /// (<see cref="LoadAsync"/>, every reload included, so a size changed by somebody else arrives with
    /// the reloads that already exist) and put right by every configuration write of this page at the
    /// moment it goes through (<see cref="ApplyConfiguration"/>).
    /// </summary>
    private IReadOnlyDictionary<Guid, LoadUnitSize> _loadUnitSizes = new Dictionary<Guid, LoadUnitSize>();

    /// <summary>
    /// Grows whenever a load unit size may have changed: at every <see cref="Fill"/> and at every
    /// <see cref="ApplyConfiguration"/> that really changes one. Apart from <see cref="Revision"/> on
    /// purpose: a size changes nothing that the checks or the drawing's structure depend on, and moving
    /// <see cref="Revision"/> would make them look stale for nothing. The page compares it to the one
    /// it last drew with, to send the canvas a new frame at once instead of at the next live event.
    /// </summary>
    public int LoadUnitSizesRevision { get; private set; }

    public PlantGraph()
    {
    }

    /// <summary>Per i test: un grafo gia' pieno, senza passare da un host.</summary>
    public PlantGraph(IReadOnlyList<PlantItemDto> items, IReadOnlyList<JunctionDto> junctions,
        IReadOnlyList<DeviceTypeDto> types) => Fill(items, junctions, types, new Dictionary<Guid, LoadUnitSize>());

    /// <summary>For tests: a graph already full, the configurations included (only their load unit size is kept).</summary>
    public PlantGraph(IReadOnlyList<PlantItemDto> items, IReadOnlyList<JunctionDto> junctions,
        IReadOnlyList<DeviceTypeDto> types, IReadOnlyList<PlantItemConfigDto> configurations) =>
        Fill(items, junctions, types, SizesOf(configurations));

    /// <summary>
    /// Devices, junctions, types and - queue item 21 - every configuration, for the load unit sizes:
    /// four independent requests in parallel, not in a queue. A configuration read that fails fails
    /// the load like the other three: the host answers all four from the same store, and a plant
    /// drawn with sizes made up in place of the ones it could not read would be the silent default
    /// this codebase does not want.
    /// </summary>
    public async Task LoadAsync(WcsClient client)
    {
        var itemsTask = client.GetItemsAsync();
        var junctionsTask = client.GetJunctionsAsync();
        var typesTask = client.GetDeviceTypesAsync();
        var configurationsTask = client.GetItemConfigurationsAsync();
        await Task.WhenAll(itemsTask, junctionsTask, typesTask, configurationsTask);
        Fill(await itemsTask, await junctionsTask, await typesTask, SizesOf(await configurationsTask));
    }

    /// <summary>
    /// The load unit size a device is drawn with (queue item 21): the one its configuration gives
    /// (<see cref="LoadUnitSize.Of(PlantItemConfigDto)"/>), or the palette's for a device this graph has
    /// no configuration of - the same answer <see cref="LoadUnitSize.Of(int, int)"/> gives a device
    /// whose own size is not set, which is what such a device would be on the WinForms canvas.
    /// </summary>
    public LoadUnitSize LoadUnitSizeOf(Guid itemId) =>
        _loadUnitSizes.TryGetValue(itemId, out var size) ? size : LoadUnitSize.Palette;

    /// <summary>
    /// A configuration this page has just written to the host (queue item 21): the load unit size it
    /// carries is the one to draw from now on. Called by every command that writes a configuration,
    /// right after the write goes through - also when the device cannot be read back afterwards,
    /// because the write itself did go through. <see cref="LoadUnitSizesRevision"/> moves only if the
    /// size really changed.
    /// </summary>
    public void ApplyConfiguration(PlantItemConfigDto configuration)
    {
        var size = LoadUnitSize.Of(configuration);
        if (_loadUnitSizes.TryGetValue(configuration.ItemId, out var known) && known == size) return;
        // A new dictionary, not the old one changed in place: the same rule as Items and Junctions,
        // whose mutators always hand out a new list.
        _loadUnitSizes = new Dictionary<Guid, LoadUnitSize>(_loadUnitSizes) { [configuration.ItemId] = size };
        LoadUnitSizesRevision++;
    }

    /// <summary>
    /// One size per device. The indexer and not ToDictionary: the real host answers one row per
    /// device (the table's key), and a device named twice by an answer that should never come is a
    /// defect the device list already says out loud (<c>DrawioDeviceList</c>) - failing the whole
    /// plant's load here over the size of a load unit would put the canvas out of reach instead.
    /// </summary>
    private static Dictionary<Guid, LoadUnitSize> SizesOf(IReadOnlyList<PlantItemConfigDto> configurations)
    {
        var sizes = new Dictionary<Guid, LoadUnitSize>();
        foreach (var configuration in configurations) sizes[configuration.ItemId] = LoadUnitSize.Of(configuration);
        return sizes;
    }

    /// <summary>
    /// Takes over what another graph is holding, <b>without becoming another object</b>: the same
    /// instance now carries the other one's devices, junctions and types, and its three counters
    /// move exactly as a re-read moves them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists so that a page can keep ONE graph for its whole life</b>, and that is review
    /// finding 2 of the final review. Everything that invalidates a stale result on the screen -
    /// <c>ChecksPanel</c>'s check outcome, <c>PlantCanvas</c>'s red highlighting - does it by
    /// keeping the <see cref="TopologyRevision"/> it computed with and comparing it against the
    /// graph's current one. That comparison only means something between two readings of the SAME
    /// object: a graph that has just been born starts its counters again from one, so a page that
    /// replaces its instance hands the panel a number that may equal the old one by pure
    /// coincidence - and on <c>DrawioEditor</c> it did, leaving on screen the outcome of a check
    /// that the save had just contradicted.
    /// </para>
    /// <para>
    /// <b>The cost is named</b>: adopting always moves the counters, even when the plant came back
    /// identical, so a save invalidates a check result that might still have been true. That is the
    /// right way round for this page - what comes in is the plant read again from the host, which
    /// somebody else may have changed in the meantime, so "this result was computed before the
    /// save" is a true statement about it - and the other way round is a result standing next to a
    /// plant that has moved under it, which is the defect this repairs.
    /// </para>
    /// </remarks>
    public void Adopt(PlantGraph other) => Fill(other.Items, other.Junctions, other.Types, other._loadUnitSizes);

    private void Fill(IReadOnlyList<PlantItemDto> items, IReadOnlyList<JunctionDto> junctions,
        IReadOnlyList<DeviceTypeDto> types, IReadOnlyDictionary<Guid, LoadUnitSize> loadUnitSizes)
    {
        Items = items;
        Junctions = junctions;
        Types = types;
        TypesByDeviceType = types.ToDictionary(t => (DeviceTypes)t.NumericCode);
        _loadUnitSizes = loadUnitSizes;
        Revision++;
        TopologyRevision++;
        FillRevision++;
        LoadUnitSizesRevision++;
    }

    /// <summary>
    /// Applica al modello locale una geometria appena scritta con successo sull'host (compito 5):
    /// sostituisce X/Y/Width/Height/Rotation dei soli dispositivi elencati, senza ricaricare l'intero
    /// grafo da capo - <see cref="Items"/> ha <c>private set</c> apposta perche' nessun chiamante
    /// esterno lo rimpiazzi a mano. I dispositivi non elencati restano invariati. Fa crescere
    /// <see cref="Revision"/> ma non <see cref="TopologyRevision"/>: uno spostamento non cambia quali
    /// dispositivi esistono ne' come sono collegati, si veda il commento di quel contatore.
    /// </summary>
    public void ApplyGeometry(
        IReadOnlyCollection<PlantItemGeometryDto> geometries,
        IReadOnlyCollection<PlantItemGeometryVersionDto> confirmations)
    {
        var byId = geometries.ToDictionary(g => g.ItemId);
        var versionsById = confirmations.ToDictionary(c => c.ItemId, c => c.GeometryVersion);
        if (byId.Count != versionsById.Count || !byId.Keys.ToHashSet().SetEquals(versionsById.Keys))
            throw new ArgumentException("Valori e conferme devono riferirsi agli stessi dispositivi.", nameof(confirmations));

        Items = Items
            .Select(item => byId.TryGetValue(item.ItemId, out var geometry)
                ? item with
                {
                    X = geometry.X,
                    Y = geometry.Y,
                    Width = geometry.Width,
                    Height = geometry.Height,
                    Rotation = geometry.Rotation,
                    GeometryVersion = versionsById[item.ItemId]
                }
                : item)
            .ToList();
        Revision++;
    }

    /// <summary>
    /// Aggiunge un dispositivo al modello locale (compito 7: creare, duplicare), o lo sostituisce se
    /// un dispositivo con lo stesso <see cref="PlantItemDto.ItemId"/> era gia' presente - quest'ultimo
    /// caso non serve a un gesto nuovo, ma tiene il metodo corretto anche se un giorno un comando
    /// volesse rimettere un dispositivo appena riletto dall'host invece di uno mai visto prima.
    /// </summary>
    public void AddItem(PlantItemDto item)
    {
        var withoutItem = Items.Where(i => i.ItemId != item.ItemId).ToList();
        withoutItem.Add(item);
        Items = withoutItem;
        Revision++;
        TopologyRevision++;
    }

    /// <summary>Toglie un dispositivo dal modello locale, per identificativo. Nessuna eccezione se non c'e' - lo stesso comportamento silenzioso di <see cref="RemoveJunctionsTouching"/>.</summary>
    public void RemoveItem(Guid itemId)
    {
        Items = Items.Where(i => i.ItemId != itemId).ToList();
        Revision++;
        TopologyRevision++;
    }

    /// <summary>
    /// Aggiunge una giunzione al modello locale (compito 7: l'annulla di una cancellazione la
    /// rimette), o la sostituisce se una giunzione con lo stesso <see cref="JunctionDto.JunctionId"/>
    /// era gia' presente - come <see cref="AddItem"/> fa per <see cref="PlantItemDto.ItemId"/>.
    /// Non e' un caso di scuola: un
    /// impianto migrato dalla tabella legacy <c>NAVIGATOR_JUNCTIONS</c> puo' portare due giunzioni
    /// sulla stessa coppia (origine, destinazione) con priorita' diverse (si veda il remarks di
    /// <c>DeleteItemCommand</c>) - in quel caso l'host collassa comunque sulla
    /// coppia e una seconda <c>POST /plant/junctions</c> restituisce la giunzione gia' esistente:
    /// senza questa deduplica, <c>DeleteItemCommand.UndoAsync</c> la infilerebbe due volte nella
    /// lista locale con lo stesso <c>JunctionId</c>.
    /// </summary>
    public void AddJunction(JunctionDto junction)
    {
        var withoutJunction = Junctions.Where(j => j.JunctionId != junction.JunctionId).ToList();
        withoutJunction.Add(junction);
        Junctions = withoutJunction;
        Revision++;
        TopologyRevision++;
    }

    /// <summary>
    /// Toglie una giunzione sola dal modello locale, per identificativo (compito 8: cancellare una
    /// giunzione, annullare la sua creazione). Nessuna eccezione se non c'e' - come
    /// <see cref="RemoveItem"/> e <see cref="RemoveJunctionsTouching"/>.
    /// </summary>
    public void RemoveJunction(Guid junctionId)
    {
        Junctions = Junctions.Where(j => j.JunctionId != junctionId).ToList();
        Revision++;
        TopologyRevision++;
    }

    /// <summary>
    /// Toglie ogni giunzione che tocca <paramref name="itemId"/>, da origine o da destinazione: la
    /// stessa regola che <c>PlantStore.DeleteItemAsync</c> applica sull'host quando un dispositivo
    /// viene cancellato (compito 7) - senza questo metodo il modello locale mostrerebbe archi verso
    /// un dispositivo che l'host non ha piu', un dato falso quanto uno zero che finge un dato vero.
    /// </summary>
    public void RemoveJunctionsTouching(Guid itemId)
    {
        Junctions = Junctions.Where(j => j.SourceItemId != itemId && j.DestinationItemId != itemId).ToList();
        Revision++;
        TopologyRevision++;
    }

    /// <summary>
    /// Vero se il dispositivo ha una posizione da disegnare (compito 5): condivisa dalla tela
    /// (<see cref="Wcs.WebEditor.Drawing.PlantCanvas"/>, che decide chi disegnare, e
    /// <see cref="Wcs.WebEditor.Drawing.ViewBox.FitTo"/>, che decide chi inquadrare) e dal controllo
    /// dei dispositivi non collocati (compito 10, <see cref="Wcs.WebEditor.Checks.JunctionChecks"/>) -
    /// la stessa regola, non copie scritte a mano che potrebbero derivare l'una dall'altra.
    /// </summary>
    public static bool IsPlaced(PlantItemDto item) => item.X is not null && item.Y is not null;
}
