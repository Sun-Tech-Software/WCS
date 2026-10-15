using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// <b>Il verbale mantiene la parola: quello che promette e quello che succede sono la stessa
/// cosa</b>, misurato contro l'host vero e riletto dall'host, non dalla tela.
/// </summary>
/// <remarks>
/// <para>
/// Le due prove chiudono le due voci che il lotto 1 aveva lasciato appuntate. La prima e' la
/// promessa <i>«it stays a drawing»</i> fatta a una cella che poi spariva; la seconda e' il caso
/// opposto - una cancellazione voluta, che deve <b>vedersi</b> fra le scritture.
/// </para>
/// <para>
/// <b>Perche' la seconda esiste.</b> Il prodotto elenca da sempre ogni scrittura sull'impianto, e
/// la cancellazione di una giunzione e' una di quelle - <c>DrawioEditor.razor</c>, «Written: the
/// deletion of junction ...». Ma <b>nessuna prova di questo repository nominava quella frase</b>:
/// la catena fra l'elenco delle cancellazioni del delta e la riga che l'operatore legge non era
/// misurata da nessuna parte. Una decisione del proprietario che si regge su una riga che nessuno
/// guarda e' una decisione che si regge sul nulla.
/// </para>
/// </remarks>
public sealed class DrawioTheReportKeepsItsWordTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-the-report-keeps-its-word";

    private static readonly Guid SourceId = new("a1b2c3d4-0000-4000-8000-000000000301");
    private static readonly Guid TargetId = new("a1b2c3d4-0000-4000-8000-000000000302");

    private const string SourceName = "KeepsWordSource";
    private const string TargetName = "KeepsWordTarget";

    private static readonly PlantItemGeometryDto SourceGeometry =
        new(SourceId, X: 160, Y: 1100, Width: 120, Height: 60, Rotation: 0);

    private static readonly PlantItemGeometryDto TargetGeometry =
        new(TargetId, X: 560, Y: 1100, Width: 120, Height: 60, Rotation: 0);

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// <b>Una sagoma che l'operatore marchia con l'API di draw.io resta il suo disegno</b>, e la
    /// prova dice anche COME draw.io la scrive - che non e' come il lettore se l'aspetta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Questa prova NON raggiunge il difetto della cella marchiata che non e' una linea, e
    /// dirlo e' meta' del suo valore.</b> Misurato, 2026-09-20: <c>graph.setAttributeForCell</c> -
    /// la chiamata che draw.io fa da se' quando si modificano i dati di una cella - scrive nel
    /// disegno un <c>&lt;UserObject&gt;</c>, non un <c>&lt;object&gt;</c>. Il lettore chiede
    /// <c>element.Name == "object"</c>, quindi <b>quel marchio non lo legge affatto</b>: la cella
    /// scende nella decorazione come una sagoma qualunque e sopravvive per la via piu' banale.
    /// Con la sottrazione ristretta rimessa apposta, questa prova era verde lo stesso.
    /// </para>
    /// <para>
    /// La cella marchiata che non e' una linea - il GRAVE della seconda revisione - la misura la
    /// rete generativa, che la contava su 32 strade; arriva alla tela per un Ctrl+V di XML
    /// modificato a mano, e un browser guidato quel gesto non lo fa (misurato nel lotto delle
    /// sagome).
    /// </para>
    /// <para>
    /// Resta qui perche' quello che misura e' vero e non lo misurava nessuno: il
    /// <c>&lt;UserObject&gt;</c> era una <i>pista</i> nella quarta revisione, e adesso e' un
    /// numero. Se un giorno il lettore imparera' a leggerlo, questa prova cambiera' esito e
    /// qualcuno dovra' venire qui a dire che cosa e' cambiato.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AShapeTheOperatorMarksThroughDrawioStaysHisDrawing()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);

        string shapeId;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            shapeId = await editor.PutAMarkedShapeOnTheCanvasAsync("junction");
            output.WriteLine($"Sagoma marchiata junction, e non e' una linea: cella {shapeId}.");

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            var report = await editor.ReportAsync();
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{report}");

            var refusals = await editor.RefusalsAsync();
            var notices = await editor.NoticesAsync();
            output.WriteLine($"RIFIUTI: {refusals.Count} | VOCI: {notices.Count}");
            foreach (var line in refusals.Concat(notices)) output.WriteLine($"DETTO: {line}");
        }

        Assert.Empty(await client.GetJunctionsAsync());

        // QUELLO CHE CONTA E' CHE RESTI, letto dall'host e poi dalla tela. La decorazione e' cio'
        // che l'impianto conserva del disegno che non e' suo: se la cella non e' li', il
        // salvataggio ha portato via il disegno dell'operatore, ed e' il difetto per cui questa
        // prova esiste - il marchio dice "sono una connessione", l'impianto non puo' tenerla, e
        // l'unica cosa che non puo' succedere e' che sparisca.
        var decoration = await client.GetPlantDecorationAsync();
        output.WriteLine($"Decorazione sull'impianto dopo il salvataggio:{Environment.NewLine}{decoration}");

        // COME draw.io l'HA SCRITTA, che e' la misura che questa prova porta: <UserObject>, non
        // <object>. Se questa riga diventa rossa, draw.io ha cambiato come scrive un marchio - e
        // allora il lettore, che chiede "object", va riguardato prima di qualunque altra cosa.
        Assert.True(decoration.Contains("<UserObject", StringComparison.Ordinal),
            $"draw.io non scrive piu' <UserObject> per una cella marchiata con la sua API: il lettore" +
            $" chiede element.Name == \"object\", e questa differenza e' il motivo per cui il marchio" +
            $" non viene letto.{Environment.NewLine}{decoration}");

        Assert.True(decoration.Contains($"\"{shapeId}\"", StringComparison.Ordinal),
            $"La cella {shapeId}, che il disegno marchia come nostra e che non e' una linea, non e' nella" +
            $" decorazione dell'impianto dopo il salvataggio: e' sparita dal disegno dell'operatore." +
            $"{Environment.NewLine}{decoration}");

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(SourceName);
        var onTheCanvas = await reopened.CellStyleAsync(shapeId);
        output.WriteLine($"Alla riapertura la cella {shapeId} ha stile: {onTheCanvas}");
    }

    /// <summary>
    /// <b>L'operatore cancella la linea: la giunzione esce dall'impianto, e il verbale lo dice fra
    /// le scritture.</b> Non serve un avviso in piu' - cancellare e' un gesto voluto - serve che il
    /// risultato si veda, e che si veda per NOME.
    /// </summary>
    [Fact]
    public async Task TheLineDeletedByHandIsDeclaredAmongTheWrites()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        var junction = await JoinThemAsync(client);
        output.WriteLine($"Giunzione seminata: {junction.JunctionId}.");

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            Assert.Contains(await editor.EdgesInTheDrawingAsync(), edge => edge.Id == junction.JunctionId.ToString());

            var left = await editor.DeleteTheCellAsync(junction.JunctionId.ToString());
            Assert.Equal(0, left);
            output.WriteLine("La linea e' stata cancellata dalla tela.");

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            var report = await editor.ReportAsync();
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{report}");

            var written = await editor.WrittenAsync();
            foreach (var line in written) output.WriteLine($"SCRITTO: {line}");
            Assert.True(written.Any(line => line.Contains(junction.JunctionId.ToString(), StringComparison.OrdinalIgnoreCase)),
                $"La giunzione {junction.JunctionId} e' uscita dall'impianto e nessuna riga delle scritture la nomina:" +
                $" la cancellazione non si vede.{Environment.NewLine}Il verbale diceva:{Environment.NewLine}{report}");
        }

        Assert.Empty(await client.GetJunctionsAsync());
    }

    private static async Task SeedTheUprightPairAsync(WcsClient client)
    {
        await PlantSeed.ResetAsync(client, SeedHolder);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, SourceId, SourceName, DeviceTypes.Automation, SourceGeometry);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, TargetId, TargetName, DeviceTypes.Automation, TargetGeometry);
    }

    private static async Task<JunctionDto> JoinThemAsync(WcsClient client)
    {
        await client.AcquireEditLockAsync(SeedHolder);
        client.EditHolder = SeedHolder;
        try
        {
            return await client.AddJunctionAsync(new JunctionRequest(SourceId, TargetId, 99));
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(SeedHolder);
        }
    }
}
