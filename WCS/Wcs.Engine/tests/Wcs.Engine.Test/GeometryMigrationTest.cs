using System.Text;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La migrazione della geometria: gira una volta sola, marca il diagramma, e dice che cosa non ha
/// portato via.
/// </summary>
public sealed class GeometryMigrationTest : IDisposable
{
    private const string Ns = "http://schemas.datacontract.org/2004/07/Syncfusion.Windows.Forms.Diagram";
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    public void Dispose() => _database.Dispose();

    private static byte[] Drawing(params (Guid Id, double X, double Y)[] nodes)
    {
        // DiagramGeometryReader.IsDevice riconosce un nodo solo se il suo PropertyBag porta
        // OrchestraType=ITEM (compito 5): senza, ogni nodo qui sotto sarebbe una decorazione e
        // Placed resterebbe sempre a zero.
        var body = string.Concat(nodes.Select(n =>
            $"<Node><PropertyBag><KeyValueOfstringanyType><Key>OrchestraType</Key><Value>ITEM</Value></KeyValueOfstringanyType></PropertyBag><Tag>{n.Id}</Tag><BoundingRect><X>{n.X}</X><Y>{n.Y}</Y><Width>10</Width><Height>10</Height></BoundingRect><RotationAngle>0</RotationAngle></Node>"));
        return Encoding.UTF8.GetBytes($"<DiagramDocumentWrapper xmlns=\"{Ns}\"><Model><Nodes>{body}</Nodes></Model></DiagramDocumentWrapper>");
    }

    private async Task<Guid> AddItemAsync()
    {
        var itemId = Guid.NewGuid();
        await using var context = _database.CreateContext();
        context.PlantItems.Add(new PlantItem { ItemId = itemId });
        await context.SaveChangesAsync();
        return itemId;
    }

