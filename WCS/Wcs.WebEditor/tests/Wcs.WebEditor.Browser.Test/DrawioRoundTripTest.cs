using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Il giro completo, con draw.io che gira davvero: impianto seminato via API, disegno aperto in un
/// browser vero, un dispositivo trascinato con il mouse, salvataggio premuto, e la verifica letta
/// <b>dall'impianto</b> - non dalla tela.
/// </summary>
/// <remarks>
/// <para>
/// E' la sola prova automatica che tenga insieme tutta la catena: draw.io, il ponte JS, la lettura
/// del disegno, il confronto del delta, i comandi di scrittura, l'host, il database. Le 746 prove
/// bUnit non possono farlo - bUnit non esegue JavaScript, quindi tutto cio' che si sa del ritorno
/// di draw.io la' dentro e' un documento scritto a mano da noi.
/// </para>
/// <para>
/// <b>La posizione si rilegge dall'host.</b> Chiederla alla tela proverebbe che draw.io ha mosso
/// una forma, che e' la parte di cui non si dubitava.
/// </para>
/// </remarks>
public sealed class DrawioRoundTripTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    /// <summary>Chi semina l'impianto: un titolare del permesso diverso da quello della pagina, e che lo rilascia subito.</summary>
    private const string SeedHolder = "browser-test-seed";

    /// <summary>
    /// Lo scostamento del trascinamento, in pixel dello schermo. Verso il basso a destra: il verso
    /// e' meta' dell'asserzione - un dispositivo che finisse a sinistra direbbe che qualcosa
    /// specchia le coordinate.
    /// </summary>
    private const double DragX = 200;
    private const double DragY = 140;

    /// <summary>
    /// Quanto deve almeno essersi spostato il dispositivo sull'impianto perche' il trascinamento
    /// conti come avvenuto. E' un limite basso di proposito: l'ingrandimento con cui draw.io
    /// dispone il disegno non e' deciso da noi, quindi 200 pixel valgono almeno 200 unita' se il
    /// disegno e' a grandezza naturale e molte di piu' se e' rimpicciolito. Un dispositivo che non
    /// si muove affatto (delta zero) cade sotto questo limite, che e' il caso che conta.
    /// </summary>
    private const double MinimumPlantMove = 20;

    /// <summary>
    /// Mette nel verbale su quale binario questa prova sta girando. <b>Anche quando passa</b>: un
    /// verde che non dice di che cosa e' il verde non e' verificabile da chi lo legge, e qui il
    /// legame con i sorgenti non lo tiene MSBuild - lo tiene <see cref="ProductBinary"/>.
    /// </summary>
    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    [Fact]
    public async Task DraggingADeviceWritesItsNewPositionAndLeavesTheOtherOneAlone()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        var (before, after) = await editor.DragDeviceAsync(PlantSeed.DraggedName, DragX, DragY);

        // Prima di guardare l'impianto si guarda il GESTO: se mxGraph non avesse mosso niente, il
        // fallimento deve dire "il trascinamento non ha mosso la sagoma", non "l'impianto non e'
        // cambiato" - sono due guasti diversi e si riparano in due posti diversi.
        Assert.True(after.X > before.X && after.Y > before.Y,
            $"Il trascinamento non ha mosso la sagoma sulla tela: era a ({before.X:0.#}, {before.Y:0.#}), " +
            $"e' a ({after.X:0.#}, {after.Y:0.#}). Il gesto non e' arrivato a draw.io.");

        await editor.SaveAsync();
        await AssertSaveWentThroughAsync(editor);

        var moved = await PlantSeed.ReadGeometryAsync(client, PlantSeed.DraggedId);
        var still = await PlantSeed.ReadGeometryAsync(client, PlantSeed.UntouchedId);

        var seededMoved = PlantSeed.DraggedGeometry;
        Assert.True(Delta(seededMoved.X, moved.X) >= MinimumPlantMove,
            $"La X del dispositivo trascinato sull'impianto e' {Show(moved.X)}: era {Show(seededMoved.X)}, e il " +
            $"trascinamento andava verso destra. Il giro non ha scritto lo spostamento.");
        Assert.True(Delta(seededMoved.Y, moved.Y) >= MinimumPlantMove,
            $"La Y del dispositivo trascinato sull'impianto e' {Show(moved.Y)}: era {Show(seededMoved.Y)}, e il " +
            $"trascinamento andava verso il basso. Il giro non ha scritto lo spostamento.");

        // La trappola pagata quattro volte in questo sotto-progetto: uno spostamento che porta con
        // se' una deformazione. Il riquadro dell'impianto e' l'inviluppo della sagoma GIA' ruotata,
        // e basta un passaggio storto perche' un semplice trascinamento lo gonfi o raddrizzi il
        // dispositivo.
        AssertSame("la larghezza del dispositivo trascinato", seededMoved.Width, moved.Width);
        AssertSame("l'altezza del dispositivo trascinato", seededMoved.Height, moved.Height);
        AssertSame("la rotazione del dispositivo trascinato", seededMoved.Rotation, moved.Rotation);

        AssertUntouched(still);
    }

    /// <summary>
    /// <b>Two saves with nothing touched are two saves</b>, and the page can say so even though
    /// they end with the same report word for word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEDIO 2 of the second revision, and this is the net for it. The empty round produces the
    /// same report every time - "1 write, the drawing's decoration" - so it is the one gesture
    /// that pins <see cref="DrawioEditorPage.SaveAsync"/> down: a wait written on the report's
    /// TEXT cannot come back from the second click at all, and this test hangs until it times
    /// out. Measured that way before the counter existed.
    /// </para>
    /// <para>
    /// It is here and not among the head tests because the head tests cannot prove it: their two
    /// saves end differently, so a wait on the text gets them through. The reviewer's red run was
    /// the case where two saves ended the SAME, and until this test there was no gesture in
    /// either suite that produced it on purpose.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoSavesThatEndTheSameWayAreStillTwoSaves()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        await editor.SaveAsync();
        await AssertSaveWentThroughAsync(editor);
        var first = await editor.ReportAsync();
        output.WriteLine($"Primo salvataggio:{Environment.NewLine}{first}");

        await editor.SaveAsync();
        await AssertSaveWentThroughAsync(editor);
        var second = await editor.ReportAsync();
        output.WriteLine($"Secondo salvataggio:{Environment.NewLine}{second}");

        // La premessa di questa prova, e l'unica cosa che la rende una rete: i due verbali sono
        // uguali. Se un giorno smettessero di esserlo, questo controllo cade e dice che la prova
        // non prova piu' quello che crede - invece di restare verde senza motivo.
        Assert.Equal(first, second);

        // E l'impianto non si e' mosso: due giri a vuoto restano due giri a vuoto.
        var graph = await client.GetPlantGraphAsync();
        Assert.Equal(2, graph.Items.Count);
        Assert.Empty(graph.Junctions);
        AssertSameGeometry("il dispositivo che le altre prove trascinano", PlantSeed.DraggedGeometry,
            PlantSeed.GeometryOf(graph, PlantSeed.DraggedId));
    }

    [Fact]
    public async Task SavingWithoutTouchingAnythingLeavesThePlantWhereItWas()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // Nessun gesto: si apre e si salva. E' il giro a vuoto - se un giorno una delle conversioni
        // fra l'impianto e il disegno si sfasasse anche di poco, un salvataggio senza nessun gesto
        // sposterebbe l'impianto INTERO, e questa e' l'unica rete che lo prende.
        await editor.SaveAsync();
        await AssertSaveWentThroughAsync(editor);

        var written = await editor.WrittenAsync();

        // MISURATO in un browser vero, e diverso da quello che si poteva pensare: un salvataggio
        // senza nessun gesto non e' un salvataggio senza scritture. Ne fa comunque UNA - la
        // decorazione del disegno, che la pagina riscrive a ogni salvataggio riuscito (si veda il
        // ramo _decorationLoaded di DrawioEditor) - e il riassunto dice "1 scritture", non "non
        // c'era niente da scrivere".
        // Si chiede quella riga per NOME e nessun'altra, invece di chiedere che nessuna nomini un
        // dispositivo: un Assert.All su un elenco vuoto passa sempre, quindi quella forma sarebbe
        // rimasta verde anche se il verbale fosse sparito del tutto, e sarebbe rimasta verde anche
        // se il giro a vuoto avesse cominciato a scrivere qualcosa che non c'entra.
        var line = Assert.Single(written);
        Assert.Contains("decoration", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PlantSeed.DraggedName, line, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantSeed.UntouchedName, line, StringComparison.Ordinal);

        // "Identico" vuol dire anche che non e' nato ne' sparito niente: i due dispositivi seminati
        // e nessuna giunzione. Le sole geometrie non lo direbbero.
        var graph = await client.GetPlantGraphAsync();
        Assert.Equal(2, graph.Items.Count);
        Assert.Empty(graph.Junctions);

        AssertSameGeometry("il dispositivo che le altre prove trascinano", PlantSeed.DraggedGeometry,
            PlantSeed.GeometryOf(graph, PlantSeed.DraggedId));
        AssertUntouched(PlantSeed.GeometryOf(graph, PlantSeed.UntouchedId));
    }

    /// <summary>
    /// Il verbale del salvataggio non deve portare nessun errore, nessun rifiuto e nessuna riga non
    /// scritta. Si legge PRIMA di guardare l'impianto: un impianto invariato per colpa di un
    /// rifiuto non e' la stessa cosa di un impianto invariato perche' non c'era niente da cambiare,
    /// e senza questo controllo le due si confonderebbero.
    /// </summary>
    private static async Task AssertSaveWentThroughAsync(DrawioEditorPage editor)
    {
        var error = await editor.SaveErrorAsync();
        Assert.True(error is null, $"Il salvataggio ha dichiarato un errore: {error}{Environment.NewLine}" +
            $"Verbale intero:{Environment.NewLine}{await editor.ReportAsync()}");

        var refusals = await editor.RefusalsAsync();
        Assert.True(refusals.Count == 0,
            $"Il salvataggio ha rifiutato qualcosa:{Environment.NewLine}{string.Join(Environment.NewLine, refusals)}");

        var notWritten = await editor.NotWrittenAsync();
        Assert.True(notWritten.Count == 0,
            $"Il salvataggio si e' fermato a meta':{Environment.NewLine}{string.Join(Environment.NewLine, notWritten)}");

        var unconfirmed = await editor.UnconfirmedAsync();
        Assert.True(unconfirmed.Count == 0,
            $"Il salvataggio ha lasciato scritture senza risposta:{Environment.NewLine}{string.Join(Environment.NewLine, unconfirmed)}");

        // Le avvertenze sono l'altra meta' del "niente di silenzioso": il codice ha scelto qualcosa
        // al posto di chi disegna. Su un impianto di due dispositivi e nessuna freccia non ce ne
        // deve essere nessuna, e lasciarle passare senza guardarle sarebbe la stessa cecita' che
        // questa prova esiste per togliere.
        var notices = await editor.NoticesAsync();
        Assert.True(notices.Count == 0,
            $"Il salvataggio ha scelto qualcosa al posto di chi disegna:{Environment.NewLine}{string.Join(Environment.NewLine, notices)}");

        var summary = await editor.SaveSummaryAsync();
        Assert.True(summary is not null, $"Il salvataggio non ha lasciato nessun riassunto. Verbale:{Environment.NewLine}{await editor.ReportAsync()}");
    }

    /// <summary>Il dispositivo che nessuno ha toccato, invariato in tutti e cinque i numeri.</summary>
    private static void AssertUntouched(PlantItemGeometryDto actual) =>
        AssertSameGeometry("il dispositivo che nessuno ha toccato", PlantSeed.UntouchedGeometry, actual);

    private static void AssertSameGeometry(string what, PlantItemGeometryDto expected, PlantItemGeometryDto actual)
    {
        AssertSame($"la X di {what}", expected.X, actual.X);
        AssertSame($"la Y di {what}", expected.Y, actual.Y);
        AssertSame($"la larghezza di {what}", expected.Width, actual.Width);
        AssertSame($"l'altezza di {what}", expected.Height, actual.Height);
        AssertSame($"la rotazione di {what}", expected.Rotation, actual.Rotation);
    }

    /// <summary>
    /// Due decimali: e' la precisione con cui il disegno scrive i numeri (<c>"0.##"</c> in
    /// <c>DrawioDocument</c>), quindi chiedere di piu' vorrebbe dire chiedere al giro una
    /// precisione che il formato non porta.
    /// </summary>
    private static void AssertSame(string what, double? expected, double? actual)
    {
        Assert.True(actual.HasValue,
            $"{what}: sull'impianto non c'e' piu' nessun valore (il dispositivo risulta non collocato). Doveva essere {Show(expected)}.");
        Assert.True(expected.HasValue, $"{what}: la prova non sa che cosa aspettarsi (valore di partenza nullo).");
        Assert.Equal(expected!.Value, actual!.Value, 2);
    }

    private static double Delta(double? from, double? to) => (to ?? 0) - (from ?? 0);

    private static string Show(double? value) => value?.ToString("0.##") ?? "(nessuno)";
}
