using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Row 9 of the parity table, measured instead of guessed: <b>aligning, distributing and giving
/// shapes the same size</b> - the eleven commands of the WinForms editor - and the question the
/// table could not answer from the code, which is whether the operator can reach any of them at all
/// through the cut-down chrome (<c>ui=min</c>) the page asks draw.io for.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was not known, and why reading the code could not settle it.</b> The actions exist in the
/// bundle - <c>alignCellsLeft</c> and the other seven are registered like any other - but an action
/// that exists is not an action anybody can reach. The two roads an operator has are the keyboard
/// and the menus, and <c>ui=min</c> takes the menu <b>bar</b> away, so what was left to establish is
/// what stays.
/// </para>
/// <para>
/// <b>MEASURED, 2026-09-18, and the answer is two-sided.</b> The eight alignment and distribution
/// commands are reachable from draw.io's own <b>right-click menu</b>, which <c>ui=min</c> does not
/// touch: right-clicking a multi-selection opens a menu carrying Group, Align and Distribute, and
/// Align opens onto Left Align, Center, Right Align, Top Align, Middle, Bottom Align. From the
/// <b>keyboard</b> they are reachable from nowhere: draw.io declares no shortcut for any of the
/// eight, and the shipped bundle binds no key to them. The size commands are the other way round -
/// Copy Size and Paste Size carry <c>Alt+Shift+F</c> and <c>Alt+Shift+V</c>, and those keys arrive.
/// </para>
/// <para>
/// <b>And what comes out of an alignment does reach the plant</b>, which is the half of row 9 the
/// table has been asserting since September without anybody having tried it. It is written as an
/// ordinary move, and it is written <b>as an envelope</b>: draw.io aligns the boxes it draws, which
/// are the shapes' own boxes, while the plant stores the box of the already-rotated shape. Two
/// devices at different rotations therefore come out left-aligned in the drawing and about a unit
/// and a half apart on the plant (measured: 200 and 201.43 for shapes at 30 and 20 degrees). On
/// devices that are not rotated - which is every device of a plant nobody has turned - the two
/// coincide.
/// </para>
/// <para>
/// <b>Every negative answer here carries its control.</b> A keystroke that "does nothing" is the
/// cheapest false measurement on this branch: an apparatus that quietly eats keys says no to
/// everything. So the test that says the alignments have no shortcut presses <c>Ctrl+G</c> and
/// <c>Ctrl+Shift+U</c> in the same page and demands that the model change both times, and the size
/// commands are measured through the keys themselves rather than through their declarations.
/// </para>
/// </remarks>
public sealed class DrawioAlignmentTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    /// <summary>Who seeds the plant: a lock holder of its own, released at once, as in the other browser tests.</summary>
    private const string SeedHolder = "browser-test-alignment";

    /// <summary>The default layer of a draw.io drawing: a cell outside it is a cell inside a group.</summary>
    private const string DefaultLayer = "1";

    /// <summary>
    /// How far apart two numbers may be and still count as the same. The drawing format carries two
    /// decimals, and the plant stores what it is given.
    /// </summary>
    private const double SameUnit = 0.05;

    /// <summary>
    /// How far apart the two devices may sit on the plant after being left-aligned in the drawing.
    /// It is not zero, and the reason is the whole nuance of this row: the plant keeps the box of
    /// the rotated shape, draw.io aligns the box of the unrotated one. Measured at 1.43 units for
    /// the two rotations this plant is seeded with; the room left is a few times that, and far
    /// smaller than the 420 units the alignment closes.
    /// </summary>
    private const double EnvelopeRoom = 6;

    /// <summary>
    /// The least the aligned device must travel for the gesture to count as having happened at all.
    /// The two seeded devices sit 420 units apart, so an alignment that arrived moves about that
    /// much: a threshold at 300 cannot be met by a stray drag or by a rounding.
    /// </summary>
    private const double MinimumTravel = 300;

    /// <summary>The six alignments and the two distributions - the eight of the eleven that draw.io has as actions.</summary>
    private static readonly string[] AlignAndDistribute =
    [
        "alignCellsLeft", "alignCellsCenter", "alignCellsRight",
        "alignCellsTop", "alignCellsMiddle", "alignCellsBottom",
        "distributeHorizontal", "distributeVertical",
    ];

    /// <summary>
    /// The actions whose shortcut draw.io <b>does</b> declare, named here as the control on the
    /// reading itself: if this inventory came back empty-handed for everything, it would be the
    /// reading that is broken and not draw.io that is silent.
    /// </summary>
    private static readonly string[] TheOnesWithAShortcut =
    [
        "group", "ungroup", "undo", "copy", "paste", "autosize", "copySize", "pasteSize",
    ];

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// The operator's road to the six alignments: right-click on a multi-selection, Align, Left
    /// Align - and then the plant has to receive it. Nothing about this gesture is ours; what is
    /// ours is only whether the geometry it produces is written like any other move.
    /// </summary>
    [Fact]
    public async Task AligningFromTheRightClickMenuReachesThePlant()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        var anchor = PlantSeed.DraggedId.ToString();
        var aligned = PlantSeed.UntouchedId.ToString();

        // The control on the measurement itself: if the two were already left-aligned in the
        // drawing, an alignment that did nothing at all would look exactly like one that worked.
        var anchorBefore = await editor.CellPositionAsync(anchor);
        var alignedBefore = await editor.CellPositionAsync(aligned);
        output.WriteLine($"Nel disegno, prima: {Show(anchorBefore)} e {Show(alignedBefore)}.");
        Assert.True(Math.Abs(anchorBefore[0] - alignedBefore[0]) > MinimumTravel,
            $"I due dispositivi partono gia' quasi allineati a sinistra ({anchorBefore[0]:0.##} e {alignedBefore[0]:0.##}): " +
            "un allineamento che non facesse niente sarebbe indistinguibile da uno riuscito, e questa misura non " +
            "direbbe piu' niente. Il seme e' cambiato.");

        await editor.SelectDevicesAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
        output.WriteLine($"Menu del tasto destro su due dispositivi selezionati:{Environment.NewLine}{await editor.TheContextMenuAsync()}");

        await editor.HoverTheMenuEntryAsync("Align");
        output.WriteLine($"Con il sottomenu \"Align\" aperto:{Environment.NewLine}{await editor.TheContextMenuAsync()}");
        await editor.ClickTheMenuEntryAsync("Left Align");

        var anchorAfter = await editor.CellPositionAsync(anchor);
        var alignedAfter = await editor.CellPositionAsync(aligned);
        output.WriteLine($"Nel disegno, dopo: {Show(anchorAfter)} e {Show(alignedAfter)}.");
        Assert.True(Math.Abs(anchorAfter[0] - alignedAfter[0]) <= SameUnit,
            $"Nel disegno i due dispositivi non sono allineati a sinistra: {anchorAfter[0]:0.##} e {alignedAfter[0]:0.##}. " +
            "La voce \"Left Align\" del menu contestuale non ha allineato niente.");

        // Aligning to the left moves the right-hand one: the anchor is the leftmost, and it must
        // have stayed exactly where it was. A "gesture" that moved both would be something else.
        Assert.True(Math.Abs(anchorBefore[0] - anchorAfter[0]) <= SameUnit && Math.Abs(anchorBefore[1] - anchorAfter[1]) <= SameUnit,
            $"Il dispositivo piu' a sinistra si e' mosso ({Show(anchorBefore)} -> {Show(anchorAfter)}): " +
            "un allineamento a sinistra porta gli altri sul suo bordo, non lui altrove.");

        await editor.SaveAsync();
        output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

        var error = await editor.SaveErrorAsync();
        Assert.True(error is null, $"Il salvataggio ha dichiarato un errore: {error}");

        var refusals = await editor.RefusalsAsync();
        Assert.DoesNotContain(refusals, r => r.Contains(PlantSeed.UntouchedId.ToString(), StringComparison.OrdinalIgnoreCase));

        // The line the table has been claiming without measuring: the geometry an alignment leaves
        // behind is written as an ordinary move, and it is named.
        var written = await editor.WrittenAsync();
        Assert.Contains(written, line =>
            line.Contains("move", StringComparison.OrdinalIgnoreCase) &&
            line.Contains(PlantSeed.UntouchedName, StringComparison.Ordinal));

        var graph = await client.GetPlantGraphAsync();
        var anchorOnPlant = PlantSeed.GeometryOf(graph, PlantSeed.DraggedId);
        var alignedOnPlant = PlantSeed.GeometryOf(graph, PlantSeed.UntouchedId);
        Report(PlantSeed.DraggedName, PlantSeed.DraggedGeometry, anchorOnPlant);
        Report(PlantSeed.UntouchedName, PlantSeed.UntouchedGeometry, alignedOnPlant);

        var travelled = Math.Abs((PlantSeed.UntouchedGeometry.X ?? 0) - (alignedOnPlant.X ?? 0));
        Assert.True(travelled >= MinimumTravel,
            $"Sull'impianto \"{PlantSeed.UntouchedName}\" si e' spostato di {travelled:0.##} unita' invece delle " +
            $"~420 dell'allineamento: il disegno e' stato allineato e l'impianto no.");

        var gap = Math.Abs((anchorOnPlant.X ?? 0) - (alignedOnPlant.X ?? 0));
        output.WriteLine(
            $"Scarto fra le due X sull'impianto dopo l'allineamento: {gap:0.###} unita'. Non e' zero, ed e' il " +
            "punto di questa misura: draw.io allinea i riquadri che disegna (la dimensione propria della sagoma), " +
            "l'impianto memorizza l'inviluppo della sagoma gia' ruotata. Su dispositivi non ruotati i due coincidono.");
        Assert.True(gap <= EnvelopeRoom,
            $"Dopo l'allineamento le due X sull'impianto distano {gap:0.###} unita', piu' di quanto la differenza " +
            $"fra riquadro proprio e inviluppo possa spiegare ({EnvelopeRoom:0} unita'): l'allineamento e' arrivato " +
            "storto, non solo spostato.");

        // The trap this sub-project has paid for four times: a move that carries a deformation.
        AssertSame($"la larghezza di \"{PlantSeed.UntouchedName}\"", PlantSeed.UntouchedGeometry.Width, alignedOnPlant.Width);
        AssertSame($"l'altezza di \"{PlantSeed.UntouchedName}\"", PlantSeed.UntouchedGeometry.Height, alignedOnPlant.Height);
        AssertSame($"la rotazione di \"{PlantSeed.UntouchedName}\"", PlantSeed.UntouchedGeometry.Rotation, alignedOnPlant.Rotation);

        // And the device nobody aligned onto: untouched on the plant too.
        AssertSame($"la X di \"{PlantSeed.DraggedName}\"", PlantSeed.DraggedGeometry.X, anchorOnPlant.X);
        AssertSame($"la Y di \"{PlantSeed.DraggedName}\"", PlantSeed.DraggedGeometry.Y, anchorOnPlant.Y);
    }

    /// <summary>
    /// The other road, and it is closed: draw.io binds no key to any of the six alignments or the
    /// two distributions. The claim is a negative one, so it is made three times over - draw.io's
    /// own declaration, the state of the actions, and a control that the keys this page does know
    /// about arrive and change the model.
    /// </summary>
    /// <remarks>
    /// <b>The control is the whole reason this test is not the owner's failed measurement written
    /// down.</b> On 2026-09-18 <c>Ctrl+G</c> was pressed at this page through a tool that never
    /// delivered it, and so was <c>Escape</c>, whose effect is known: two negatives from an
    /// apparatus that was answering no to everything. Here <c>Ctrl+G</c> has to group and
    /// <c>Ctrl+Shift+U</c> has to ungroup, in the same page and after the inventory has been read,
    /// or the negative result is thrown out with a message that says so.
    /// </remarks>
    [Fact]
    public async Task NoKeystrokeOfDrawioAlignsOrDistributes()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder + "-keys");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.SelectDevicesAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);

        var inventory = await editor.ActionInventoryAsync([.. AlignAndDistribute, .. TheOnesWithAShortcut]);
        foreach (var line in inventory) output.WriteLine(line);

        foreach (var name in AlignAndDistribute)
        {
            var line = LineFor(inventory, name);

            // The action has to be there and has to be usable: "no shortcut" on an action that does
            // not exist, or that is greyed out, would be a different fact wearing this one's name.
            Assert.Contains("enabled=yes", line, StringComparison.Ordinal);
            Assert.True(line.Contains("shortcut=(none declared)", StringComparison.Ordinal),
                $"draw.io dichiara una scorciatoia per \"{name}\": {line}. La riga 9 della tabella di parita' dice " +
                "che dalla tastiera non ci si arriva, e da oggi sarebbe falsa.");
        }

        foreach (var name in TheOnesWithAShortcut)
        {
            var line = LineFor(inventory, name);
            Assert.False(line.Contains("shortcut=(none declared)", StringComparison.Ordinal),
                $"Nemmeno \"{name}\" dichiara una scorciatoia: {line}. Allora non e' draw.io a tacere sugli " +
                "allineamenti, e' questa lettura a non leggere niente - e il risultato negativo qui sopra non vale.");
        }

        // Il controllo che questo mandato esiste per non ripetere: una scorciatoia di cui si conosce
        // l'effetto, premuta dallo stesso apparato, sulla stessa pagina, sulla stessa selezione.
        var grouped = PlantSeed.DraggedId.ToString();
        var parentBefore = await editor.CellParentAsync(grouped);
        Assert.Equal(DefaultLayer, parentBefore);

        await editor.GroupSelectionAsync();
        var parentGrouped = await editor.CellParentAsync(grouped);
        output.WriteLine($"Controllo - Ctrl+G: il genitore di \"{PlantSeed.DraggedName}\" passa da \"{parentBefore}\" a \"{parentGrouped}\".");
        Assert.True(parentGrouped is not null && parentGrouped != DefaultLayer,
            $"Ctrl+G non ha raggruppato niente (il genitore e' ancora \"{parentGrouped}\"): l'apparato non consegna " +
            "i tasti a draw.io, quindi il \"nessuna scorciatoia\" misurato qui sopra non e' una misura, e' il " +
            "silenzio di uno strumento rotto. E' esattamente l'errore che questa prova esiste per non ripetere.");

        await editor.UngroupSelectionAsync();
        var parentUngrouped = await editor.CellParentAsync(grouped);
        output.WriteLine($"Controllo - Ctrl+Shift+U: il genitore torna \"{parentUngrouped}\".");
        Assert.True(parentUngrouped == DefaultLayer,
            $"Ctrl+Shift+U non ha sciolto il gruppo (il genitore e' \"{parentUngrouped}\"): il secondo controllo " +
            "dell'apparato non passa, e il risultato negativo qui sopra resta senza rete.");
    }

    /// <summary>
    /// The last three of the eleven - "same width", "same height", "same size" in the WinForms
    /// editor. draw.io does it in two moves instead of one, Copy Size then Paste Size, and unlike
    /// the alignments those two <b>do</b> have keystrokes: this measures the keystrokes themselves,
    /// not what draw.io says about them, and then follows the result all the way to the plant.
    /// </summary>
    /// <remarks>
    /// It is one size and not three: draw.io carries width and height together, so "same width
    /// only" and "same height only" have no equivalent. And what lands on the plant is again the
    /// <b>envelope</b>: a 100x50 shape at 20 degrees given the size of a 120x60 one comes back as
    /// 133.28 x 97.42, not 120x60.
    /// </remarks>
    [Fact]
    public async Task TheSameSizeTravelsFromOneDeviceToAnotherWithAltShiftFAndAltShiftV()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder + "-size");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        var source = PlantSeed.DraggedId.ToString();
        var target = PlantSeed.UntouchedId.ToString();

        var sourceBox = await editor.CellGeometryAsync(source);
        var targetBefore = await editor.CellGeometryAsync(target);
        output.WriteLine($"Nel disegno, prima: sorgente {Show(sourceBox)}, bersaglio {Show(targetBefore)}.");

        // Without this the measurement could not fail: two shapes already the same size would come
        // out "the same size" whatever the keys did.
        Assert.True(Math.Abs(sourceBox[2] - targetBefore[2]) > 1 && Math.Abs(sourceBox[3] - targetBefore[3]) > 1,
            $"I due dispositivi hanno gia' la stessa dimensione nel disegno ({Show(sourceBox)} e {Show(targetBefore)}): " +
            "questa misura non potrebbe fallire. Il seme e' cambiato.");

        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.CopySizeAsync();
        await editor.SelectDevicesAsync(PlantSeed.UntouchedName);
        await editor.PasteSizeAsync();

        var targetAfter = await editor.CellGeometryAsync(target);
        output.WriteLine($"Nel disegno, dopo Alt+Shift+F e Alt+Shift+V: bersaglio {Show(targetAfter)}.");
        Assert.True(Math.Abs(sourceBox[2] - targetAfter[2]) <= SameUnit && Math.Abs(sourceBox[3] - targetAfter[3]) <= SameUnit,
            $"Il bersaglio non ha preso la dimensione della sorgente: {Show(targetAfter)} contro {Show(sourceBox)}. " +
            "O i tasti Alt+Shift+F / Alt+Shift+V non arrivano a draw.io, o non fanno quello che draw.io dichiara.");

        await editor.SaveAsync();
        output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

        var error = await editor.SaveErrorAsync();
        Assert.True(error is null, $"Il salvataggio ha dichiarato un errore: {error}");
        var refusals = await editor.RefusalsAsync();
        Assert.DoesNotContain(refusals, r => r.Contains(PlantSeed.UntouchedId.ToString(), StringComparison.OrdinalIgnoreCase));

        var graph = await client.GetPlantGraphAsync();
        var onPlant = PlantSeed.GeometryOf(graph, PlantSeed.UntouchedId);
        Report(PlantSeed.UntouchedName, PlantSeed.UntouchedGeometry, onPlant);

        // The resize arrived, and it arrived as an envelope: bigger than it was in both directions,
        // and NOT the 120x60 of the drawing - which is the fact worth having written down.
        Assert.True((onPlant.Width ?? 0) > (PlantSeed.UntouchedGeometry.Width ?? 0) + 1,
            $"La larghezza sull'impianto e' {Show(onPlant.Width)}: era {Show(PlantSeed.UntouchedGeometry.Width)}, e la " +
            "dimensione nel disegno e' cresciuta. Il ridimensionamento non e' arrivato.");
        Assert.True((onPlant.Height ?? 0) > (PlantSeed.UntouchedGeometry.Height ?? 0) + 1,
            $"L'altezza sull'impianto e' {Show(onPlant.Height)}: era {Show(PlantSeed.UntouchedGeometry.Height)}. " +
            "Il ridimensionamento non e' arrivato.");
        AssertSame($"la rotazione di \"{PlantSeed.UntouchedName}\"", PlantSeed.UntouchedGeometry.Rotation, onPlant.Rotation);

        output.WriteLine(
            $"Sull'impianto la dimensione e' {Show(onPlant.Width)} x {Show(onPlant.Height)}, non {sourceBox[2]:0.##} x " +
            $"{sourceBox[3]:0.##}: l'impianto memorizza l'inviluppo della sagoma ruotata, non il riquadro che draw.io " +
            "disegna. Due dispositivi resi \"della stessa dimensione\" nel disegno hanno sull'impianto due numeri " +
            "diversi ogni volta che le loro rotazioni sono diverse.");
    }

    /// <summary>
    /// Row 12's other half: grouping is reachable from the right-click menu as well as from
    /// <c>Ctrl+G</c>. What the save then does with a grouped device is measured whole in
    /// <see cref="DrawioGroupedCellsTest"/>; here only the road is measured, and nothing is saved.
    /// </summary>
    [Fact]
    public async Task GroupingIsReachableFromTheRightClickMenuToo()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder + "-group");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        var cell = PlantSeed.DraggedId.ToString();
        Assert.Equal(DefaultLayer, await editor.CellParentAsync(cell));

        await editor.SelectDevicesAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
        await editor.ClickTheMenuEntryAsync("Group");

        var parent = await editor.CellParentAsync(cell);
        output.WriteLine($"Dopo \"Group\" dal menu contestuale, il genitore di \"{PlantSeed.DraggedName}\" e' \"{parent}\".");
        Assert.True(parent is not null && parent != DefaultLayer,
            $"La voce \"Group\" del menu contestuale non ha raggruppato niente: il genitore e' ancora \"{parent}\".");

        await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
        await editor.ClickTheMenuEntryAsync("Ungroup");
        var back = await editor.CellParentAsync(cell);
        output.WriteLine($"Dopo \"Ungroup\", il genitore torna \"{back}\".");
        Assert.True(back == DefaultLayer,
            $"La voce \"Ungroup\" non ha sciolto il gruppo: il genitore e' \"{back}\".");
    }

    private static string LineFor(string[] inventory, string name) =>
        inventory.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"L'inventario non porta nessuna riga per \"{name}\".");

    private void Report(string what, PlantItemGeometryDto seeded, PlantItemGeometryDto now) => output.WriteLine(
        $"{what}: prima ({Show(seeded.X)}, {Show(seeded.Y)}) {Show(seeded.Width)}x{Show(seeded.Height)} a {Show(seeded.Rotation)} gradi; " +
        $"dopo ({Show(now.X)}, {Show(now.Y)}) {Show(now.Width)}x{Show(now.Height)} a {Show(now.Rotation)} gradi.");

    private static void AssertSame(string what, double? expected, double? actual)
    {
        Assert.True(actual.HasValue, $"{what}: non c'e' piu' nessun valore. Doveva essere {Show(expected)}.");
        Assert.True(expected.HasValue, $"{what}: la prova non sa che cosa aspettarsi (valore di partenza nullo).");
        Assert.True(Math.Abs(expected!.Value - actual!.Value) <= SameUnit,
            $"{what}: {Show(actual)} invece di {Show(expected)}.");
    }

    private static string Show(double? value) => value?.ToString("0.##") ?? "(nessuno)";

    private static string Show(double[] box) => "[" + string.Join(", ", box.Select(v => v.ToString("0.##"))) + "]";
}
