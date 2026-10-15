using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// <b>Edit Data di draw.io su una cella nostra: che cosa ne resta del marchio.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>La domanda.</b> Il marchio di una cella nostra viaggia in un <c>&lt;object wcs="..."&gt;</c> e
/// il lettore chiede <c>element.Name == "object"</c>. Misurato nel lotto 2:
/// <c>graph.setAttributeForCell</c> - un'altra via per marchiare una cella - produce invece un
/// <c>&lt;UserObject&gt;</c>, che il lettore non legge affatto. Il nostro ponte quella via non la
/// usa; ma <b>Edit Data e' una porta che draw.io offre all'operatore</b> (Ctrl+M, e l'azione e'
/// abilitata), e se passandoci la cella diventasse un <c>&lt;UserObject&gt;</c> il marchio
/// diventerebbe illeggibile e la cella perderebbe il legame con l'impianto.
/// </para>
/// <para>
/// <b>La risposta, misurata: no.</b> La cella resta un <c>&lt;object&gt;</c> e il marchio resta
/// leggibile. Questa prova esiste per tenerla vera: il bundle di draw.io si aggiorna, e il giorno
/// in cui quell'aggiornamento cambiasse la forma con cui il dialogo riscrive una cella, ogni cella
/// nostra che l'operatore ha aperto smetterebbe di essere nostra - in silenzio, perche' non c'e'
/// niente di invalido in un <c>&lt;UserObject&gt;</c>.
/// </para>
/// <para>
/// <b>E fin dove arriva la porta, che e' una premessa che ho sbagliato e che qui e' rimisurata.</b>
/// Il lotto 3 concludeva che da Edit Data il marchio non si puo' TOGLIERE, perche' i nomi delle
/// proprieta' sono etichette e non campi. E' falso: ogni riga ha un cestino
/// (<c>&lt;a class="geButton" title="Delete"&gt;</c>) e l'Apply fa <c>removeAttribute</c>. La
/// misura sbagliata veniva da uno strumento che quel bottone non lo vedeva - cercava fra i
/// <c>&lt;button&gt;</c> e per testo, e il cestino e' un <c>&lt;a&gt;</c> senza testo.
/// </para>
/// <para>
/// Quindi <b>togliere il marchio e' Ctrl+M, un clic, Apply</b>, e
/// <see cref="TakingOurMarkOffWithTheBinDoesNotCostTheJunction"/> misura che cosa ne segue.
/// </para>
/// </remarks>
public sealed class DrawioEditDataKeepsOurMarkTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-edit-data-keeps-our-mark";

    private static readonly Guid SourceId = new("a1b2c3d4-0000-4000-8000-000000000401");
    private static readonly Guid TargetId = new("a1b2c3d4-0000-4000-8000-000000000402");
    private const string SourceName = "EditDataSource";
    private const string TargetName = "EditDataTarget";

    private static readonly PlantItemGeometryDto SourceGeometry =
        new(SourceId, X: 160, Y: 1300, Width: 120, Height: 60, Rotation: 0);

    private static readonly PlantItemGeometryDto TargetGeometry =
        new(TargetId, X: 560, Y: 1300, Width: 120, Height: 60, Rotation: 0);

    [Fact]
    public async Task EditDataLeavesOurCellAnObjectAndOurMarkReadable()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        var junction = await SeedAJunctionAsync(client);
        var cellId = junction.JunctionId.ToString();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(SourceName);

        foreach (var line in await editor.ActionInventoryAsync("editData"))
            output.WriteLine($"AZIONE {line}");

        Assert.Equal("object", await editor.ValueNodeNameAsync(cellId));
        Assert.Equal("junction", await editor.ValueAttributeAsync(cellId, "wcs"));

        output.WriteLine($"Edit Data: {await editor.OpenEditDataAndApplyAsync(cellId)}");

        var node = await editor.ValueNodeNameAsync(cellId);
        var mark = await editor.ValueAttributeAsync(cellId, "wcs");
        output.WriteLine($"Dopo Apply: la cella e' un nodo <{node}>, e il marchio e' '{mark}'.");

        Assert.True(node == "object",
            $"Edit Data ha riscritto la cella come <{node}>: il lettore chiede element.Name == \"object\"," +
            " quindi da adesso il marchio di ogni cella che l'operatore apre in quel riquadro e' illeggibile.");
        Assert.Equal("junction", mark);

        // E il giro fino all'impianto, perche' un marchio leggibile nel modello e illeggibile nel
        // disegno che parte sarebbe la stessa perdita un passo piu' in la'.
        await editor.RecordReturnedDrawingAsync();
        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        output.WriteLine($"Verbale:{Environment.NewLine}{await editor.ReportAsync()}");

        var drawing = await editor.LastReturnedDrawingAsync();
        Assert.True(drawing.Contains($"""<object label="" wcs="junction" """, StringComparison.Ordinal)
                || drawing.Contains($"wcs=\"junction\"", StringComparison.Ordinal),
            $"Il disegno tornato indietro non porta piu' wcs=\"junction\":{Environment.NewLine}{drawing}");

        var after = await client.GetJunctionsAsync();
        output.WriteLine($"Giunzioni sull'impianto dopo il salvataggio: {after.Count}.");
        Assert.Equal(junction.JunctionId, Assert.Single(after).JunctionId);
    }

    /// <summary>
    /// <b>Il marchio tolto con il cestino di Edit Data non costa ne' la cella ne' la giunzione.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per tre lotti questo caso e' stato chiamato una decisione del proprietario - <i>o si perde la
    /// cella dell'operatore, o si perde la giunzione</i> - e valeva 264 strade esentate nella rete
    /// del disegno. Era un falso dilemma: quella non e' una linea dell'operatore, e' <b>la nostra
    /// cella senza marchio</b>, e l'impianto sa su quale identificativo aveva disegnato quella
    /// giunzione. Sapendolo, non si perde niente.
    /// </para>
    /// <para>
    /// Quello che questa prova misura e' il giro intero contro l'host: la giunzione resta, la cella
    /// <b>non</b> resta anche nella decorazione - che e' cio' che metteva due elementi sotto un
    /// identificativo - e alla riapertura la cella c'e' di nuovo, marchiata.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TakingOurMarkOffWithTheBinDoesNotCostTheJunction()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        var junction = await SeedAJunctionAsync(client);
        var cellId = junction.JunctionId.ToString();

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            Assert.Equal("junction", await editor.ValueAttributeAsync(cellId, "wcs"));

            output.WriteLine($"Edit Data: {await editor.RemoveTheDataRowThroughEditDataAsync(cellId, "junction")}");
            var mark = await editor.ValueAttributeAsync(cellId, "wcs");
            output.WriteLine($"DOPO IL CESTINO: wcs='{mark}'.");
            Assert.Equal("(absent)", mark);

            // E UN CAPO STACCATO, perche' senza marchio ma con i due capi sui dispositivi la cella
            // resta riconosciuta per gli ESTREMI e non succede niente - misurato sul binario vero.
            // Il caso da misurare e' quello in cui la nostra cella, perso il marchio, smette ANCHE
            // di dire collegamento: allora scende nella decorazione mentre l'impianto tiene ancora
            // la giunzione, ed e' li' che nasceva il doppione.
            var ends = await editor.TakeOneEndOffAsync(cellId);
            output.WriteLine($"Capi dopo lo stacco: {ends[0]} / {ends[1]}.");

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            var report = await editor.ReportAsync();
            output.WriteLine($"Verbale:{Environment.NewLine}{report}");

            Assert.True((await editor.RefusalsAsync()).Any(line =>
                    line.Contains("the mark was taken off this cell", StringComparison.Ordinal)),
                $"Il salvataggio non dice all'operatore che cosa e' successo alla cella." +
                $"{Environment.NewLine}{report}");
        }

        // LA GIUNZIONE RESTA, letta dall'host.
        var after = await client.GetJunctionsAsync();
        output.WriteLine($"GIUNZIONI SULL'IMPIANTO ADESSO: {after.Count}.");
        Assert.Equal(junction.JunctionId, Assert.Single(after).JunctionId);

        // E LA CELLA NON E' ANCHE NELLA DECORAZIONE: e' quello che metteva due elementi sotto un
        // identificativo sulla tela, e nessuno dei due si vedeva sopra l'altro.
        var decoration = await client.GetPlantDecorationAsync();
        output.WriteLine($"Decorazione:{Environment.NewLine}{decoration}");
        Assert.False(decoration.Contains($"\"{cellId}\"", StringComparison.Ordinal),
            $"La cella {cellId} e' rimasta anche nella decorazione: alla riapertura la tela ne porta due." +
            $"{Environment.NewLine}{decoration}");

        // E ALLA RIAPERTURA C'E', UNA VOLTA E MARCHIATA.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(SourceName);
        var edges = await reopened.EdgesInTheDrawingAsync();
        foreach (var edge in edges) output.WriteLine($"Alla riapertura: {edge}.");
        var drawn = Assert.Single(edges, edge => edge.Id == cellId);
        Assert.Equal("junction", drawn.Wcs);
    }

    private static async Task<JunctionDto> SeedAJunctionAsync(WcsClient client)
    {
        await PlantSeed.ResetAsync(client, SeedHolder);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, SourceId, SourceName, DeviceTypes.Automation, SourceGeometry);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, TargetId, TargetName, DeviceTypes.Automation, TargetGeometry);

        await client.AcquireEditLockAsync(SeedHolder);
        client.EditHolder = SeedHolder;
        try { return await client.AddJunctionAsync(new JunctionRequest(SourceId, TargetId, 99)); }
        finally { client.EditHolder = null; await client.ReleaseEditLockAsync(SeedHolder); }
    }
}
