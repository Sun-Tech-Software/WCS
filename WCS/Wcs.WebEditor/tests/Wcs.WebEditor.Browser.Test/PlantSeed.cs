using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// L'impianto minimo su cui queste prove lavorano: <b>due dispositivi collocati e nessuna
/// giunzione</b>, seminati con il client vero dell'API - cioe' con lo stesso codice con cui
/// l'Editor scrive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Le rotazioni non sono zero, e non sono 45.</b> Non zero perche' e' esattamente li' che questo
/// sotto-progetto ha sbagliato piu' volte: il riquadro che l'impianto memorizza e' l'inviluppo
/// della sagoma GIA' ruotata, non la sua dimensione propria, e un trascinamento che non conservi la
/// rotazione la deforma. Non 45 perche' a 45 gradi l'inviluppo non si puo' invertire
/// (<c>NodePlacement.IsDegenerateRotation</c>) e il salvataggio rifiuta certi gesti: una prova
/// costruita li' proverebbe il rifiuto, non il giro.
/// </para>
/// <para>
/// I riquadri sono gli inviluppi veri di un dispositivo 120x60 a 30 gradi e di uno 100x50 a 20
/// gradi: numeri che un impianto migrato potrebbe davvero portare, non un rettangolo inventato che
/// si inverte in una dimensione propria negativa.
/// </para>
/// </remarks>
public static class PlantSeed
{
    /// <summary>Il dispositivo che la prova trascina con il mouse.</summary>
    public static readonly Guid DraggedId = new("a1b2c3d4-0000-4000-8000-000000000001");

    /// <summary>Il dispositivo che nessuno tocca: il testimone che dice se il salvataggio abbia mosso qualcos'altro.</summary>
    public static readonly Guid UntouchedId = new("a1b2c3d4-0000-4000-8000-000000000002");

    /// <summary>L'etichetta con cui draw.io disegna il dispositivo trascinato: e' da questa che la prova lo ritrova sulla tela.</summary>
    public const string DraggedName = "DraggedDevice";

    /// <summary>L'etichetta del dispositivo fermo.</summary>
    public const string UntouchedName = "UntouchedDevice";

    public static readonly PlantItemGeometryDto DraggedGeometry =
        new(DraggedId, X: 200, Y: 150, Width: 133.92, Height: 111.96, Rotation: 30);

    public static readonly PlantItemGeometryDto UntouchedGeometry =
        new(UntouchedId, X: 620, Y: 420, Width: 111.07, Height: 81.19, Rotation: 20);

    /// <summary>
    /// Quanto si aspetta che il permesso di modifica torni libero prima di dichiararlo occupato:
    /// si veda <see cref="AcquireWaitingForTheHandoverAsync"/>. <b>Non e' il tempo che si aspetta</b>
    /// - si aspetta la condizione - e' il punto oltre il quale un permesso che non si libera
    /// diventa un fallimento che si legge.
    /// <para>
    /// <b>Supera il minuto</b>, e il numero non e' a caso: il permesso che la pagina di una prova
    /// precedente non ha fatto in tempo a restituire scade comunque da solo dopo il contratto
    /// dell'host, che dura 60 secondi (<c>EditLockEndpoints.Lease</c>). Un tetto piu' basso
    /// scadrebbe proprio mentre il permesso sta per liberarsi - misurato: con mezzo minuto, una
    /// esecuzione su tre diventava rossa senza che niente fosse rotto.
    /// </para>
    /// </summary>
    private static readonly TimeSpan HandoverCeiling = TimeSpan.FromSeconds(75);

    private static readonly TimeSpan HandoverInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// La priorita' con cui nasce la giunzione facoltativa del seme: <b>bassa</b>, che nel WCS e' il
    /// numero piu' ALTO (99, si veda <c>JunctionWeights</c>) - la stessa che hanno tutte le
    /// giunzioni dell'impianto vero, e quella che <c>DrawioDocument</c> disegna sottile.
    /// </summary>
    private const int JunctionPriority = 99;

