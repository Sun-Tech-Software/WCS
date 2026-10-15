using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Il pannello delle forme di draw.io con dentro i nostri dispositivi, in un browser vero: che la
/// sezione ci sia, che porti una voce per ogni tipo del registro, e che il gesto arrivi fino in
/// fondo - una voce trascinata sulla tela <b>crea il dispositivo sull'impianto</b>, e lo si rilegge
/// dall'host.
/// </summary>
/// <remarks>
/// <para>
/// E' qui e non fra le prove bUnit perche' la' dentro non gira nessun JavaScript: che la pagina
/// MANDI la sezione lo prova <c>DrawioEditorPageTest</c>, e che il metodo della creazione faccia la
/// cosa giusta lo prova <c>DrawioEditorCreateTest</c>, ma che draw.io chiami quel metodo - e con
/// quali celle - si puo' misurare solo dove draw.io gira davvero. Sono due meta' e nessuna delle due
/// basta: il ponte legge i nomi degli attributi come letterali, e se divergessero dalle costanti C#
/// ogni prova unitaria resterebbe verde.
/// </para>
/// <para>
/// <b>Quanti tipi ci sono non si conta a mano</b>: lo si chiede all'host, con il client vero. Un
/// sette scritto qui renderebbe rossa questa prova il giorno che si aggiunge un tipo, cioe' il
/// giorno in cui il pannello deve aggiornarsi da solo.
/// </para>
/// </remarks>
public sealed class DrawioShapesPanelTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    /// <summary>Chi semina l'impianto: come in <c>DrawioRoundTripTest</c>, un titolare diverso da quello della pagina.</summary>
    private const string SeedHolder = "browser-test-panel-seed";

    /// <summary>
    /// La promessa che il titolo della sezione deve portare, e che questo compito esiste per fare
    /// arrivare <b>prima del gesto</b>. E' ripetuta qui come testo e non presa da
    /// <c>DrawioLibrary.SectionTitle</c> di proposito: questo progetto non ha nessun riferimento a
    /// <c>Wcs.WebEditor</c> - guida un processo gia' costruito - e soprattutto qui si guarda quello
    /// che chi disegna LEGGE. Se un domani il titolo si accorcia a "WCS devices", la promessa
    /// cade e questa prova diventa rossa, che e' esattamente cio' che deve succedere.
    /// <para>
    /// Dal lotto della creazione la promessa e' di segno opposto a quella di prima ("non li crea"):
    /// il gesto crea davvero, e il titolo lo dice.
    /// </para>
    /// </summary>
    private const string TitlePromise = "Create";

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// <b>Non semina l'impianto, e non e' una dimenticanza.</b> Il pannello nasce dal REGISTRO DEI
    /// TIPI che il motore dichiara, non dai dispositivi collocati: un impianto vuoto o pieno non
    /// cambia una virgola di quello che questa prova guarda. Non seminando, non prende nemmeno il
    /// permesso di modifica - e cosi' non aspetta che la prova qui sotto finisca di restituirlo, una
    /// consegna che dura fino alla scadenza del contratto di un minuto quando il circuito Blazor
    /// della pagina precedente ci mette il suo tempo a chiudersi (misurato: con due classi di prove
    /// in parallelo non basta mezzo minuto).
    /// </summary>
    [Fact]
    public async Task ThePanelCarriesOneEntryForEveryDeviceTypeTheHostDeclares()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);

        var title = await editor.DeviceSectionTitleAsync();
        var titleText = (await title.InnerTextAsync()).Trim();
        Assert.Contains(TitlePromise, titleText);

        // E la promessa si LEGGE, non e' soltanto scritta: draw.io taglia i titoli delle sezioni
        // con i puntini, e un titolo troppo lungo lascerebbe questa prova verde mostrando a chi
        // guarda "Dispositivi WCS: li...". E' successo davvero alla prima stesura del titolo:
        // 347 pixel di testo in 182 di spazio.
        var (text, room) = await editor.TitleWidthsAsync(title);
        output.WriteLine($"Titolo della sezione: \"{titleText}\", {text} pixel di testo in {room} di spazio.");
        Assert.True(text <= room,
            $"Il titolo della sezione e' tagliato dal pannello: \"{titleText}\" occupa {text} pixel e ne ha {room}. " +
            "Chi guarda legge i puntini al posto della promessa.");

        var entries = await editor.OpenDeviceSectionAsync();
        var types = await client.GetDeviceTypesAsync();
        var shown = await entries.CountAsync();
        output.WriteLine($"Tipi dichiarati dall'host: {types.Count} ({string.Join(", ", types.Select(t => t.Label))}). Voci nel pannello: {shown}.");

        // Il registro non e' vuoto: senza questa riga, un host che non dichiarasse nessun tipo
        // renderebbe l'uguaglianza qui sotto vera con zero contro zero, e il verde non direbbe
        // piu' niente.
        Assert.NotEmpty(types);
        Assert.Equal(types.Count, shown);
    }

    /// <summary>
    /// <b>LA TRAPPOLA, in un browser vero.</b> Aprire un impianto manda ogni suo dispositivo dentro
    /// il modello di draw.io in un colpo solo - sull'impianto del proprietario sono 51 celle, e
    /// succede a ogni riapertura. Zero di loro deve diventare un dispositivo.
    /// </summary>
    /// <remarks>
    /// <b>E' ancorata</b>: la prova aspetta che tutti e due i dispositivi seminati siano DISEGNATI
    /// sulla tela prima di contare, quindi il documento e' arrivato davvero e le celle sono
    /// davvero entrate nel modello. Senza quell'attesa, un impianto che non arriva mai lascerebbe
    /// il conto invariato e la prova verde - misurando il nulla.
    /// </remarks>
    [Fact]
    public async Task OpeningAPlantWithDevicesCreatesNothing()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();
        Assert.Equal(2, before.Items.Count);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        var after = await client.GetPlantGraphAsync();
        output.WriteLine($"Dispositivi prima: {before.Items.Count}, dopo l'apertura: {after.Items.Count}.");
        Assert.Equal(before.Items.Count, after.Items.Count);
        // E la pagina non ha nemmeno DETTO di aver creato qualcosa: il verbale del rilascio non c'e'.
        Assert.Equal("(nessun verbale della creazione)", await editor.CreationReportAsync());
    }

    /// <summary>
    /// Il giro completo della decisione del proprietario: si trascina una voce con il mouse e il
    /// dispositivo <b>c'e' sull'impianto</b> - riletto dall'host, con il tipo che la voce
    /// dichiarava e con una posizione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// E' la prova che vale il lotto, perche' e' l'unica che attraversa tutti e tre i pezzi insieme:
    /// la voce del pannello che dichiara il tipo, il ponte che sente l'aggiunta e ne legge marchio,
    /// tipo e riquadro, e la pagina che scrive. Le prove unitarie ne coprono uno per volta, e nessuna
    /// di loro vedrebbe due grafie divergere fra il C# e il JavaScript.
    /// </para>
    /// <para>
    /// Il tipo atteso si chiede all'host (la PRIMA voce del pannello e' il PRIMO tipo del registro,
    /// nell'ordine in cui il registro li dichiara - e' la promessa di <c>DrawioLibrary.Build</c>):
    /// scriverlo qui sarebbe stato scriverlo due volte, e una delle due si sarebbe sbagliata.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DraggingAnEntryCreatesTheDeviceOnThePlant()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();
        var types = await client.GetDeviceTypesAsync();
        Assert.NotEmpty(types);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        await editor.DragEntryOntoTheCanvasAsync(0);
        await editor.WaitForTheCreationReportAsync();

        // Il verbale intero nel messaggio: un "non c'era la riga" senza dire che cosa c'era fa
        // ripartire da zero chi lo legge.
        var report = await editor.CreationReportAsync();
        var created = await editor.CreatedAsync();
        Assert.True(created.Count == 1,
            $"Il rilascio non ha dichiarato la nascita di un dispositivo solo. Il verbale diceva:{Environment.NewLine}{report}");
        Assert.Empty(await editor.NotCreatedAsync());

        // E la parola si MISURA: l'impianto e' cresciuto di uno, ed e' il tipo della prima voce.
        var after = await client.GetPlantGraphAsync();
        Assert.Equal(before.Items.Count + 1, after.Items.Count);
        var born = Assert.Single(after.Items, item => before.Items.All(was => was.ItemId != item.ItemId));
        output.WriteLine($"Nato: {born.ItemId}, tipo {born.DeviceType} (atteso {types[0].NumericCode}), nome \"{born.Name}\".");
        Assert.Equal(types[0].NumericCode, born.DeviceType);
        // CON un nome suggerito (decisione del proprietario, 17/9/2026): trascinare non lo chiede,
        // ma il dispositivo non nasce muto. Il nome atteso non si ricalcola qui - la regola sta in
        // T/SuggestedDeviceNameTest e T/DrawioEditorCreateTest, e riscriverla sarebbe una prova
        // cieca per costruzione: qui si misura che l'ETICHETTA DEL TIPO, quella che il registro
        // dichiara per la voce trascinata, sia dentro il nome che l'host ha davvero memorizzato.
        Assert.False(string.IsNullOrEmpty(born.Name), "Il dispositivo nato non porta nessun nome: alla riapertura la sagoma sarebbe muta.");
        Assert.StartsWith(types[0].Label, born.Name, StringComparison.Ordinal);
        // E collocato: un dispositivo senza riquadro non verrebbe disegnato da nessuna parte.
        var geometry = Assert.Single(after.Geometries, g => g.ItemId == born.ItemId);
        Assert.True(geometry.Width is > 0 && geometry.Height is > 0,
            $"Il dispositivo nato non ha un riquadro: {geometry.Width} x {geometry.Height}.");
        // E la riga nomina il dispositivo, non un conto: l'identificativo perche' e' l'unica cosa
        // che non cambia, e il NOME perche' e' quello che l'operatore legge sulla sagoma dopo un
        // ricaricamento - una riga che non lo dicesse lascerebbe cinque riquadri da indovinare.
        Assert.Contains(born.ItemId.ToString(), created[0]);
        Assert.Contains(born.Name!, created[0]);
        // E la vecchia frase, che oggi sarebbe falsa, non c'e' piu'.
        Assert.DoesNotContain("NO NAME", created[0]);
    }

    /// <summary>
    /// <b>La sagoma porta il suo nome dal primo istante</b>, senza ricaricare: l'etichetta che
    /// draw.io <b>disegna</b> per la cella appena nata e' il nome che l'host dichiara per quel
    /// dispositivo, e non piu' l'etichetta del tipo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Misurato dal proprietario in un browser vero prima della riparazione</b>: appena creata la
    /// sagoma diceva "Group Automation" (il tipo), e solo riaprendo la pagina diceva
    /// "Group Automation 1" (il nome). Il verbale della creazione spendeva due frasi a spiegare
    /// quella divergenza, e su venti celle incollate le spendeva venti volte.
    /// </para>
    /// <para>
    /// <b>Le due osservazioni sono indipendenti</b>, ed e' quello che le da' valore: da una parte il
    /// nome che l'HOST ha memorizzato, dall'altra l'etichetta che draw.io disegna. La regola del
    /// nome non viene ricalcolata qui - sta in <c>T/SuggestedDeviceNameTest</c> - perche' una prova
    /// che la riscrivesse sarebbe cieca per costruzione.
    /// </para>
    /// <para>
    /// <b>E' ancorata sul punto che la fa fallire sul codice di prima</b>: il nome dell'impianto
    /// dev'essere diverso dall'etichetta del tipo. Senza quella riga, un giorno in cui il nome
    /// suggerito tornasse a essere la sola etichetta del tipo questa prova resterebbe verde
    /// misurando due cose uguali per caso.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheShapeJustDraggedCarriesItsNameOnTheDrawingAtOnce()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();
        var types = await client.GetDeviceTypesAsync();
        Assert.NotEmpty(types);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        await editor.DragEntryOntoTheCanvasAsync(0);
        await editor.WaitForTheCreationReportAsync();
        var after = await WaitForItemCountAsync(client, before.Items.Count + 1, editor);
        var born = Assert.Single(after.Items, item => before.Items.All(was => was.ItemId != item.ItemId));

        // La cella del nato e' quella che NON porta un guid: le seminate portano il loro. E' anche il
        // fatto che rende questa prova una prova su un dispositivo CREATO e non su uno seminato -
        // dove l'identificativo della cella e il guid sono la stessa stringa, ed e' la cecita' per
        // cui l'ultimo difetto di questa zona e' sfuggito a due revisioni.
        var cellId = Assert.Single(await editor.MarkedCellIdsAsync(), id => !Guid.TryParse(id, out _));
        Assert.NotEqual(born.ItemId.ToString(), cellId);

        Assert.False(string.IsNullOrEmpty(born.Name),
            $"Il dispositivo nato ({born.ItemId}) non porta nessun nome sull'impianto: non ci sarebbe niente da mostrare sulla sagoma.");
        Assert.NotEqual(types[0].Label, born.Name);

        output.WriteLine($"Nato {born.ItemId}, cella {cellId}, nome sull'impianto \"{born.Name}\", " +
            $"etichetta del tipo \"{types[0].Label}\", etichetta sulla tela \"{await editor.CellLabelAsync(cellId)}\".");

        // IL CUORE: l'etichetta che draw.io disegna, senza nessun ricaricamento.
        await editor.WaitForCellLabelAsync(cellId, born.Name!);

        // E il verbale non spiega piu' una divergenza che non c'e'.
        var created = Assert.Single(await editor.CreatedAsync());
        Assert.DoesNotContain("has not been touched", created);
        Assert.DoesNotContain("next time this page is opened", created);
        Assert.DoesNotContain("did not take", created);
    }

    /// <summary>
    /// <b>L'incollaggio di piu' celle</b>, che arriva alla pagina come <b>una sola chiamata</b>:
    /// ognuna delle sagome incollate porta il nome del <b>suo</b> dispositivo, non tutte lo stesso e
    /// non solo la prima.
    /// </summary>
    /// <remarks>
    /// <b>E' ancorata al numero di celle in quattro punti</b> - le celle incollate, i dispositivi
    /// nati, i nomi distinti e le etichette disegnate - perche' una prova che incollasse e guardasse
    /// una cella sola resterebbe verde su una pagina che ne ribattezza una e dimentica le altre, che
    /// e' precisamente la forma della perdita silenziosa di questo ramo.
    /// </remarks>
    [Fact]
    public async Task EveryShapeOfAPasteCarriesItsOwnNameOnTheDrawing()
    {
        const int many = 3;

        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        // Tre rilasci, perche' e' da celle che DICHIARANO il tipo che si copia: la copia di una cella
        // seminata non ne dichiara nessuno (DrawioDocument non scrive l'attributo) e verrebbe
        // rifiutata, il che misurerebbe un'altra cosa.
        var drop = default((float X, float Y));
        for (var n = 0; n < many; n++)
        {
            drop = await editor.DragEntryOntoTheCanvasAsync(n);
            await editor.WaitForTheCreationReportAsync();
            await WaitForItemCountAsync(client, before.Items.Count + n + 1, editor);
            await editor.WaitForCreationIdleAsync();
        }

        var afterTheDrops = await client.GetPlantGraphAsync();
        var droppedCells = (await editor.MarkedCellIdsAsync()).Where(id => !Guid.TryParse(id, out _)).ToArray();
        Assert.Equal(many, droppedCells.Length);

        // Il clic porta il fuoco della tastiera dentro l'iframe; la selezione si prende dal modello e
        // si CONTA, perche' un Ctrl+C su una selezione vuota copierebbe il nulla.
        await editor.ClickAtAsync(drop.X, drop.Y);
        Assert.Equal(many, await editor.SelectCellsAsync(droppedCells));
        await editor.ArmThePasteProbeAsync();
        await editor.CopyAsync();
        await editor.PasteAsync();

        await WaitForThePasteToLandAsync(editor, before.Items.Count + many * 2, client);
        var afterThePaste = await WaitForItemCountAsync(client, afterTheDrops.Items.Count + many, editor);
        Assert.Empty(await editor.NotCreatedAsync());

        var bornOfThePaste = afterThePaste.Items
            .Where(item => afterTheDrops.Items.All(was => was.ItemId != item.ItemId)).ToList();
        Assert.Equal(many, bornOfThePaste.Count);
        var pastedCells = (await editor.MarkedCellIdsAsync())
            .Where(id => !Guid.TryParse(id, out _) && !droppedCells.Contains(id)).ToArray();
        Assert.Equal(many, pastedCells.Length);

        // I nomi che l'HOST dichiara per i nati dall'incollaggio: tutti diversi, e nessuno vuoto -
        // senza questo, "l'insieme delle etichette e' l'insieme dei nomi" sarebbe vero anche di tre
        // stringhe vuote.
        var expected = bornOfThePaste.Select(item => item.Name ?? "").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(many, expected.Distinct(StringComparer.Ordinal).Count());
        Assert.All(expected, name => Assert.False(string.IsNullOrEmpty(name)));

        var drawn = await WaitForLabelsAsync(editor, pastedCells, expected);
        output.WriteLine($"Celle incollate: {string.Join(", ", pastedCells)}. " +
            $"Nomi sull'impianto: {string.Join(", ", expected)}. Etichette sulla tela: {string.Join(", ", drawn)}.");
        Assert.Equal(expected, drawn);
    }

    /// <summary>
    /// <b>Un Ctrl+Z dopo un incollaggio</b>: se ne vanno <b>tutte</b> le sagome incollate, con le
    /// loro etichette, e si fermano li' - le sagome che c'erano prima restano, e <b>con i loro
    /// nomi</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// E' il gesto in cui una etichetta non significativa costa di piu': un incollaggio lascia nella
    /// pila UNA voce significativa (l'arrivo delle celle) seguita da una voce per ogni etichetta.
    /// L'annullamento le attraversa tutte e si ferma sull'arrivo, quindi un gesto solo si disfa con
    /// una pressione sola per qualunque numero di celle. <b>Misurato su venti</b>, fuori da questa
    /// prova, con una sonda che leggeva la pila di draw.io il 18/9/2026: la pila e' passata da
    /// <c>indexOfNextAdd=37 di 37</c> a <c>58 di 58</c> - <b>una</b> voce significativa
    /// (<c>SIG/60</c>, le venti celle che arrivano insieme) e sopra di lei <b>venti</b> voci
    /// <c>insig/1[mxValueChange]</c>, le nostre etichette - e un Ctrl+Z ha riportato
    /// <c>indexOfNextAdd</c> a 37 e le celle marchiate da 50 a 30.
    /// </para>
    /// <para>
    /// La stessa misura, fatta nel lotto <c>0ccfd67</c>, aveva dato <b>ventidue</b> voci non
    /// significative invece di venti: due etichette del giro precedente erano ancora in volo e sono
    /// atterrate sopra l'incollaggio. Quelle due sono il difetto che il lotto dopo ha chiuso - si
    /// veda <see cref="ALabelLandingOnSomebodyElsesGestureDoesNotTakeThatGestureAway"/> - e oggi
    /// resterebbero significative.
    /// </para>
    /// <para>
    /// <b>Le sagome di prima, con i loro nomi, sono l'ancora.</b> Senza quel controllo la prova
    /// resterebbe verde anche se l'annullamento fosse andato oltre l'incollaggio a mangiarsi le
    /// etichette dei rilasci precedenti - che e' proprio il modo in cui un annullamento troppo
    /// lungo fa danno, ed e' l'altra faccia della stessa bandiera.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OneUndoTakesAwayEveryShapeOfAPasteAndStopsThere()
    {
        const int many = 3;

        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var drop = default((float X, float Y));
        for (var n = 0; n < many; n++)
        {
            drop = await editor.DragEntryOntoTheCanvasAsync(n);
            await editor.WaitForTheCreationReportAsync();
            await WaitForItemCountAsync(client, before.Items.Count + n + 1, editor);
            await editor.WaitForCreationIdleAsync();
        }

        var afterTheDrops = await client.GetPlantGraphAsync();
        var droppedCells = (await editor.MarkedCellIdsAsync()).Where(id => !Guid.TryParse(id, out _)).ToArray();
        Assert.Equal(many, droppedCells.Length);

        // LE ETICHETTE DEI RILASCI DEVONO ESSERE GIA' ARRIVATE PRIMA DELL'INCOLLAGGIO. Non e' una
        // cautela: una etichetta ancora in volo entrerebbe nella pila SOPRA l'incollaggio, e
        // l'annullamento se la porterebbe via insieme a lui. Aspettarla qui e' cio' che rende
        // deterministico il controllo di sotto.
        var nameOfCell = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in droppedCells)
        {
            var label = await WaitForANonTypeLabelAsync(editor, id, client);
            nameOfCell[id] = label;
        }

        await editor.ClickAtAsync(drop.X, drop.Y);
        Assert.Equal(many, await editor.SelectCellsAsync(droppedCells));
        await editor.ArmThePasteProbeAsync();
        await editor.CopyAsync();
        await editor.PasteAsync();

        await WaitForThePasteToLandAsync(editor, before.Items.Count + many * 2, client);
        var afterThePaste = await WaitForItemCountAsync(client, afterTheDrops.Items.Count + many, editor);
        Assert.Empty(await editor.NotCreatedAsync());
        var pastedCells = (await editor.MarkedCellIdsAsync())
            .Where(id => !Guid.TryParse(id, out _) && !droppedCells.Contains(id)).ToArray();
        Assert.Equal(many, pastedCells.Length);

        // Le etichette dell'incollaggio arrivano anche loro prima dell'annullamento, altrimenti si
        // misurerebbe un gesto a meta'.
        var expected = afterThePaste.Items
            .Where(item => afterTheDrops.Items.All(was => was.ItemId != item.ItemId))
            .Select(item => item.Name ?? "").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, await WaitForLabelsAsync(editor, pastedCells, expected));

        // UNA pressione sola.
        await editor.UndoAsync();
        await editor.WaitForMarkedCellsAsync(before.Items.Count + many);

        var left = await editor.MarkedCellIdsAsync();
        Assert.All(pastedCells, id => Assert.DoesNotContain(id, left));
        Assert.All(droppedCells, id => Assert.Contains(id, left));

        // E si e' fermato li': i tre rilasciati portano ancora il NOME, non l'etichetta del tipo.
        foreach (var id in droppedCells)
            Assert.Equal(nameOfCell[id], await editor.CellLabelAsync(id));

        output.WriteLine($"Un solo Ctrl+Z dopo un incollaggio di {many}: via le {many} incollate " +
            $"({string.Join(", ", pastedCells)}), restano le {many} rilasciate con i nomi " +
            $"{string.Join(", ", droppedCells.Select(id => nameOfCell[id]))}. " +
            $"L'impianto ne tiene {(await client.GetPlantGraphAsync()).Items.Count}.");
    }

    /// <summary>
    /// <b>Un gesto estraneo arrivato fra la nascita della sagoma e l'arrivo della sua etichetta non
    /// viene portato via da un Ctrl+Z.</b> L'operatore sposta una sagoma mentre la creazione e'
    /// ancora in volo: la prima pressione tocca <b>soltanto</b> l'etichetta appena atterrata, lo
    /// spostamento resta dov'e', e ci vuole una seconda pressione per disfarlo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// E' il prezzo dichiarato dal lotto <c>0ccfd67</c>, misurato li' e chiuso qui: un'etichetta non
    /// significativa che atterrasse <b>sopra</b> il gesto di qualcun altro farebbe attraversare
    /// l'annullamento fino a quel gesto, che sparirebbe insieme a lei. La regola che il ponte adesso
    /// applica e' «non significativa <b>solo</b> se atterra direttamente sopra la nascita a cui
    /// appartiene».
    /// </para>
    /// <para>
    /// <b>Quanto costa davvero il ripiego, misurato e non dedotto.</b> Qui costa <b>due</b>
    /// pressioni perche' il gesto e' un rilascio solo, con una etichetta sola: non e' il costo del
    /// ripiego, e' il costo di questo caso. Le etichette che atterrano dopo l'intruso restano
    /// significative <b>una per una</b>, perche' la camminata del ponte si ferma sull'intruso per
    /// tutte. Misurato il 18/9/2026 su un incollaggio di tre disturbato subito dopo la prima
    /// etichetta: la pila e' rimasta
    /// <c>6:SIG[la nascita] | 7:insig[etichetta 1] | 8:SIG[lo spostamento] | 9:SIG[etichetta 2] |
    /// 10:SIG[etichetta 3]</c>, e ci sono volute <b>quattro</b> pressioni per togliere le tre celle
    /// incollate. Su un incollaggio di venti disturbato prima della prima etichetta sarebbero
    /// ventidue. Il baratto resta quello giusto - il gesto di un altro non sparisce - ma il numero
    /// e' questo, e una prova sola non lo misura: si veda il rapporto del lotto.
    /// </para>
    /// <para>
    /// <b>Le tre ancore, perche' questa prova non possa passare a vuoto.</b> Il gesto estraneo deve
    /// essere <b>davvero avvenuto</b> (si guarda la posizione, non la buona volonta' di chi l'ha
    /// armato); l'etichetta del NOME deve essere <b>davvero arrivata</b> prima della pressione,
    /// altrimenti nella pila non ci sarebbe nessuna voce di etichetta e la prova misurerebbe un
    /// annullamento qualunque; e la <b>seconda</b> pressione deve riportare indietro lo spostamento,
    /// altrimenti «lo spostamento e' ancora li'» sarebbe vero anche di uno spostamento che non e'
    /// mai entrato nella pila.
    /// </para>
    /// <para>
    /// <b>Come si sincronizza la prima pressione senza cecita'.</b> Non si asserisce subito dopo il
    /// tasto - un annullamento non ancora eseguito lascerebbe tutto al suo posto e la prova sarebbe
    /// verde per il motivo sbagliato. Si aspetta l'effetto che l'annullamento ha <b>in tutti e
    /// due</b> i mondi, quello riparato e quello di prima: l'etichetta che torna a quella del tipo.
    /// Prima della riparazione quella pressione sola annullava, nello stesso giro sincrono, anche lo
    /// spostamento - quindi quando l'etichetta e' tornata indietro, li' lo spostamento era gia'
    /// tornato indietro anche lui, e la riga qui sotto lo vedeva.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALabelLandingOnSomebodyElsesGestureDoesNotTakeThatGestureAway()
    {
        const double dx = 60;
        const double dy = 0;

        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();
        var types = await client.GetDeviceTypesAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        // La vittima del gesto estraneo e' una sagoma SEMINATA, che con il rilascio non c'entra
        // niente: il suo identificativo nel disegno e' il guid dell'impianto.
        var unrelated = PlantSeed.UntouchedId.ToString();
        var wasAt = await editor.CellPositionAsync(unrelated);

        output.WriteLine(await editor.ArmAnUnrelatedGestureAtTheNextBirthAsync(unrelated, dx, dy));

        var drop = await editor.DragEntryOntoTheCanvasAsync(0);
        await editor.WaitForTheCreationReportAsync();
        Assert.Empty(await editor.NotCreatedAsync());
        var born = Assert.Single(
            (await client.GetPlantGraphAsync()).Items,
            item => before.Items.All(was => was.ItemId != item.ItemId));

        var cellId = Assert.Single(await editor.MarkedCellIdsAsync(), id => !Guid.TryParse(id, out _));

        // ANCORA 1: il gesto estraneo e' avvenuto davvero, e la sagoma seminata si e' mossa.
        var moved = await WaitForPositionAsync(editor, unrelated, wasAt[0] + dx, wasAt[1] + dy);
        var gesture = await editor.TheUnrelatedGestureAsync();
        Assert.True(gesture.Fired && gesture.Done,
            $"Il gesto estraneo non e' mai avvenuto (visto={gesture.Fired}, fatto={gesture.Done}, " +
            $"guasto=\"{gesture.Error}\"): senza di lui questa prova non misura niente.");

        // ANCORA 2: l'etichetta del NOME e' arrivata, cioe' nella pila c'e' davvero la sua voce.
        Assert.NotEqual(types[0].Label, born.Name);
        await editor.WaitForCellLabelAsync(cellId, born.Name!);

        output.WriteLine($"Prima del Ctrl+Z, la pila:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", await editor.UndoStackAsync()));

        // Il clic porta il fuoco della tastiera dentro draw.io.
        await editor.ClickAtAsync(drop.X, drop.Y);
        await editor.UndoAsync();

        // La prima pressione tocca l'etichetta e si ferma li'.
        await editor.WaitForCellLabelAsync(cellId, types[0].Label!);

        // IL CUORE: lo spostamento di chi disegnava e' ancora dov'era, e la sagoma nata pure.
        var afterOne = await editor.CellPositionAsync(unrelated);
        Assert.Equal(moved, afterOne);
        Assert.Contains(cellId, await editor.MarkedCellIdsAsync());
        output.WriteLine($"Un Ctrl+Z: l'etichetta di {cellId} e' tornata \"{types[0].Label}\", e lo spostamento " +
            $"di {unrelated} e' ancora a ({afterOne[0]}, {afterOne[1]}) invece che a ({wasAt[0]}, {wasAt[1]}).");

        // ANCORA 3: la seconda pressione disfa lo spostamento - e senza di lei "e' ancora li'"
        // sarebbe vero anche di uno spostamento mai entrato nella pila.
        await editor.UndoAsync();
        var afterTwo = await WaitForPositionAsync(editor, unrelated, wasAt[0], wasAt[1]);
        Assert.Equal(wasAt, afterTwo);
        Assert.Contains(cellId, await editor.MarkedCellIdsAsync());
        output.WriteLine($"Il secondo Ctrl+Z ha disfatto lo spostamento ({afterTwo[0]}, {afterTwo[1]}), e la sagoma " +
            $"nata e' ancora nel disegno. La pila:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", await editor.UndoStackAsync()));
    }

    /// <summary>
    /// Aspetta che questa cella stia in questo punto, e se non ci arriva dice <b>dove</b> sta: un
    /// "non e' la posizione attesa" senza il punto letto non distingue uno spostamento mai avvenuto
    /// da uno spostamento annullato.
    /// </summary>
    private static async Task<double[]> WaitForPositionAsync(
        DrawioEditorPage editor, string cellId, double x, double y)
    {
        var ceiling = DateTime.UtcNow.AddSeconds(30);
        var at = await editor.CellPositionAsync(cellId);
        while ((at[0] != x || at[1] != y) && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(200);
            at = await editor.CellPositionAsync(cellId);
        }

        Assert.True(at[0] == x && at[1] == y,
            $"La cella {cellId} sta a ({at[0]}, {at[1]}) invece che a ({x}, {y}).");
        return at;
    }

    /// <summary>
    /// Aspetta che questa cella porti l'etichetta del NOME che l'impianto le ha dato - cioe' che la
    /// creazione abbia finito - e la restituisce.
    /// </summary>
    /// <remarks>
    /// Il nome si chiede all'IMPIANTO, non si ricalcola: una prova che si riscrivesse la regola dei
    /// nomi suggeriti sarebbe cieca proprio al caso che sta misurando.
    /// </remarks>
    private static async Task<string> WaitForANonTypeLabelAsync(
        DrawioEditorPage editor, string cellId, Wcs.Api.Client.WcsClient client)
    {
        var ceiling = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var graph = await client.GetPlantGraphAsync();
            var names = graph.Items.Select(item => item.Name ?? "").ToHashSet(StringComparer.Ordinal);
            var label = await editor.CellLabelAsync(cellId) ?? "";
            if (names.Contains(label) && !string.IsNullOrEmpty(label)) return label;
            if (DateTime.UtcNow >= ceiling)
                throw new InvalidOperationException(
                    $"La cella {cellId} porta \"{label}\", che non e' il nome di nessun dispositivo " +
                    $"dell'impianto ({string.Join(", ", names)}): l'etichetta della creazione non e' mai arrivata.");
            await Task.Delay(200);
        }
    }

    /// <summary>
    /// Aspetta che le etichette disegnate per queste celle siano, in qualunque ordine, quelle attese,
    /// e restituisce le ultime lette: cosi' il confronto che fallisce dice <b>che cosa</b> c'era
    /// sulla tela invece di dire soltanto che non era quello giusto.
    /// </summary>
    private static async Task<string[]> WaitForLabelsAsync(
        DrawioEditorPage editor, string[] cellIds, string[] expected)
    {
        var ceiling = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var read = new List<string>();
            foreach (var id in cellIds) read.Add(await editor.CellLabelAsync(id) ?? "");
            var drawn = read.Order(StringComparer.Ordinal).ToArray();
            if (drawn.SequenceEqual(expected, StringComparer.Ordinal) || DateTime.UtcNow >= ceiling) return drawn;
            await Task.Delay(200);
        }
    }

    /// <summary>
    /// <b>Il difetto trovato dal proprietario usando il prodotto (17/9/2026)</b>: si trascina una
    /// sagoma, si da' un nome al dispositivo nato e si conferma - e <b>la tela prende l'etichetta
    /// nuova</b>. Letta dal MODELLO di draw.io, non da una riga della pagina.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Prima della correzione qui si leggeva</b>: «The new name is on the plant, but the drawing
    /// did not take it (the cell is no longer there, or the selection could not be followed)» - con
    /// la cella al suo posto sulla tela per tutto il tempo. L'impianto aveva il nome, il disegno no,
    /// e la ragione dichiarata era falsa.
    /// </para>
    /// <para>
    /// <b>La giuntura fra due lotti, e nessuno dei due poteva vederla da solo</b>: il pannello e'
    /// nato quando l'identificativo della cella e il guid del dispositivo erano la stessa cosa (per
    /// un dispositivo seminato <c>DrawioDocument</c> scrive il guid come <c>&lt;object id&gt;</c>),
    /// la creazione dal trascinamento ha rotto quell'equivalenza, e ogni prova della rinomina usava
    /// un dispositivo seminato. Per questo qui si pretende che i due <b>siano diversi</b>
    /// (<c>NotEqual</c>) prima ancora di rinominare: senza quella riga la prova tornerebbe cieca il
    /// giorno che qualcosa riportasse il guid sulla cella.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RenamingTheDeviceJustDraggedChangesItsLabelOnTheDrawing()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var dropped = await editor.DragEntryOntoTheCanvasAsync(0);
        await editor.WaitForTheCreationReportAsync();
        var afterTheDrop = await WaitForItemCountAsync(client, before.Items.Count + 1, editor);
        await editor.WaitForCreationIdleAsync();
        var born = Assert.Single(afterTheDrop.Items, item => before.Items.All(was => was.ItemId != item.ItemId));

        // La cella del nato e' quella che NON porta un guid: le seminate portano il loro. E' il
        // fatto da cui il difetto nasce, quindi si misura invece di darlo per buono.
        await editor.ClickAtAsync(dropped.X, dropped.Y);
        var cellId = Assert.Single(await editor.MarkedCellIdsAsync(), id => !Guid.TryParse(id, out _));
        Assert.NotEqual(born.ItemId.ToString(), cellId);
        output.WriteLine($"Nato {born.ItemId}, cella {cellId}, etichetta di partenza \"{await editor.CellLabelAsync(cellId)}\".");

        // Selezionata dal modello, e si CONTA quanto draw.io ha poi davvero selezionato: una sagoma
        // appena rilasciata puo' stare sotto al pannello delle forme, e un clic a vuoto lascerebbe
        // la selezione vuota - il pannello non si aprirebbe e la prova misurerebbe il nulla.
        Assert.Equal(1, await editor.SelectCellAsync(cellId));

        const string renamed = "RinominatoDopoIlTrascinamento";
        await editor.TypePropertyAsync("Name", renamed);
        await editor.ConfirmPropertiesAsync();
        await editor.WaitForPropertiesWrittenAsync(renamed);

        // Nessuna riga di fallimento: e' quella che diceva il falso.
        var declared = await editor.PropertyWriteErrorAsync();
        Assert.True(declared is null, $"La pagina ha dichiarato un fallimento del disegno: \"{declared}\".");

        // L'impianto ha il nome - quella meta' non e' mai stata il difetto - riletto dall'host.
        Assert.Equal(renamed, (await client.GetItemConfigurationAsync(born.ItemId))!.Name);

        // E il cuore della prova: l'etichetta che draw.io DISEGNA per quella cella.
        await editor.WaitForCellLabelAsync(cellId, renamed);
    }

    /// <summary>
    /// <b>Che cosa si vede sulla sagoma DOPO UN RICARICAMENTO</b>, che fino a oggi non era
    /// sorvegliato da nessuna prova: si trascina, si riapre la pagina da capo, e la cella del
    /// dispositivo nato porta il suo nome nel <b>modello di draw.io</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>E' la prova che chiude la riserva lasciata aperta dal correttore precedente</b>: «quel
    /// comportamento non e' sorvegliato da nessuna prova - se cambia, nessuno se ne accorge». Prima
    /// del nome suggerito, qui si sarebbe letta un'etichetta VUOTA: <c>DrawioDocument.Build</c>
    /// scrive <c>label="{item.Name}"</c>, e un dispositivo senza nome torna muto.
    /// </para>
    /// <para>
    /// <b>La riapertura e' vera</b>: un secondo contesto del browser, con il suo cancello della
    /// chiave e la sua seminatura - non un <c>reload</c> della stessa scheda, e soprattutto non il
    /// documento ricostruito dalla prova. E la cella si cerca <b>per il guid del dispositivo</b>,
    /// perche' alla riapertura e' seminata come tutte le altre: l'identificativo di draw.io viveva
    /// solo nella sessione che l'ha rilasciata.
    /// </para>
    /// <para>
    /// <b>L'atteso non e' ricalcolato qui</b>: si confronta l'etichetta che draw.io disegna con il
    /// nome che l'HOST dichiara per quel dispositivo, cioe' due osservazioni indipendenti della
    /// stessa cosa. Riscrivere la regola del nome in questa prova l'avrebbe resa cieca per
    /// costruzione. Ed e' ancorata: il nome dell'host deve essere non vuoto, altrimenti "vuoto
    /// uguale a vuoto" passerebbe proprio nel caso che questa prova esiste per vedere.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AfterAReloadTheShapeCarriesTheNameTheDeviceWasBornWith()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();

        Wcs.Api.Contracts.Dtos.PlantItemConfigDto born;
        var dropping = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await using (dropping)
        {
            await dropping.WaitForDeviceAsync(PlantSeed.DraggedName);
            await dropping.DragEntryOntoTheCanvasAsync(0);
            await dropping.WaitForTheCreationReportAsync();
            Assert.Empty(await dropping.NotCreatedAsync());
            var after = await WaitForItemCountAsync(client, before.Items.Count + 1, dropping);
            born = Assert.Single(after.Items, item => before.Items.All(was => was.ItemId != item.ItemId));
        }

        Assert.False(string.IsNullOrEmpty(born.Name),
            $"Il dispositivo nato ({born.ItemId}) non porta nessun nome sull'impianto: alla riapertura la sagoma sarebbe muta, ed e' proprio quello che questa prova deve vedere.");

        // La riapertura, da zero: un'altra scheda, un altro cancello della chiave, un altro giro di
        // andata del documento.
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(PlantSeed.DraggedName);
        await reopened.WaitForMarkedCellsAsync(before.Items.Count + 1);
        var marked = await reopened.MarkedCellIdsAsync();
        output.WriteLine($"Nato {born.ItemId}, nome sull'impianto \"{born.Name}\". Celle marchiate alla riapertura: {string.Join(", ", marked)}.");

        // La cella c'e', e porta il guid: e' seminata come le altre.
        Assert.Contains(born.ItemId.ToString(), marked);
        Assert.Equal(born.Name, await reopened.CellLabelAsync(born.ItemId.ToString()));
    }

    /// <summary>
    /// Due rilasci della <b>stessa voce</b>: due dispositivi diversi, non uno contato due volte e
    /// non un secondo rilascio scambiato per il primo che torna. E' la misura che tiene in piedi la
    /// mappa di sessione dal lato opposto a quello della trappola: li' la mappa deve dire "questo
    /// c'e' gia'", qui deve dire "questo e' un altro".
    /// </summary>
    /// <remarks>
    /// Il copia e incolla, che qui mancava, adesso c'e' nelle due prove qui sotto: quello che
    /// leggevi in questo posto - «da un browser guidato non si puo' incollare» - era falso, e la
    /// misura che lo sosteneva era cieca (la selezione era vuota, quindi Ctrl+C copiava il nulla).
    /// </remarks>
    [Fact]
    public async Task DraggingTheSameEntryTwiceCreatesTwoDifferentDevices()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        await editor.DragEntryOntoTheCanvasAsync(0);
        await editor.WaitForTheCreationReportAsync();
        Assert.Single(await editor.CreatedAsync());
        await editor.WaitForCreationIdleAsync();

        await editor.DragEntryOntoTheCanvasAsync(0);

        // Si aspetta l'IMPIANTO e non una riga a schermo: il verbale del rilascio viene azzerato a
        // ogni gesto, quindi "c'e' una riga Created" era gia' vero prima del secondo rilascio e non
        // distinguerebbe il secondo dispositivo dal primo.
        var after = await WaitForItemCountAsync(client, before.Items.Count + 2, editor);
        output.WriteLine($"Dispositivi prima: {before.Items.Count}, dopo i due rilasci: {after.Items.Count}.");
        Assert.Empty(await editor.NotCreatedAsync());

        var born = after.Items.Where(item => before.Items.All(was => was.ItemId != item.ItemId)).ToList();
        Assert.Equal(2, born.Count);
        Assert.NotEqual(born[0].ItemId, born[1].ItemId);
        Assert.Equal(born[0].DeviceType, born[1].DeviceType);

        // E DUE NOMI DIVERSI, contro l'host vero. E' la meta' che le prove bUnit non possono dare:
        // li' il finto host riecheggia il nome sulla rilettura perche' e' scritto cosi', qui si
        // misura che la rilettura VERA di CreateItemCommand riporti davvero il nome scritto un
        // istante prima - se non lo riportasse, il secondo rilascio si chiamerebbe come il primo.
        output.WriteLine($"Nomi dei due nati: \"{born[0].Name}\" e \"{born[1].Name}\".");
        Assert.All(born, item => Assert.False(string.IsNullOrEmpty(item.Name)));
        Assert.NotEqual(born[0].Name, born[1].Name);
    }

    /// <summary>
    /// <b>Riga 7: copia e incolla, con il mouse e la tastiera veri.</b> Si rilascia una sagoma, la
    /// si seleziona, Ctrl+C e Ctrl+V: la cella incollata porta un identificativo nuovo, e
    /// <b>l'impianto cresce di un secondo dispositivo</b>, riletto dall'host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>E' la prova che il lotto precedente aveva dichiarato impossibile</b>, e la dichiarazione
    /// era falsa: Ctrl+V non e' legato a nessuna azione di draw.io - questo e' vero - ma
    /// l'incollaggio gli arriva dall'evento <c>paste</c> del browser, che una pressione di tasti di
    /// Playwright produce eccome. Quello che mancava era la <b>selezione</b>: senza, Ctrl+C copiava
    /// il nulla e Ctrl+V lo rimetteva, e il tentativo si era concluso con "non si puo' fare".
    /// </para>
    /// <para>
    /// Per questo la selezione qui e' <b>misurata</b> (<c>SelectCellAsync</c> torna quante celle
    /// draw.io ha selezionate, e la prova pretende una) e l'incollaggio pure
    /// (<c>WaitForMarkedCellsAsync</c> aspetta la quarta cella marchiata nel MODELLO). Senza quelle
    /// due righe questa prova potrebbe di nuovo misurare il nulla e chiamarlo un esito.
    /// </para>
    /// <para>
    /// Prima della correzione del ponte di questo lotto l'esito era: cella incollata nel modello,
    /// <b>impianto fermo a 3</b>, e nessuna riga ne' <c>Created</c> ne' <c>NOT created</c> - il
    /// verbale continuava a mostrare quello del rilascio precedente.
    /// </para>
    /// <para>
    /// <b>L'INSTABILITA' CHE QUESTA PROVA HA PORTATO PER DUE GIORNI E' CHIUSA (19/9/2026).</b>
    /// Falliva cosi': <c>Il disegno porta 3 celle marchiate invece di 4</c> - le due seminate piu'
    /// quella rilasciata, l'incollata non arrivava mai - bruciando tutti i 30 secondi di tetto di
    /// <see cref="DrawioEditorPage.WaitForMarkedCellsAsync"/>. <b>Cinque volte in ventun giri</b>
    /// delle quattro prove che incollano, cioe' circa un incollaggio su diciassette.
    /// </para>
    /// <para>
    /// <b>Non era del prodotto e non si perdeva niente</b>: a ogni caduta l'impianto, letto
    /// dall'host nel momento del rosso, aveva esattamente i dispositivi che il disegno mostrava -
    /// tre e tre, cinque e cinque. Era la gestualita' della prova a essere piu' veloce della
    /// macchina degli appunti di draw.io: il Ctrl del Ctrl+V arrivava <b>due millisecondi</b> dopo
    /// quello rilasciato dal Ctrl+C, cioe' prima del riordino che draw.io programma sul keyup, e
    /// l'evento <c>paste</c> finiva sulla textarea del <c>typingShim</c> invece che sul
    /// <c>clipboardElt</c>, che e' l'unico elemento su cui draw.io ascolta. La cura sta in
    /// <see cref="DrawioEditorPage.CopyAsync"/>, che ha la misura per esteso.
    /// </para>
    /// <para>
    /// Le tre cose che l'indagine del 18/9 aveva gia' escluso reggono tutte e nessuna era la causa:
    /// <b>non e' una regressione</b> (riprodotta a <c>dcbcf8a</c>, con il corpo della prova
    /// identico), <b>non e' il primo giro dopo una ricostruzione</b> (e' caduta al primo, al
    /// quinto, al sesto, al ventiduesimo e al trentesimo giro, con l'host costruito una volta
    /// sola), <b>non e' una selezione vuota</b> (la riga
    /// <c>Assert.Equal(1, SelectCellAsync(cellId))</c> la misura prima del Ctrl+C).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CopyingADeviceAndPastingItCreatesASecondDevice()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var dropped = await editor.DragEntryOntoTheCanvasAsync(0);
        await editor.WaitForTheCreationReportAsync();
        var afterTheDrop = await WaitForItemCountAsync(client, before.Items.Count + 1, editor);
        await editor.WaitForCreationIdleAsync();
        var first = Assert.Single(afterTheDrop.Items, item => before.Items.All(was => was.ItemId != item.ItemId));

        // Il clic porta il fuoco della tastiera dentro l'iframe: senza, Ctrl+C e Ctrl+V non
        // arrivano affatto a draw.io. La selezione la si prende dal modello, e la si CONTA.
        await editor.ClickAtAsync(dropped.X, dropped.Y);
        var cellId = Assert.Single(await editor.MarkedCellIdsAsync(), id => !Guid.TryParse(id, out _));
        Assert.Equal(1, await editor.SelectCellAsync(cellId));

        await editor.ArmThePasteProbeAsync();
        await editor.CopyAsync();
        await editor.PasteAsync();

        // Prima l'incollaggio nel disegno, poi il dispositivo sull'impianto: se la prima attesa
        // scade, il guasto e' nel browser; se scade la seconda, e' nella pagina.
        await WaitForThePasteToLandAsync(editor, 4, client);
        var after = await WaitForItemCountAsync(client, before.Items.Count + 2, editor);

        var second = Assert.Single(after.Items,
            item => before.Items.All(was => was.ItemId != item.ItemId) && item.ItemId != first.ItemId);
        output.WriteLine($"Rilasciato {first.ItemId}, incollato {second.ItemId}, tipo {second.DeviceType}.");
        Assert.Equal(first.DeviceType, second.DeviceType);

        // E ADESSO IL VERBALE DELLA PAGINA, che e' un'altra cosa dall'impianto e arriva dopo.
        // Senza questa riga la prova e' andata rossa il 18/9/2026, e il modo in cui e' andata rossa
        // e' la diagnosi: "Assert.Contains() Failure: Collection: []" - il verbale VUOTO - in
        // **3 secondi**, cioe' piu' in fretta dei 4 di un giro che passa, con l'impianto che aveva
        // gia' il secondo dispositivo (la riga qui sopra lo aveva appena letto dall'host e
        // stampato). Una prova che fallisce piu' in fretta di quando riesce non e' una prova lenta:
        // e' una lettura fatta prima che ci fosse qualcosa da leggere.
        //
        // Le due attese di sopra non coprono questa: guardano il MODELLO di draw.io e l'IMPIANTO
        // via HTTP, e l'impianto puo' vincere sul rendering di Blazor. La pagina, per di piu',
        // SVUOTA il verbale all'inizio di ogni gesto (ClearCreationReport) e il div sparisce del
        // tutto finche' non c'e' di nuovo qualcosa da dire - quindi fra lo svuotamento e il
        // riempimento non c'e' nessun verbale, ed e' li' che la lettura cadeva. Aspettarlo e' cio'
        // che fanno le prove sorelle dopo un rilascio; questa, dopo l'incollaggio, non lo faceva.
        // Lo svuotamento e' gia' avvenuto per certo quando si arriva qui - precede la scrittura
        // sull'host, che l'attesa sull'impianto ha appena visto finire - quindi questa attesa non
        // puo' essere soddisfatta dal verbale del rilascio di prima.
        await editor.WaitForTheCreationReportAsync();

        Assert.Empty(await editor.NotCreatedAsync());
        // E la nascita e' DICHIARATA, con l'identificativo: un dispositivo senza nome non si ritrova
        // in nessun altro modo.
        Assert.Contains(await editor.CreatedAsync(), line => line.Contains(second.ItemId.ToString(), StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Riga 8: taglia</b> su un dispositivo lasciato cadere nella sessione. Fino al 27/9 Ctrl+X e
    /// Ctrl+V ne facevano nascere uno nuovo e il salvataggio cancellava il vecchio (l'identita'
    /// cambiava); su un dispositivo letto dall'impianto lo stesso gesto lo perdeva del tutto
    /// (<see cref="DrawioCopyCutOfAPlantDeviceTest"/>). Decisione del proprietario del 27/9: il taglio
    /// su una selezione che contiene un dispositivo non fa niente, e la pagina lo dice - anche per
    /// quello appena creato, che per la pagina e' un dispositivo come gli altri.
    /// </summary>
    [Fact]
    public async Task CuttingADroppedDeviceIsRefusedToo()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var dropped = await editor.DragEntryOntoTheCanvasAsync(0);
        await editor.WaitForTheCreationReportAsync();
        var afterTheDrop = await WaitForItemCountAsync(client, before.Items.Count + 1, editor);
        await editor.WaitForCreationIdleAsync();
        var born = Assert.Single(afterTheDrop.Items, item => before.Items.All(was => was.ItemId != item.ItemId));

        await editor.ClickAtAsync(dropped.X, dropped.Y);
        var cellId = Assert.Single(await editor.MarkedCellIdsAsync(), id => !Guid.TryParse(id, out _));
        await editor.WaitForTheDeviceMenuAsync(cellId, isDevice: true, running: false);
        Assert.Equal(1, await editor.SelectCellAsync(cellId));

        await editor.CutAsync();

        await editor.WaitForTextAsync("#drawio-cut-refused", "A plant device cannot be cut");
        Assert.True(await editor.HasCellAsync(cellId));
        await editor.SaveAsync();
        var afterTheSave = await client.GetPlantGraphAsync();
        Assert.Contains(afterTheSave.Items, item => item.ItemId == born.ItemId);
        Assert.Equal(afterTheDrop.Items.Count, afterTheSave.Items.Count);
    }

    /// <summary>
    /// <b>Ctrl+Z subito dopo il rilascio</b>, nel prodotto vero: la sagoma sparisce dalla tela e il
    /// dispositivo <b>resta sull'impianto</b> - il proprietario ha accettato questo costo sapendolo -
    /// ma il salvataggio successivo se lo porta via attraverso la strada delle cancellazioni che
    /// esiste gia', e lo dice nel verbale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// E' la risposta buona alla domanda del mandato, e questa prova e' il posto in cui viene
    /// misurata invece che dedotta: la strada passa per il delta, che deve accorgersi che la cella
    /// non c'e' piu' e proporre la cancellazione del dispositivo che la mappa di sessione teneva
    /// legato a lei. La stessa risposta vale per il TAGLIA (Ctrl+X), che e' una rimozione piu' un
    /// incollaggio: la meta' che toglie la cella e' questa.
    /// </para>
    /// <para>
    /// <b>UN gesto dell'operatore, UN Ctrl+Z, e questa prova e' il posto in cui quel numero e'
    /// misurato.</b> Il rilascio lascia due voci nella pila di draw.io - la cella, e poi l'etichetta
    /// che la pagina le mette - e per un periodo (dal lotto dell'etichetta alla nascita) ne
    /// servivano davvero due. Adesso la voce dell'etichetta nasce <b>non significativa</b> - perche'
    /// atterra direttamente sopra la nascita, che e' l'unico caso in cui il ponte lo concede: si
    /// veda <see cref="ALabelLandingOnSomebodyElsesGestureDoesNotTakeThatGestureAway"/> per
    /// l'altro - e il ciclo di <c>mxUndoManager.undo</c> l'annulla e prosegue fino alla nascita
    /// della sagoma. Misurato qui il 18/9/2026: la pila passa da <c>indexOfNextAdd=0 di 0</c> a
    /// <c>2 di 2</c> (<c>0:SIG/3[mxGeometryChange,mxGeometryChange,mxChildChange]</c> e
    /// <c>1:insig/1[mxValueChange]</c>), e una pressione la riporta a <b>0</b>. La
    /// prova e' scritta perche' non possa confondere i due casi: fra il Ctrl+Z e le asserzioni c'e'
    /// una sola pressione, e l'etichetta del nome viene <b>vista arrivare</b> prima, altrimenti una
    /// pagina che avesse smesso di etichettare resterebbe verde con un passo solo.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UndoingADropLeavesTheDeviceOnThePlantAndTheNextSaveTakesItAway()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();
        var types = await client.GetDeviceTypesAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var dropped = await editor.DragEntryOntoTheCanvasAsync(0);
        await editor.WaitForTheCreationReportAsync();
        var born = Assert.Single((await client.GetPlantGraphAsync()).Items,
            item => before.Items.All(was => was.ItemId != item.ItemId));

        // Il clic porta il fuoco della tastiera dentro draw.io, come per la duplicazione.
        await editor.ClickAtAsync(dropped.X, dropped.Y);
        var cellId = Assert.Single(await editor.MarkedCellIdsAsync(), id => !Guid.TryParse(id, out _));

        // L'ETICHETTA DEL NOME DEV'ESSERE ARRIVATA PRIMA DELL'ANNULLAMENTO, ed e' l'ancora di tutta
        // la prova: se la pagina smettesse di etichettare, nella pila resterebbe una voce sola e
        // "un Ctrl+Z basta" tornerebbe vero senza che nessuno abbia riparato niente. Qui si aspetta
        // di vedere il NOME sulla cella, e si controlla che il nome non sia l'etichetta del tipo -
        // senza quella riga, un nome suggerito che coincidesse col tipo renderebbe l'attesa
        // soddisfatta dalla sola etichetta della sagoma appena nata.
        Assert.NotEqual(types[0].Label, born.Name);
        await editor.WaitForCellLabelAsync(cellId, born.Name!);

        // UN SOLO annullamento, e se ne vanno insieme l'etichetta e la sagoma. La voce
        // dell'etichetta e' non significativa, quindi il ciclo di mxUndoManager.undo la attraversa
        // e arriva alla nascita della cella.
        await editor.UndoAsync();
        await editor.WaitForTheCellToBeGoneAsync(cellId, before.Items.Count);
        output.WriteLine($"Un solo Ctrl+Z: la cella {cellId} non c'e' piu' e il disegno e' tornato a " +
            $"{before.Items.Count} celle marchiate, mentre l'impianto tiene \"{born.Name}\".");

        // La sagoma non c'e' piu', ma il dispositivo si': e' il costo accettato, e questa riga e'
        // la misura che dice che e' proprio cosi' e non un'impressione.
        var afterTheUndo = await client.GetPlantGraphAsync();
        Assert.Contains(afterTheUndo.Items, item => item.ItemId == born.ItemId);

        await editor.SaveAsync();

        var report = await editor.ReportAsync();
        var afterTheSave = await client.GetPlantGraphAsync();
        Assert.DoesNotContain(afterTheSave.Items, item => item.ItemId == born.ItemId);
        output.WriteLine($"Nato {born.ItemId}, annullato, e il salvataggio lo ha tolto. Verbale:{Environment.NewLine}{report}");

        // E non in silenzio: il verbale lo NOMINA. Un dispositivo che esiste e che nessuno vede e'
        // la perdita peggiore di questo lotto; uno che se ne va senza che il verbale lo dica e' la
        // stessa perdita all'incontrario.
        // Per NOME, da quando il rilascio ne da' uno: il verbale chiama un dispositivo con il suo
        // nome e ripiega sull'identificativo solo quando non ne ha (`DrawioEditor.NameOf`).
        // Ancorato sul nome non vuoto: con la stringa vuota, "una riga lo contiene" sarebbe vero di
        // qualunque verbale.
        Assert.False(string.IsNullOrEmpty(born.Name),
            $"Il dispositivo nato ({born.ItemId}) non porta nessun nome: il verbale non avrebbe modo di chiamarlo.");
        var written = await editor.WrittenAsync();
        Assert.True(written.Any(line => line.Contains(born.Name!, StringComparison.Ordinal)),
            $"Il salvataggio ha cancellato {born.ItemId} (\"{born.Name}\") senza nominarlo. Il verbale diceva:{Environment.NewLine}{report}");
    }

    /// <summary>
    /// Aspetta che l'IMPIANTO arrivi al numero di dispositivi atteso, e se non ci arriva lo dice
    /// insieme al verbale che la pagina mostrava: un "atteso 4, trovato 3" senza il verbale non dice
    /// se la creazione sia stata rifiutata, non sia mai partita, o sia fallita sull'host.
    /// <para>
    /// Il tetto non e' il tempo che la prova aspetta - aspetta la condizione - e' il punto oltre il
    /// quale un'attesa che non finisce diventa un fallimento che si legge.
    /// </para>
    /// </summary>
    private static async Task<Wcs.Api.Contracts.Dtos.PlantGraphDto> WaitForItemCountAsync(
        Wcs.Api.Client.WcsClient client, int expected, DrawioEditorPage editor)
    {
        var ceiling = DateTime.UtcNow.AddSeconds(30);
        var graph = await client.GetPlantGraphAsync();
        while (graph.Items.Count != expected && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(200);
            graph = await client.GetPlantGraphAsync();
        }

        Assert.True(graph.Items.Count == expected,
            $"L'impianto ha {graph.Items.Count} dispositivi invece di {expected}. " +
            $"Il verbale del rilascio diceva:{Environment.NewLine}{await editor.CreationReportAsync()}");
        return graph;
    }

    /// <summary>
    /// Waits for the marked cells <b>after a paste</b> and, when they do not arrive, says where the
    /// missing ones were lost instead of only how many are there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The question it exists to answer is the plant's.</b> "Il disegno porta 5 celle marchiate
    /// invece di 8" (seen on 2026-09-19, and "3 invece di 4" on 2026-09-18) does not say whether
    /// the operator lost devices or only saw fewer shapes, and those two are a product defect and a
    /// drawing defect. The plant is read here, from the host, at the moment of the failure.
    /// </para>
    /// <para>
    /// <b>Each piece is read on its own and a piece that cannot be read does not take the others
    /// down with it</b>: a diagnosis that dies while collecting itself would replace the failure
    /// that matters with one that does not, and the whole point of this helper is the occurrence
    /// it catches.
    /// </para>
    /// <para>
    /// <b>The answer it gave, on 2026-09-19, on five falls out of twenty-one rounds:</b> the plant
    /// and the drawing agreed every single time - three and three, five and five. Nothing was lost
    /// silently; the paste simply did not happen. Why it did not is in
    /// <see cref="DrawioEditorPage.CopyAsync"/>.
    /// </para>
    /// </remarks>
    private static async Task WaitForThePasteToLandAsync(
        DrawioEditorPage editor, int expected, Wcs.Api.Client.WcsClient client)
    {
        var started = DateTime.UtcNow;
        try
        {
            await editor.WaitForMarkedCellsAsync(expected);
        }
        catch (Exception failure)
        {
            var waited = (DateTime.UtcNow - started).TotalSeconds;
            var plant = await SafelyAsync(async () =>
            {
                var graph = await client.GetPlantGraphAsync();
                return $"{graph.Items.Count} dispositivi - " +
                    string.Join(", ", graph.Items.Select(item => $"{item.ItemId} \"{item.Name}\""));
            });
            var created = await SafelyAsync(async () => string.Join(" | ", await editor.CreatedAsync()));
            var notCreated = await SafelyAsync(async () => string.Join(" | ", await editor.NotCreatedAsync()));
            var error = await SafelyAsync(async () => await editor.CreationErrorAsync() ?? "(nessuno)");
            var probe = await SafelyAsync(editor.ThePasteProbeAsync);
            var clipboard = await SafelyAsync(editor.TheSystemClipboardAsync);

            throw new InvalidOperationException(
                $"L'INCOLLAGGIO NON HA CONSEGNATO. {failure.Message}{Environment.NewLine}" +
                $"Attesa durata {waited:F0} s.{Environment.NewLine}" +
                $"L'IMPIANTO: {plant}{Environment.NewLine}" +
                $"IL VERBALE DELLA PAGINA: creati [{created}], non creati [{notCreated}], motivo {error}{Environment.NewLine}" +
                $"GLI APPUNTI DI SISTEMA: {clipboard}{Environment.NewLine}" +
                $"LA SONDA DELL'INCOLLAGGIO:{Environment.NewLine}{probe}",
                failure);
        }
    }

    /// <summary>What the question answered, or why it could not be asked. Never throws.</summary>
    private static async Task<string> SafelyAsync(Func<Task<string>> ask)
    {
        try { return await ask(); }
        catch (Exception refused) { return $"(non si e' lasciato leggere: {refused.Message})"; }
    }
}