    private async Task SetDiagramAsync(byte[] xml)
    {
        await using var context = _database.CreateContext();
        var diagram = await PlantStore.GetOrCreateDiagramAsync(context);
        diagram.DiagramXml = xml;
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task PlacesTheDevicesAndMarksTheDiagram()
    {
        var itemId = await AddItemAsync();
        await SetDiagramAsync(Drawing((itemId, 100, 200)));

        var report = await GeometryMigration.RunOnceAsync(_database);

        Assert.True(report.Executed);
        Assert.Equal(1, report.Placed);
        Assert.Equal(0, report.ItemsWithoutPosition);

        await using var context = _database.CreateContext();
        var row = await PlantStore.FindItemAsync(context, itemId);
        Assert.Equal(100, row!.X);
        Assert.Equal(200, row.Y);
        var diagram = await PlantStore.LoadDiagramAsync(context);
        Assert.NotNull(diagram!.GeometryMigratedAt);
    }

    [Fact]
    public async Task TheSecondStartDoesNothing()
    {
        var itemId = await AddItemAsync();
        await SetDiagramAsync(Drawing((itemId, 100, 200)));
        await GeometryMigration.RunOnceAsync(_database);

        // Qualcuno sposta il dispositivo dopo la migrazione: il secondo avvio non deve riportarlo
        // dov'era il disegno, perche' la verita' ora e' il modello.
        await using (var context = _database.CreateContext())
        {
            await PlantStore.SetGeometryAsync(context, itemId, new ItemGeometry(7, 7, 7, 7, 7));
            await context.SaveChangesAsync();
        }

        var second = await GeometryMigration.RunOnceAsync(_database);

        Assert.False(second.Executed);
        await using var verify = _database.CreateContext();
        Assert.Equal(7, (await PlantStore.FindItemAsync(verify, itemId))!.X);
    }

    [Fact]
    public async Task ADeviceTheDrawingDoesNotNameStaysUnplacedAndIsCounted()
    {
        var collocato = await AddItemAsync();
        var assente = await AddItemAsync();
        await SetDiagramAsync(Drawing((collocato, 1, 1)));

        var report = await GeometryMigration.RunOnceAsync(_database);

        Assert.Equal(1, report.Placed);
        Assert.Equal(1, report.ItemsWithoutPosition);

        await using var context = _database.CreateContext();
        Assert.Null((await PlantStore.FindItemAsync(context, assente))!.X);
    }

    [Fact]
    public async Task APlantWithoutADrawingIsMarkedAnyway()
    {
        await AddItemAsync();

        var report = await GeometryMigration.RunOnceAsync(_database);

        Assert.True(report.Executed);
        Assert.Equal(0, report.Placed);

        await using var context = _database.CreateContext();
        Assert.NotNull((await PlantStore.LoadDiagramAsync(context))!.GeometryMigratedAt);
    }

    /// <summary>
    /// Correzione della revisione: un Guid che il disegno porta, non corrispondente a nessun
    /// <c>PlantItem</c>, e con la geometria illeggibile in <b>tutte</b> le sue occorrenze, prima
    /// spariva dal verbale — non era fra i collocati (<see cref="GeometryMigrationReport.Placed"/>),
    /// ne' fra quelli senza posizione (che scorre solo gli identificativi noti), ne' fra le
    /// identita' sconosciute (calcolate solo su cio' che e' finito in
    /// <see cref="DiagramGeometryResult.Geometries"/>, dove un Guid cosi' non entra mai). Adesso
    /// <see cref="GeometryMigrationReport.GuidsWithoutGeometry"/> lo conta.
    /// </summary>
    [Fact]
    public async Task AnUnknownGuidWithUnreadableGeometryIsCountedNotLost()
    {
        var unknownId = Guid.NewGuid();
        await SetDiagramAsync(DrawingWithUnreadableGeometry(unknownId));

        var report = await GeometryMigration.RunOnceAsync(_database);

        Assert.True(report.Executed);
        // I quattro numeri di prima della correzione: tutti a zero, il Guid e' invisibile.
        Assert.Equal(0, report.Placed);
        Assert.Equal(0, report.ItemsWithoutPosition);
        Assert.Equal(0, report.UnknownGuids);
        // Il numero nuovo: il Guid c'e', anche se non e' un PlantItem.
        Assert.Equal(1, report.GuidsWithoutGeometry);
    }

    /// <summary>
    /// Un nodo riconosciuto come dispositivo (il PropertyBag lo dice), con un Tag ma senza nessun
    /// BoundingRect: esattamente il caso "geometria illeggibile in tutte le occorrenze" di
    /// <see cref="DiagramGeometryReader"/>.
    /// </summary>
    private static byte[] DrawingWithUnreadableGeometry(Guid id) =>
        Encoding.UTF8.GetBytes(
            $"<DiagramDocumentWrapper xmlns=\"{Ns}\"><Model><Nodes>" +
            "<Node><PropertyBag><KeyValueOfstringanyType><Key>OrchestraType</Key>" +
            $"<Value>ITEM</Value></KeyValueOfstringanyType></PropertyBag><Tag>{id}</Tag></Node>" +
            "</Nodes></Model></DiagramDocumentWrapper>");

    /// <summary>
    /// Correzione della revisione d'insieme: un blob presente e XML valido, con dispositivi noti
    /// nel database, che pero' non ne colloca nessuno - il caso di un disegno scritto da una
    /// versione che non mette OrchestraType/DiagramType nel PropertyBag, o un formato diverso.
    /// Prima marcava lo stesso (<c>Placed = 0</c> era indistinguibile da un impianto nuovo): il
    /// disegno si congelava con tutti i dispositivi "senza posizione" e senza modo di riprovare.
    /// Adesso solleva, come il blob malformato: non marcato, il prossimo avvio riprova.
    /// </summary>
    [Fact]
    public async Task ADrawingThatPlacesNoneOfTheKnownItemsIsNotMarked()
    {
        var itemId = await AddItemAsync();
        // Disegno valido, un dispositivo riconosciuto (OrchestraType=ITEM) ma con un Tag che non
        // corrisponde a nessun PlantItem: nessuno dei dispositivi noti risulta collocato.
        await SetDiagramAsync(Drawing((Guid.NewGuid(), 1, 1)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => GeometryMigration.RunOnceAsync(_database));

        await using var context = _database.CreateContext();
        var diagram = await PlantStore.LoadDiagramAsync(context);
        Assert.Null(diagram!.GeometryMigratedAt);
        Assert.Null((await PlantStore.FindItemAsync(context, itemId))!.X);
    }
}