    /// <summary>
    /// Porta l'impianto allo stato di partenza: via tutto quello che c'era - <b>la decorazione
    /// compresa</b> - e dentro i due dispositivi con la loro geometria. Il permesso di modifica si
    /// prende e si <b>rilascia</b>:
    /// se restasse a chi semina, il salvataggio dalla pagina troverebbe l'impianto occupato e si
    /// fermerebbe dicendo "sola lettura" - la prova diventerebbe rossa per un motivo che non e'
    /// quello che sta provando.
    /// </summary>
    /// <param name="withJunction">
    /// Se seminare anche <b>una giunzione</b> fra i due dispositivi, che la maggior parte delle
    /// prove non vuole (l'impianto minimo e' due dispositivi e nessun legame) e che serve a chi deve
    /// misurare che cosa accade a una freccia - per esempio quando i due dispositivi che collega
    /// vengono raggruppati: si veda <c>DrawioGroupedCellsTest</c>. Restituita, cosi' che chi la
    /// semina possa poi chiedere all'impianto se ci sia ancora.
    /// </param>
    /// <param name="untouchedLinkedCellId">
    /// La cella del gestionale collegata al dispositivo fermo (<c>LinkedCellId</c>), zero per nessuna:
    /// un'UDC che arriva su un dispositivo con una cella collegata viene registrata li' nel gestionale
    /// (<c>AllocateOnItem</c>): e' la chiamata al gestionale che, con il gestionale che non risponde,
    /// fallisce nella prova della voce 31 in <c>DrawioMissionsBrowserTest</c>.
    /// </param>
    public static async Task<JunctionDto?> ResetAsync(WcsClient client, string holder, bool withJunction = false,
        decimal untouchedLinkedCellId = 0)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);

        client.EditHolder = holder;
        try
        {
            await client.RemoveAllItemsAsync();
            // LA DECORAZIONE VA VIA CON IL RESTO, e non ci andava. Misurato: una prova che salva
            // una nota e una freccia libere le lascia sull'impianto, e la prova successiva le
            // ritrova sulla tela - conta un arco che non ha disegnato lei. Un seme che non
            // azzera quello che l'impianto tiene del disegno non e' uno stato di partenza: e'
            // quello che ha lasciato chi e' passato prima.
            await client.SetPlantDecorationAsync(string.Empty);
            await client.CreateItemAsync(Configuration(DraggedId, DraggedName, DeviceTypes.Automation));
            await client.CreateItemAsync(Configuration(UntouchedId, UntouchedName, DeviceTypes.Loading, untouchedLinkedCellId));
            var dragged = await client.GetItemAsync(DraggedId) ?? throw new InvalidOperationException();
            var untouched = await client.GetItemAsync(UntouchedId) ?? throw new InvalidOperationException();
            await client.SetItemGeometriesAsync(
            [
                ToUpdate(DraggedGeometry, dragged.GeometryVersion),
                ToUpdate(UntouchedGeometry, untouched.GeometryVersion)
            ]);
            return withJunction
                ? await client.AddJunctionAsync(new JunctionRequest(DraggedId, UntouchedId, JunctionPriority))
                : null;
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>
    /// Adds a device of a kind <see cref="ResetAsync"/> does not seed, with its geometry: it is for
    /// whoever has to test something that depends on the TYPE - a Storage device, for instance, which
    /// is the only one to get the door map instead of the single cell.
    /// </summary>
    /// <remarks>
    /// Called AFTER <see cref="ResetAsync"/>, which empties the plant: the permit is taken and
    /// released here as it is there and for the same reason - left with whoever seeded, the save from
    /// the page would find the plant taken.
    /// </remarks>
    public static async Task AddDeviceAsync(WcsClient client, string holder, Guid id, string name, DeviceTypes type,
        PlantItemGeometryDto geometry)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            await client.CreateItemAsync(Configuration(id, name, type));
            var created = await client.GetItemAsync(id) ?? throw new InvalidOperationException();
            await client.SetItemGeometriesAsync([ToUpdate(geometry, created.GeometryVersion)]);
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>
    /// Adds <paramref name="drawn"/> Test devices laid out on a row with a junction each way between
    /// neighbours, and <paramref name="shapeless"/> Test devices with no shape at all, under ONE
    /// permit: a plant with more devices than the device list shows under its ceiling (a third of the
    /// window), which the two devices of <see cref="ResetAsync"/> never reach. Called AFTER
    /// <see cref="ResetAsync"/>; the permit is given back for the same reason as there.
    /// </summary>
    /// <remarks>
    /// <b>Why most of them have no shape.</b> MEASURED on 27/9: a drawing of 31,009 characters (14 drawn
    /// devices, 11 junctions) saved, one of 48,897 (22 drawn devices, 19 junctions) did not - the
    /// circuit closed ("Connection disconnected with error") and the save never ended. It was SignalR's
    /// ceiling on one message from the browser, 32 KB, with the drawing in one message: queue item 13,
    /// fixed since - the drawing is streamed, and <see cref="AddAGridOfDrawnDevicesAsync"/> seeds a
    /// plant whose drawing is well over that ceiling. Kept as it is because a long list is what its
    /// callers need, and a device without a shape is a row of the list and nothing of the drawing.
    /// </remarks>
    public static async Task AddManyDevicesAsync(WcsClient client, string holder, int drawn, int shapeless)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            for (var n = 1; n <= drawn; n++)
            {
                await client.CreateItemAsync(Configuration(BulkDeviceId(n), BulkDeviceName(n), DeviceTypes.Automation));
                var created = await client.GetItemAsync(BulkDeviceId(n)) ?? throw new InvalidOperationException();
                await client.SetItemGeometriesAsync([ToUpdate(BulkDeviceGeometry(n), created.GeometryVersion)]);
            }
            for (var n = 1; n < drawn; n++)
            {
                await client.AddJunctionAsync(new JunctionRequest(BulkDeviceId(n), BulkDeviceId(n + 1), JunctionPriority));
                await client.AddJunctionAsync(new JunctionRequest(BulkDeviceId(n + 1), BulkDeviceId(n), JunctionPriority));
            }
            for (var n = 1; n <= shapeless; n++)
                await client.CreateItemAsync(Configuration(
                    new Guid($"c1b2c3d4-0000-4000-8000-{n:000000000000}"), $"ShapelessDevice{n:00}", DeviceTypes.Automation));
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>
    /// Queue item 13: adds <paramref name="count"/> drawn Test devices on a grid of
    /// <paramref name="columns"/> columns, right of the two devices of the seed - a plant whose
    /// drawing is as large as a real one (DEMOPLANT has 51 devices), with the whole of it on the
    /// canvas. The devices follow each other as a snake (left to right, then right to left on the next
    /// row), with a junction each way between each device and the next, so that every arrow joins two
    /// neighbours; all but the one in the middle (<see cref="GridFreeDevice"/>), which no arrow
    /// touches, so that a test can take it with the mouse - an arrow over a shape's middle takes the
    /// press, and draw.io's own windows cover the edges of a drawing this wide. Under ONE permit, given
    /// back as in <see cref="ResetAsync"/>; the geometries go in one batch, as a save writes them.
    /// </summary>
    public static async Task AddAGridOfDrawnDevicesAsync(WcsClient client, string holder, int count, int columns)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            var geometries = new List<PlantItemGeometryUpdateDto>();
            for (var n = 1; n <= count; n++)
            {
                await client.CreateItemAsync(Configuration(GridDeviceId(n), GridDeviceName(n), DeviceTypes.Automation));
                var created = await client.GetItemAsync(GridDeviceId(n)) ?? throw new InvalidOperationException();
                geometries.Add(ToUpdate(GridDeviceGeometry(n, columns), created.GeometryVersion));
            }
            await client.SetItemGeometriesAsync(geometries);
            var free = GridFreeDevice(count, columns);
            for (var n = 1; n < count; n++)
            {
                if (n == free - 1 || n == free) continue;
                await client.AddJunctionAsync(new JunctionRequest(GridDeviceId(n), GridDeviceId(n + 1), JunctionPriority));
                await client.AddJunctionAsync(new JunctionRequest(GridDeviceId(n + 1), GridDeviceId(n), JunctionPriority));
            }
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>The number of junctions <see cref="AddAGridOfDrawnDevicesAsync"/> seeds for <paramref name="count"/> devices.</summary>
    public static int GridJunctions(int count) => 2 * (count - 1 - 2);

    /// <summary>The device in the middle of <see cref="AddAGridOfDrawnDevicesAsync"/>'s grid that no arrow touches.</summary>
    public static int GridFreeDevice(int count, int columns)
    {
        var row = count / columns / 2;
        var column = columns / 2;
        return row * columns + (row % 2 == 0 ? column : columns - 1 - column) + 1;
    }

    /// <summary>The identifier of the <paramref name="n"/>-th device (from 1) of <see cref="AddAGridOfDrawnDevicesAsync"/>.</summary>
    public static Guid GridDeviceId(int n) => new($"e1b2c3d4-0000-4000-8000-{n:000000000000}");

    /// <summary>The label of the <paramref name="n"/>-th device (from 1) of <see cref="AddAGridOfDrawnDevicesAsync"/>.</summary>
    public static string GridDeviceName(int n) => $"GridDevice{n:000}";

    /// <summary>
    /// Where the <paramref name="n"/>-th device (from 1) of <see cref="AddAGridOfDrawnDevicesAsync"/> is
    /// seeded: 200 x 150 apart, so that a device fits between two neighbours with room to spare.
    /// </summary>
    public static PlantItemGeometryDto GridDeviceGeometry(int n, int columns)
    {
        var row = (n - 1) / columns;
        var column = row % 2 == 0 ? (n - 1) % columns : columns - 1 - (n - 1) % columns;
        return new(GridDeviceId(n), X: 900 + column * 200, Y: 150 + row * 150, Width: 120, Height: 60, Rotation: 0);
    }

    /// <summary>The identifier of the <paramref name="n"/>-th drawn device (from 1) of <see cref="AddManyDevicesAsync"/>.</summary>
    public static Guid BulkDeviceId(int n) => new($"b1b2c3d4-0000-4000-8000-{n:000000000000}");

    /// <summary>The label of the <paramref name="n"/>-th drawn device (from 1) of <see cref="AddManyDevicesAsync"/>.</summary>
    public static string BulkDeviceName(int n) => $"BulkDevice{n:00}";

    /// <summary>
    /// Where the <paramref name="n"/>-th drawn device (from 1) of <see cref="AddManyDevicesAsync"/> is
    /// seeded: on a row right of the two devices of the seed, so nothing overlaps them.
    /// </summary>
    public static PlantItemGeometryDto BulkDeviceGeometry(int n) =>
        new(BulkDeviceId(n), X: 900 + (n - 1) * 160, Y: 150, Width: 120, Height: 60, Rotation: 0);

    /// <summary>
    /// Adds a device with NO geometry: a "device of the plant without a shape" (row 15's own words),
    /// for whoever tests re-pointing a shape at a device that needs to stay reassociable ACROSS a
    /// save - which a device drawn and then undrawn by deleting its cell cannot do, because the very
    /// save that would leave it clean also deletes it (nothing draws it any more). Seeding it without
    /// a shape from the start is the only way to have that state survive a save.
    /// </summary>
    public static async Task AddDeviceWithoutAShapeAsync(WcsClient client, string holder, Guid id, string name, DeviceTypes type)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            await client.CreateItemAsync(Configuration(id, name, type));
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>
    /// Adds ONE cell to the plant's free drawing (decoration) marked <c>wcs="device"</c> but
    /// carrying an identifier no device of the plant has - a shape the plant does not know, present
    /// from the very FIRST load of the page rather than dropped by the operator during the session.
    /// For whoever needs that starting state without the drop itself counting as unsaved work of its
    /// own: a shape dropped live is a real model edit the moment it lands (a birth draw.io's own
    /// model fires a CHANGE for), and lights the "changed since last save" mark before any
    /// reassociation gesture even runs - this way, seeded straight into what the plant already
    /// holds, the freshly opened page starts clean around it (same premise as
    /// <c>AnUntouchedPageClosesWithoutAsking</c>: an untouched page never asks, whatever it draws).
    /// </summary>
    public static async Task SeedOrphanDeviceCellAsync(WcsClient client, string holder, string cellId, string label)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            var current = await client.GetPlantGraphAsync();
            var orphan =
                $"""
                <object id="{cellId}" label="{label}" wcs="device"><mxCell style="rounded=0;whiteSpace=wrap;html=1;" vertex="1" parent="1"><mxGeometry x="900" y="700" width="120" height="60" as="geometry"/></mxCell></object>
                """;
            await client.SetPlantDecorationAsync((current.Decoration ?? "") + orphan);
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>
    /// Writes a whole configuration over a device the plant already has, with the permit taken and
    /// given back as for every seed write: for whoever needs a device whose configuration is not the
    /// seed's bare one (row 63, the duplicate copies it field by field).
    /// </summary>
    public static async Task SaveConfigurationAsync(WcsClient client, string holder, PlantItemConfigDto config)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            await client.SaveItemConfigurationAsync(config);
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>
    /// Adds one junction between two devices already seeded, at the seed's priority, with the permit
    /// taken and given back as for every seed write: for whoever needs a longer way than the seed's one
    /// junction (item 54: from the untouched device, a relocator, to the warehouse it serves).
    /// </summary>
    public static async Task<JunctionDto> AddJunctionAsync(WcsClient client, string holder, Guid from, Guid to)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            return await client.AddJunctionAsync(new JunctionRequest(from, to, JunctionPriority));
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>
    /// Replaces the door map of the devices <paramref name="mappings"/> name, with the permit taken and
    /// given back as for every seed write (item 54: a warehouse whose cells the change of destination
    /// can choose). Refused by the host in Run: the seed writes it with the engine stopped.
    /// </summary>
    public static async Task ReplaceDoorMappingsAsync(WcsClient client, string holder, IEnumerable<DoorMappingDto> mappings)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            await client.ReplaceDoorMappingsAsync(mappings);
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>
    /// Adds ONE plain note to the plant's free drawing (decoration): a drawing-only shape with no mark
    /// of ours at all, there from the first load of the page - for whoever has to show that a shape
    /// which is not a device is treated as one nowhere (row 63: no device entries in its right-click
    /// menu).
    /// </summary>
    public static async Task SeedNoteAsync(WcsClient client, string holder, string cellId, string label)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            var current = await client.GetPlantGraphAsync();
            var note =
                $"""
                <mxCell id="{cellId}" value="{label}" style="text;html=1;strokeColor=#000000;fillColor=#ffffff;align=center;" vertex="1" parent="1"><mxGeometry x="420" y="40" width="120" height="40" as="geometry"/></mxCell>
                """;
            await client.SetPlantDecorationAsync((current.Decoration ?? "") + note);
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    private static PlantItemGeometryUpdateDto ToUpdate(PlantItemGeometryDto geometry, Guid version) =>
        new(geometry.ItemId, geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.Rotation, version);

    /// <summary>
    /// Prende il permesso di modifica, <b>aspettando che la prova precedente abbia finito di
    /// restituirlo</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MISURATO, e non era ovvio: la pagina rilascia il permesso preso al salvataggio nel proprio
    /// <c>DisposeAsync</c>, cioe' quando il <b>circuito Blazor</b> viene smontato - non quando il
    /// browser chiude la scheda. Fra il <c>page.CloseAsync()</c> della prova e quel momento passa
    /// un tempo che non e' nostro, e sotto carico (due classi di prove in parallelo, due host e due
    /// browser) supera l'istante in cui la prova successiva semina. Senza questa attesa, la seconda
    /// prova di una classe falliva raccontando un permesso "di qualcun altro" che era il suo
    /// stesso predecessore in chiusura.
    /// </para>
    /// <para>
    /// La guardia resta: se il permesso non si libera davvero, il fallimento dice chi lo tiene, da
    /// quando e per quanto lo si e' aspettato. Aspettare non e' ignorare.
    /// </para>
    /// </remarks>
    private static async Task AcquireWaitingForTheHandoverAsync(WcsClient client, string holder)
    {
        var giveUpAt = DateTime.UtcNow + HandoverCeiling;
        while (true)
        {
            var lease = await client.AcquireEditLockAsync(holder);
            if (lease.IsMine) return;
            if (DateTime.UtcNow > giveUpAt)
                throw new InvalidOperationException(
                    $"Il permesso di modifica dell'impianto e' di \"{lease.Holder}\" dalle {lease.SinceLocal:HH:mm:ss} " +
                    $"e non si e' liberato entro {HandoverCeiling.TotalSeconds:0} secondi: su questo host di prova non " +
                    "dovrebbe esserci nessun altro, e quel tempo e' piu' lungo del contratto dell'host - anche un " +
                    "permesso dimenticato sarebbe gia' scaduto. Non e' piu' la consegna fra una prova e l'altra: " +
                    "qualcuno lo sta rinnovando.");
            await Task.Delay(HandoverInterval);
        }
    }

    /// <summary>
    /// Aspetta che il permesso di modifica sia <b>libero</b> e lo restituisce subito: serve a una
    /// prova che apre una SECONDA pagina sullo stesso impianto senza riseminarlo. La prima pagina
    /// rilascia il permesso quando il suo circuito Blazor viene smontato, che non e' quando il
    /// browser chiude la scheda (si veda <see cref="AcquireWaitingForTheHandoverAsync"/>): senza
    /// questa attesa il salvataggio della seconda pagina si ferma dicendo "sola lettura", e la prova
    /// diventerebbe rossa per un motivo che non e' quello che sta provando.
    /// </summary>
    public static async Task WaitForTheEditPermitToBeFreeAsync(WcsClient client, string holder)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        await client.ReleaseEditLockAsync(holder);
    }

    /// <summary>
    /// Porta una giunzione gia' seminata a una priorita' qualunque - un numero che non e' ne' 0 ne'
    /// 99, cioe' uno di quelli che un impianto migrato porta e che l'Editor non scrive mai.
    /// <see cref="ResetAsync"/> non lo puo' fare: la <c>POST</c> che semina si collassa sulla coppia
    /// e la priorita' la decide la creazione, mentre questa e' l'unica rotta che la cambia.
    /// </summary>
    public static async Task SetJunctionPriorityAsync(WcsClient client, string holder, Guid junctionId, int priority)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            var junction = (await client.GetJunctionsAsync()).FirstOrDefault(j => j.JunctionId == junctionId)
                ?? throw new InvalidOperationException($"La giunzione {junctionId} non e' sull'impianto: non le si puo' cambiare la priorita'.");
            var found = await client.SetJunctionPriorityAsync(junctionId,
                new JunctionRequest(junction.SourceItemId, junction.DestinationItemId, priority));
            if (!found)
                throw new InvalidOperationException($"L'host non ha trovato la giunzione {junctionId} da cambiare.");
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>
    /// Replaces the host's PLC connections with the client, as the seed does every other write: the
    /// edit permit taken and RELEASED (the page's own save would otherwise find it taken). Since
    /// Task 4b <c>PUT /plc/connections</c> requires the permit (423 without) and the engine stopped
    /// (409 otherwise) - the test host's engine is stopped.
    /// </summary>
    public static async Task ReplacePlcConnectionsAsync(WcsClient client, string holder, IEnumerable<PlcConnectionDto> connections)
    {
        await AcquireWaitingForTheHandoverAsync(client, holder);
        client.EditHolder = holder;
        try
        {
            await client.ReplacePlcConnectionsAsync(connections);
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(holder);
        }
    }

    /// <summary>La geometria di un dispositivo com'e' adesso sull'host, o solleva se il dispositivo non c'e' piu'.</summary>
    public static async Task<PlantItemGeometryDto> ReadGeometryAsync(WcsClient client, Guid id) =>
        GeometryOf(await client.GetPlantGraphAsync(), id);

    /// <summary>La geometria di un dispositivo dentro un impianto gia' letto, o solleva se non c'e' piu'.</summary>
    public static PlantItemGeometryDto GeometryOf(PlantGraphDto graph, Guid id) =>
        graph.Geometries.FirstOrDefault(g => g.ItemId == id)
        ?? throw new InvalidOperationException($"Il dispositivo {id} non e' piu' nell'impianto: il salvataggio lo ha portato via.");

    private static PlantItemConfigDto Configuration(Guid id, string name, DeviceTypes type, decimal linkedCellId = 0) => new()
    {
        ItemId = id,
        Name = name,
        Asi = name,
        DeviceType = (short)type,
        LinkedCellId = linkedCellId,
        // Test: nessun PLC da cercare. Con Connected il motore proverebbe ad aprire una connessione
        // verso un impianto che non esiste.
        ConnectionType = (short)ConnectionTypes.Test,
    };
}
