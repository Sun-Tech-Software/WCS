using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// La pagina <c>/editor/drawio</c> guidata dal di fuori, come la guiderebbe una persona: si scrive
/// la chiave, si preme il pulsante, si trascina con il mouse, si preme il salvataggio.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nessun gancio nel prodotto.</b> Tutto quello che questa classe usa - l'identificativo
/// dell'iframe, le classi del verbale del salvataggio, il pulsante che draw.io stesso disegna in
/// modalita' incorporata - esiste gia' perche' serve alla pagina, non perche' serviva alla prova.
/// </para>
/// <para>
/// <b>Perche' l'XPath.</b> mxGraph non mette l'identificativo della cella sugli elementi che
/// disegna (misurato in un browser vero: nessun <c>data-cell-id</c> vive nella tela, l'attributo
/// compare solo nelle esportazioni). L'unica cosa nostra che arriva fino al DOM e' l'ETICHETTA del
/// dispositivo, e mxGraph la disegna in un gruppo che segue immediatamente quello della sagoma:
/// dall'etichetta si risale al gruppo dell'etichetta, si prende il gruppo precedente - la sagoma -
/// e dentro c'e' l'immagine da afferrare. E' fragile rispetto a un cambiamento di draw.io, ed e'
/// proprio per questo che questa prova esiste: se draw.io cambia il modo di disegnare, qui diventa
/// rosso invece di passare inosservato.
/// </para>
/// </remarks>
public sealed class DrawioEditorPage : IAsyncDisposable
{
    private const string ClipboardMarker = "(the clipboard was not written)";

    /// <summary>
    /// Il tetto a ogni attesa su una condizione. Non e' il tempo che la prova aspetta - aspetta la
    /// condizione - e' il punto oltre il quale un'attesa che non finisce diventa un fallimento che
    /// si legge invece di una prova appesa.
    /// </summary>
    private const int ConditionCeiling = 60_000;

    /// <summary>Ogni quanto si rilegge il riquadro di una sagoma mentre si aspetta che si fermi.</summary>
    private const int SettleInterval = 100;

    /// <summary>
    /// Sotto quanti pixel due riquadri contano come lo stesso riquadro. Le coordinate del browser
    /// sono frazionarie e l'ultimo decimale balla da una lettura all'altra: chiedere l'uguaglianza
    /// esatta vorrebbe dire aspettare una cosa che non arriva.
    /// </summary>
    private const double SamePixel = 0.5;

    /// <summary>
    /// How long the pointer rests on a shape before and after a hand-drawn arrow's drag. draw.io
    /// only shows a shape's connection points while the pointer is over it, and it only decides
    /// whether an arrow hangs from a cell while the pointer rests on it: pressing or letting go the
    /// same instant the pointer arrives measures a gesture no operator ever makes.
    /// </summary>
    private const int HoverPause = 250;

    /// <summary>
    /// How far ahead of the page's own clock <see cref="StopTheClockAsync"/> asks Playwright to stop
    /// it: wider than the trip of the command to the page, which under the whole suite was measured
    /// in the tens of milliseconds - asking for a moment already past is refused by Playwright.
    /// </summary>
    private const int ClockStopMargin = 1_000;

    /// <summary>
    /// How long <see cref="DragAnArrowByHandAsync"/> waits for an arrow to turn up in the model
    /// before answering "none". Short on purpose: the answer it serves may well be "none", and
    /// waiting long for a negative would only make a measurement slow, not truer.
    /// </summary>
    private const int GestureCeiling = 3_000;

    /// <summary>
    /// How far short of the target shape <see cref="DrawioArrowLanding.BesideTheShape"/> lets go,
    /// in pixels to its left. Wide of mxGraph's connection tolerance, which is a handful of pixels,
    /// and still well inside the gap the two devices of the by-hand tests leave between them: the
    /// drop has to land on empty canvas, not on the other shape.
    /// </summary>
    private const double BesideGap = 80;

    /// <summary>How far inside the canvas the empty spot of <see cref="ClickEmptyCanvasAsync"/> is looked for.</summary>
    private const float EmptySpotInset = 60;

    /// <summary>
    /// How far from a shape a point still counts as taken: a click near the edge of a device is a
    /// click on the device, because mxGraph reaches a little beyond the drawing.
    /// </summary>
    private const float EmptySpotClearance = 40;

    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly List<string> _consoleErrors = [];
    private readonly List<string> _badResponses = [];

    private DrawioEditorPage(IBrowserContext context, IPage page)
    {
        _context = context;
        _page = page;
        _page.Console += (_, message) =>
        {
            if (message.Type == "error") _consoleErrors.Add(message.Text);
        };
        // Le richieste andate male si raccolgono mentre passano: quando la pagina non parte, il
        // fatto che serve e' quasi sempre un file che l'host non ha servito, e cercarlo dopo non si
        // puo' piu'. Solo quelle: un elenco di tutte le richieste seppellirebbe l'unica che conta.
        _page.Response += (_, response) =>
        {
            var contentType = response.Headers.TryGetValue("content-type", out var value) ? value : string.Empty;
            if (response.Status >= 400 || string.IsNullOrEmpty(contentType))
                _badResponses.Add($"{response.Status} {response.Url} (content-type: \"{contentType}\")");
        };
        // Queue item 13: every message of the Blazor circuit, both ways, with its size - what the
        // circuit's own ceiling on a message from the browser is measured against.
        _page.WebSocket += (_, socket) =>
        {
            if (!socket.Url.Contains("/_blazor", StringComparison.Ordinal)) return;
            socket.FrameSent += (_, frame) => _circuitFrames.Enqueue(CircuitFrame.Of(fromTheBrowser: true, frame));
            socket.FrameReceived += (_, frame) =>
            {
                _circuitFrames.Enqueue(CircuitFrame.Of(fromTheBrowser: false, frame));
                if (CallsSetHover(frame)) _pageSetItsOwnHover.TrySetResult();
            };
        };
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<CircuitFrame> _circuitFrames = new();

    /// <summary>
    /// Queue item 9: completed by the first message of the circuit that calls the live module's
    /// <c>setHover</c> - the page switching the hover on, which it does once after every opening
    /// (since the missions plan of 30/9 whatever the engine's state; until then it put the hover in
    /// line with the engine). Read by <see cref="ThePageHasSetItsOwnHoverAsync"/>.
    /// </summary>
    private readonly TaskCompletionSource _pageSetItsOwnHover = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Whether a message from the server calls <c>setHover</c>. Blazor's protocol is MessagePack, and
    /// MessagePack writes a string as its own bytes: the identifier of the call is there as text.
    /// </summary>
    private static bool CallsSetHover(IWebSocketFrame frame) =>
        (frame.Binary is { } bytes ? System.Text.Encoding.Latin1.GetString(bytes) : frame.Text ?? "")
            .Contains("setHover", StringComparison.Ordinal);

    /// <summary>
    /// Queue item 13: every message of the page's Blazor circuit so far, in the order the browser saw
    /// them, each with its direction, its size on the wire and the readable words at its head (the
    /// hub method and, for an interop call, the name of what is called).
    /// </summary>
    public IReadOnlyList<CircuitFrame> CircuitFrames() => _circuitFrames.ToArray();

    /// <summary>
    /// Queue item 13: how long, as JSON, the bridge's answer to <c>markedCells()</c> is on the drawing
    /// now - an answer that grows with the plant, asked by the page in a duplicate and in a
    /// reassociation.
    /// </summary>
    public Task<int> MarkedCellsAnswerLengthAsync() => _page.EvaluateAsync<int>(
        "async () => JSON.stringify((await import('/_content/Wcs.WebEditor/drawio-bridge.js')).markedCells()).length");

    /// <summary>
    /// Apre la pagina, supera il cancello della chiave e torna quando l'iframe di draw.io e' nel
    /// documento. Non torna quando il disegno e' arrivato: quello lo aspetta
    /// <see cref="WaitForDeviceAsync"/>.
    /// </summary>
    public static Task<DrawioEditorPage> OpenAsync(IBrowser browser, WcsHostProcess host) =>
        OpenAsync(browser, host, initScript: null);

    /// <summary>
    /// Row 33, fix round 1: the same opening as <see cref="OpenAsync"/>, but with
    /// <paramref name="initScript"/> run through Playwright's <c>AddInitScriptAsync</c> - BEFORE any
    /// script of the new context's own pages, iframes included. Used to seed draw.io's OWN shared
    /// memory (<c>localStorage['.drawio-config']</c>, read by <c>mxSettings</c>) ahead of draw.io's
    /// own startup: EditorUi's constructor reads <c>mxSettings.isRulerOn()</c> and builds its ruler
    /// right there (MEASURED in app.min.js), well before this bridge's 'init'/OnDrawioReady - a value
    /// set from this page's own JS AFTER the iframe has already loaded would arrive too late to
    /// matter, which is exactly why an ordinary <c>page.EvaluateAsync</c> after <see cref="OpenAsync"/>
    /// could not measure this test's own premise.
    /// </summary>
    public static Task<DrawioEditorPage> OpenWithInitScriptAsync(IBrowser browser, WcsHostProcess host, string initScript) =>
        OpenAsync(browser, host, initScript);

    /// <summary>
    /// Queue item 9: the same opening as <see cref="OpenAsync"/>, with Playwright's fake clock
    /// installed on the context BEFORE the page is navigated - every timer of the page and of
    /// draw.io's iframe runs on it from the first script on. Until <see cref="StopTheClockAsync"/> it
    /// flows with real time, so the page loads and behaves exactly as with the browser's own clock;
    /// installed after the load instead, <c>performance.now()</c> would restart from zero under a
    /// page that has already read it.
    /// </summary>
    public static Task<DrawioEditorPage> OpenWithAClockOfItsOwnAsync(IBrowser browser, WcsHostProcess host) =>
        OpenAsync(browser, host, initScript: null, ownClock: true);

    /// <summary>
    /// Queue item 8: the same opening as <see cref="OpenAsync"/>, in a window of the given size - for
    /// whoever has to measure what the page does to draw.io on a smaller (or larger) screen than the
    /// wide window every other test uses.
    /// </summary>
    public static Task<DrawioEditorPage> OpenInAWindowOfAsync(IBrowser browser, WcsHostProcess host, int width, int height) =>
        OpenAsync(browser, host, initScript: null, new ViewportSize { Width = width, Height = height });

    private static async Task<DrawioEditorPage> OpenAsync(IBrowser browser, WcsHostProcess host, string? initScript,
        ViewportSize? window = null, bool ownClock = false)
    {
        // Una finestra larga: draw.io adatta il disegno allo spazio che ha, e su una finestra
        // piccola l'impianto finisce a un ingrandimento cosi' basso che un trascinamento di pochi
        // pixel sposterebbe il dispositivo di migliaia di unita'.
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = window ?? new ViewportSize { Width = 1600, Height = 1000 },
        });
        if (initScript is not null) await context.AddInitScriptAsync(initScript);
        if (ownClock) await context.Clock.InstallAsync();
        var page = await context.NewPageAsync();
        var editor = new DrawioEditorPage(context, page);

        await page.GotoAsync(host.PageUrl("editor/drawio"));
        await editor.PassTheKeyGateAsync(host.ApiKey);
        return editor;
    }

    /// <summary>
    /// Passa il cancello della chiave e torna quando l'iframe di draw.io e' nel documento. Estratto
    /// da <see cref="OpenAsync"/> perche' non lo si attraversa solo all'apertura: un'importazione
    /// confermata ricarica la pagina PER INTERO (ImportExport, NavigationManager.NavigateTo con
    /// forceLoad), il circuito Blazor vecchio muore con il suo EditorSessionState - registrato
    /// AddScoped, cioe' uno per circuito - e la chiave va ridata come alla prima apertura.
    /// </summary>
    private async Task PassTheKeyGateAsync(string apiKey)
    {

        // PRIMA di scrivere si aspetta che il circuito Blazor abbia preso possesso del campo.
        // Misurato in un browser vero: la pagina arriva gia' resa dal server, e per qualche
        // centinaio di millisecondi il campo e' una casella morta - accetta i tasti, ma nessuno li
        // ascolta, e la prima resa interattiva li cancella. Scrivere li' dentro lascia il pulsante
        // "Connect" disabilitato per sempre, che e' esattamente come questa prova falliva prima.
        // La condizione e' il marchio che Blazor stesso mette sull'elemento quando gli aggancia i
        // propri gestori (_blazorEvents...): e' un dettaglio interno del framework, ma e' il fatto
        // che serve - se un domani cambiasse nome, questa attesa scade e il messaggio qui sotto
        // dice dove guardare, invece di lasciare un clic scaduto su un pulsante disabilitato.
        await WaitForConditionAsync(
            "() => { const field = document.querySelector('input.key-field');" +
            " return !!field && Object.keys(field).some(k => k.startsWith('_blazorEvents')); }",
            "il campo della chiave non e' mai diventato interattivo: il circuito Blazor non si e' collegato");

        await StepAsync(
            () => _page.FillAsync("input.key-field", apiKey, new PageFillOptions { Timeout = ConditionCeiling }),
            "la chiave non si e' potuta scrivere nel campo");

        // Il pulsante si accende solo quando il valore e' arrivato al server: aspettarlo qui vuol
        // dire "la chiave scritta e' stata vista", invece di scoprirlo come un clic scaduto.
        await WaitForConditionAsync(
            "() => { const button = document.querySelector('button.key-button'); return !!button && !button.disabled; }",
            "il pulsante \"Connect\" e' rimasto disabilitato: la chiave scritta nel campo non e' arrivata al server");

        await StepAsync(
            () => _page.ClickAsync("button.key-button", new PageClickOptions { Timeout = ConditionCeiling }),
            "il pulsante \"Connect\" non si e' potuto premere");

        // La chiave accettata fa sparire il cancello e comparire l'iframe. Se invece l'host l'ha
        // rifiutata, o non risponde, la pagina lo scrive - e il messaggio di questa attesa porta
        // quello che la pagina diceva, che e' l'unica cosa utile da leggere.
        await WaitForConditionAsync(
            "() => !!document.getElementById('drawio-frame')",
            "il disegno non si e' aperto dopo la chiave: la pagina e' rimasta al cancello");
    }

    /// <summary>
    /// Aspetta che il dispositivo con questa etichetta sia <b>disegnato sulla tela</b>. E' la sola
    /// prova che il giro di andata sia finito davvero: draw.io ha detto <c>init</c>, la pagina gli
    /// ha mandato il documento e draw.io lo ha reso. Aspettare l'evento <c>init</c> da solo direbbe
    /// meno - la tela potrebbe essere ancora vuota.
    /// </summary>
    public async Task<ILocator> WaitForDeviceAsync(string deviceName)
    {
        await FailIfBridgeUnavailableAsync();
        var shape = Shape(deviceName);
        // E' l'attesa piu' esposta di tutta la prova: dentro ci stanno il caricamento di draw.io
        // intero, il suo 'init' e il documento che la pagina gli manda. Su una macchina lenta e'
        // qui che si rompera', quindi e' qui che il messaggio non puo' essere un tempo scaduto e
        // basta.
        await StepAsync(
            () => shape.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ConditionCeiling }),
            $"il dispositivo \"{deviceName}\" non e' mai comparso sulla tela: draw.io non si e' caricato, non ha " +
            "chiesto il documento, oppure il documento non lo portava");
        return shape;
    }

    /// <summary>
    /// Trascina un dispositivo con il mouse vero: premere sulla sagoma, muovere, rilasciare. Non
    /// imposta nessun valore e non chiama nessuna funzione di draw.io - se mxGraph smettesse di
    /// muovere le celle al trascinamento, questo non lo mimerebbe.
    /// </summary>
    /// <returns>Il riquadro della sagoma sullo schermo prima e dopo il gesto.</returns>
    public async Task<(LocatorBoundingBoxResult Before, LocatorBoundingBoxResult After)> DragDeviceAsync(string deviceName, double dx, double dy)
    {
        var shape = await WaitForDeviceAsync(deviceName);
        var before = await BoxOf(shape, deviceName);

        var startX = before.X + before.Width / 2;
        var startY = before.Y + before.Height / 2;
        var endX = startX + dx;
        var endY = startY + dy;

        var viewport = _page.ViewportSize!;
        if (endX < 0 || endY < 0 || endX > viewport.Width || endY > viewport.Height)
            throw new InvalidOperationException(
                $"Il trascinamento di \"{deviceName}\" finirebbe fuori dalla finestra ({endX:0}, {endY:0} in " +
                $"{viewport.Width}x{viewport.Height}): la prova sposterebbe il mouse dove il browser non manda eventi. " +
                "Va scelto un altro scostamento, o una finestra piu' grande.");

        await _page.Mouse.MoveAsync((float)startX, (float)startY);
        await _page.Mouse.DownAsync();
        // A passi: mxGraph fa partire lo spostamento solo dopo una soglia di movimento, e un salto
        // secco da un punto all'altro non gliela fa superare in modo affidabile.
        await _page.Mouse.MoveAsync((float)endX, (float)endY, new MouseMoveOptions { Steps = 12 });
        await _page.Mouse.UpAsync();

        var after = await SettledBoxAsync(deviceName);
        return (before, after);
    }

    /// <summary>
    /// The on-screen box of a device's shape, once it has stopped moving. Public because a
    /// measurement has to be able to report where the shapes actually were, not only whether an
    /// assertion held.
    /// </summary>
    public Task<LocatorBoundingBoxResult> DeviceBoxAsync(string deviceName) => SettledBoxAsync(deviceName);

    /// <summary>
    /// Starts recording the XML draw.io hands back, next to the bridge's own listener and without
    /// touching it: the same postMessage reaches both.
    /// </summary>
    /// <remarks>
    /// The drawing that comes back is the fact everything else is read from - what the reader sees
    /// is exactly this text. Asking draw.io for it separately (an extra <c>export</c> action) would
    /// not do: the bridge listens to <c>export</c> too, so the question itself would fire a save.
    /// </remarks>
    public Task RecordReturnedDrawingAsync() => _page.EvaluateAsync(
        """
        () => {
          window.__wcsReturnedDrawings = [];
          window.addEventListener('message', e => {
            if (typeof e.data !== 'string' || !e.data.length) return;
            let m;
            try { m = JSON.parse(e.data); } catch { return; }
            if (m.event === 'save' || m.event === 'export') window.__wcsReturnedDrawings.push(m.xml || '');
          });
        }
        """);

    /// <summary>
    /// The last drawing draw.io handed back, or throws when none was recorded - which means either
    /// that <see cref="RecordReturnedDrawingAsync"/> was never called, or that the save never got a
    /// drawing at all.
    /// </summary>
    public async Task<string> LastReturnedDrawingAsync()
    {
        var drawings = await _page.EvaluateAsync<string[]>("() => window.__wcsReturnedDrawings || []");
        if (drawings.Length == 0)
            throw new InvalidOperationException(
                "draw.io did not hand back any drawing: either the recording was never started, or the save " +
                "produced nothing to read.");
        return drawings[^1];
    }

    /// <summary>
    /// Selects the named devices with the mouse, the way a person does it: click the first, then
    /// shift-click the rest.
    /// </summary>
    public async Task SelectDevicesAsync(params string[] deviceNames)
    {
        if (deviceNames.Length == 0) throw new ArgumentException("No device to select.", nameof(deviceNames));

        for (var i = 0; i < deviceNames.Length; i++)
        {
            var shape = await WaitForDeviceAsync(deviceNames[i]);
            await shape.ClickAsync(new LocatorClickOptions
            {
                Modifiers = i == 0 ? [] : [KeyboardModifier.Shift],
                Timeout = ConditionCeiling,
            });
        }

        // NO wait here for the drawing to "settle", and the absence is a decision. Batch B's
        // properties panel does not touch the iframe's width any more (see .drawio-property-dock in
        // wcs-webeditor.css), so after a click the drawing stands still. A pause put here would keep
        // the dragging tests green if the panel ever went back to moving the drawing - which is
        // exactly the defect those tests have just caught. They stay sensitive on purpose.
    }

    /// <summary>Annulla l'ultimo gesto, con la scorciatoia che draw.io stesso vi lega (<c>bindAction(90,!0,"undo")</c>).</summary>
    public Task UndoAsync() => _page.Keyboard.PressAsync("Control+z");

    /// <summary>
    /// Groups whatever is selected, with the keystroke draw.io itself binds to it (measured in the
    /// shipped app.min.js: <c>bindAction(71, true, "group")</c>).
    /// </summary>
    public Task GroupSelectionAsync() => _page.Keyboard.PressAsync("Control+g");

    /// <summary>
    /// Ungroups whatever is selected (<c>bindAction(85, true, "ungroup", true)</c> - Ctrl+Shift+U).
    /// </summary>
    public Task UngroupSelectionAsync() => _page.Keyboard.PressAsync("Control+Shift+U");

    /// <summary>
    /// Row 37: the REAL gesture, on a real keyboard event through draw.io's own key handler - not a
    /// direct call of anything (<c>bindAction(76, true, "layers", true)</c> - Ctrl+Shift+L, measured
    /// in the shipped <c>app.min.js</c>).
    /// </summary>
    public Task PressLayersShortcutAsync() => _page.Keyboard.PressAsync("Control+Shift+L");

    /// <summary>
    /// Row 37: whether draw.io's own Layers window exists and is showing right now
    /// (<c>ui.actions.layersWindow</c>, created - and only created - inside the "layers" action's own
    /// function). Null-safe on purpose: a window that was never created is exactly the "closed" this
    /// batch is measuring, not a failure to read.
    /// </summary>
    public Task<bool> LayersWindowIsOpenAsync() => InTheGraphAsync<bool>(
        "return ui.actions.layersWindow != null && ui.actions.layersWindow.window.isVisible();");

    /// <summary>
    /// Row 37: the "layers" action itself, read the way draw.io's OWN <c>showLayersDialog</c> would
    /// see it - not <see cref="ActionInventoryAsync"/>'s <c>action.enabled</c>, which is a plain
    /// field (<c>Action.prototype.isEnabled=function(){return this.enabled}</c>, measured in
    /// <c>app.min.js</c>) that <c>closeTheLayersWindow</c> leaves untouched on purpose - it overrides
    /// the <c>isEnabled</c> METHOD instead, the same choice <c>keepOnePageOnly</c> already made for
    /// <c>insertPage</c>/<c>duplicatePage</c>, so the field alone would report "enabled" for an
    /// action nothing can actually reach.
    /// </summary>
    /// <remarks>
    /// Both halves matter, and neither alone would prove the fix: <c>isEnabled()</c> called closes
    /// the keyboard shortcut and an ordinary menu click, but <c>showLayersDialog</c>'s own call of
    /// <c>actions.get("layers").funct()</c> does not ask <c>isEnabled()</c> first - it is what this
    /// reads by calling <c>funct()</c> directly afterwards, the one road <c>isEnabled</c> alone
    /// cannot shut.
    /// </remarks>
    public Task<string> LayersActionStateAsync() => InTheGraphAsync<string>(
        """
        const action = ui.actions.get('layers');
        if (!action) return 'THE ACTION DOES NOT EXIST';
        const windowBefore = ui.actions.layersWindow != null;
        const isEnabled = action.isEnabled();
        let threw = '(none)';
        try { action.funct(); } catch (e) { threw = String(e); }
        const windowAfter = ui.actions.layersWindow != null;
        return 'isEnabled=' + isEnabled + ' threw=' + threw
          + ' windowBefore=' + windowBefore + ' windowAfter=' + windowAfter;
        """);

    /// <summary>
    /// Row 37 (fix round 1 - the Tags window, closed the same day as Layers): the REAL gesture,
    /// Ctrl+K, through draw.io's own key handler (<c>this.keyHandler.bindAction(75, true, "tags")</c>,
    /// measured in <c>app.min.js</c>).
    /// </summary>
    public Task PressTagsShortcutAsync() => _page.Keyboard.PressAsync("Control+k");

    /// <summary>
    /// The Tags window, MEASURED to live on <c>ui.menus.tagsWindow</c> and not on
    /// <c>ui.actions.tagsWindow</c> (unlike the Layers window): the "tags" action's own callback is
    /// bound to the Menus instance, not to Actions - confirmed against
    /// <c>EditorUi.prototype.getWindows</c>, whose own list reads
    /// <c>this.actions.layersWindow</c> and <c>this.menus.tagsWindow</c> from two different objects.
    /// </summary>
    public Task<bool> TagsWindowIsOpenAsync() => InTheGraphAsync<bool>(
        "return ui.menus.tagsWindow != null && ui.menus.tagsWindow.window.isVisible();");

    /// <summary>The "tags" action itself, read the same way as <see cref="LayersActionStateAsync"/> and for the same reasons.</summary>
    public Task<string> TagsActionStateAsync() => InTheGraphAsync<string>(
        """
        const action = ui.actions.get('tags');
        if (!action) return 'THE ACTION DOES NOT EXIST';
        const windowBefore = ui.menus.tagsWindow != null;
        const isEnabled = action.isEnabled();
        let threw = '(none)';
        try { action.funct(); } catch (e) { threw = String(e); }
        const windowAfter = ui.menus.tagsWindow != null;
        return 'isEnabled=' + isEnabled + ' threw=' + threw
          + ' windowBefore=' + windowBefore + ' windowAfter=' + windowAfter;
        """);

    /// <summary>
    /// What draw.io says about each named action, right now: whether it exists at all, the keyboard
    /// shortcut it <b>declares</b>, and whether the current selection has it enabled. One line per
    /// name, in the order asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shortcut is draw.io's own claim, not a deduction.</b> Every action of draw.io is
    /// registered with <c>addAction(name, funct, enabled, iconCls, shortcut)</c>, and the last
    /// argument is the string draw.io itself prints next to the entry in its menus - that is, what
    /// it tells the operator the keystroke is. An action registered without it declares none.
    /// </para>
    /// <para>
    /// <b>What it cannot say.</b> The key bindings live in a place this cannot reach: draw.io binds
    /// them through <c>bindAction(keyCode, control, name)</c>, which stores a <b>closure</b> per key
    /// code inside the key handler, with no way back to the action's name. So a missing shortcut
    /// here is draw.io's own declaration and has to be read together with the bindings listed in the
    /// shipped bundle, never on its own - and a measurement that concludes "no keystroke" owes a
    /// control that the keystrokes it does know about still arrive.
    /// </para>
    /// </remarks>
    public Task<string[]> ActionInventoryAsync(params string[] names) => InTheGraphAsync<string[]>(
        """
        const out = [];
        for (const name of args) {
          const action = ui.actions.get(name);
          if (!action) { out.push(name + ': THE ACTION DOES NOT EXIST'); continue; }
          out.push(name +
            ': shortcut=' + (action.shortcut ? String(action.shortcut) : '(none declared)') +
            ', enabled=' + (action.enabled ? 'yes' : 'no'));
        }
        return out;
        """, names);

    /// <summary>
    /// Calls an action of draw.io by name, the way a plugin would - <c>ui.actions.get(name).funct()</c> -
    /// and says what happened. It is <b>not</b> a way for the operator to reach anything: it is the
    /// separate question of whether the function behind a menu entry works at all.
    /// </summary>
    public Task<string> InvokeActionAsync(string name) => InTheGraphAsync<string>(
        """
        const action = ui.actions.get(args);
        if (!action) return 'THE ACTION DOES NOT EXIST';
        if (!action.enabled) return 'the action is disabled for this selection, and was not called';
        try { action.funct(); } catch (e) { return 'the call threw: ' + String(e); }
        return 'called';
        """, name);

    /// <summary>
    /// Draws a live frame the way the page does, through the page's own module instance: the same
    /// URL imports the same module, so this is the object the page talks to.
    /// </summary>
    public Task<string> RenderLiveFrameAsync(string frameJson) => _page.EvaluateAsync<string>(
        "async (json) => (await import('/_content/Wcs.WebEditor/drawio-live.js')).render(json)", frameJson);

    /// <summary>
    /// Owner's decision of 27/9 on the cut: what draw.io offers for "Cut" outside the right-click menu
    /// with this page's <c>ui=min</c> - whether it built a menubar at all, and the visible elements of
    /// the iframe whose text is "Edit" or "Cut". A measurement, for the report: nothing is pressed.
    /// </summary>
    public Task<string> WhereCutIsOfferedAsync() => InTheGraphAsync<string>(
        """
        const doc = w.document;
        const visible = (e) => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
        const named = (text) => Array.from(doc.querySelectorAll('a, div, span, td, button'))
          .filter((e) => e.childElementCount === 0 && visible(e) && (e.textContent || '').trim() === text).length;
        return 'menubar=' + (ui.menubar ? 'yes' : 'no') + ' menubarContainer=' + (ui.menubarContainer ? 'yes' : 'no')
          + ' visibleEdit=' + named('Edit') + ' visibleCut=' + named('Cut')
          + ' cutAction=' + (ui.actions.get('cut') ? 'yes' : 'no');
        """);

    /// <summary>
    /// Starts writing down every text the page element <paramref name="selector"/> shows from now on,
    /// with the milliseconds since this call - a diagnostic for a line whose intermediate states say
    /// where time went (the Test Route outcome: queued, being executed, final). Read back with
    /// <see cref="WatchedTextsAsync"/>. Nothing of the page is changed.
    /// </summary>
    public Task WatchTextsAsync(string selector) => _page.EvaluateAsync(
        """
        (selector) => {
          const start = performance.now();
          const seen = [];
          (window.__wcsWatched = window.__wcsWatched || {})[selector] = seen;
          const note = () => {
            const e = document.querySelector(selector);
            const text = e ? e.textContent.trim() : '(none)';
            if (!seen.length || seen[seen.length - 1].text !== text) seen.push({ ms: Math.round(performance.now() - start), text });
          };
          note();
          new MutationObserver(note).observe(document.body, { subtree: true, childList: true, characterData: true });
        }
        """, selector);

    /// <summary>What <see cref="WatchTextsAsync"/> wrote down, one "+ms: text" per change.</summary>
    public Task<string> WatchedTextsAsync(string selector) => _page.EvaluateAsync<string>(
        "(selector) => ((window.__wcsWatched || {})[selector] || []).map(s => '+' + s.ms + ' ms: ' + s.text).join(' | ')", selector);

    /// <summary>
    /// The model's CELLS as draw.io's codec encodes them, and the length of its undo history - unlike
    /// <see cref="ModelAndUndoAsync"/> without the view's own attributes (dx, dy: the canvas size and
    /// scroll), which change when anything on the page above the drawing appears or goes away.
    /// </summary>
    public Task<string[]> ModelCellsAndUndoAsync() => InTheGraphAsync<string[]>(
        """
        const xml = w.mxUtils.getXml(new w.mxCodec().encode(ui.editor.graph.getModel()));
        return [xml, String(ui.editor.undoManager.history.length)];
        """);

    /// <summary>
    /// Tells the bridge that NO cell is a device, as it believes before the page's first word after an
    /// attach (setDeviceMenu with an empty set) - the window in which only the cell's own mark can say
    /// what is a device. The page says it again only when what it would say changes.
    /// </summary>
    public Task ForgetWhichCellsAreDevicesAsync() => _page.EvaluateAsync(
        "async () => (await import('/_content/Wcs.WebEditor/drawio-bridge.js')).setDeviceMenu(false, [], [])");

    /// <summary>Queue item 15: how many cells draw.io's model holds, the root and the layer included.</summary>
    public Task<int> CellCountAsync() => InTheGraphAsync<int>(
        "return Object.keys(ui.editor.graph.getModel().cells).length;");

    /// <summary>
    /// Queue item 15: waits until draw.io's model holds at least <paramref name="atLeast"/> cells - a
    /// paste of thousands is applied in one edit, but not in the instant the keystroke returns.
    /// </summary>
    public async Task WaitForTheCellCountAsync(int atLeast)
    {
        await InTheGraphAsync<bool>("w.__wcsModelForCount = ui.editor.graph.getModel(); return true;");
        await WaitForConditionAsync(
            "() => { const w = document.getElementById('drawio-frame').contentWindow;" +
            $" return Object.keys(w.__wcsModelForCount.cells).length >= {atLeast.ToString(CultureInfo.InvariantCulture)}; }}",
            $"draw.io's model never held {atLeast.ToString(CultureInfo.InvariantCulture)} cells: the paste did not land whole");
    }

    /// <summary>Whether draw.io's model holds a cell with this identifier.</summary>
    public Task<bool> HasCellAsync(string cellId) => InTheGraphAsync<bool>(
        "return !!ui.editor.graph.getModel().getCell(args);", cellId);

    /// <summary>The model as draw.io would encode it, and the length of its undo history.</summary>
    public Task<string[]> ModelAndUndoAsync() => InTheGraphAsync<string[]>(
        """
        const xml = w.mxUtils.getXml(ui.editor.getGraphXml());
        return [xml, String(ui.editor.undoManager.history.length)];
        """);

    /// <summary>
    /// What of the live status is on the canvas right now: [0] the live marks drawn (load unit boxes
    /// and status dots, the module's shapes tagged <c>data-wcs-live</c>, hidden ones not counted),
    /// [1] the paths of the arrows in the overlay pane (every path that is not part of a mark), [2]
    /// the cell overlays on every cell of the drawing - since queue item 21 the module puts none, so
    /// only draw.io's own are ever there.
    /// </summary>
    public Task<int[]> LiveObjectsAsync() => InTheGraphAsync<int[]>(
        """
        const graph = ui.editor.graph;
        let overlays = 0;
        for (const key of Object.keys(graph.getModel().cells)) {
          const cell = graph.getModel().cells[key];
          overlays += (graph.getCellOverlays(cell) || []).length;
        }
        const pane = graph.getView().getOverlayPane();
        const marks = [...pane.querySelectorAll('[data-wcs-live]')].filter((g) => g.style.visibility !== 'hidden').length;
        const paths = [...pane.querySelectorAll('path')].filter((p) => !p.closest('[data-wcs-live]')).length;
        return [marks, paths, overlays];
        """);

    /// <summary>Whether every arrow path drawn in the overlay pane is hidden - vacuously true when there is none.</summary>
    public Task<bool> MovementArrowsHiddenAsync() => InTheGraphAsync<bool>(
        """
        const pane = ui.editor.graph.getView().getOverlayPane();
        for (const p of pane.querySelectorAll('path')) {
          if (p.closest('[data-wcs-live]')) continue;
          if (w.getComputedStyle(p).visibility !== 'hidden') return false;
        }
        return true;
        """);

    /// <summary>The identifiers of whatever is selected right now, read straight off the model.</summary>
    public Task<string[]> SelectedCellIdsAsync() => InTheGraphAsync<string[]>(
        "return ui.editor.graph.getSelectionCells().map((c) => String(c.id));");

    /// <summary>
    /// Row 35: scrolls whatever is currently selected in draw.io back to the middle of the visible
    /// canvas - the same call the bridge's own <c>selectCell</c> makes (<c>scrollCellToVisible</c>).
    /// draw.io's own zoom actions do not promise to keep any particular cell in view (they zoom
    /// around the canvas's own current centre, not around a chosen device), so a device the test just
    /// zoomed past the edge of the browser window cannot be reached by a real mouse move without
    /// this. False when nothing is selected - the caller's own gesture to select first is what this
    /// measures, not a fallback of its own.
    /// </summary>
    public Task<bool> ScrollSelectionIntoViewAsync() => InTheGraphAsync<bool>(
        """
        const cell = ui.editor.graph.getSelectionCell();
        if (!cell) return false;
        ui.editor.graph.scrollCellToVisible(cell, true);
        return true;
        """);

    /// <summary>
    /// Replaces a cell by a NEW object carrying the same id, in one model edit (removed, then a clone
    /// without overlays added under the same parent), the new one moved by (<paramref name="dx"/>,
    /// <paramref name="dy"/>) drawing units - so that whatever is still placed on the OLD object's
    /// box shows. True if the drawing now holds the new object under that id.
    /// </summary>
    public Task<bool> ReplaceCellKeepingItsIdAsync(string cellId, double dx = 0, double dy = 0) => InTheGraphAsync<bool>(
        """
        const model = ui.editor.graph.getModel();
        const old = model.getCell(args.id);
        if (!old) throw new Error('the drawing has no cell with id ' + args.id);
        const parent = old.getParent();
        const copy = old.clone();
        copy.overlays = null;
        copy.setId(args.id);
        const geometry = copy.getGeometry().clone();
        geometry.x += args.dx;
        geometry.y += args.dy;
        copy.setGeometry(geometry);
        model.beginUpdate();
        try {
          model.remove(old);
          model.add(parent, copy);
        } finally {
          model.endUpdate();
        }
        return model.getCell(args.id) === copy && copy !== old;
        """, new { id = cellId, dx, dy });

    /// <summary>
    /// Turns a cell to <paramref name="degrees"/> through its style (<c>rotation=</c>), as draw.io's
    /// own Arrange panel does - one undoable model edit. Real plant devices carry <c>rotation=-180</c>.
    /// </summary>
    public Task SetCellRotationAsync(string cellId, double degrees) => InTheGraphAsync<bool>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args.id);
        if (!cell) throw new Error('the drawing has no cell with id ' + args.id);
        graph.setCellStyles(w.mxConstants.STYLE_ROTATION, args.degrees, [cell]);
        return true;
        """, new { id = cellId, degrees });

    /// <summary>
    /// Sets the view's scale directly, as draw.io's own zoom buttons end up doing (<c>zoomTo</c>). The
    /// view revalidates inside the call, overlays included, so what is read right after is already
    /// the new scale's.
    /// </summary>
    public Task<double> ZoomToAsync(double scale) => InTheGraphAsync<double>(
        """
        ui.editor.graph.zoomTo(args);
        return ui.editor.graph.getView().scale;
        """, scale);

    /// <summary>
    /// Queue item 21: where the live marks of a cell are PAINTED, in the coordinates of the view's
    /// panes (the ones the cell's own view state is in): for each mark tagged with this cell, its
    /// kind, whether it is visible, and the four corners of the painted element's box (the
    /// <c>rect</c> of a load unit, the <c>ellipse</c> of a status dot) carried through every transform
    /// the SVG applies to it - rotation included - in the order top left, top right, bottom right,
    /// bottom left of the unturned box. Read off the DOM, not off the module: this is what the
    /// operator sees. Next to it the cell's own unrotated box, its rotation in degrees (the style's)
    /// and the view's scale. JSON, read by the caller.
    /// </summary>
    public Task<string> LiveMarksOfCellAsync(string cellId) => InTheGraphAsync<string>(
        """
        const graph = ui.editor.graph;
        const view = graph.getView();
        const cell = graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell with id ' + args);
        const state = view.getState(cell);
        if (!state) throw new Error('the view has no state for cell ' + args);
        const pane = view.getOverlayPane();
        const toPane = pane.getCTM().inverse();
        const marks = [];
        for (const g of pane.querySelectorAll('[data-wcs-live]')) {
          if (g.getAttribute('data-wcs-cell') !== String(args)) continue;
          const painted = g.querySelector('rect, ellipse');
          const corners = [];
          if (painted) {
            const b = painted.getBBox();
            const m = toPane.multiply(painted.getCTM());
            for (const [x, y] of [[b.x, b.y], [b.x + b.width, b.y], [b.x + b.width, b.y + b.height], [b.x, b.y + b.height]]) {
              const p = new w.DOMPoint(x, y).matrixTransform(m);
              corners.push([p.x, p.y]);
            }
          }
          marks.push({ kind: g.getAttribute('data-wcs-live'), visible: g.style.visibility !== 'hidden', corners });
        }
        const rotation = Number(w.mxUtils.getValue(state.style, w.mxConstants.STYLE_ROTATION, 0));
        return JSON.stringify({
          cell: { x: state.x, y: state.y, width: state.width, height: state.height, rotation, scale: view.scale },
          marks
        });
        """, cellId);

    /// <summary>
    /// Missions plan of 30/9 (queue item 31): waits until the live load-unit mark of this cell is
    /// <paramref name="shown"/> on the canvas, or is not - read off draw.io's DOM, the module's shape
    /// tagged <c>data-wcs-live="load"</c>, hidden ones as not there. An event of the page, not a time,
    /// with <see cref="ConditionCeiling"/> as the point where a mark that never moves is a failure.
    /// </summary>
    public Task WaitForTheLoadMarkAsync(string cellId, bool shown) => WaitForConditionAsync(
        "() => { const f = document.getElementById('drawio-frame'); const d = f && f.contentDocument; if (!d) return false;" +
        " const marks = [...d.querySelectorAll('[data-wcs-live=\"load\"]')].filter((g) => g.getAttribute('data-wcs-cell') === " +
        JsonSerializer.Serialize(cellId) + " && g.style.visibility !== 'hidden');" +
        $" return marks.length {(shown ? "> 0" : "=== 0")}; }}",
        $"the load unit mark of cell {cellId} never {(shown ? "showed" : "went")} on the canvas");

    /// <summary>
    /// Missions plan of 30/9: waits until the route on hover is drawn - the two dashed arrows of the
    /// live module's <c>showRoute</c>, the orange one to the final destination (#ff8c00) and the yellow
    /// one to the next step (#ffd700) - read off draw.io's DOM, outside the live marks. The page draws
    /// them only after it has read the route from the host, so this is the whole round: hover, page,
    /// host, canvas.
    /// </summary>
    public Task WaitForTheRouteOnHoverAsync() => WaitForConditionAsync(
        "() => { const f = document.getElementById('drawio-frame'); const d = f && f.contentDocument; if (!d) return false;" +
        " const drawn = (colour) => [...d.querySelectorAll('path')].some((p) => !p.closest('[data-wcs-live]')" +
        " && (p.getAttribute('stroke') || '').toLowerCase() === colour && f.contentWindow.getComputedStyle(p).visibility !== 'hidden');" +
        " return drawn('#ff8c00') && drawn('#ffd700'); }",
        "the route on hover (an orange and a yellow arrow) was never drawn on the canvas");

    /// <summary>How many visible live marks (load unit, status dot) the canvas shows for this cell id.</summary>
    public Task<int> LiveMarkCountOfCellAsync(string cellId) => InTheGraphAsync<int>(
        """
        const pane = ui.editor.graph.getView().getOverlayPane();
        return [...pane.querySelectorAll('[data-wcs-live]')]
          .filter((g) => g.getAttribute('data-wcs-cell') === String(args) && g.style.visibility !== 'hidden').length;
        """, cellId);

    /// <summary>
    /// Queue item 22: where the device's OWN shape is painted right now, read off the DOM as
    /// <see cref="LiveMarksOfCellAsync"/> reads the marks and in the same coordinates (the view's
    /// panes): the four corners of the painted element (the <c>image</c> a plant device is drawn as)
    /// carried through every transform the SVG applies to it, top left, top right, bottom right,
    /// bottom left of its unturned box. During a drag or a resize this is the PREVIEW the operator
    /// sees, which neither the model nor the cell's view state says: mxGraph paints the shape at the
    /// preview and then puts the state back (mxGraphHandler.updateLivePreview, resetPreviewStates;
    /// mxVertexHandler.updateLivePreview, setState). JSON: <c>{ corners: [[x, y], ...] }</c>.
    /// </summary>
    public Task<string> PaintedDeviceOfCellAsync(string cellId) => InTheGraphAsync<string>(
        """
        const view = ui.editor.graph.getView();
        const cell = ui.editor.graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell with id ' + args);
        const state = view.getState(cell);
        if (!state || !state.shape || !state.shape.node) throw new Error('the view is not painting cell ' + args);
        const painted = state.shape.node.querySelector('image') || state.shape.node.querySelector('rect, path, ellipse');
        if (!painted) throw new Error('the shape of cell ' + args + ' has no painted element');
        const toPane = view.getOverlayPane().getCTM().inverse();
        const b = painted.getBBox();
        const m = toPane.multiply(painted.getCTM());
        const corners = [];
        for (const [x, y] of [[b.x, b.y], [b.x + b.width, b.y], [b.x + b.width, b.y + b.height], [b.x, b.y + b.height]]) {
          const p = new w.DOMPoint(x, y).matrixTransform(m);
          corners.push([p.x, p.y]);
        }
        return JSON.stringify({ corners });
        """, cellId);

    /// <summary>
    /// Queue item 32: the live arrows of one colour as the operator sees them - the motion arrow of a
    /// step (#ff0000), the route on hover (#ff8c00 to the final destination, #ffd700 to the next
    /// step) - read off the DOM in the coordinates of <see cref="PaintedDeviceOfCellAsync"/>: for each
    /// visible arrow the first and the last point of its shaft (the stroked path; the head is the
    /// filled one), carried through every transform the SVG applies to it. Read after one animation
    /// frame of the page: the live module redraws its arrows on the next frame after whatever moved a
    /// device (queueRedraw), and a frame asked for now comes after the one it asked for. JSON:
    /// <c>[{ start: [x, y], end: [x, y] }, ...]</c>.
    /// </summary>
    public async Task<string> LiveArrowsAsync(string colour)
    {
        await _page.EvaluateAsync("() => new Promise((r) => requestAnimationFrame(() => r()))");
        return await InTheGraphAsync<string>(
            """
            const pane = ui.editor.graph.getView().getOverlayPane();
            const toPane = pane.getCTM().inverse();
            const at = (path, length) => {
              const q = path.getPointAtLength(length);
              const p = new w.DOMPoint(q.x, q.y).matrixTransform(toPane.multiply(path.getCTM()));
              return [p.x, p.y];
            };
            const arrows = [];
            for (const path of pane.querySelectorAll('path')) {
              if (path.closest('[data-wcs-live]')) continue;
              if ((path.getAttribute('stroke') || '').toLowerCase() !== args) continue;
              if ((path.getAttribute('fill') || '').toLowerCase() !== 'none') continue;
              if (w.getComputedStyle(path).visibility === 'hidden') continue;
              arrows.push({ start: at(path, 0), end: at(path, path.getTotalLength()) });
            }
            return JSON.stringify(arrows);
            """, colour);
    }

    /// <summary>
    /// Queue item 22: presses the real mouse button at a point of the page and keeps it down, so that
    /// a test can look at the canvas in the middle of a gesture. The gesture goes on with
    /// <see cref="MoveTheMouseToAsync"/> and ends with <see cref="ReleaseTheMouseAsync"/> or with a key
    /// (<see cref="PressAsync"/>).
    /// </summary>
    public async Task PressTheMouseAtAsync(double x, double y)
    {
        await _page.Mouse.MoveAsync((float)x, (float)y);
        await _page.Mouse.DownAsync();
    }

    /// <summary>
    /// Moves the real mouse to a point of the page in <paramref name="steps"/> steps, with the button
    /// as it is. In steps for the reason <see cref="DragDeviceAsync"/> gives: mxGraph starts moving a
    /// cell only past a threshold, and a single jump does not cross it reliably.
    /// </summary>
    public Task MoveTheMouseToAsync(double x, double y, int steps) =>
        _page.Mouse.MoveAsync((float)x, (float)y, new MouseMoveOptions { Steps = steps });

    /// <summary>Lets the real mouse button go where the mouse is.</summary>
    public Task ReleaseTheMouseAsync() => _page.Mouse.UpAsync();

    /// <summary>
    /// Whether draw.io's graph carries a getTooltipForCell of its OWN (the live module's hook sits on
    /// the instance; draw.io's is on Graph.prototype) - the trace a hook left behind would leave.
    /// </summary>
    public Task<bool> TheGraphHasATooltipHookAsync() => InTheGraphAsync<bool>(
        "return Object.prototype.hasOwnProperty.call(ui.editor.graph, 'getTooltipForCell');");

    /// <summary>
    /// Queue item 22: whether the graph's cell renderer carries a redraw of its OWN (the live module's
    /// hook sits on the instance; mxGraph's is on mxCellRenderer.prototype, and draw.io swaps its own
    /// in only for the length of a print, an export or a page update) - the trace a hook left behind
    /// would leave.
    /// </summary>
    public Task<bool> TheGraphHasARendererHookAsync() => InTheGraphAsync<bool>(
        "return Object.prototype.hasOwnProperty.call(ui.editor.graph.cellRenderer, 'redraw');");

    /// <summary>
    /// Queue item 22: paints the whole drawing ELSEWHERE, the way a print does - mxGraph's own export
    /// of the view, <c>mxUtils.getViewXml</c>, at <paramref name="scale"/> and with no translation:
    /// temporary states of the same cells (mxTemporaryCellStates), painted by the graph's own cell
    /// renderer onto panes of their own, with the view's events off (draw.io's print, Ctrl+P, does the
    /// same in mxPrintPreview.addGraphFragment; draw.io itself never calls getViewXml). Answers how many times the renderer painted the cell
    /// <paramref name="cellId"/> during it, and where the last of those paints put it - the premise
    /// that the cell was painted somewhere else.
    /// </summary>
    public Task<string> PaintTheDrawingElsewhereAsync(string cellId, double scale) => InTheGraphAsync<string>(
        """
        const graph = ui.editor.graph;
        const renderer = graph.cellRenderer;
        const hadOwn = Object.prototype.hasOwnProperty.call(renderer, 'redraw');
        const inner = renderer.redraw;
        let paints = 0;
        let last = null;
        renderer.redraw = function (state, force, rendering) {
          if (state && state.cell && String(state.cell.id) === args.id && (rendering == null || rendering)) {
            paints++;
            last = { x: state.x, y: state.y, width: state.width, height: state.height };
          }
          return inner.apply(this, arguments);
        };
        try {
          w.mxUtils.getViewXml(graph, args.scale, null, 0, 0);
        } finally {
          if (hadOwn) renderer.redraw = inner;
          else delete renderer.redraw;
        }
        return JSON.stringify({ paints, last });
        """, new { id = cellId, scale });

    /// <summary>
    /// The tooltip draw.io itself gives this cell (Graph.prototype.getTooltipForCell, around any hook
    /// on the instance), as text.
    /// </summary>
    public Task<string> DrawioOwnTooltipTextAsync(string cellId) => InTheGraphAsync<string>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell with id ' + args);
        const div = w.document.createElement('div');
        div.innerHTML = w.Graph.prototype.getTooltipForCell.call(graph, cell) || '';
        return div.textContent;
        """, cellId);

    /// <summary>
    /// Waits for draw.io's tooltip (<c>div.mxTooltip</c>, mxTooltipHandler's) to be on screen with
    /// every one of <paramref name="texts"/> in it, and returns its text. The tooltip comes by
    /// itself after the mouse rests on a cell (mxTooltipHandler.delay): a condition, not a time.
    /// </summary>
    public async Task<string> WaitForTheTooltipAsync(params string[] texts)
    {
        const string Find = """
            (texts) => {
              const d = document.getElementById('drawio-frame').contentWindow.document;
              const tip = [...d.querySelectorAll('div.mxTooltip')].find((t) => t.style.visibility !== 'hidden');
              return tip && texts.every((x) => tip.textContent.includes(x)) ? tip.textContent : null;
            }
            """;
        await StepAsync(
            () => _page.WaitForFunctionAsync(Find, texts, new PageWaitForFunctionOptions { Timeout = ConditionCeiling }),
            $"draw.io's tooltip never showed {string.Join(" and ", texts)}");
        return await _page.EvaluateAsync<string>(Find, texts);
    }

    /// <summary>Moves the real mouse to the middle of a cell (the centre of its box, which a rotation leaves where it is), without pressing.</summary>
    public async Task HoverTheMiddleOfCellAsync(string cellId)
    {
        var box = await CellBoxOnScreenAsync(cellId);
        await _page.Mouse.MoveAsync((float)(box[0] + box[2] / 2), (float)(box[1] + box[3] / 2));
    }

    /// <summary>A real left click (Playwright's mouse, not a call into draw.io) in the middle of a cell.</summary>
    public async Task ClickTheMiddleOfCellAsync(string cellId)
    {
        var box = await CellBoxOnScreenAsync(cellId);
        await _page.Mouse.ClickAsync((float)(box[0] + box[2] / 2), (float)(box[1] + box[3] / 2));
    }

    /// <summary>Calls one of the live module's exported functions without arguments (<c>forget</c>, <c>clear</c>), through the page's own instance.</summary>
    public Task CallTheLiveModuleAsync(string name) => _page.EvaluateAsync(
        "async (name) => (await import('/_content/Wcs.WebEditor/drawio-live.js'))[name]()", name);

    /// <summary>How many cell overlays a single cell carries right now.</summary>
    public Task<int> OverlayCountOfCellAsync(string cellId) => InTheGraphAsync<int>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell with id ' + args);
        return (graph.getCellOverlays(cell) || []).length;
        """, cellId);


    /// <summary>
    /// Turns live hover on through the page's own module instance, with a STUB .NET reference
    /// instead of a real Blazor circuit. The stub's invokeMethodAsync always
    /// REJECTS, standing in for a disposed circuit (fix round 1, finding 2): every call is recorded
    /// in window.__wcsHoverCalls regardless, and an uncaught rejection is counted in
    /// window.__wcsUnhandledRejections, which <see cref="UnhandledRejectionCountAsync"/> reads back.
    /// </summary>
    /// <remarks>
    /// Queue item 9: since Task 4 the page calls <c>setHover</c> itself, once after every opening - on,
    /// whatever the engine's state, since the missions plan of 30/9 (until then with the engine's
    /// state: off in the tests that use this) - with its own .NET reference, and nothing orders that
    /// call with the test's steps. Measured: in every run
    /// seen it landed before draw.io had the drawing, but with the live module imported after the
    /// plant was read (forced by holding the module back 1.5 s) it came only after the first live
    /// frame, when the canvas already shows the devices the tests wait for. Landing after this
    /// method, it would put the page's reference in place of the stub, which would then never hear
    /// the end of a hover (and, while it switched the hover off, it took the route drawn so far with
    /// it). So this first waits for the page's own call.
    /// </remarks>
    public async Task EnableLiveHoverAsync()
    {
        await ThePageHasSetItsOwnHoverAsync();
        await EnableTheStubHoverAsync();
    }

    /// <summary>
    /// Waits for the circuit's message that carries the page's own <c>setHover</c> call (see
    /// <see cref="EnableLiveHoverAsync"/>): an event of the socket, not a time, with
    /// <see cref="ConditionCeiling"/> as the point where a call that never comes becomes a failure.
    /// </summary>
    private Task ThePageHasSetItsOwnHoverAsync() => StepAsync(
        () => _pageSetItsOwnHover.Task.WaitAsync(TimeSpan.FromMilliseconds(ConditionCeiling)),
        "the page never called setHover on the live module after opening");

    private Task EnableTheStubHoverAsync() => _page.EvaluateAsync(
        """
        async () => {
          window.__wcsUnhandledRejections = 0;
          window.addEventListener('unhandledrejection', () => { window.__wcsUnhandledRejections++; });
          window.__wcsHoverCalls = [];
          const stub = {
            invokeMethodAsync: (name, arg) => {
              window.__wcsHoverCalls.push([name, arg]);
              return Promise.reject(new Error('drawio-live browser test: simulated disposed .NET reference'));
            },
          };
          const mod = await import('/_content/Wcs.WebEditor/drawio-live.js');
          mod.setHover(true, stub);
        }
        """);

    /// <summary>
    /// Waits until the stub .NET reference of <see cref="EnableLiveHoverAsync"/> has recorded an
    /// OnLiveHover call with <paramref name="expectedArg"/> (a cell id, or null for "hover ended"):
    /// Playwright's own wait on the page, with <see cref="ConditionCeiling"/> - not a poll of ours with
    /// a ceiling of its own (final review, I3: a 2000 ms one made a live-overlay test intermittent).
    /// </summary>
    public Task WaitForHoverCallAsync(string? expectedArg) => WaitForConditionAsync(
        "() => (window.__wcsHoverCalls || []).some(call => call[0] === 'OnLiveHover' && call[1] === " +
        JsonSerializer.Serialize(expectedArg) + ")",
        $"OnLiveHover was never called with {expectedArg ?? "null"}");

    /// <summary>Every OnLiveHover call the stub .NET reference of <see cref="EnableLiveHoverAsync"/> has recorded, as JSON.</summary>
    public Task<string> HoverCallsAsync() => _page.EvaluateAsync<string>("() => JSON.stringify(window.__wcsHoverCalls || [])");

    /// <summary>How many promise rejections reached the page unhandled since <see cref="EnableLiveHoverAsync"/> ran.</summary>
    public Task<int> UnhandledRejectionCountAsync() => _page.EvaluateAsync<int>("() => window.__wcsUnhandledRejections || 0");

    /// <summary>
    /// Queue item 9: stops the clock of a page opened by <see cref="OpenWithAClockOfItsOwnAsync"/>. From
    /// here on no timer of the page or of draw.io fires until <see cref="RunTheClockForAsync"/> or
    /// <see cref="ResumeTheClockAsync"/> says so - the mouse, the page's own calls and the circuit's
    /// messages keep working, only time stands still.
    /// </summary>
    /// <remarks>
    /// Playwright can only pause at a moment that has not passed yet on the page's clock, which keeps
    /// flowing while the command travels: the moment asked for is the page's own now plus
    /// <see cref="ClockStopMargin"/>. The jump fires the timers due within it once, as if the page had
    /// stalled that long - and it is taken while nothing of what the caller is measuring has started.
    /// </remarks>
    public async Task StopTheClockAsync()
    {
        var pageNow = await _page.EvaluateAsync<double>("() => Date.now()");
        await _page.Clock.PauseAtAsync(DateTimeOffset.FromUnixTimeMilliseconds((long)pageNow + ClockStopMargin).UtcDateTime);
    }

    /// <summary>Moves the stopped clock of <see cref="StopTheClockAsync"/> forward, firing every timer that falls due.</summary>
    public Task RunTheClockForAsync(long milliseconds) => _page.Clock.RunForAsync(milliseconds);

    /// <summary>Lets the clock stopped by <see cref="StopTheClockAsync"/> flow with real time again.</summary>
    public Task ResumeTheClockAsync() => _page.Clock.ResumeAsync();

    /// <summary>Calls the module's showRoute directly, the way the page will once Task 4 wires it.</summary>
    public Task ShowLiveRouteAsync(string fromCellId, string? nextCellId, string finalCellId) => _page.EvaluateAsync(
        """
        async (args) => (await import('/_content/Wcs.WebEditor/drawio-live.js')).showRoute(args.from, args.next, args.final)
        """,
        new { from = fromCellId, next = nextCellId, final = finalCellId });

    /// <summary>Moves the real mouse to the centre of a device's shape, without pressing any button.</summary>
    public async Task HoverOverDeviceAsync(string deviceName)
    {
        var box = await DeviceBoxAsync(deviceName);
        await _page.Mouse.MoveAsync((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2));
    }

    /// <summary>
    /// Moves the real mouse OFF draw.io's own diagram container entirely (onto the shapes panel
    /// beside it), without ever passing through an intermediate mouseMove over empty canvas - the
    /// gesture fix round 1's finding 4 is about.
    /// </summary>
    public async Task MoveMouseOffTheCanvasContainerAsync()
    {
        var container = await Frame.Locator("div.geDiagramContainer").First.BoundingBoxAsync()
            ?? throw new InvalidOperationException("La tela di draw.io non ha nessun riquadro sullo schermo.");
        await _page.Mouse.MoveAsync((float)(container.X - 5), (float)(container.Y + container.Height / 2));
    }

    /// <summary>
    /// Row 33: whether the ruler switch is checked right now - the page's own memory of what the
    /// operator asked for, read the way a person reads a checkbox, not off any JS module.
    /// </summary>
    public Task<bool> RulerSwitchCheckedAsync() => _page.Locator("#drawio-ruler-switch").IsCheckedAsync();

    /// <summary>
    /// Row 33: how many of draw.io's OWN ruler elements (<c>div.geRuler</c>, one per axis - measured
    /// in app.min.js's <c>mxRuler</c> constructor, which gives each one exactly that class name and
    /// appends it to draw.io's own diagram container) are on the canvas right now. Two when the
    /// rulers are on, zero when they are off - draw.io tears the elements down rather than hiding
    /// them (mxDualRuler.destroy calls mxRuler.destroy on each).
    /// </summary>
    public Task<int> RulerElementCountAsync() => Frame.Locator("div.geRuler").CountAsync();

    /// <summary>Waits until draw.io's own ruler elements reach this count, on the real DOM inside the iframe.</summary>
    public Task WaitForRulerElementCountAsync(int expected) => WaitForConditionAsync(
        "() => { const f = document.getElementById('drawio-frame'); const d = f && f.contentDocument;" +
        $" return !!d && d.querySelectorAll('div.geRuler').length === {expected}; }}",
        $"draw.io's own ruler elements never reached a count of {expected}");

    /// <summary>
    /// Reloads the page IN PLACE - same tab, same browser context - and passes the key gate again: a
    /// full reload tears down the Blazor circuit (and with it EditorSessionState), so the key has to
    /// be given again exactly as at the first opening (see <see cref="PassTheKeyGateAsync"/>'s own
    /// remark). The SAME context is the point: row 33's memory lives in that context's localStorage,
    /// which survives a reload of one of its tabs but would not survive a brand new
    /// <see cref="OpenAsync"/>, which opens a fresh context on purpose.
    /// </summary>
    public async Task ReloadAndPassTheKeyGateAsync(string apiKey)
    {
        await _page.ReloadAsync();
        await PassTheKeyGateAsync(apiKey);
    }

    /// <summary>Row 35: the pointer position indicator's text right now, or null if the element is not on the page at all.</summary>
    public Task<string?> PointerPositionTextAsync() => TextOrNull("#drawio-pointer-position");

    /// <summary>
    /// Row 35: waits until the pointer position indicator's text satisfies <paramref name="until"/> -
    /// used both to wait for a real reading to land after a mouse move (<c>text != ""</c>) and to wait
    /// for it to go blank again (<c>text == ""</c>). A condition rather than a fixed string: the text
    /// carries a floating position that this page (rightly) does not promise byte-for-byte between
    /// two otherwise identical moves.
    /// </summary>
    public Task WaitForPointerPositionAsync(Func<string, bool> until, string what) => StepAsync(
        async () =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ConditionCeiling);
            while (true)
            {
                var text = await PointerPositionTextAsync() ?? "";
                if (until(text)) return;
                if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
                await Task.Delay(50);
            }
        },
        what);

    /// <summary>
    /// Renders a live frame on a FRESH adoption whose very first mxGraphView.prototype.addListener
    /// call throws, once - simulating adopt() failing midway through installing its listeners (fix
    /// round 1, finding 6).
    /// <para>
    /// Since Task 4 the page draws its own frames through this same module (the page and this
    /// helper import the same URL, so they share one instance): by the time a test gets here the
    /// page has most likely adopted the graph already, and a render on the graph it already has does
    /// not adopt at all - the broken listener would never be reached. <c>clear()</c> lets the graph
    /// go first. The clear, the break and the render run in ONE task of the page's event loop, so no
    /// frame of the page can come in between to adopt the graph again or to take the broken listener
    /// for itself. The original is put back whatever happened, so a break the render did not reach
    /// cannot leak into what follows.
    /// </para>
    /// </summary>
    public Task<string> RenderLiveFrameOnABrokenAdoptionAsync(string frameJson) => _page.EvaluateAsync<string>(
        """
        async (json) => {
          const mod = await import('/_content/Wcs.WebEditor/drawio-live.js');
          const proto = document.getElementById('drawio-frame').contentWindow.mxGraphView.prototype;
          const original = proto.addListener;
          mod.clear();
          proto.addListener = function (...a) {
            proto.addListener = original;
            throw new Error('drawio-live browser test: forced addListener failure');
          };
          try { return mod.render(json); } finally { proto.addListener = original; }
        }
        """, frameJson);

    /// <summary>
    /// Queue item 21: like <see cref="RenderLiveFrameOnABrokenAdoptionAsync"/>, but the break is the
    /// LAST thing adopt() installs - the container's own 'mouseleave' - so that everything before it,
    /// the tooltip hook included, is already on the graph when the throw comes. Answers the render's
    /// own answer and whether the graph still carries a tooltip hook afterwards.
    /// </summary>
    public Task<string> RenderLiveFrameOnALateBrokenAdoptionAsync(string frameJson) => _page.EvaluateAsync<string>(
        """
        async (json) => {
          const mod = await import('/_content/Wcs.WebEditor/drawio-live.js');
          const w = document.getElementById('drawio-frame').contentWindow;
          const container = w.document.querySelector('.geDiagramContainer');
          mod.clear();
          container.addEventListener = function (...a) {
            delete container.addEventListener;
            throw new Error('drawio-live browser test: forced addEventListener failure');
          };
          let answer;
          try { answer = mod.render(json); } finally { delete container.addEventListener; }
          return answer;
        }
        """, frameJson);

    /// <summary>
    /// Renders nothing: breaks mxShape.prototype.destroy so that every shape's own destroy() throws,
    /// calls one of the module's exported functions (<c>forget</c>, <c>clear</c>) and reports whether
    /// the call itself threw. The original is put back whatever happened.
    /// </summary>
    public Task<bool> CallThrowsWhenShapesAreBrokenAsync(string name) => _page.EvaluateAsync<bool>(
        """
        async (name) => {
          const mod = await import('/_content/Wcs.WebEditor/drawio-live.js');
          const w = document.getElementById('drawio-frame').contentWindow;
          const original = w.mxShape.prototype.destroy;
          w.mxShape.prototype.destroy = function () {
            throw new Error('drawio-live browser test: forced destroy failure');
          };
          let threw = false;
          try { mod[name](); } catch (e) { threw = true; } finally { w.mxShape.prototype.destroy = original; }
          return threw;
        }
        """, name);

    /// <summary>
    /// Renders a live frame carrying one motion (so drawio-live.js holds a real arrow shape), breaks
    /// mxShape.prototype.destroy so that shape's own destroy() throws, calls clear() and reports
    /// whether the call itself threw - proving (or not) that clear() swallows an internal failure
    /// instead of letting it escape to its caller (fix round 1, finding 7).
    /// </summary>
    public Task<bool> ClearThrowsWhenAShapeIsBrokenAsync() => _page.EvaluateAsync<bool>(
        """
        async () => {
          const mod = await import('/_content/Wcs.WebEditor/drawio-live.js');
          const w = document.getElementById('drawio-frame').contentWindow;
          const original = w.mxShape.prototype.destroy;
          w.mxShape.prototype.destroy = function () {
            throw new Error('drawio-live browser test: forced destroy failure');
          };
          let threw = false;
          try { mod.clear(); } catch (e) { threw = true; }
          w.mxShape.prototype.destroy = original;
          return threw;
        }
        """);

    /// <summary>
    /// Gives a cell one of draw.io's OWN overlays - the warning icon Graph.setCellWarning pushes,
    /// through Graph.prototype.addCellOverlay exactly as the product's own validation and comment
    /// features do. It is here so a test can prove drawio-live.js's guard leaves overlays that are
    /// not its own alone (fix round 2, finding 1).
    /// </summary>
    public Task SetCellWarningAsync(string cellId, string text) => InTheGraphAsync<bool>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args.id);
        if (!cell) throw new Error('the drawing has no cell with id ' + args.id);
        graph.setCellWarning(cell, args.text);
        return true;
        """, new { id = cellId, text });

    /// <summary>
    /// Right-clicks a device the way a person does, to bring up draw.io's own context menu, and
    /// waits until a menu is really there.
    /// </summary>
    /// <remarks>
    /// The selection survives the click: mxGraph only re-selects on a right-click when the cell
    /// under the pointer is <b>not</b> already selected, so a multi-selection right-clicked on one
    /// of its own members stays whole. That matters here, because the entries this menu is opened
    /// for - Align, Distribute - only appear above one cell.
    /// </remarks>
    public async Task RightClickOnDeviceAsync(string deviceName)
    {
        var shape = await WaitForDeviceAsync(deviceName);
        var box = await BoxOf(shape, deviceName);
        await _page.Mouse.ClickAsync(
            (float)(box.X + box.Width / 2),
            (float)(box.Y + box.Height / 2),
            new MouseClickOptions { Button = MouseButton.Right });

        await WaitForConditionAsync(
            "() => { const d = document.getElementById('drawio-frame').contentDocument;" +
            " return !!d && d.querySelectorAll('.mxPopupMenu').length > 0; }",
            $"il tasto destro su \"{deviceName}\" non ha aperto nessun menu contestuale di draw.io");
    }

    /// <summary>
    /// Every context menu draw.io currently has open, and every entry in each - the text as the
    /// operator reads it, with the shortcut column draw.io prints next to it and whether the entry
    /// is greyed out. A submenu is a menu of its own, so it appears here once it has been opened.
    /// </summary>
    public Task<string> TheContextMenuAsync() => InTheGraphAsync<string>(
        """
        const doc = ui.editor.graph.container.ownerDocument;
        const menus = doc.querySelectorAll('.mxPopupMenu');
        if (menus.length === 0) return '(no context menu is open)';
        const lines = [];
        for (let i = 0; i < menus.length; i++) {
          const menu = menus[i];
          const shown = menu.style.display !== 'none' && menu.offsetParent !== null;
          lines.push('menu ' + i + (shown ? ' (shown)' : ' (hidden)') + ':');
          const rows = menu.querySelectorAll('tr.mxPopupMenuItem');
          for (let j = 0; j < rows.length; j++) {
            const cells = rows[j].querySelectorAll('td');
            const parts = [];
            for (let k = 0; k < cells.length; k++) {
              const text = String(cells[k].textContent || '').trim();
              if (text.length) parts.push(text);
            }
            const disabled = String(rows[j].className).indexOf('Disabled') >= 0;
            lines.push('  - ' + parts.join(' | ') + (disabled ? '   [greyed out]' : ''));
          }
          if (rows.length === 0) lines.push('  (no entries)');
        }
        return lines.join('\n');
        """);

    /// <summary>
    /// Row 63: waits until the bridge has been told what the NEXT right-click menu on
    /// <paramref name="cellId"/> will be built from - the cell a device (or not), the engine running
    /// (or not). The page tells the bridge after its own render, so a right-click made the moment the
    /// device shows up, or the moment "Engine: Run" is on the page, could find a menu built from what
    /// the bridge knew a moment before: this waits for the condition, not for a time.
    /// </summary>
    public Task WaitForTheDeviceMenuAsync(string cellId, bool isDevice, bool running) => WaitForConditionAsync(
        "async () => { const s = (await import('/_content/Wcs.WebEditor/drawio-bridge.js')).deviceMenuState();" +
        $" return s.hooked && s.devices.includes('{cellId}') === {(isDevice ? "true" : "false")}" +
        $" && s.running === {(running ? "true" : "false")}; }}",
        $"the bridge was never told that {cellId} is {(isDevice ? "" : "NOT ")}a device with the engine {(running ? "running" : "not running")}");

    /// <summary>What the bridge will build the next right-click menu from, as it holds it now.</summary>
    public Task<string> TheDeviceMenuStateAsync() => _page.EvaluateAsync<string>(
        "async () => JSON.stringify((await import('/_content/Wcs.WebEditor/drawio-bridge.js')).deviceMenuState())");

    /// <summary>
    /// Right-clicks the cell with this id where draw.io draws it - the centre of a shape, the middle
    /// of an arrow's middle segment - and waits until a menu is really there. The point comes from
    /// draw.io's own view, as for <see cref="CellBoxOnScreenAsync"/>.
    /// </summary>
    public async Task RightClickOnCellAsync(string cellId)
    {
        var point = await InTheGraphAsync<double[]>(
            """
            const graph = ui.editor.graph;
            const cell = graph.getModel().getCell(args.id);
            if (!cell) throw new Error('the drawing has no cell with id ' + args.id);
            const state = graph.view.getState(cell);
            if (!state) throw new Error('the drawing holds the cell ' + args.id + ' but is not drawing it');
            let x = state.getCenterX();
            let y = state.getCenterY();
            const points = graph.getModel().isEdge(cell) ? state.absolutePoints : null;
            if (points && points.length >= 2) {
              const k = Math.floor((points.length - 2) / 2);
              x = (points[k].x + points[k + 1].x) / 2;
              y = (points[k].y + points[k + 1].y) / 2;
            }
            const container = graph.container.getBoundingClientRect();
            const frame = graph.container.ownerDocument.defaultView.frameElement;
            const outer = frame ? frame.getBoundingClientRect() : { left: 0, top: 0 };
            return [outer.left + container.left + x - graph.container.scrollLeft,
                    outer.top + container.top + y - graph.container.scrollTop];
            """,
            new { id = cellId });

        await _page.Mouse.ClickAsync((float)point[0], (float)point[1], new MouseClickOptions { Button = MouseButton.Right });
        await WaitForConditionAsync(
            "() => { const d = document.getElementById('drawio-frame').contentDocument;" +
            " return !!d && d.querySelectorAll('.mxPopupMenu').length > 0; }",
            $"il tasto destro sulla cella {cellId} non ha aperto nessun menu contestuale di draw.io");
    }

    /// <summary>
    /// Makes draw.io's graph refuse every selection from now on - its own setSelectionCell throws - for
    /// the one test that has to see a cell added and then not selected. On the graph instance of this
    /// page only; the page is closed at the end of that test.
    /// </summary>
    public Task MakeSelectingACellFailAsync() => InTheGraphAsync<bool>(
        "ui.editor.graph.setSelectionCell = function () { throw new Error('selection refused on purpose (browser test)'); }; return true;");

    /// <summary>
    /// Final review, M1: makes the NEXT change of draw.io's model throw from a listener of its own -
    /// once, the listener removes itself first. The change is already in the model when its listeners
    /// run (endUpdate announces it after executing it), so this is a cell added and then an exception:
    /// the one case in which the bridge's drawDeviceCell used to answer 'failed' for a drawn cell.
    /// </summary>
    public Task MakeTheNextModelChangeThrowInAListenerAsync() => InTheGraphAsync<bool>(
        "const model = ui.editor.graph.getModel();" +
        " const listener = function () { model.removeListener(listener); throw new Error('a listener of the model threw on purpose (browser test)'); };" +
        " model.addListener('change', listener); return true;");

    /// <summary>
    /// Closes draw.io's right-click menu, if one is open, through its own handler's hideMenu, and
    /// waits until none is left. A menu left open over the canvas takes the next click on whatever
    /// part of the canvas it covers - and a right-click on one of its rows presses that row.
    /// </summary>
    public async Task CloseTheContextMenuAsync()
    {
        await InTheGraphAsync<bool>("ui.editor.graph.popupMenuHandler.hideMenu(); return true;");
        await WaitForConditionAsync(
            "() => { const d = document.getElementById('drawio-frame').contentDocument;" +
            " return !!d && d.querySelectorAll('.mxPopupMenu').length === 0; }",
            "draw.io's right-click menu did not close");
    }

    /// <summary>The id of the element that has the keyboard focus in the PAGE (not inside draw.io's iframe), or "" when none has an id.</summary>
    public Task<string> FocusedElementIdAsync() => _page.EvaluateAsync<string>(
        "() => (document.activeElement && document.activeElement.id) || ''");

    /// <summary>The text of the page's element matching <paramref name="selector"/>, or null when there is none.</summary>
    public Task<string?> TextOfAsync(string selector) => TextOrNull(selector);

    /// <summary>The property row that currently owns the keyboard focus, or null outside the properties.</summary>
    public Task<string?> FocusedPropertyAsync() => _page.EvaluateAsync<string?>(
        "() => document.activeElement && document.activeElement.closest('[data-property]')?.getAttribute('data-property')");

    public Task<double> PropertyDockWidthAsync() => _page.EvaluateAsync<double>(
        "() => document.getElementById('drawio-property-dock').getBoundingClientRect().width");

    public Task WaitForPropertyDockWidthAsync(double width) => WaitForConditionAsync(
        $"() => Math.abs(document.getElementById('drawio-property-dock').getBoundingClientRect().width - {width.ToString(CultureInfo.InvariantCulture)}) <= 1",
        $"the property dock never reached {width} pixels");

    public async Task DragPropertyDockByAsync(double delta)
    {
        await WaitForThePropertyDockResizerAsync();
        var box = await _page.Locator("#drawio-property-dock-resizer").BoundingBoxAsync()
            ?? throw new InvalidOperationException("The property dock separator is not on screen.");
        await _page.Mouse.MoveAsync((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2));
        await _page.Mouse.DownAsync();
        await _page.Mouse.MoveAsync((float)(box.X + box.Width / 2 - delta), (float)(box.Y + box.Height / 2));
        await _page.Mouse.UpAsync();
    }

    public async Task FocusPropertyDockResizerAsync()
    {
        await WaitForThePropertyDockResizerAsync();
        await _page.Locator("#drawio-property-dock-resizer").FocusAsync();
    }

    public async Task DoubleClickPropertyDockResizerAsync()
    {
        await WaitForThePropertyDockResizerAsync();
        await _page.Locator("#drawio-property-dock-resizer").DblClickAsync();
    }

    /// <summary>
    /// The separator answers only once drawio-property-dock.js has attached its listeners, which happens
    /// after the first render: a drag sent before that is lost (seen once in six runs).
    /// </summary>
    private Task WaitForThePropertyDockResizerAsync() => WaitForConditionAsync(
        "() => document.getElementById('drawio-property-dock')?.dataset.resizerAttached === 'true'",
        "the property dock separator never got its listeners");
    public Task<string?> PropertyDockMemoryAsync() => _page.EvaluateAsync<string?>(
        "() => localStorage.getItem('wcs.drawio.propertyDock.width.v1')");
    public Task SetPropertyDockMemoryAsync(string value) => _page.EvaluateAsync(
        "value => localStorage.setItem('wcs.drawio.propertyDock.width.v1', value)", value);
    public Task<bool> PropertyDockStorageIsDeniedAsync() => _page.EvaluateAsync<bool>(
        """
        () => {
          let readDenied = false, writeDenied = false;
          try { localStorage.getItem('wcs.drawio.propertyDock.width.v1'); } catch { readDenied = true; }
          try { localStorage.setItem('wcs.drawio.propertyDock.width.v1', '560'); } catch { writeDenied = true; }
          return readDenied && writeDenied;
        }
        """);

    /// <summary>
    /// Scrolls only the properties, then returns the description footer's top before and after,
    /// its bottom, the dock's bottom and the scroll amount. The footer belongs outside that moving
    /// area: this measures the layout in a real browser rather than inferring it from CSS.
    /// </summary>
    public Task<double[]> PropertyDescriptionLayoutAfterScrollingAsync() => _page.EvaluateAsync<double[]>(
        """
        async () => {
          const dock = document.getElementById('drawio-property-dock');
          const scroll = dock && dock.querySelector('.drawio-property-scroll');
          const footer = document.getElementById('drawio-property-description');
          if (!dock || !scroll || !footer) throw new Error('the property description layout is incomplete');
          const beforeTop = footer.getBoundingClientRect().top;
          scroll.scrollTop = scroll.scrollHeight;
          await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
          const after = footer.getBoundingClientRect();
          return [beforeTop, after.bottom, dock.getBoundingClientRect().bottom, scroll.scrollTop, after.top];
        }
        """);

    /// <summary>Waits until the keyboard focus of the PAGE is on one of the elements with these ids.</summary>
    public Task WaitForTheFocusOnAsync(params string[] elementIds) => WaitForConditionAsync(
        $"() => {System.Text.Json.JsonSerializer.Serialize(elementIds)}.includes((document.activeElement && document.activeElement.id) || '')",
        $"the keyboard focus never reached {string.Join(" or ", elementIds)}");

    /// <summary>Whether the element with this id is a &lt;details&gt; that is open right now.</summary>
    public Task<bool> IsOpenAsync(string elementId) => _page.EvaluateAsync<bool>(
        "(id) => { const e = document.getElementById(id); return !!e && e.open === true; }", elementId);

    /// <summary>
    /// Takes the bridge off as the page does when it goes (its <c>detach</c>, on the page's own module
    /// instance), leaving draw.io where it is - the one way to look at what the bridge leaves behind
    /// in a draw.io that is still there. Answers whether the <c>createPopupMenu</c> that ui.menus
    /// carried just before the detach - the bridge's, while it is attached - is STILL the one there.
    /// </summary>
    public async Task<bool> DetachTheBridgeAndSayWhetherItsPopupHookStayedAsync()
    {
        await InTheGraphAsync<bool>("w.__wcsPopupBeforeDetach = ui.menus.createPopupMenu; return true;");
        await _page.EvaluateAsync("async () => (await import('/_content/Wcs.WebEditor/drawio-bridge.js')).detach()");
        return await InTheGraphAsync<bool>("return ui.menus.createPopupMenu === w.__wcsPopupBeforeDetach;");
    }

    /// <summary>
    /// Queue item 8's counterpart of <see cref="DetachTheBridgeAndSayWhetherItsPopupHookStayedAsync"/>:
    /// takes the bridge off as the page does when it goes, and answers whether the
    /// <c>mxWindow.prototype.fit</c> draw.io's windows carried just before - the bridge's, while it is
    /// attached - is STILL the one there.
    /// </summary>
    public async Task<bool> DetachTheBridgeAndSayWhetherItsWindowHookStayedAsync()
    {
        await InTheGraphAsync<bool>("w.__wcsFitBeforeDetach = w.mxWindow.prototype.fit; return true;");
        await _page.EvaluateAsync("async () => (await import('/_content/Wcs.WebEditor/drawio-bridge.js')).detach()");
        return await InTheGraphAsync<bool>("return w.mxWindow.prototype.fit === w.__wcsFitBeforeDetach;");
    }

    /// <summary>
    /// Hovers a context-menu entry by the text the operator reads, which is how a submenu of
    /// draw.io opens, and waits until one more menu is on screen than there was.
    /// </summary>
    public async Task HoverTheMenuEntryAsync(string label)
    {
        var before = await OpenMenuCountAsync();
        await StepAsync(
            () => MenuEntry(label).HoverAsync(new LocatorHoverOptions { Timeout = ConditionCeiling }),
            $"la voce \"{label}\" non c'e' nel menu contestuale di draw.io, oppure non si e' potuta sorvolare");

        await WaitForConditionAsync(
            "() => { const d = document.getElementById('drawio-frame').contentDocument;" +
            $" return !!d && d.querySelectorAll('.mxPopupMenu').length > {before}; }}",
            $"sorvolare \"{label}\" non ha aperto nessun sottomenu");
    }

    /// <summary>Clicks a context-menu entry by the text the operator reads.</summary>
    public Task ClickTheMenuEntryAsync(string label) => StepAsync(
        () => MenuEntry(label).ClickAsync(new LocatorClickOptions { Timeout = ConditionCeiling }),
        $"la voce \"{label}\" non c'e' nel menu contestuale di draw.io, oppure non si e' potuta premere");

    /// <summary>The decoded image and accessibility state of a right-click menu entry.</summary>
    public Task<DrawioContextMenuIcon> ContextMenuEntryIconAsync(string label) =>
        MenuEntry(label)
            .Locator("xpath=ancestor::tr[contains(@class,'mxPopupMenuItem')][1]//td[contains(@class,'mxPopupMenuIcon')]//img")
            .EvaluateAsync<DrawioContextMenuIcon>(
                "image => ({ source: image.src, alt: image.getAttribute('alt'), ariaHidden: image.getAttribute('aria-hidden'), role: image.getAttribute('role'), complete: image.complete, naturalWidth: image.naturalWidth, naturalHeight: image.naturalHeight })");

    /// <summary>How many context menus draw.io has open right now, submenus included.</summary>
    public Task<int> OpenMenuCountAsync() => _page.EvaluateAsync<int>(
        "() => { const d = document.getElementById('drawio-frame').contentDocument;" +
        " return d ? d.querySelectorAll('.mxPopupMenu').length : 0; }");

    /// <summary>
    /// The <b>last</b> menu entry with this text. Last and not first because a submenu is drawn over
    /// the menu that opened it and carries entries of its own: "Horizontal" lives in Distribute and
    /// in Spacing alike, and the one just opened is the one the operator is looking at.
    /// </summary>
    private ILocator MenuEntry(string label) =>
        Frame.GetByText(label, new FrameLocatorGetByTextOptions { Exact = true }).Last;

    /// <summary>
    /// Il titolo della sezione dei dispositivi nel pannello delle forme, <b>come lo vede chi
    /// disegna</b>: fra i titoli visibili del pannello, quello che nomina i dispositivi del WCS.
    /// Solleva se non c'e' - e' l'esito che questo compito esiste per rendere impossibile.
    /// </summary>
    /// <remarks>
    /// Si cerca per testo e non per un identificativo messo apposta: il pannello e' roba di draw.io,
    /// e l'unica cosa nostra che ci arriva e' cio' che c'e' scritto. E' anche cio' che la prova deve
    /// guardare - un titolo che smettesse di dirlo sarebbe il difetto, non un dettaglio.
    /// </remarks>
    public async Task<ILocator> DeviceSectionTitleAsync()
    {
        var titles = Frame.Locator(".geSidebarContainer .geTitle", new FrameLocatorLocatorOptions { HasTextString = "WCS devices" });
        await StepAsync(
            () => titles.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ConditionCeiling }),
            "la sezione dei dispositivi WCS non e' comparsa nel pannello delle forme di draw.io: draw.io non e' " +
            "partito, la configurazione non gli e' arrivata, oppure la sezione c'e' ma non e' fra quelle aperte");

        var count = await titles.CountAsync();
        if (count != 1)
            throw new InvalidOperationException(
                $"Il pannello mostra {count} sezioni che nominano i dispositivi WCS invece di una sola: quale sia " +
                "quella di cui parla questa prova non e' piu' deciso.");
        return titles.First;
    }

    /// <summary>
    /// Apre la sezione dei dispositivi (draw.io fa nascere chiuse le sezioni che arrivano dalla
    /// configurazione) e torna le sue voci, <b>quando ce n'e' almeno una</b>.
    /// </summary>
    /// <remarks>
    /// Il clic sul titolo e' il gesto di chi disegna, non una scorciatoia: e' cosi' che una sezione
    /// si apre. L'attesa e' sulla prima voce e non su un tempo, perche' le voci nascono
    /// dall'apertura.
    /// </remarks>
    public async Task<ILocator> OpenDeviceSectionAsync()
    {
        var title = await DeviceSectionTitleAsync();
        var entries = EntriesUnder(title);
        if (await entries.CountAsync() > 0) return entries;

        await title.ClickAsync();
        await StepAsync(
            () => entries.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ConditionCeiling }),
            "la sezione dei dispositivi WCS si e' aperta senza nessuna voce dentro: la configurazione e' arrivata " +
            "con una sezione vuota");
        return entries;
    }

    /// <summary>
    /// Trascina la voce di indice <paramref name="index"/> della sezione dei dispositivi al centro
    /// della tela, con il mouse vero. Restituisce il PUNTO in cui si e' rilasciato, in pixel della
    /// pagina: mxGraph centra sul puntatore la cella che deposita, quindi quel punto sta dentro la
    /// sagoma appena nata ed e' il modo di ritrovarla senza passare dall'etichetta.
    /// <para>
    /// <b>Dall'etichetta non si puo'</b>, ed e' misurato: il pannello delle forme disegna ogni
    /// propria voce come un mini-disegno, etichetta compresa, quindi con la sezione aperta
    /// "Automation" sta sia nella miniatura sia sulla tela e la ricerca per etichetta trova due
    /// elementi. Il nome del dispositivo non aiuta: nato da un rilascio, non ne ha uno.
    /// </para>
    /// </summary>
    public async Task<(float X, float Y)> DragEntryOntoTheCanvasAsync(int index)
    {
        var entries = await OpenDeviceSectionAsync();
        var entry = entries.Nth(index);
        var from = await entry.BoundingBoxAsync()
            ?? throw new InvalidOperationException(
                $"La voce {index} della sezione dei dispositivi e' nel DOM ma non ha nessun riquadro sullo schermo: " +
                "non la si puo' afferrare con il mouse.");

        // Il bersaglio e' il centro della tela di draw.io, letto da lei: un punto fisso cadrebbe
        // fuori appena la finestra o il pannello cambiano larghezza.
        var canvas = Frame.Locator(".geDiagramContainer").First;
        var to = await canvas.BoundingBoxAsync()
            ?? throw new InvalidOperationException("La tela di draw.io non ha nessun riquadro sullo schermo.");

        await _page.Mouse.MoveAsync((float)(from.X + from.Width / 2), (float)(from.Y + from.Height / 2));
        await _page.Mouse.DownAsync();
        // A passi, come per il trascinamento di un dispositivo: mxGraph fa partire il trascinamento
        // solo dopo una soglia di movimento.
        var dropX = (float)(to.X + to.Width / 2);
        var dropY = (float)(to.Y + to.Height / 2);
        await _page.Mouse.MoveAsync(dropX, dropY, new MouseMoveOptions { Steps = 16 });
        await _page.Mouse.UpAsync();
        return (dropX, dropY);
    }

    /// <summary>
    /// Clicca un punto della pagina con il mouse vero. Serve a selezionare la sagoma appena
    /// rilasciata prendendola dal punto in cui e' stata lasciata cadere - e a portare il fuoco della
    /// tastiera dentro l'iframe, che dopo il solo trascinamento non ci va: misurato, Ctrl+C e Ctrl+V
    /// subito dopo un rilascio non arrivavano a draw.io e l'incollaggio non avveniva affatto.
    /// </summary>
    public Task ClickAtAsync(float x, float y) => _page.Mouse.ClickAsync(x, y);

    /// <summary>Clicks the element of the page (not of draw.io's frame) that <paramref name="selector"/> finds.</summary>
    public Task ClickAsync(string selector) => _page.Locator(selector).ClickAsync();

    public Task<string> InputValueOfAsync(string selector) => _page.Locator(selector).InputValueAsync();

    /// <summary>
    /// Presses the page's button <paramref name="selector"/> finds from script (<c>click()</c>), which moves
    /// no keyboard focus, and - in the same task, so before any answer of the host to the press can arrive
    /// - puts the focus on <paramref name="thenFocus"/> when there is one: the operator moving the focus
    /// elsewhere while the page is still answering the press.
    /// </summary>
    public async Task PressFromScriptAsync(string selector, string? thenFocus = null)
    {
        var focused = await _page.EvaluateAsync<string>(
            """
            ([selector, thenFocus]) => {
              const button = document.querySelector(selector);
              if (!button) return `nothing matches ${selector}`;
              button.click();
              if (thenFocus) document.querySelector(thenFocus)?.focus();
              return (document.activeElement && document.activeElement.id) || '';
            }
            """,
            new[] { selector, thenFocus ?? "" });
        if (thenFocus is { Length: > 0 } && focused != thenFocus.TrimStart('#'))
            throw new InvalidOperationException($"The focus did not go to {thenFocus} after the press on {selector}: it is on '{focused}'.");
        if (focused.StartsWith("nothing matches", StringComparison.Ordinal)) throw new InvalidOperationException(focused);
    }

    /// <summary>
    /// Compito 1: types into a page-owned text or number input (not draw.io's frame) - the source and
    /// destination filters and the LU Id Type field of the Operations section.
    /// </summary>
    public Task FillAsync(string selector, string text) => _page.Locator(selector).FillAsync(text);

    /// <summary>Compito 2: double-clicks a page-owned element - a cell of the device list, which opens its editor.</summary>
    public Task DoubleClickAsync(string selector) => _page.Locator(selector).DblClickAsync();

    /// <summary>
    /// Compito 2: waits until the page-owned element <paramref name="selector"/> finds carries
    /// <paramref name="attribute"/> with exactly <paramref name="value"/> - the mark of the list row
    /// whose device or arrow is the one selected on the drawing, which arrives only after draw.io has
    /// told the page over the circuit.
    /// </summary>
    public Task WaitForAttributeAsync(string selector, string attribute, string value) => WaitForConditionAsync(
        $"() => {{ const found = document.querySelector({JsonSerializer.Serialize(selector)});" +
        $" return !!found && found.getAttribute({JsonSerializer.Serialize(attribute)}) === {JsonSerializer.Serialize(value)}; }}",
        $"{selector} never carried {attribute}=\"{value}\"");

    /// <summary>Compito 1: picks an option of a page-owned &lt;select&gt; by its value - the source and destination pickers.</summary>
    public Task SelectOptionAsync(string selector, string value) =>
        _page.Locator(selector).SelectOptionAsync(new SelectOptionValue { Value = value });

    /// <summary>
    /// Waits until the element of the page that <paramref name="selector"/> finds contains
    /// <paramref name="text"/>. A wait that expires says what the page was showing and what the
    /// console said, as <see cref="StepAsync"/> does - not only that the time ran out.
    /// </summary>
    public async Task WaitForTextAsync(string selector, string text)
    {
        try
        {
            await Microsoft.Playwright.Assertions.Expect(_page.Locator(selector)).ToContainTextAsync(text,
                new() { Timeout = ConditionCeiling });
        }
        catch (PlaywrightException ex)
        {
            var visible = await _page.InnerTextAsync("body");
            throw new InvalidOperationException(
                $"\"{text}\" never showed on {selector}.{Environment.NewLine}" +
                $"The page showed:{Environment.NewLine}{visible.Trim()}{Environment.NewLine}" +
                $"Console errors:{Environment.NewLine}" +
                (_consoleErrors.Count == 0 ? "(none)" : string.Join(Environment.NewLine, _consoleErrors)), ex);
        }
    }

    /// <summary>
    /// Fix round 1, MINOR 11: the same wait as <see cref="WaitForTextAsync(string, string)"/>, but
    /// against a regular expression - for a line whose final wording can be one of several
    /// (DrawioCommandFollower.OutcomeFor: executed / executed with a warning / FAILED), where waiting
    /// for one fixed substring would either miss the others or, worse, match a transient state
    /// ("is being executed") that is not final at all.
    /// </summary>
    public async Task WaitForTextAsync(string selector, System.Text.RegularExpressions.Regex pattern)
    {
        try
        {
            await Microsoft.Playwright.Assertions.Expect(_page.Locator(selector)).ToContainTextAsync(pattern,
                new() { Timeout = ConditionCeiling });
        }
        catch (PlaywrightException ex)
        {
            var visible = await _page.InnerTextAsync("body");
            throw new InvalidOperationException(
                $"\"{pattern}\" never showed on {selector}.{Environment.NewLine}" +
                $"The page showed:{Environment.NewLine}{visible.Trim()}{Environment.NewLine}" +
                $"Console errors:{Environment.NewLine}" +
                (_consoleErrors.Count == 0 ? "(none)" : string.Join(Environment.NewLine, _consoleErrors)), ex);
        }
    }

    /// <summary>
    /// Quanto e' largo il testo di un titolo del pannello e quanto spazio ha: le due misure con cui
    /// si sa se draw.io lo stia <b>tagliando</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Esiste perche' "il titolo c'e'" e "il titolo si legge" sono due domande diverse, e la prima
    /// resta vera anche quando la seconda e' falsa: draw.io disegna i titoli delle sezioni con
    /// <c>white-space:nowrap; overflow:hidden; text-overflow:ellipsis</c>, quindi una promessa
    /// troppo lunga finisce nei puntini mentre <c>innerText</c> continua a portarla per intero. E'
    /// il caso vero, non teorico: la prima stesura del titolo misurava 347 pixel in un titolo che
    /// ne offre 182.
    /// </para>
    /// <para>
    /// <b>Non si misura il riquadro del titolo, ma quello del TESTO</b>, e la differenza e' stata
    /// misurata: il titolo di draw.io e' un &lt;a&gt; che contiene la maniglia "apri/chiudi" in
    /// posizione assoluta e poi il testo in uno &lt;span&gt;. Su quell'&lt;a&gt; lo
    /// <c>scrollWidth</c> resta uguale al <c>clientWidth</c> anche quando il testo trabocca -
    /// l'idioma solito per scoprire i puntini li' non funziona, e una prova che lo usasse sarebbe
    /// verde su un titolo tagliato. Lo span, invece, dichiara i suoi 347 pixel. Lo spazio vero e'
    /// il riquadro del titolo meno i suoi margini interni, che e' dove il testo puo' stare.
    /// </para>
    /// </remarks>
    public async Task<(int Text, int Room)> TitleWidthsAsync(ILocator title)
    {
        var measured = await title.EvaluateAsync<int[]>(
            """
            e => {
              const holder = Array.from(e.querySelectorAll('*'))
                .find(n => n.children.length === 0 && n.textContent.trim().length > 0);
              // NESSUN RIPIEGO su "e": misurare il riquadro del titolo invece del testo darebbe
              // scrollWidth == clientWidth sempre, cioe' una prova VERDE su un titolo tagliato -
              // esattamente la cecita' che il commento qui sopra racconta. Se il testo non sta piu'
              // in un elemento suo, la struttura di draw.io e' cambiata e questa misura non vale
              // piu': meglio fermarsi dicendolo.
              if (!holder) throw new Error(
                "Il titolo della sezione non ha piu' un elemento che porti il testo da solo: draw.io " +
                "ha cambiato il modo in cui disegna i titoli, e questa misura del taglio non vale " +
                "piu'. Va rifatta su com'e' fatto il titolo adesso, non aggiustata qui.");
              const style = getComputedStyle(e);
              const room = e.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
              return [Math.ceil(holder.scrollWidth), Math.floor(room)];
            }
            """);
        return (measured[0], measured[1]);
    }

    /// <summary>Le voci che stanno sotto un titolo del pannello: draw.io le mette nel fratello subito dopo.</summary>
    private static ILocator EntriesUnder(ILocator title) =>
        title.Locator("xpath=following-sibling::div[1]").Locator("a.geItem");

    /// <summary>
    /// Preme il salvataggio e aspetta il verbale. <b>Il pulsante e' quello di draw.io</b>: la pagina
    /// non ne ha uno proprio - in modalita' incorporata con <c>noSaveBtn=1</c> draw.io disegna lui
    /// il pulsante "Save and Exit", e il suo evento <c>save</c> e' cio' che il ponte ascolta.
    /// <para>
    /// La scritta e' in inglese perche' l'iframe parte con <c>lang=en</c> (si veda <c>FrameUrl</c> in
    /// DrawioEditor.razor): e' draw.io a tradurre il proprio pulsante, non noi. Se un domani la
    /// lingua dell'Editor cambiasse, questa e' una delle due cose che si muovono con lei.
    /// </para>
    /// <para>
    /// <b>What it waits for is the page's SAVE COUNTER to move</b> - not the report, and not the
    /// report's text. Waiting for the report to EXIST returned instantly from the second save on,
    /// because the first save's report is still on the page (review finding 4 of the first
    /// revision); waiting for its TEXT to change closed most of that hole but not all of it, and
    /// MEDIO 2 of the second revision measured what was left: the new head test was red in 2 runs
    /// out of 7, and when it fell the second save's report was identical to the first word for
    /// word, junction identifier included. Two saves can end exactly the same way, and a
    /// condition written on the text cannot tell them apart - the previous author said so in his
    /// own reservation, believing no test needed it yet, while one already did.
    /// </para>
    /// <para>
    /// <c>#drawio-save-round</c> is rendered by the page on every render, outside the report's
    /// <c>@if</c>, and grows by one at the end of every save round. So it answers "did a save
    /// finish after my click?" - which is the question - instead of "does the page look
    /// different?", and it answers it even when the two rounds are indistinguishable. It also
    /// removes the window where the report element is momentarily absent: a counter that is
    /// always there cannot be read as null and then be found "different" from null by the report
    /// that was already on the page, which is the one mechanism that produces exactly the failure
    /// the reviewer measured.
    /// </para>
    /// </summary>
    public async Task SaveAsync()
    {
        // Read BEFORE the click, and with textContent - the same property the condition below
        // compares - so that "different" means different in one single spelling.
        var before = await _page.EvaluateAsync<string?>(
            "() => { const r = document.getElementById('drawio-save-round'); return r ? r.textContent : null; }");
        if (before is null)
            throw new InvalidOperationException(
                "La pagina non porta il contatore dei salvataggi (#drawio-save-round): o non e' l'Editor del " +
                "disegno, o il contatore e' sparito dal suo markup. Senza di lui questa attesa tornerebbe a " +
                "confrontare due testi, e due salvataggi possono finire con lo stesso testo.");

        var candidates = Frame.Locator("button.geEmbedBtn");
        await StepAsync(
            () => candidates.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ConditionCeiling }),
            "draw.io non ha disegnato nessun pulsante incorporato: la barra del salvataggio non e' comparsa");

        // Uno solo, e con scritto sopra un salvataggio: sono le due cose che rendono il clic qui
        // sotto un GESTO DI SALVATAGGIO e non un tiro a indovinare. Se draw.io un giorno ne
        // disegnasse due (basta cambiare noSaveBtn/noExitBtn/saveAndExit nell'indirizzo
        // dell'iframe), premere il primo proverebbe qualcos'altro senza dirlo.
        var count = await candidates.CountAsync();
        if (count != 1)
            throw new InvalidOperationException(
                $"draw.io disegna {count} pulsanti incorporati invece di uno solo: quale sia il salvataggio non e' " +
                "piu' deciso. Si e' mosso qualcosa nei parametri dell'iframe (noSaveBtn/noExitBtn/saveAndExit) " +
                "o in draw.io stesso.");

        var button = candidates.First;
        var label = (await button.InnerTextAsync()).Trim();
        if (!label.Contains("Save", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"L'unico pulsante incorporato di draw.io porta scritto \"{label}\", che non e' un salvataggio: " +
                "premerlo non proverebbe quello che questa prova crede di provare.");

        await button.ClickAsync();

        // Il contatore cresce di uno alla FINE del giro, subito prima del ridisegno che porta il
        // verbale: aspettare che cambi e' aspettare la fine del giro - non un tempo, e non un
        // testo che due salvataggi possono avere uguale.
        await WaitForConditionAsync(
            "() => { const r = document.getElementById('drawio-save-round');" +
            $" return !!r && r.textContent !== {JsonSerializer.Serialize(before)}; }}",
            $"il salvataggio non e' arrivato in fondo: il contatore dei salvataggi della pagina e' ancora a " +
            $"{before}. Il clic non ha raggiunto draw.io, il ponte non ha riportato il disegno, oppure il giro " +
            $"e' ancora in corso.");
    }

    /// <summary>
    /// Queue item 14: drops the page's circuit connection the way Blazor's own tests do
    /// (<c>Blazor._internal.forceCloseConnection</c>) and holds Blazor's reconnection back - its
    /// negotiate request - until <paramref name="whileDown"/> is done: whatever that does, it does with
    /// the connection down, and not in a race with a reconnection on localhost. Then lets the
    /// reconnection through; the circuit is the same one (the server keeps a dropped circuit for minutes).
    /// </summary>
    public async Task DropTheConnectionAsync(Func<Task> whileDown)
    {
        const string Negotiate = "**/_blazor/negotiate*";
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task HoldAsync(IRoute route)
        {
            asked.TrySetResult();
            await release.Task;
            await route.ContinueAsync();
        }

        await _page.RouteAsync(Negotiate, HoldAsync);
        try
        {
            await _page.EvaluateAsync("() => Blazor._internal.forceCloseConnection()");
            try
            {
                await asked.Task.WaitAsync(TimeSpan.FromMilliseconds(ConditionCeiling));
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $"Blazor never tried to reconnect after the connection was dropped (within {ConditionCeiling / 1000:0} seconds): " +
                    "no negotiate request reached the route that holds it.");
            }
            await whileDown();
        }
        finally
        {
            release.TrySetResult();
            await _page.UnrouteAsync(Negotiate, HoldAsync);
        }
    }

    /// <summary>
    /// Queue item 14: hands a save over to the bridge as draw.io does it - the message draw.io posts at
    /// its Save button (<c>{ event: 'save', xml }</c>), with draw.io's own drawing, posted from draw.io's
    /// frame, which is the only source the bridge listens to - and waits until the bridge holds its
    /// number. Not the button itself: while the connection is down Blazor covers the page with its
    /// reconnection dialog, and no click reaches draw.io.
    /// </summary>
    public async Task HandOverASaveAsDrawioDoesAsync()
    {
        var held = (await SavesTheBridgeHoldsAsync()).Length;
        await _page.EvaluateAsync(
            """
            async () => {
              window.__wcsBridge = await import('/_content/Wcs.WebEditor/drawio-bridge.js');
              const w = document.getElementById('drawio-frame').contentWindow;
              const xml = await new Promise((resolve) => w.Draw.loadPlugin((ui) => resolve(ui.getFileData(true))));
              new w.Function('m', "parent.postMessage(m, '*')")(JSON.stringify({ event: 'save', xml }));
            }
            """);
        await WaitForConditionAsync(
            "() => window.__wcsBridge.savesHandedOver().length > " + held,
            "the bridge never held the save handed over: the message did not reach it, or it was not taken as draw.io's");
    }

    /// <summary>
    /// Final review, C18: waits until the bridge holds no save handed over - the page has told it to let
    /// go of the ones nobody will ask for again. Reads the module <see cref="HandOverASaveAsDrawioDoesAsync"/>
    /// imported, the same instance the page's script runs in.
    /// </summary>
    public Task WaitUntilTheBridgeHoldsNoSaveAsync() => WaitForConditionAsync(
        "() => !!window.__wcsBridge && window.__wcsBridge.savesHandedOver().length === 0",
        "the bridge still holds a save handed over: the page did not tell it to let go");

    /// <summary>Queue item 14: the numbers of the saves the bridge has handed over and holds still - no answer came for them.</summary>
    public Task<long[]> SavesTheBridgeHoldsAsync() => _page.EvaluateAsync<long[]>(
        "async () => (await import('/_content/Wcs.WebEditor/drawio-bridge.js')).savesHandedOver()");

    /// <summary>
    /// Task 12: the next save's answer is lost in a drop of the connection, as a drop loses it. Blazor's
    /// own object that carries the bridge's calls to the page (<c>DotNet.DotNetObject</c>) is wrapped
    /// for one call of <c>OnDrawioSaved</c>: the call goes to the page as always, its round runs to the
    /// end on the server, and when the answer comes back it is kept here instead of reaching the bridge
    /// - which waits for it for good, as for an answer sent while the connection was down - and the
    /// connection is dropped the way Blazor's own tests drop it. Blazor then reconnects by itself, the
    /// same circuit. <see cref="TheLostSaveAnswerAsync"/> reads what the answer was.
    /// </summary>
    public Task LoseTheNextSaveAnswerInADropAsync() => StepAsync(
        () => _page.EvaluateAsync(
            """
            () => {
              const proto = DotNet.DotNetObject.prototype;
              const original = proto.invokeMethodAsync;
              window.__wcsLostSaveAnswer = undefined;
              proto.invokeMethodAsync = function (method, ...args) {
                const call = original.call(this, method, ...args);
                if (method !== 'OnDrawioSaved') return call;
                proto.invokeMethodAsync = original;
                return call.then((answer) => {
                  window.__wcsLostSaveAnswer = answer;
                  Blazor._internal.forceCloseConnection();
                  return new Promise(() => {});
                });
              };
            }
            """),
        "Blazor's call carrier could not be wrapped: DotNet.DotNetObject is not where Blazor put it");

    /// <summary>Task 12: waits until the save's answer that <see cref="LoseTheNextSaveAnswerInADropAsync"/> kept from the bridge came back, and returns it.</summary>
    public async Task<bool> TheLostSaveAnswerAsync()
    {
        await WaitForConditionAsync(
            "() => window.__wcsLostSaveAnswer !== undefined",
            "the save's answer never came back to the browser: the call did not reach the page, or its round never ended");
        return await _page.EvaluateAsync<bool>("() => window.__wcsLostSaveAnswer === true");
    }

    /// <summary>
    /// Task 12: reloads the page - the plant read again from the host - and passes the key gate. A
    /// "Leave site?" the reload raises is accepted: the operator chose to reopen the plant.
    /// </summary>
    public async Task ReopenThePlantAsync(string apiKey)
    {
        void Accept(object? sender, IDialog dialog) => _ = dialog.AcceptAsync();
        _page.Dialog += Accept;
        try
        {
            await _page.ReloadAsync();
        }
        finally
        {
            _page.Dialog -= Accept;
        }
        await PassTheKeyGateAsync(apiKey);
    }

    /// <summary>The text of the page's save counter (<c>#drawio-save-round</c>): it changes at the end of every save round.</summary>
    public Task<string?> SaveRoundAsync() => TextOrNull("#drawio-save-round");

    /// <summary>
    /// Queue item 8: whether draw.io's own Save button can be reached by the mouse, told the way a
    /// hand would find it - what is under the middle of the button, asked of draw.io's document
    /// (<c>elementFromPoint</c>) and of the page's (the iframe, at the same spot on the screen), with
    /// the page scrolled as Playwright would scroll it to click there. Read without clicking: a
    /// covered button is reported with what covers it, not as a click that timed out.
    /// </summary>
    public async Task<DrawioSaveButtonReach> SaveButtonReachAsync()
    {
        var button = Frame.Locator("button.geEmbedBtn");
        await StepAsync(
            () => button.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ConditionCeiling }),
            "draw.io non ha disegnato nessun pulsante incorporato: la barra del salvataggio non e' comparsa");
        // Where a click would bring the button: inside the window, if the page can scroll. Not a
        // click, and not an actionability check - just the scroll, so the reading below is of the
        // spot the hand would reach.
        await button.First.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = ConditionCeiling });
        return await _page.EvaluateAsync<DrawioSaveButtonReach>(
            """
            () => {
              const describe = e => {
                if (!e) return '(nothing)';
                const w = e.closest ? e.closest('.mxWindow') : null;
                const title = w ? (w.querySelector('.mxWindowTitle') || {}).textContent || '' : '';
                return e.tagName.toLowerCase() + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : '') +
                  (w ? ' inside the draw.io window "' + title.trim() + '"' : '');
              };
              const frame = document.getElementById('drawio-frame');
              const doc = frame.contentDocument;
              const b = doc.querySelector('button.geEmbedBtn');
              const r = b.getBoundingClientRect();
              const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
              const inside = doc.elementFromPoint(cx, cy);
              const f = frame.getBoundingClientRect();
              const outside = document.elementFromPoint(f.left + cx, f.top + cy);
              // draw.io's own floating windows on screen: the outer element of each (mxWindow puts the
              // class on a table inside its div as well).
              const windows = Array.from(doc.querySelectorAll('.mxWindow'))
                .filter(w => !(w.parentElement && w.parentElement.closest('.mxWindow')))
                .filter(w => w.style.display !== 'none' && w.getClientRects().length > 0)
                .map(w => { const q = w.getBoundingClientRect();
                  return { title: ((w.querySelector('.mxWindowTitle') || {}).textContent || '').trim(),
                    x: Math.round(q.left), y: Math.round(q.top), width: Math.round(q.width), height: Math.round(q.height) }; });
              const body = document.querySelector('.drawio-body').getBoundingClientRect();
              // Everything else in the page's column, whether or not it still fits in it: from the
              // column's top to the bottom of its last strip, less the drawing.
              const shellElement = document.querySelector('.drawio-shell');
              const last = shellElement.lastElementChild;
              const contentHeight = last.getBoundingClientRect().bottom + parseFloat(getComputedStyle(last).marginBottom)
                - shellElement.getBoundingClientRect().top;
              const open = Array.from(document.querySelectorAll('.drawio-sections > .drawio-window'))
                .filter(w => !w.hidden);
              return {
                openWindows: open.map(w => w.id),
                insideTheFrame: !!inside && (inside === b || b.contains(inside)),
                onThePage: outside === frame,
                underTheButton: describe(inside),
                onThePageAtTheButton: describe(outside),
                button: [r.left, r.top, r.width, r.height].map(Math.round).join(','),
                frameTop: Math.round(f.top),
                frameHeight: Math.round(f.height),
                bodyHeight: Math.round(body.height),
                windowRoom: Math.round(window.innerHeight - (contentHeight - body.height)),
                pageScrollHeight: document.scrollingElement.scrollHeight,
                windowHeight: window.innerHeight,
                drawioWindows: windows,
              };
            }
            """);
    }

    /// <summary>Waits for a section's floating window (its id, the section's) to be open or shut.</summary>
    public Task WaitForTheSectionAsync(string windowId, bool open) => WaitForConditionAsync(
        $"() => {{ const d = document.getElementById({JsonSerializer.Serialize(windowId)}); return !!d && d.hidden === {(open ? "false" : "true")}; }}",
        $"the window of the section \"{windowId}\" never became {(open ? "open" : "shut")}");

    /// <summary>
    /// Aspetta che il verbale dell'ULTIMO RILASCIO compaia: e' quello che dice che cosa e' nato
    /// sull'impianto e che cosa no. Aspettare lui e' aspettare la fine del giro - permesso, motore,
    /// due scritture e la rilettura - non un tempo.
    /// </summary>
    public Task WaitForTheCreationReportAsync() => WaitForConditionAsync(
        "() => !!document.getElementById('drawio-creation-report')",
        "il rilascio non ha lasciato nessun verbale della creazione: il ponte non ha sentito l'aggiunta, " +
        "oppure la pagina e' rimasta ferma");

    /// <summary>Aspetta che il giro di creazione abbia restituito la cintura di scrittura.</summary>
    public Task WaitForCreationIdleAsync() => WaitForConditionAsync(
        "() => { const state = document.getElementById('drawio-creation-state');" +
        " return !!state && state.textContent.trim() === 'idle'; }",
        "the plant reached the expected item count, but the creation writer never returned to idle");

    // ---- The reports of the save and of the drop ------------------------------------------------
    //
    // Since the passata grafica (compito 3, 29/9) each report is in the window behind the "Details" of
    // its line in the row under the bar, shut unless somebody opened it. Every helper below reads it as
    // the page holds it - textContent, by the ids and classes of before - and none of them opens the
    // window: a test that reads a report is asking what the save or the drop came to, not whether the
    // operator looked at it. What is laid out (innerText) is the business of the tests that look at
    // the row itself.

    /// <summary>Le righe "Created:" del verbale del rilascio: un dispositivo nato, per riga.</summary>
    public Task<IReadOnlyList<string>> CreatedAsync() => TextContentLinesOf(".drawio-created");

    /// <summary>Le righe di cio' che il rilascio NON ha creato, con il perche'.</summary>
    public Task<IReadOnlyList<string>> NotCreatedAsync() => TextContentLinesOf(".drawio-not-created");

    /// <summary>Il motivo che copre l'intero rilascio, quando ce n'e' uno: permesso, motore, impianto non caricato.</summary>
    public Task<string?> CreationErrorAsync() => TextContentOfAsync("#drawio-creation-error");

    /// <summary>Tutto il verbale del rilascio in chiaro, una riga per voce, per un messaggio di fallimento che si legga.</summary>
    public Task<string> CreationReportAsync() => ReportLinesAsync("drawio-creation-report", "(nessun verbale della creazione)");

    /// <summary>Il messaggio di errore del salvataggio, o nullo se non ce n'e' stato.</summary>
    public Task<string?> SaveErrorAsync() => TextContentOfAsync("#drawio-save-error");

    /// <summary>Il riassunto del salvataggio, o nullo se manca.</summary>
    public Task<string?> SaveSummaryAsync() => TextContentOfAsync("#drawio-save-summary");

    /// <summary>Le righe "Scritto:" del verbale.</summary>
    public Task<IReadOnlyList<string>> WrittenAsync() => TextContentLinesOf(".drawio-written");

    /// <summary>Le righe dei rifiuti: ogni cosa che il salvataggio ha scelto di non scrivere, nominandola.</summary>
    public Task<IReadOnlyList<string>> RefusalsAsync() => TextContentLinesOf(".drawio-refusal");

    /// <summary>Le righe di cio' che non e' stato scritto perche' il salvataggio si e' interrotto.</summary>
    public Task<IReadOnlyList<string>> NotWrittenAsync() => TextContentLinesOf(".drawio-not-written");

    /// <summary>Le righe delle scritture partite e rimaste senza risposta.</summary>
    public Task<IReadOnlyList<string>> UnconfirmedAsync() => TextContentLinesOf(".drawio-unconfirmed");

    /// <summary>Le avvertenze del verbale: scelte fatte al posto di chi disegna, dichiarate.</summary>
    public Task<IReadOnlyList<string>> NoticesAsync() => TextContentLinesOf(".drawio-notice");

    /// <summary>Tutto il verbale in chiaro, una riga per voce, per un messaggio di fallimento che si legga.</summary>
    public Task<string> ReportAsync() => ReportLinesAsync("drawio-save-report", "(nessun verbale)");

    /// <summary>The text of every element <paramref name="selector"/> finds, as the page holds it (textContent), trimmed.</summary>
    private async Task<IReadOnlyList<string>> TextContentLinesOf(string selector) =>
        (await _page.Locator(selector).AllTextContentsAsync()).Select(t => t.Trim()).ToList();

    /// <summary>
    /// A report read whole for a failure message: the text of each of its paragraphs and entries, one per
    /// line - the textContent of the report itself would run them into one another.
    /// </summary>
    private async Task<string> ReportLinesAsync(string reportId, string none) =>
        await _page.EvaluateAsync<string?>(
            "(id) => { const r = document.getElementById(id);" +
            " return r ? Array.from(r.querySelectorAll('p, li'), (e) => e.textContent.trim()).join('\\n') : null; }",
            reportId)
        ?? none;

    /// <summary>
    /// The lines of the row of outcomes under the bar that are on screen, top to bottom, by their ids
    /// (<c>{id}-notice</c>): the ones hidden behind "+N more" are left out. What the browser lays out
    /// (<c>checkVisibility</c>), not the <c>hidden</c> attribute: a line's own display outweighs the
    /// browser's rule for [hidden], and only the page's stylesheet hides it.
    /// </summary>
    public Task<string[]> OutcomeLinesAsync() => _page.EvaluateAsync<string[]>(
        "() => Array.from(document.querySelectorAll('#drawio-notices > .drawio-notices-line')).filter((l) => l.checkVisibility()).map((l) => l.id)");

    /// <summary>
    /// What the browser hits at the middle of the page's element <paramref name="selector"/> finds
    /// (<c>elementFromPoint</c>, the same test a click makes): whether it is that element or inside it.
    /// </summary>
    public async Task<DrawioStripButtonHit> HitAtTheMiddleOfAsync(string selector) =>
        await _page.EvaluateAsync<DrawioStripButtonHit?>(
            """
            (s) => {
              __DESCRIBE__
              const b = document.querySelector(s);
              if (!b) return null;
              const r = b.getBoundingClientRect();
              const x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y);
              return { id: b.id, x, y, hitsIt: !!hit && (hit === b || b.contains(hit)), hit: describe(hit) };
            }
            """.Replace("__DESCRIBE__", DescribeAnElement),
            selector)
        ?? throw new InvalidOperationException($"Nothing on the page matches {selector}, so nothing can be hit at its middle.");

    public async ValueTask DisposeAsync()
    {
        await _page.CloseAsync();
        await _context.CloseAsync();
    }

    /// <summary>
    /// Closes the tab the way the operator does - with the page's <c>beforeunload</c> handlers run -
    /// and says whether the browser asked "Leave site?" first. When it asks, the question is
    /// REFUSED, which is the operator changing their mind: the page must then still be there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both answers wait for an event that settles them, never for a time.</b> Playwright's
    /// <c>CloseAsync</c> with <c>RunBeforeUnload</c> returns at once, before the handlers have run,
    /// so "no dialog yet" means nothing. What settles it is whichever comes first of the page's
    /// <c>Dialog</c> and its <c>Close</c>: a page that closed has run every <c>beforeunload</c>
    /// handler it had - its own and draw.io's inside the iframe - and none of them asked. A test that
    /// only waited a while and saw no dialog would pass on a question that came a moment later.
    /// </para>
    /// <para>
    /// With a <c>Dialog</c> handler registered Playwright no longer dismisses dialogs by itself, so
    /// the one heard here is dismissed explicitly below.
    /// </para>
    /// </remarks>
    public async Task<DrawioTabClosing> CloseTheTabAsync()
    {
        var asked = new TaskCompletionSource<IDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDialog(object? sender, IDialog dialog) => asked.TrySetResult(dialog);
        void OnClose(object? sender, IPage page) => closed.TrySetResult();

        _page.Dialog += OnDialog;
        _page.Close += OnClose;
        try
        {
            await _page.CloseAsync(new PageCloseOptions { RunBeforeUnload = true });
            var first = await Task.WhenAny(asked.Task, closed.Task, Task.Delay(ConditionCeiling));

            if (first == closed.Task) return new DrawioTabClosing(Asked: false, DialogType: null, StillOpen: false);

            if (first != asked.Task)
                throw new TimeoutException(
                    $"Closing the tab ended in neither a question nor a closed page within {ConditionCeiling} ms: " +
                    "the browser did not run the close at all, so this measures nothing.");

            var dialog = asked.Task.Result;
            var type = dialog.Type;
            await dialog.DismissAsync();

            // Refused: the page must still answer. An evaluation is a round trip into it, and it
            // throws if the page went away after all.
            var stillOpen = !_page.IsClosed
                && await _page.EvaluateAsync<bool>("() => !!document.getElementById('drawio-frame')");
            return new DrawioTabClosing(Asked: true, DialogType: type, StillOpen: stillOpen);
        }
        finally
        {
            _page.Dialog -= OnDialog;
            _page.Close -= OnClose;
        }
    }

    /// <summary>
    /// Subscribes, <b>from the parent page</b>, to draw.io's own selection, and returns once the
    /// subscription is really in place. From then on <see cref="SelectedCellsAsync"/> tells what the
    /// operator has selected inside the iframe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all.</b> The embedding protocol - the nineteen JSON events draw.io
    /// posts to its parent - has no event for the selection. Measured: the parent does not need one,
    /// because the iframe is served by our own host and is therefore <b>same origin</b>, and draw.io
    /// hands any caller its own <c>EditorUi</c> through the entry point it opens for plugins:
    /// <c>window.Draw.loadPlugin(fn)</c> calls <c>fn(ui)</c> - at once if the app is already up,
    /// otherwise as soon as it is. From <c>ui.editor.graph</c> the selection is an ordinary mxGraph
    /// subscription.
    /// </para>
    /// <para>
    /// <b>Asking early does not disturb draw.io.</b> Measured: <c>window.Draw</c> is there at 266 ms
    /// - after the <c>configure</c> event, long before <c>init</c> at 1066 ms - and a callback asked
    /// for then is QUEUED (<c>App.DrawPlugins.length</c> becomes 1) and runs at 1043 ms, with
    /// <c>ui.initEmbedDone</c> already true. So the embedding handshake is not hijacked by our
    /// presence in that queue: <c>init</c> and <c>load</c> still arrive, the plant still gets drawn,
    /// and the save still works. The one visible trace is <c>App.embedModePluginsCount</c> ending at
    /// -1, which draw.io only ever compares with <c>0&lt;</c>.
    /// </para>
    /// <para>
    /// <b>This is a measurement, not a hook.</b> Nothing in the product is arranged for it: it is
    /// the very path a properties panel living next to the iframe would have to take, and this test
    /// is here so that a draw.io upgrade that took the path away is read here as a red test instead
    /// of being discovered when the panel is half written.
    /// </para>
    /// </remarks>
    public async Task WatchSelectionAsync()
    {
        // draw.io defines window.Draw while app.min.js runs, which is well before it says 'init'.
        // Polling for it is what makes this safe to call the moment the iframe exists.
        await WaitForConditionAsync(
            "() => { const f = document.getElementById('drawio-frame');" +
            " try { return !!(f && f.contentWindow && f.contentWindow.Draw && f.contentWindow.Draw.loadPlugin); }" +
            " catch (e) { return false; } }",
            "draw.io never exposed window.Draw.loadPlugin in the iframe: the only way the parent has of knowing " +
            "what is selected is gone");

        await StepAsync(
            () => _page.EvaluateAsync(
                """
                () => new Promise((resolve, reject) => {
                  const w = document.getElementById('drawio-frame').contentWindow;
                  window.__wcsSelection = [];
                  const timer = setTimeout(() => reject(new Error('Draw.loadPlugin never called back')), 30000);
                  w.Draw.loadPlugin(function (ui) {
                    clearTimeout(timer);
                    const graph = ui.editor.graph;
                    graph.getSelectionModel().addListener(w.mxEvent.CHANGE, function () {
                      window.__wcsSelection = graph.getSelectionCells().map(function (c) {
                        const v = c.value;
                        return {
                          id: c.id,
                          label: (v != null && v.getAttribute) ? v.getAttribute('label') : String(v),
                          wcs: (v != null && v.getAttribute) ? v.getAttribute('wcs') : null
                        };
                      });
                    });
                    resolve();
                  });
                })
                """),
            "the subscription to draw.io's selection could not be installed");
    }

    /// <summary>
    /// Changes one of our attributes on one cell of the real mxGraph model, the way the operator
    /// does it through draw.io's own "Edit Data" dialog - and gives back what the model holds
    /// afterwards, so that a gesture that did not take is read as a gesture that did not take
    /// instead of as a plant that did not change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not the dialog.</b> "Edit Data" is a modal of draw.io's own, with its own field names
    /// and its own OK button: driving it would measure draw.io's dialog, and would go red the day
    /// that dialog is restyled - which is not what row 20 is about. The model is the same place the
    /// dialog writes into, and it is the place the save reads from.
    /// </para>
    /// <para>
    /// The value of a marked cell is an XML element, not a string, and mxGraph will not notice an
    /// attribute changed in place: it is CLONED, changed, and set through the model inside an
    /// update, which is exactly what draw.io's own <c>mxCellAttributeChange</c> does. Without the
    /// clone the canvas keeps showing the old data and - worse for a test - the change may or may
    /// not be in the XML that comes back, depending on nothing in particular.
    /// </para>
    /// </remarks>
    public async Task<string?> SetCellAttributeAsync(string cellId, string attribute, string value)
    {
        await WaitForConditionAsync(
            "() => { const f = document.getElementById('drawio-frame');" +
            " try { return !!(f && f.contentWindow && f.contentWindow.Draw && f.contentWindow.Draw.loadPlugin); }" +
            " catch (e) { return false; } }",
            "draw.io never exposed window.Draw.loadPlugin in the iframe: the drawing's model cannot be reached");

        return await _page.EvaluateAsync<string?>(
            """
            (args) => new Promise((resolve, reject) => {
              const w = document.getElementById('drawio-frame').contentWindow;
              const timer = setTimeout(() => reject(new Error('Draw.loadPlugin never called back')), 30000);
              w.Draw.loadPlugin(function (ui) {
                clearTimeout(timer);
                try {
                  const model = ui.editor.graph.getModel();
                  const cell = model.getCell(args.id);
                  if (!cell) { reject(new Error('the drawing has no cell with id ' + args.id)); return; }
                  const current = cell.value;
                  if (current == null || !current.setAttribute) {
                    reject(new Error('cell ' + args.id + ' carries no attributes: its value is ' + String(current)));
                    return;
                  }
                  const next = current.cloneNode(true);
                  next.setAttribute(args.attribute, args.value);
                  model.beginUpdate();
                  try { model.setValue(cell, next); } finally { model.endUpdate(); }
                  const after = model.getCell(args.id).value;
                  resolve(after && after.getAttribute ? after.getAttribute(args.attribute) : null);
                } catch (e) { reject(e); }
              });
            })
            """,
            new { id = cellId, attribute, value });
    }

    /// <summary>
    /// What this embedded draw.io offers for carrying a drawing away, as facts rather than as
    /// belief: whether the word "Extras" is anywhere in its document, and what shortcut each of the
    /// named actions declares. <c>(missing)</c> for an action that does not exist at all,
    /// <c>(none)</c> for one that exists and declares no shortcut - which is the same as
    /// unreachable, with no menu bar to find it in.
    /// </summary>
    /// <remarks>
    /// It is here because the page's own messages name a road out of the drawing, and naming one
    /// that does not exist has already happened four times on this branch. A message can be checked
    /// against this.
    /// </remarks>
    /// <remarks>
    /// One <c>key=value</c> line per fact rather than a dictionary: a plain JS object comes back
    /// from this bridge as an EMPTY dictionary - measured, 2026-09-19 - and a test reading an empty
    /// dictionary with <c>TryGetValue</c> would have been green while measuring nothing at all.
    /// </remarks>
    public Task<string[]> TheRoadsOutOfTheDrawingAsync() => InTheGraphAsync<string[]>(
        """
        const doc = ui.editor.graph.container.ownerDocument;
        const found = [];
        found.push('menubar=' + String(ui.menubar));
        found.push('format=' + String(ui.format));
        found.push('the word Extras in the document=' + String(String(doc.body.textContent || '').indexOf('Extras') >= 0));
        for (const name of ['editDiagram', 'editStyle', 'selectAll', 'copy', 'export']) {
          let action = null;
          try { action = ui.actions.get(name); } catch (e) { action = null; }
          found.push(name + '=' + (action ? String(action.shortcut == null ? '(none)' : action.shortcut) : '(missing)'));
        }
        return found;
        """);

    /// <summary>
    /// Puts a cell with no mark of ours on the canvas - a note, which is what "the operator's free
    /// drawing" is made of - and gives back the identifier draw.io chose for it.
    /// </summary>
    /// <remarks>
    /// Through the model and not by dragging draw.io's own "note" shape out of its General section:
    /// that section is born closed in this embed (see <c>DrawioLibrary</c>'s remarks), so the drag
    /// would measure the panel's fold-out rather than what is being asked here. The cell is the same
    /// either way - an unmarked vertex on the default layer - and it is the cell, not the way it got
    /// there, that the measurement is about.
    /// </remarks>
    /// <summary>
    /// Puts on the canvas a SHAPE that the drawing marks as ours - a vertex whose value is an XML
    /// node carrying <c>wcs=</c>. It is what a Ctrl+V of hand-edited XML produces, and it is a cell
    /// that says it is a junction of ours and is not a line at all.
    /// </summary>
    /// <remarks>
    /// Built here and not through <see cref="SetCellAttributeAsync"/>, which needs the cell's value
    /// to be an XML node already: a note inserted with a plain string label carries no attributes
    /// and that call rejects on it.
    /// </remarks>
    public Task<string> PutAMarkedShapeOnTheCanvasAsync(string mark) => InTheGraphAsync<string>(
        """
        const graph = ui.editor.graph;
        const model = graph.getModel();
        // THROUGH draw.io's OWN setAttributeForCell, and not by handing insertVertex a node built
        // here: a node from a foreign XML document stays in the model - measured, the guard below
        // was green on it - but the drawing draw.io hands back carries a bare <mxCell> with no
        // mark at all, so the save would never see the cell this test is about. This is the call
        // draw.io makes itself when the operator edits a cell's data.
        let cell = null;
        model.beginUpdate();
        try
        {
          cell = graph.insertVertex(graph.getDefaultParent(), null,
            'a shape of ours that is not a line', 60, 760, 200, 80, 'rounded=0;');
          graph.setAttributeForCell(cell, 'wcs', args);
        }
        finally { model.endUpdate(); }
        const after = model.getCell(cell.id);
        if (!after) throw new Error('draw.io did not keep the marked shape in its model');
        const kept = after.value && after.value.getAttribute ? after.value.getAttribute('wcs') : null;
        if (kept !== args)
          throw new Error('draw.io did not keep the mark on the shape: its value is ' + String(after.value)
            + ' and wcs reads ' + String(kept));
        return String(cell.id);
        """,
        mark);

    /// <summary>Deletes one cell, which is what the operator does when he selects a line and presses Canc.</summary>
    public Task<int> DeleteTheCellAsync(string cellId) => InTheGraphAsync<int>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell with id ' + args);
        graph.removeCells([cell], true);
        return graph.getModel().getCell(args) ? 1 : 0;
        """,
        cellId);

    /// <summary>
    /// A line of the OPERATOR'S own between two cells: a plain string value, no mark, no head -
    /// what the free drawing is made of. Through the model, for the reason
    /// <see cref="DrawAnArrowAsync"/> gives; the line's identifier comes back.
    /// </summary>
    public Task<string> DrawAFreeLineAsync(string sourceCellId, string targetCellId) => InTheGraphAsync<string>(
        """
        const graph = ui.editor.graph;
        const model = graph.getModel();
        const source = model.getCell(args.source);
        const target = model.getCell(args.target);
        if (!source) throw new Error('the drawing has no cell with id ' + args.source);
        if (!target) throw new Error('the drawing has no cell with id ' + args.target);
        let edge = null;
        model.beginUpdate();
        try { edge = graph.insertEdge(graph.getDefaultParent(), null, '', source, target, 'endArrow=none;html=1;'); }
        finally { model.endUpdate(); }
        if (!edge || !model.getCell(edge.id)) throw new Error('draw.io did not keep the line in its model');
        return String(edge.id);
        """,
        new { source = sourceCellId, target = targetCellId });

    /// <summary>
    /// Where a line's two ends are, as draw.io's model and view hold them: the cell each end hangs
    /// from (or "(none)"), the loose point each end has in its geometry (or "(none)"), and whether
    /// the view has a state for the line with its points worked out - an edge whose end has neither
    /// a cell nor a point has none, and draw.io draws nothing for it.
    /// </summary>
    public Task<string[]> EdgeEndsAsync(string cellId) => InTheGraphAsync<string[]>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args);
        if (!cell) return ['(no such cell)'];
        const geo = cell.geometry;
        const point = (p) => p ? (Math.round(p.x) + ',' + Math.round(p.y)) : '(none)';
        const state = graph.getView().getState(cell);
        return [
          cell.source && cell.source.id != null ? String(cell.source.id) : '(none)',
          cell.target && cell.target.id != null ? String(cell.target.id) : '(none)',
          point(geo ? geo.getTerminalPoint(true) : null),
          point(geo ? geo.getTerminalPoint(false) : null),
          state && state.absolutePoints && state.absolutePoints.length >= 2 && state.absolutePoints.every(p => p != null)
            ? 'drawn' : 'not drawn'
        ];
        """,
        cellId);

    /// <summary>
    /// Where the view draws a line, point by point, in the graph's coordinates (the view's scale and
    /// translation taken back off): the first and the last are where its two ends meet what they hang
    /// from. Empty when the view has no state for it.
    /// </summary>
    public Task<string[]> EdgePointsAsync(string cellId) => InTheGraphAsync<string[]>(
        """
        const graph = ui.editor.graph;
        const view = graph.getView();
        const cell = graph.getModel().getCell(args);
        if (!cell) return ['(no such cell)'];
        const state = view.getState(cell);
        if (!state || !state.absolutePoints) return [];
        return state.absolutePoints.map(p => p == null ? '(null)'
          : (Math.round(p.x / view.scale - view.translate.x) + ',' + Math.round(p.y / view.scale - view.translate.y)));
        """,
        cellId);

    /// <summary>The Delete key, as the operator presses it on the selection: draw.io's "delete" action, which takes the cells and not their connections.</summary>
    public Task PressDeleteAsync() => _page.Keyboard.PressAsync("Delete");

    public Task<string> PutAFreeNoteOnTheCanvasAsync(string label) => InTheGraphAsync<string>(
        """
        const graph = ui.editor.graph;
        const model = graph.getModel();
        let cell = null;
        model.beginUpdate();
        try { cell = graph.insertVertex(graph.getDefaultParent(), null, args, 40, 600, 220, 60, 'shape=note;'); }
        finally { model.endUpdate(); }
        if (!cell || !model.getCell(cell.id)) throw new Error('draw.io did not keep the note in its model');
        return String(cell.id);
        """,
        label);

    /// <summary>
    /// Row 37, fix round 2: sets a cell's OWN visibility flag directly on draw.io's model
    /// (<c>model.setVisible</c>), bypassing every window and every action - the same call
    /// <c>model.setVisible(layer1, false)</c> the parity inventory's own measurements use for the
    /// default layer, generalised to any cell so a test can reach it for a marked cell, an unmarked
    /// one, or the layer itself without going through a UI gesture at all.
    /// </summary>
    public Task SetCellModelVisibleAsync(string cellId, bool visible) => InTheGraphAsync<bool>(
        """
        const graph = ui.editor.graph;
        const model = graph.getModel();
        const cell = model.getCell(args.id);
        if (!cell) throw new Error('the drawing has no cell ' + args.id);
        model.beginUpdate();
        try { model.setVisible(cell, args.visible); } finally { model.endUpdate(); }
        return true;
        """,
        new { id = cellId, visible });

    /// <summary>
    /// Row 37, fix round 2: adds a tag to a cell through draw.io's own <c>addTagsForCells</c> (the
    /// same call the Tags window's own "add" checkbox makes), so a test can then hide it with
    /// <see cref="SetHiddenTagsAsync"/> without ever opening that window.
    /// </summary>
    public Task AddTagToCellAsync(string cellId, string tag) => InTheGraphAsync<bool>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args.id);
        if (!cell) throw new Error('the drawing has no cell ' + args.id);
        graph.addTagsForCells([cell], [args.tag]);
        return true;
        """,
        new { id = cellId, tag });

    /// <summary>
    /// Row 37, fix round 2: sets draw.io's <c>hiddenTags</c> directly (<c>graph.setHiddenTags</c>) -
    /// the same field Edit Link's <c>{"tags":{"hidden":[...]}}</c> custom action writes, and the
    /// same field the (now closed) Tags window's eye icon writes - without going through either.
    /// </summary>
    public Task SetHiddenTagsAsync(params string[] tags) => InTheGraphAsync<bool>(
        """
        ui.editor.graph.setHiddenTags(args);
        ui.editor.graph.refresh();
        return true;
        """,
        tags);

    /// <summary>
    /// Whether ANY cell - marked or not - is actually RENDERED on screen right now, the same
    /// computation <see cref="MarkedCellVisibilityAsync"/> makes for marked cells alone: a shape node
    /// in the DOM whose computed style is neither <c>display:none</c> nor <c>visibility:hidden</c>.
    /// </summary>
    public Task<bool> CellRenderedAsync(string cellId) => InTheGraphAsync<bool>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell ' + args);
        const state = graph.getView().getState(cell);
        const node = state && state.shape && state.shape.node;
        return !!node && w.getComputedStyle(node).display !== 'none' && w.getComputedStyle(node).visibility !== 'hidden';
        """,
        cellId);

    /// <summary>
    /// Row 37, fix round 3 (the second belt's own test): calls draw.io's own
    /// <c>graph.customLinkClicked</c> DIRECTLY with a <c>"data:action/json,..."</c> link carrying a
    /// <c>"hide"</c> action for the given cell - the same call a real click on such a link makes
    /// (<c>EditorUi.prototype.handleCustomLink</c> -&gt; <c>Graph.prototype.handleCustomLink</c> -&gt;
    /// <c>executeCustomActions</c>) - without needing the link to be an actual rendered
    /// <c>&lt;a&gt;</c> in the DOM. Gives back the cell's OWN <c>model.isVisible</c> flag straight
    /// after: with <c>ignoreCustomActionLinks</c> in place this must stay <c>true</c>, because
    /// <c>handleCustomLink</c> is a no-op and <c>setCellsVisible</c> is never reached.
    /// </summary>
    public Task<bool> InvokeHideCustomLinkAsync(string cellId) => InTheGraphAsync<bool>(
        """
        const graph = ui.editor.graph;
        const model = graph.getModel();
        const cell = model.getCell(args);
        if (!cell) throw new Error('the drawing has no cell ' + args);
        const link = 'data:action/json,' + JSON.stringify({ actions: [{ hide: { cells: [String(cell.id)] } }] });
        graph.customLinkClicked(link, cell);
        return model.isVisible(cell);
        """,
        cellId);

    /// <summary>
    /// Row 37, fix round 4: groups TWO OR MORE cells and hides the new group, as ONE atomic edit -
    /// not two - through draw.io's own <c>graph.groupCells</c> (the same call Ctrl+G ultimately
    /// makes, see <see cref="GroupSelectionAsync"/>) wrapped in an OUTER
    /// <c>model.beginUpdate()/endUpdate()</c>: mxGraphModel coalesces nested update calls into a
    /// single top-level edit, so <c>model.setVisible(group, false)</c> lands inside the SAME edit
    /// <c>groupCells</c> makes, and only ONE <c>mxEvent.CHANGE</c> fires when the OUTER
    /// <c>endUpdate</c> runs. Gives back the new group's id.
    /// </summary>
    /// <remarks>
    /// TWO OR MORE, not one: MEASURED in the bundle, <c>mxGraph.prototype.groupCells</c> only does
    /// anything at all when <c>1&lt;cells.length</c> - handed a single cell it returns the
    /// auto-created group WITHOUT ever adding it to the model or reparenting anything, a no-op that
    /// looks like success (it returns a truthy cell) and is not.
    /// </remarks>
    public Task<string> GroupAndHideInOneEditAsync(params string[] cellIds) => InTheGraphAsync<string>(
        """
        const graph = ui.editor.graph;
        const model = graph.getModel();
        const cells = args.map(function (id) {
          const cell = model.getCell(id);
          if (!cell) throw new Error('the drawing has no cell ' + id);
          return cell;
        });
        let group = null;
        model.beginUpdate();
        try {
          group = graph.groupCells(null, 0, cells);
          model.setVisible(group, false);
        } finally {
          model.endUpdate();
        }
        if (!group || model.getParent(cells[0]) !== group)
          throw new Error('graph.groupCells did not actually group the cells (need at least two)');
        return String(group.id);
        """,
        cellIds);

    /// <summary>
    /// The operator's two keystrokes for carrying the drawing away: a click on the canvas to put the
    /// keyboard focus inside draw.io, then <b>Ctrl+A</b> and <b>Ctrl+C</b>. Gives back how many
    /// cells ended up selected and what really landed on the <b>system</b> clipboard, url-decoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Real keystrokes and the real clipboard.</b> This is the road two of the page's messages
    /// name, and those messages have been wrong before: an assertion on what an action object
    /// declares would be the same kind of claim that sent the operator to a menu that is not there.
    /// The clipboard is written with a marker first, so that "nothing happened" cannot read as a
    /// success on whatever some earlier test left behind.
    /// </para>
    /// <para>
    /// draw.io puts its own url-encoded <c>&lt;mxGraphModel&gt;</c> on the clipboard, which is what
    /// another draw.io takes back with Ctrl+V. It is decoded here so that whoever reads a failure
    /// reads XML instead of percent signs.
    /// </para>
    /// </remarks>
    public async Task<(int Selected, string Clipboard)> SelectEverythingAndCopyAsync(int expectedSelectionCount)
    {
        await _context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await ClickEmptyCanvasAsync();

        await _page.Keyboard.PressAsync("Control+a");
        var selectionCeiling = DateTime.UtcNow.AddMilliseconds(ConditionCeiling);
        var selected = await InTheGraphAsync<int>("return ui.editor.graph.getSelectionCount();");
        while (selected != expectedSelectionCount && DateTime.UtcNow < selectionCeiling)
        {
            await Task.Delay(SettleInterval);
            selected = await InTheGraphAsync<int>("return ui.editor.graph.getSelectionCount();");
        }
        if (selected != expectedSelectionCount)
            throw new InvalidOperationException(
                $"Ctrl+A selected {selected.ToString(CultureInfo.InvariantCulture)} draw.io cells instead of " +
                expectedSelectionCount.ToString(CultureInfo.InvariantCulture));
        await WaitForDrawioToPutItsClipboardElementAwayAsync();

        await PutOnTheSystemClipboardAsync(ClipboardMarker);
        await _page.Keyboard.PressAsync("Control+c");
        await WaitForDrawioToPutItsClipboardElementAwayAsync();
        await WaitForClipboardReplacementForTestAsync(ClipboardMarker);

        var text = await ReadSystemClipboardTextAsync();
        return (selected, Uri.UnescapeDataString(text));
    }

    /// <summary>
    /// Presses <b>Ctrl+E</b> as the operator does, after a click that puts the keyboard focus inside
    /// draw.io - the gesture two refusals of <c>DrawioDelta</c> name as the remedy for a mirrored
    /// cell.
    /// </summary>
    public async Task PressEditStyleShortcutAsync()
    {
        await _page.Keyboard.PressAsync("Control+e");
        await WaitForConditionAsync(
            "() => { const d = document.getElementById('drawio-frame').contentDocument;" +
            " return !!d && d.querySelectorAll('textarea').length > 0; }",
            "Ctrl+E non ha aperto nessun riquadro con un'area di testo: la strada che i rifiuti dello specchio indicano non esiste");
    }

    /// <summary>The name of the XML element a cell's value is, or what it is when it is not a node at all.</summary>
    public Task<string> ValueNodeNameAsync(string cellId) => InTheGraphAsync<string>(
        """
        const cell = ui.editor.graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell with id ' + args);
        const value = cell.value;
        if (value == null) return '(null)';
        return value.nodeName ? String(value.nodeName) : '(not a node: ' + String(value) + ')';
        """,
        cellId);

    /// <summary>One attribute of a cell's value node, read out of draw.io's own model.</summary>
    public Task<string> ValueAttributeAsync(string cellId, string attribute) => InTheGraphAsync<string>(
        """
        const cell = ui.editor.graph.getModel().getCell(args.id);
        if (!cell) throw new Error('the drawing has no cell with id ' + args.id);
        const value = cell.value;
        if (!value || !value.getAttribute) return '(the value carries no attributes)';
        const read = value.getAttribute(args.attribute);
        return read == null ? '(absent)' : String(read);
        """,
        new { id = cellId, attribute });

    /// <summary>
    /// Opens draw.io's own <b>Edit Data</b> on a cell and clicks Apply without changing anything,
    /// which is the least an operator can do in that dialog. Gives back an account of what was
    /// driven, so that a dialog that did not open reads as a dialog that did not open.
    /// </summary>
    public async Task<string> OpenEditDataAndApplyAsync(string cellId)
    {
        await ClickEmptyCanvasAsync();
        var selected = await SelectCellAsync(cellId);
        if (selected != 1)
            throw new InvalidOperationException($"La cella {cellId} non si e' selezionata ({selected} celle): Edit Data aprirebbe su un'altra cella.");

        await _page.Keyboard.PressAsync("Control+m");
        await WaitForConditionAsync(
            "() => { const d = document.getElementById('drawio-frame').contentDocument;" +
            " return !!d && Array.from(d.querySelectorAll('button')).some(b => (b.textContent || '').trim().toLowerCase() === 'apply'); }",
            "Ctrl+M non ha aperto nessun riquadro con un pulsante Apply: Edit Data non e' raggiungibile da qui");

        return await InTheGraphAsync<string>(
            """
            const dialog = ui.dialogs && ui.dialogs.length > 0 ? ui.dialogs[ui.dialogs.length - 1].container : null;
            if (!dialog) throw new Error('draw.io has no dialog open');
            const text = String(dialog.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 200);
            const buttons = Array.from(dialog.querySelectorAll('button'));
            const apply = buttons.find(b => (b.textContent || '').trim().toLowerCase() === 'apply');
            if (!apply) throw new Error('the dialog has no Apply button, only: ' + buttons.map(b => b.textContent).join(' / '));
            apply.click();
            return 'il riquadro diceva: "' + text + '", pulsanti: ' + buttons.map(b => (b.textContent || '').trim()).join(' / ');
            """);
    }

    /// <summary>
    /// Opens draw.io's own <b>Edit Data</b> on a cell, clicks the <b>bin</b> on the row whose value
    /// is <paramref name="value"/>, and applies. It is three gestures - Ctrl+M, one click, Apply -
    /// and it is how an operator takes one of our marks off a cell.
    /// </summary>
    /// <remarks>
    /// <b>The bin is an <c>&lt;a&gt;</c> with no text</b> (<c>class="geButton" title="Delete"</c>),
    /// which is why a search through <c>querySelectorAll('button')</c> and <c>textContent</c> does
    /// not find it: a measurement made with a tool that cannot see that control concluded the row
    /// could not be removed. It can. The row is found from its VALUE field and the bin is taken
    /// from the same row, so this cannot delete a different property than the one asked for.
    /// </remarks>
    public async Task<string> RemoveTheDataRowThroughEditDataAsync(string cellId, string value)
    {
        await ClickEmptyCanvasAsync();
        var selected = await SelectCellAsync(cellId);
        if (selected != 1)
            throw new InvalidOperationException($"La cella {cellId} non si e' selezionata ({selected} celle).");

        await _page.Keyboard.PressAsync("Control+m");
        await WaitForConditionAsync(
            "() => { const d = document.getElementById('drawio-frame').contentDocument;" +
            " return !!d && Array.from(d.querySelectorAll('button')).some(b => (b.textContent || '').trim().toLowerCase() === 'apply'); }",
            "Ctrl+M non ha aperto Edit Data");

        return await InTheGraphAsync<string>(
            """
            const dialog = ui.dialogs && ui.dialogs.length > 0 ? ui.dialogs[ui.dialogs.length - 1].container : null;
            if (!dialog) throw new Error('draw.io has no dialog open');
            const field = Array.from(dialog.querySelectorAll('input, textarea'))
              .find(i => String(i.value) === args.value);
            if (!field) throw new Error('no field in Edit Data holds "' + args.value + '"');

            let row = field.parentElement, bin = null;
            while (row && !bin) {
              bin = Array.from(row.querySelectorAll('a')).find(a => a.getAttribute('title') === 'Delete');
              if (!bin) row = row.parentElement;
            }
            if (!bin) throw new Error('the row holding "' + args.value + '" has no bin: Edit Data cannot remove it');

            bin.click();
            const apply = Array.from(dialog.querySelectorAll('button'))
              .find(b => (b.textContent || '').trim().toLowerCase() === 'apply');
            apply.click();
            return 'cestino della riga che valeva "' + args.value + '" premuto, poi Apply';
            """,
            new { id = cellId, value });
    }

    /// <summary>What the dialog draw.io has open carries in its text area - for Edit Style, the style of the selected cell.</summary>
    public Task<string> TheTextInTheDialogAsync() => InTheGraphAsync<string>(
        """
        const doc = ui.editor.graph.container.ownerDocument;
        const areas = doc.querySelectorAll('textarea');
        if (areas.length === 0) return '(no dialog with a text area is open)';
        return String(areas[areas.length - 1].value || '');
        """);

    /// <summary>
    /// The arrowheads draw.io really draws on this cell: the value of <c>endArrow</c> and
    /// <c>startArrow</c> in the style <b>after the stylesheet's defaults have been folded in</b>,
    /// which is the style the shape is painted from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is not the cell's own style string, and that is the whole point.</b> The cell draw.io
    /// builds for an arrow pulled with the mouse carries no <c>endArrow</c> at all, so reading the
    /// string alone cannot answer "is there a head?". <c>graph.getCellStyle</c> is what mxGraph
    /// itself calls before painting (<c>mxCellState.style</c>), so what comes back here is what
    /// <c>mxConnector</c> reads when it decides whether to draw a marker.
    /// </para>
    /// <para>
    /// A key the resolved style does not carry comes back as <c>(absent)</c> rather than as an
    /// empty string: "no <c>startArrow</c> anywhere" and "<c>startArrow=</c>" are two different
    /// facts, and the first is the ordinary one.
    /// </para>
    /// <para>
    /// <b>In the RESOLVED style, <c>(absent)</c> means "no head", the opposite of what it means in
    /// the cell's own style string</b>, and the two must not be confused. Measured on 2026-09-19,
    /// and the reason is in the bundled <c>mxgraph/src/view/mxStylesheet.js</c>: resolving a style
    /// <b>deletes</b> a key whose value is <c>none</c>. So a cell that says nothing keeps the
    /// default edge style's <c>endArrow=classic</c> and comes back <c>classic</c>, while a cell
    /// that says <c>endArrow=none</c> comes back <c>(absent)</c> - and <c>mxConnector</c> draws a
    /// marker only for a name it finds.
    /// </para>
    /// </remarks>
    public Task<string[]> HeadsDrawioResolvesAsync(string cellId) => InTheGraphAsync<string[]>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell with id ' + args);
        const style = graph.getCellStyle(cell) || {};
        const read = (k) => style[k] === undefined || style[k] === null ? '(absent)' : String(style[k]);
        return [read('endArrow'), read('startArrow')];
        """,
        cellId);

    /// <summary>
    /// Rewrites a cell's style <b>through draw.io's own Edit Style dialog</b> - select the cell,
    /// Ctrl+E, type, Apply - which is the road an operator takes to move an arrow's head, and the
    /// one the mirror's refusals already name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dialog is driven and not simulated: the text really goes into draw.io's text area and
    /// the Apply button is really clicked, so what the model ends up carrying is what draw.io
    /// decided to store, not what this helper wrote. The style that comes back is read out of the
    /// model afterwards for exactly that reason.
    /// </para>
    /// <para>
    /// <b>The click on empty canvas comes first</b>, because that is what puts the keyboard focus
    /// inside the iframe; selecting first and clicking after would take the selection away again.
    /// </para>
    /// </remarks>
    public async Task<string> ChangeTheStyleWithEditStyleAsync(string cellId, string newStyle)
    {
        await ClickEmptyCanvasAsync();
        var selected = await SelectCellAsync(cellId);
        if (selected != 1)
            throw new InvalidOperationException(
                $"La cella {cellId} non si e' selezionata ({selected} celle selezionate): Edit Style aprirebbe su un'altra cella.");

        await PressEditStyleShortcutAsync();
        await InTheGraphAsync<string>(
            """
            const doc = ui.editor.graph.container.ownerDocument;
            const dialog = ui.dialogs && ui.dialogs.length > 0 ? ui.dialogs[ui.dialogs.length - 1].container : null;
            if (!dialog) throw new Error('draw.io has no dialog open: Ctrl+E did not reach Edit Style');
            const area = dialog.querySelector('textarea');
            if (!area) throw new Error('the dialog draw.io opened has no text area');
            area.value = args.style;
            const buttons = Array.from(dialog.querySelectorAll('button'));
            const apply = buttons.find(b => (b.textContent || '').trim().toLowerCase() === 'apply');
            if (!apply) throw new Error('the Edit Style dialog has no Apply button, only: ' + buttons.map(b => b.textContent).join(' / '));
            apply.click();
            return String(doc.title || '');
            """,
            new { style = newStyle });

        var ceiling = DateTime.UtcNow.AddMilliseconds(ConditionCeiling);
        // Compared trimmed, because draw.io trims what the dialog hands it and nothing else: if it
        // ever normalized the style further, the throw below prints both and says so.
        var held = await CellStyleAsync(cellId);
        while (held.Trim() != newStyle.Trim() && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(SettleInterval);
            held = await CellStyleAsync(cellId);
        }

        if (held.Trim() != newStyle.Trim())
            throw new InvalidOperationException(
                $"Edit Style non ha scritto lo stile sulla cella {cellId}. Chiesto:{Environment.NewLine}{newStyle}" +
                $"{Environment.NewLine}Il modello porta:{Environment.NewLine}{held}");

        await CloseEveryDialogAsync();
        return held;
    }

    /// <summary>The style string the model holds for this cell - what Edit Style must be showing.</summary>
    public Task<string> CellStyleAsync(string cellId) => InTheGraphAsync<string>(
        """
        const cell = ui.editor.graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell with id ' + args);
        return String(cell.style || '');
        """,
        cellId);

    /// <summary>
    /// Draws an arrow between two cells of the real mxGraph model, marked <c>wcs="junction"</c> and
    /// hooked to the two ports the registry declares - that is, the arrow the operator ends up with
    /// after pulling one and telling draw.io's "Edit Data" that it is a junction of ours. Gives back
    /// the identifier draw.io chose for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ends are given as CELL identifiers</b>, which is what the model holds: for a seeded
    /// device that is its guid, for one just dropped it is draw.io's own. That difference is the
    /// whole point of the measurement this serves, so the helper must not translate anything.
    /// </para>
    /// <para>
    /// <b>The value is an XML element and not a string.</b> A cell whose value is a string comes
    /// back as a bare <c>&lt;mxCell&gt;</c> and carries no attributes, so it could never be a
    /// junction: the element is built with draw.io's own <c>mxUtils.createXmlDocument</c>, exactly
    /// as its "Edit Data" dialog does, and it is what makes the arrow come back as
    /// <c>&lt;object wcs="junction"&gt;</c> in the XML the bridge hands over.
    /// </para>
    /// <para>
    /// <b>Why the model and not the mouse.</b> Pulling an arrow by hand means hovering a shape's
    /// edge until draw.io shows its directional arrows and dragging from there: that measures
    /// draw.io's hover affordance, and it goes red the day that affordance is restyled. The model
    /// is the same place the gesture writes into, and it is the place the save reads from. The
    /// arrow's identifier comes back so that a gesture that did not take is read as a gesture that
    /// did not take rather than as a plant that did not change.
    /// </para>
    /// </remarks>
    public Task<string> DrawAnArrowAsync(string sourceCellId, string targetCellId) => InTheGraphAsync<string>(
        """
        const w = ui.editor.graph.container.ownerDocument.defaultView;
        const graph = ui.editor.graph;
        const model = graph.getModel();
        const source = model.getCell(args.source);
        const target = model.getCell(args.target);
        if (!source) throw new Error('the drawing has no cell with id ' + args.source);
        if (!target) throw new Error('the drawing has no cell with id ' + args.target);
        const node = w.mxUtils.createXmlDocument().createElement('object');
        node.setAttribute('label', '');
        node.setAttribute('wcs', 'junction');
        let edge = null;
        model.beginUpdate();
        try {
          edge = graph.insertEdge(graph.getDefaultParent(), null, node, source, target,
            'edgeStyle=orthogonalEdgeStyle;exitX=0.96;exitY=0.5;exitDx=0;exitDy=0;entryX=0.04;entryY=0.5;entryDx=0;entryDy=0;');
        } finally { model.endUpdate(); }
        if (!edge || !model.getCell(edge.id)) throw new Error('draw.io did not keep the arrow in its model');
        return String(edge.id);
        """,
        new { source = sourceCellId, target = targetCellId });

    /// <summary>
    /// Every edge draw.io's model holds, whoever made it: its identifier, the two cells it hangs
    /// from, our mark, and <b>what kind of thing its value is</b>.
    /// </summary>
    /// <remarks>
    /// The kind of the value is here because it is the fact the whole question turns on. A cell
    /// whose value is a plain string can carry no attribute at all, so it can never be marked;
    /// reading only the mark would say "no mark" for both a bare string and an
    /// <c>&lt;object&gt;</c> that simply has no <c>wcs</c>, and those are two different worlds.
    /// </remarks>
    public Task<DrawioEdge[]> EdgesInTheDrawingAsync() => InTheGraphAsync<DrawioEdge[]>(
        """
        const model = ui.editor.graph.getModel();
        const found = [];
        for (const key of Object.keys(model.cells || {})) {
          const cell = model.cells[key];
          if (!cell || !cell.edge) continue;
          const value = cell.value;
          const carries = !!(value && typeof value.getAttribute === 'function');
          found.push({
            id: String(cell.id),
            source: cell.source && cell.source.id != null ? String(cell.source.id) : null,
            target: cell.target && cell.target.id != null ? String(cell.target.id) : null,
            wcs: carries ? value.getAttribute('wcs') : null,
            valueKind: value == null ? 'none' : (carries ? 'element:' + String(value.nodeName) : typeof value),
            style: String(cell.style || '')
          });
        }
        return found;
        """);

    /// <summary>
    /// Takes one end of a line off the cell it hangs from - the gesture of dragging the arrow's
    /// endpoint away from a device and dropping it on empty canvas - and gives back what the two
    /// ends are afterwards, so that a gesture which did not take reads as a gesture that did not
    /// take.
    /// </summary>
    /// <remarks>
    /// <b>Through the model and not with the mouse.</b> Dragging an endpoint means grabbing a
    /// handle draw.io draws only while the edge is selected and only a few pixels wide: that drag
    /// measures mxGraph's hit-testing, which is not what is being asked here. <c>setTerminal</c>
    /// inside an update is the very call draw.io's own <c>mxEdgeHandler</c> ends up making, and it
    /// leaves the model in the state the save reads - the <c>target</c> attribute simply gone from
    /// the cell, which is what the drawing that comes back carries.
    /// </remarks>
    public Task<string[]> TakeOneEndOffAsync(string cellId) => InTheGraphAsync<string[]>(
        """
        const model = ui.editor.graph.getModel();
        const cell = model.getCell(args);
        if (!cell) throw new Error('the drawing has no cell with id ' + args);
        model.beginUpdate();
        try { model.setTerminal(cell, null, false); } finally { model.endUpdate(); }
        const after = model.getCell(args);
        return [
          after.source && after.source.id != null ? String(after.source.id) : '(none)',
          after.target && after.target.id != null ? String(after.target.id) : '(none)'
        ];
        """,
        cellId);

    /// <summary>
    /// Hooks one end of a line onto another cell - dragging the arrow's endpoint from one shape
    /// onto another - and gives back what the two ends are afterwards. The companion of
    /// <see cref="TakeOneEndOffAsync"/>, through the model and for the same reason.
    /// </summary>
    public Task<string[]> HookOneEndOntoAsync(string cellId, string ontoCellId) => InTheGraphAsync<string[]>(
        """
        const model = ui.editor.graph.getModel();
        const cell = model.getCell(args.id);
        if (!cell) throw new Error('the drawing has no cell with id ' + args.id);
        const onto = model.getCell(args.onto);
        if (!onto) throw new Error('the drawing has no cell with id ' + args.onto);
        model.beginUpdate();
        try { model.setTerminal(cell, onto, false); } finally { model.endUpdate(); }
        const after = model.getCell(args.id);
        return [
          after.source && after.source.id != null ? String(after.source.id) : '(none)',
          after.target && after.target.id != null ? String(after.target.id) : '(none)'
        ];
        """,
        new { id = cellId, onto = ontoCellId });

    /// <summary>
    /// Pulls an arrow between two devices <b>with the mouse</b>, the way the operator pulls it: the
    /// pointer walks over the source shape until draw.io shows its connection points, presses on
    /// its border, drags as far as the target shape and lets go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No cell is inserted into the model</b>, and that is the whole point.
    /// <see cref="DrawAnArrowAsync"/> calls <c>graph.insertEdge</c> with <c>wcs="junction"</c>
    /// already in it, that is, it hands the drawing a cell only OUR document knows how to make.
    /// Here draw.io builds the cell, and what that cell looks like is exactly the question to
    /// measure.
    /// </para>
    /// <para>
    /// <b>Where it presses.</b> In the middle of a shape mxGraph moves it; on the border it
    /// connects. So the press lands on the right-hand border at half height, which is also the
    /// connection point <c>exitX=1, exitY=0.5</c>.
    /// </para>
    /// <para>
    /// <b>Where it lets go is the caller's to choose</b>, and it is no detail: on the left-hand
    /// connection point the arrow hooks a port (<c>entryX</c> in its style), in the middle of the
    /// shape it hooks the whole cell and has no port at all, and beside the shape it hooks nothing.
    /// The save treats the three differently - see <c>Writable</c> in <c>DrawioDelta</c>, and the
    /// notice for an arrow with an end in the air - so one landing alone would measure a third of
    /// the question.
    /// </para>
    /// <para>
    /// <b>Why it does not give back a boolean.</b> A gesture that did not take is not a plant that
    /// did not change: two faults, repaired in two different places. What comes back says whether
    /// an arrow was born, what it hangs from, and whether the press became a MOVE of the shape
    /// instead - that is, the way this measurement could come to nothing without noticing.
    /// </para>
    /// </remarks>
    public async Task<DrawioHandDrawnArrow> DragAnArrowByHandAsync(
        string fromDevice, string toDevice, DrawioArrowLanding landing)
    {
        var to = await SettledBoxAsync(toDevice);
        return await DragAnArrowByHandAsync(
            fromDevice, (to.X, to.Y, to.Width, to.Height), toDevice, landing);
    }

    /// <summary>
    /// The same gesture, aimed at any cell of the drawing rather than at a device: the target's box
    /// comes from draw.io's own view (see <see cref="CellBoxOnScreenAsync"/>) because a shape the
    /// operator drew carries no image and no label of ours, so the XPath the devices are found by
    /// does not reach it.
    /// </summary>
    public async Task<DrawioHandDrawnArrow> DragAnArrowByHandOntoCellAsync(
        string fromDevice, string toCellId, DrawioArrowLanding landing)
    {
        var to = await CellBoxOnScreenAsync(toCellId);
        return await DragAnArrowByHandAsync(
            fromDevice, (to[0], to[1], to[2], to[3]), $"la cella {toCellId}", landing);
    }

    private async Task<DrawioHandDrawnArrow> DragAnArrowByHandAsync(
        string fromDevice, (double X, double Y, double Width, double Height) to, string toName,
        DrawioArrowLanding landing)
    {
        var from = await SettledBoxAsync(fromDevice);
        var before = (await EdgesInTheDrawingAsync()).Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);

        var pressX = (float)(from.X + from.Width);
        var pressY = (float)(from.Y + from.Height / 2);
        // BesideTheShape lands on empty canvas, clear of mxGraph's connection tolerance: the drop
        // has to be near enough to be the same missed aim an operator makes, and far enough that
        // draw.io does not hook the shape anyway. The gap the two seeded devices leave between them
        // is what makes room for it.
        var landX = (float)(landing switch
        {
            DrawioArrowLanding.OnThePort => to.X,
            DrawioArrowLanding.BesideTheShape => to.X - BesideGap,
            _ => to.X + to.Width / 2,
        });
        var landY = (float)(to.Y + to.Height / 2);

        // The pointer walks over the shape FIRST, and in more than one move: draw.io shows the
        // connection points while the pointer travels over the shape, and a single jump onto the
        // border can arrive before the highlight - the gesture would become a press on nothing.
        await _page.Mouse.MoveAsync((float)(from.X + from.Width / 2), (float)(from.Y + from.Height / 2));
        await _page.Mouse.MoveAsync(pressX, pressY, new MouseMoveOptions { Steps = 6 });
        await Task.Delay(HoverPause);

        await _page.Mouse.DownAsync();
        await _page.Mouse.MoveAsync(landX, landY, new MouseMoveOptions { Steps = 20 });
        // It rests on the landing point: it is while the pointer is still there that draw.io
        // decides whether to hook the arrow to a cell or let it end in empty space.
        await Task.Delay(HoverPause);
        await _page.Mouse.UpAsync();

        var born = await NewEdgesAsync(before);
        var after = await SettledBoxAsync(fromDevice);
        return new DrawioHandDrawnArrow
        {
            NewEdges = born,
            SourceShapeMoved = !IsSameBox(from, after),
            Account =
                $"premuto a ({pressX:0.#}, {pressY:0.#}) sul bordo di \"{fromDevice}\" " +
                $"[{from.X:0.#}, {from.Y:0.#}, {from.Width:0.#}x{from.Height:0.#}], " +
                $"rilasciato a ({landX:0.#}, {landY:0.#}) su \"{toName}\" " +
                $"[{to.X:0.#}, {to.Y:0.#}, {to.Width:0.#}x{to.Height:0.#}], {landing}; " +
                $"archi nel disegno prima: {before.Count}, nati: {born.Count}",
        };
    }

    /// <summary>
    /// Where draw.io is really drawing a cell, in the coordinates the mouse of this test moves in:
    /// left, top, width, height.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three frames of reference meet here, and leaving any of them out gives a point that looks
    /// plausible and is wrong: the cell's box in draw.io's view already carries the zoom and the
    /// pan (<c>mxCellState</c>), the canvas can be scrolled inside its container, and the whole
    /// thing lives in an iframe that starts partway down the page. The iframe's own rectangle is
    /// asked of <c>frameElement</c>, which is readable because the frame is same-origin - the same
    /// fact every other helper in this class rests on.
    /// </para>
    /// <para>
    /// It is checked against the devices' own DOM boxes by the measurement that uses it: a formula
    /// nobody compares with anything is a formula that can be quietly off by the height of a
    /// toolbar, and the gesture would then press on empty canvas and report "no arrow" - a false
    /// negative that reads exactly like the defect being looked for.
    /// </para>
    /// </remarks>
    public Task<double[]> CellBoxOnScreenAsync(string cellId) => InTheGraphAsync<double[]>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args.id);
        if (!cell) throw new Error('the drawing has no cell with id ' + args.id);
        const state = graph.view.getState(cell);
        if (!state) throw new Error('the drawing holds the cell ' + args.id + ' but is not drawing it');
        const container = graph.container.getBoundingClientRect();
        const frame = graph.container.ownerDocument.defaultView.frameElement;
        const outer = frame ? frame.getBoundingClientRect() : { left: 0, top: 0 };
        return [
          outer.left + container.left + state.x - graph.container.scrollLeft,
          outer.top + container.top + state.y - graph.container.scrollTop,
          state.width, state.height
        ];
        """,
        new { id = cellId });

    /// <summary>
    /// The zoom draw.io draws at (<c>view.scale</c>): how many px on the screen a unit of the drawing
    /// is, to turn a distance of the plant into one on the screen.
    /// </summary>
    public Task<double> ViewScaleAsync() => InTheGraphAsync<double>("return ui.editor.graph.view.scale;");

    /// <summary>
    /// Where draw.io's canvas is, in the coordinates the mouse of this test moves in: the box the
    /// drawing scrolls in - mxGraph's container, its scrollbars out (<c>clientWidth</c>,
    /// <c>clientHeight</c>) - as <c>[x, y, width, height]</c>. It is where a test stages the windows
    /// over the canvas; which part of it the bridge finds free is not asked here.
    /// </summary>
    public Task<double[]> CanvasBoxOnScreenAsync() => InTheGraphAsync<double[]>(
        """
        const container = ui.editor.graph.container;
        const box = container.getBoundingClientRect();
        const frame = container.ownerDocument.defaultView.frameElement;
        const outer = frame ? frame.getBoundingClientRect() : { left: 0, top: 0 };
        const border = frame ? [frame.clientLeft, frame.clientTop] : [0, 0];
        return [
          outer.left + border[0] + box.left + container.clientLeft,
          outer.top + border[1] + box.top + container.clientTop,
          container.clientWidth, container.clientHeight
        ];
        """);

    /// <summary>
    /// The bridge's own choice of the free part of the canvas (<c>drawio-bridge.js</c>,
    /// <c>largestFreeRectangle</c>), called on boxes this test writes and on nothing of the page's:
    /// a canvas, the windows over it and the size of the cell to be placed, each box as
    /// <c>[left, top, right, bottom]</c>. Returns the rectangle chosen, in the same form, or null when
    /// the bridge found none. The function is pure - it reads no window, no draw.io and no memory - so
    /// the module instance the call reaches does not matter.
    /// </summary>
    public Task<double[]?> LargestFreeRectangleAsync(double[] canvas, double[][] covers, double cellWidth, double cellHeight) =>
        _page.EvaluateAsync<double[]?>(
            """
            async ([canvas, covers, least]) => {
              const bridge = await import('/_content/Wcs.WebEditor/drawio-bridge.js');
              const box = (b) => ({ left: b[0], top: b[1], right: b[2], bottom: b[3] });
              const free = bridge.largestFreeRectangle(box(canvas), covers.map(box), { width: least[0], height: least[1] });
              return free ? [free.left, free.top, free.right, free.bottom] : null;
            }
            """,
            new object[] { canvas, covers, new[] { cellWidth, cellHeight } });

    /// <summary>
    /// Queue item 36: draw.io's own panels over the canvas - the <c>div.mxWindow</c> elements of its
    /// document it shows (the shapes panel, the format panel, any other of its windows open) - each as
    /// <c>[x, y, width, height]</c> in the coordinates the mouse of this test moves in. Read off the
    /// DOM, not off the bridge.
    /// </summary>
    public Task<double[][]> DrawioPanelBoxesOnScreenAsync() => InTheGraphAsync<double[][]>(
        """
        const doc = ui.editor.graph.container.ownerDocument;
        const frame = doc.defaultView.frameElement;
        const outer = frame ? frame.getBoundingClientRect() : { left: 0, top: 0 };
        const border = frame ? [frame.clientLeft, frame.clientTop] : [0, 0];
        return [...doc.querySelectorAll('div.mxWindow')]
          .filter((e) => e.getClientRects().length > 0 && doc.defaultView.getComputedStyle(e).visibility !== 'hidden')
          .map((e) => { const r = e.getBoundingClientRect(); return [outer.left + border[0] + r.left, outer.top + border[1] + r.top, r.width, r.height]; });
        """);

    /// <summary>
    /// Queue item 33: where the drawing's view stands - mxGraph's container scrolled by
    /// <c>[scrollLeft, scrollTop]</c> and its view translated by <c>[translate.x, translate.y]</c>,
    /// the two things a bringing into view moves.
    /// </summary>
    public Task<double[]> ViewPositionAsync() => InTheGraphAsync<double[]>(
        """
        const graph = ui.editor.graph;
        return [graph.container.scrollLeft, graph.container.scrollTop, graph.view.translate.x, graph.view.translate.y];
        """);

    /// <summary>
    /// Scrolls draw.io's canvas by <paramref name="dx"/>, <paramref name="dy"/> px, as its scrollbars
    /// would: the drawing moves the other way on the screen. Returns how far it did scroll, which the
    /// container may cut at its ends.
    /// </summary>
    public Task<double[]> ScrollTheCanvasByAsync(double dx, double dy) => InTheGraphAsync<double[]>(
        """
        const container = ui.editor.graph.container;
        const before = [container.scrollLeft, container.scrollTop];
        container.scrollLeft += args[0];
        container.scrollTop += args[1];
        return [container.scrollLeft - before[0], container.scrollTop - before[1]];
        """, new[] { dx, dy });

    /// <summary>
    /// The edges the model holds that it did not hold before, waiting for one to turn up rather
    /// than reading straight after the mouse button came up.
    /// </summary>
    /// <remarks>
    /// The wait has a ceiling and the ceiling is short, because the answer this serves may well be
    /// "none": waiting for a negative cannot be waiting for a condition. What it buys is that a
    /// "none" is a none, and not mxGraph still finishing the edit.
    /// </remarks>
    private async Task<IReadOnlyList<DrawioEdge>> NewEdgesAsync(HashSet<string> before)
    {
        var giveUpAt = DateTime.UtcNow.AddMilliseconds(GestureCeiling);
        while (true)
        {
            var born = (await EdgesInTheDrawingAsync()).Where(edge => !before.Contains(edge.Id)).ToList();
            if (born.Count > 0 || DateTime.UtcNow > giveUpAt) return born;
            await Task.Delay(SettleInterval);
        }
    }

    /// <summary>
    /// Gli identificativi delle celle che nel modello portano il marchio <c>wcs="device"</c>, cioe'
    /// i dispositivi <b>disegnati</b>: le seminate portano il guid dell'impianto, quelle nate da un
    /// rilascio o da un incollaggio portano un identificativo di draw.io.
    /// </summary>
    /// <remarks>
    /// Si legge il MODELLO e non l'SVG a video: una cella incollata puo' essere disegnata sopra a
    /// un'altra, e contare le sagome sullo schermo direbbe meno di quello che il disegno contiene -
    /// che e' poi cio' che il salvataggio manda indietro.
    /// </remarks>
    public Task<string[]> MarkedCellIdsAsync() => InTheGraphAsync<string[]>(
        """
        const model = ui.editor.graph.getModel();
        const found = [];
        for (const key of Object.keys(model.cells || {})) {
          const cell = model.cells[key];
          const value = cell ? cell.value : null;
          if (value && value.getAttribute && value.getAttribute('wcs') === 'device') found.push(String(cell.id));
        }
        return found;
        """);

    /// <summary>
    /// Seleziona <b>dal modello</b> la cella con questo identificativo e restituisce quante celle
    /// draw.io ha poi davvero selezionate.
    /// </summary>
    /// <remarks>
    /// <b>Il numero restituito e' il punto.</b> Il tentativo di copia e incolla del lotto
    /// precedente concludeva che «da un browser guidato non si puo' incollare», e la misura era
    /// cieca: la selezione era vuota, quindi Ctrl+C copiava il nulla e Ctrl+V lo rimetteva. Una
    /// prova che chiama questo metodo e non guarda quanto torna misurerebbe di nuovo il nulla.
    /// <para>
    /// La selezione dal modello e non con il mouse perche' il bersaglio e' l'incollaggio, non il
    /// clic: una sagoma appena rilasciata puo' essere coperta dal pannello delle forme, e un clic a
    /// vuoto lascerebbe di nuovo la selezione vuota. Il fuoco della tastiera dentro l'iframe lo
    /// porta comunque un clic sulla tela (<see cref="ClickAtAsync"/>), che resta necessario.
    /// </para>
    /// </remarks>
    public Task<int> SelectCellAsync(string cellId) => InTheGraphAsync<int>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args);
        if (!cell) throw new Error('il disegno non ha nessuna cella ' + args);
        graph.setSelectionCell(cell);
        return graph.getSelectionCells().length;
        """, cellId);

    /// <summary>
    /// Seleziona <b>dal modello</b> piu' celle insieme e restituisce quante ne risultano
    /// selezionate: e' cosi' che si prepara un incollaggio di <b>piu' celle in un colpo solo</b>,
    /// che e' il gesto in cui il ponte consegna alla pagina un elenco e non una cella.
    /// </summary>
    /// <remarks>
    /// Il numero che torna e' il punto, esattamente come per <see cref="SelectCellAsync"/>: una
    /// prova che incolla senza guardare quante celle aveva in mano puo' misurare il nulla e
    /// chiamarlo un esito - e' gia' successo su questo ramo.
    /// </remarks>
    public Task<int> SelectCellsAsync(params string[] cellIds) => InTheGraphAsync<int>(
        """
        const graph = ui.editor.graph;
        const model = graph.getModel();
        const cells = args.map(function (id) {
          const cell = model.getCell(id);
          if (!cell) throw new Error('il disegno non ha nessuna cella ' + id);
          return cell;
        });
        graph.setSelectionCells(cells);
        return graph.getSelectionCells().length;
        """, cellIds);

    /// <summary>
    /// L'etichetta che draw.io <b>disegna</b> per questa cella, chiesta al suo modello con
    /// <c>graph.getLabel</c> - cioe' la stessa funzione da cui passa il disegno della sagoma.
    /// </summary>
    /// <remarks>
    /// Si chiede al MODELLO e non a una riga della pagina: la rinomina attraversa il circuito
    /// Blazor, il ponte e <c>cellLabelChanged</c> dentro l'iframe, e ognuno dei tre puo' non fare
    /// nulla mentre la pagina continua a dire il nome nuovo - che e' esattamente la forma del
    /// difetto trovato dal proprietario il 17/9/2026.
    /// </remarks>
    public Task<string?> CellLabelAsync(string cellId) => InTheGraphAsync<string?>(
        """
        const graph = ui.editor.graph;
        const cell = graph.getModel().getCell(args);
        if (!cell) throw new Error('il disegno non ha nessuna cella ' + args);
        const label = graph.getLabel(cell);
        return label == null ? null : String(label);
        """, cellId);

    /// <summary>
    /// Aspetta che la cella porti questa etichetta nel modello, e se non ci arriva dice quale
    /// portava: "l'etichetta non e' cambiata" senza dire in che cosa non e' cambiata fa ripartire da
    /// zero chi lo legge.
    /// </summary>
    public async Task WaitForCellLabelAsync(string cellId, string expected)
    {
        var ceiling = DateTime.UtcNow.AddMilliseconds(ConditionCeiling);
        var label = await CellLabelAsync(cellId);
        while (label != expected && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(SettleInterval);
            label = await CellLabelAsync(cellId);
        }

        if (label != expected)
            throw new InvalidOperationException(
                $"La cella {cellId} porta l'etichetta \"{label}\" invece di \"{expected}\": il disegno non ha preso il nome nuovo.");
    }

    /// <summary>Copia la selezione, con la scorciatoia che draw.io lega a "copy" (<c>bindAction(67,!0,"copy")</c>).</summary>
    public async Task CopyAsync()
    {
        await _page.Keyboard.PressAsync("Control+c");
        await WaitForDrawioToPutItsClipboardElementAwayAsync();
    }

    /// <summary>Taglia la selezione (<c>bindAction(88,!0,"cut")</c>): una copia piu' una rimozione dal modello.</summary>
    public async Task CutAsync()
    {
        await _page.Keyboard.PressAsync("Control+x");
        await WaitForDrawioToPutItsClipboardElementAwayAsync();
    }

    /// <summary>
    /// Waits until draw.io has put its clipboard element away again, which is what makes the NEXT
    /// Ctrl+V land where draw.io listens for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read in draw.io's own code</b> (<c>EditorUi.installNativeClipboardHandler</c>): the only
    /// listener that turns a pasted <c>&lt;mxGraphModel&gt;</c> into cells sits on
    /// <c>clipboardElt</c>, a hidden contentEditable div. That div is appended to the canvas and
    /// focused on the <b>Ctrl keydown</b>, guarded by a flag, and it is removed again - with the
    /// focus handed back to the canvas, which then gives it to the typing shim textarea - in a
    /// <c>setTimeout(0)</c> scheduled by the <b>Ctrl keyup</b>. The same timeout clears the flag.
    /// </para>
    /// <para>
    /// <b>The race, measured on 2026-09-19</b> with the probe of <see cref="ArmThePasteProbeAsync"/>:
    /// copy's Ctrl keyup at 18 ms, paste's Ctrl keydown at <b>20 ms</b> - the flag was still set,
    /// so that keydown did nothing at all - the tidy-up at 21 ms, which detached the div and moved
    /// the focus to the typing shim, and the <c>paste</c> event at 22 ms landing <b>on the typing
    /// shim</b> with all 1828 characters of the drawing in it and nobody listening. No cells, no
    /// devices, no message: "Il disegno porta 3 celle marchiate invece di 4", 30 s of ceiling
    /// burnt. Three reds in eleven rounds of the four pasting tests before this wait existed.
    /// </para>
    /// <para><b>The ceiling is a failure.</b> This wait separates two keyboard gestures. If the
    /// clipboard element remains attached, the next Ctrl keydown is not known to start a new draw.io
    /// clipboard cycle, so continuing would turn an unobserved focus race into a false success.</para>
    /// </remarks>
    private async Task WaitForDrawioToPutItsClipboardElementAwayAsync()
    {
        var ceiling = DateTime.UtcNow.AddMilliseconds(ConditionCeiling);
        var attached = await InTheGraphAsync<bool>("return !!(ui.clipboardElt && ui.clipboardElt.parentNode);");
        while (attached && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(SettleInterval);
            attached = await InTheGraphAsync<bool>("return !!(ui.clipboardElt && ui.clipboardElt.parentNode);");
        }

        if (!attached) return;
        var probe = await ThePasteProbeAsync();
        throw new InvalidOperationException(
            $"draw.io kept clipboardElt attached beyond the wait ceiling; paste probe: {probe}");
    }

    internal async Task WaitForClipboardReplacementForTestAsync(
        string marker,
        int ceilingMs = ConditionCeiling)
    {
        var ceiling = DateTime.UtcNow.AddMilliseconds(ceilingMs);
        var text = await ReadSystemClipboardTextAsync();
        while (text == marker && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(SettleInterval);
            text = await ReadSystemClipboardTextAsync();
        }

        if (text != marker) return;
        var probe = await ThePasteProbeAsync();
        throw new InvalidOperationException(
            $"system clipboard still contains marker '{marker}'; paste probe: {probe}");
    }

    /// <summary>
    /// Incolla. <b>Ctrl+V non e' legato a nessuna azione di draw.io</b> - l'incollaggio gli arriva
    /// dall'evento <c>paste</c> vero del browser - e una pressione di tasti di Playwright lo
    /// produce lo stesso, perche' e' il browser a generarlo: misurato il 17/9/2026, si veda
    /// <c>DrawioShapesPanelTest.CopyingADeviceAndPastingItCreatesASecondDevice</c>.
    /// <para>
    /// <b>Che l'evento arrivi non basta: deve arrivare sull'elemento giusto</b>, e quale sia - piu'
    /// il rosso da 30 secondi che si prende quando non ci arriva - e' scritto in
    /// <see cref="CopyAsync"/>.
    /// </para>
    /// </summary>
    public Task PasteAsync() => _page.Keyboard.PressAsync("Control+v");

    /// <summary>
    /// Arms the paste probe inside the iframe: from here on every Ctrl+C/Ctrl+X/Ctrl+V that reaches
    /// draw.io's document, every clipboard event it receives and every edit committed to its model
    /// are written down in the page, and <see cref="ThePasteProbeAsync"/> reads the record back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It only listens</b>: no <c>preventDefault</c>, no clipboard written, nothing focused.
    /// A probe that had to touch the clipboard to read it would be the one thing that could make a
    /// rare race disappear, and a race that vanishes when it is watched has not been measured.
    /// </para>
    /// <para>
    /// <b>The record exists to say WHERE a paste that brings nothing lost its cells</b>, and the
    /// four layers are not interchangeable: the keystroke may never reach the iframe at all (the
    /// focus went somewhere else), the <c>paste</c> event may never fire although the keystroke did
    /// (draw.io's clipboard handler), it may fire carrying nothing (the copy wrote nothing), or it
    /// may fire with the cells in it and the model take none. Only the last one is the product's.
    /// </para>
    /// <para>
    /// <b>What a healthy paste of three says</b>, measured on 2026-09-19 so that a probe that
    /// records nothing cannot pass for a paste that lost nothing: the Ctrl of the copy at 3 ms
    /// moving the focus from the <c>typingShim</c> to the <c>clipboardElt</c> draw.io has just
    /// attached, the <c>copy</c> event on it at 6 ms carrying no readable data (the clipboard is
    /// write-only during a copy, which is why the system clipboard is read separately), draw.io's
    /// tidy-up at 21 ms taking the element away again, the Ctrl of the paste at 28 ms bringing it
    /// back, the <c>paste</c> event at 30 ms <b>on the clipboardElt</b> with
    /// <c>text/plain+text/html</c> and <b>5669 characters</b> of url-encoded
    /// <c>&lt;mxGraphModel&gt;</c>, then at 37 ms <b>one</b> model edit of nine changes carrying
    /// the three child changes - each marked <c>device</c>, parent <c>1</c>, previous an object
    /// whose id is <c>null</c>, which is the temporary parent the clipboard is decoded into - and
    /// three later edits of one value change each, the labels.
    /// </para>
    /// </remarks>
    public Task<string> ArmThePasteProbeAsync() => InTheGraphAsync<string>(
        """
        const w = document.getElementById('drawio-frame').contentWindow;
        const model = ui.editor.graph.getModel();
        if (w.__wcsPasteProbe && typeof w.__wcsPasteProbe.off === 'function') w.__wcsPasteProbe.off();
        const log = [];
        const t0 = Date.now();
        const since = () => String(Date.now() - t0) + ' ms';
        const place = (node) => {
          if (!node) return 'nessuno';
          if (node === ui.clipboardElt) return 'clipboardElt';
          if (node === ui.typingShim) return 'typingShim';
          if (node === ui.editor.graph.container) return 'il contenitore del disegno';
          const tag = node.tagName ? String(node.tagName).toLowerCase() : String(node.nodeName || node);
          const id = node.id ? '#' + node.id : '';
          const held = (typeof node.value === 'string') ? ' (contiene ' + node.value.length + ' caratteri)' : '';
          return tag + id + held;
        };
        // Whether draw.io's clipboard element is in the DOM at all: installNativeClipboardHandler
        // appends it on the Ctrl keydown and takes it away again in a setTimeout(0) after the Ctrl
        // keyup, and copy/cut/paste are listened for on IT and nowhere else.
        const clipboardHome = () => (ui.clipboardElt && ui.clipboardElt.parentNode) ? 'attaccato' : 'staccato';
        const onKey = (e) => {
          const key = String(e.key || '').toLowerCase();
          const interesting = (key === 'control' || key === 'meta')
            || ((e.ctrlKey || e.metaKey) && (key === 'c' || key === 'v' || key === 'x'));
          if (!interesting) return;
          log.push(since() + ' TASTO ' + key + ' (' + e.type + '), fuoco su ' + place(w.document.activeElement)
            + ', clipboardElt ' + clipboardHome()
            + ', iframe a fuoco ' + w.document.hasFocus() + ', pagina a fuoco ' + document.hasFocus());
        };
        const onFocus = (e) => log.push(since() + ' FUOCO ' + e.type + ' su ' + place(e.target)
          + ', clipboardElt ' + clipboardHome());
        const onClip = (phase) => (e) => {
          let types = 'nessuno';
          let text = '';
          try {
            const data = e.clipboardData;
            if (data) {
              types = Array.prototype.slice.call(data.types || []).join('+') || 'nessuno';
              text = String(data.getData('text/plain') || '');
            }
          } catch (err) { types = 'illeggibili (' + err.message + ')'; }
          log.push(since() + ' APPUNTI ' + e.type + ' [' + phase + '] su ' + place(e.target) + ', tipi ' + types
            + ', testo di ' + text.length + ' caratteri: ' + JSON.stringify(text.slice(0, 160)));
        };
        const onChange = (sender, evt) => {
          const edit = evt.getProperty('edit');
          const changes = (edit && edit.changes) ? edit.changes : [];
          const said = [];
          for (const change of changes) {
            if (change && 'child' in change) {
              const child = change.child;
              const mark = (child && child.value && typeof child.value.getAttribute === 'function')
                ? child.value.getAttribute('wcs') : null;
              said.push('figlio ' + (child ? String(child.id) : 'nullo') + ' marchio ' + JSON.stringify(mark)
                + ' genitore ' + (change.parent ? String(change.parent.id) : 'nullo')
                + ' prima ' + (change.previous ? String(change.previous.id) : 'nullo'));
            } else if (change && 'root' in change) said.push('radice');
            else if (change && 'geometry' in change) said.push('geometria');
            else if (change && 'value' in change) said.push('valore');
            else if (change && 'style' in change) said.push('stile');
            else said.push('altro');
          }
          log.push(since() + ' MODELLO un edit di ' + changes.length + ': ' + (said.join('; ') || 'niente'));
        };
        const inTheFall = onClip('in discesa');
        const onTheWayUp = onClip('in salita');
        w.document.addEventListener('keydown', onKey, true);
        w.document.addEventListener('keyup', onKey, true);
        w.document.addEventListener('focusin', onFocus, true);
        w.document.addEventListener('focusout', onFocus, true);
        w.document.addEventListener('copy', inTheFall, true);
        w.document.addEventListener('cut', inTheFall, true);
        w.document.addEventListener('paste', inTheFall, true);
        w.document.addEventListener('copy', onTheWayUp, false);
        w.document.addEventListener('paste', onTheWayUp, false);
        model.addListener(w.mxEvent.CHANGE, onChange);
        w.__wcsPasteProbe = {
          log: log,
          off: function () {
            w.document.removeEventListener('keydown', onKey, true);
            w.document.removeEventListener('keyup', onKey, true);
            w.document.removeEventListener('focusin', onFocus, true);
            w.document.removeEventListener('focusout', onFocus, true);
            w.document.removeEventListener('copy', inTheFall, true);
            w.document.removeEventListener('cut', inTheFall, true);
            w.document.removeEventListener('paste', inTheFall, true);
            w.document.removeEventListener('copy', onTheWayUp, false);
            w.document.removeEventListener('paste', onTheWayUp, false);
            model.removeListener(onChange);
          }
        };
        return 'sonda armata';
        """);

    /// <summary>
    /// What the probe of <see cref="ArmThePasteProbeAsync"/> wrote down, one line per event, with
    /// draw.io's own clipboard and the size of the model at the end.
    /// </summary>
    public Task<string> ThePasteProbeAsync() => InTheGraphAsync<string>(
        """
        const w = document.getElementById('drawio-frame').contentWindow;
        const probe = w.__wcsPasteProbe;
        const lines = (probe && probe.log) ? probe.log.slice() : ['(la sonda non era armata)'];
        let held = 'sconosciuto';
        try {
          const cells = (w.mxClipboard && typeof w.mxClipboard.getCells === 'function') ? w.mxClipboard.getCells() : null;
          held = cells ? (String(cells.length) + ' celle') : 'mxClipboard non risponde';
        } catch (err) { held = 'illeggibile (' + err.message + ')'; }
        lines.push('mxClipboard di draw.io: ' + held);
        lines.push('clipboardElt di draw.io: ' + ((ui.clipboardElt && ui.clipboardElt.parentNode) ? 'attaccato' : 'staccato'));
        lines.push('celle nel modello: ' + Object.keys(ui.editor.graph.getModel().cells || {}).length);
        return lines.join('\n');
        """);

    /// <summary>
    /// What is on the browser's <b>system</b> clipboard right now, read and never written: it is
    /// the only place that says whether the Ctrl+C put draw.io's <c>&lt;mxGraphModel&gt;</c> there
    /// at all, which separates "the copy wrote nothing" from "the paste brought nothing".
    /// </summary>
    public async Task<string> TheSystemClipboardAsync()
    {
        var text = await ReadSystemClipboardTextAsync();
        return $"{text.Length} caratteri: {text[..Math.Min(text.Length, 300)]}";
    }

    internal Task<string> ReadSystemClipboardTextForTestAsync() => ReadSystemClipboardTextAsync();

    private async Task<string> ReadSystemClipboardTextAsync()
    {
        await _context.GrantPermissionsAsync(["clipboard-read"]);
        return await _page.EvaluateAsync<string>(
            "async () => { try { return await navigator.clipboard.readText(); } " +
            "catch (e) { return 'GLI APPUNTI NON SI SONO LASCIATI LEGGERE: ' + e.message; } }");
    }

    /// <summary>
    /// Aspetta che il modello contenga <paramref name="expected"/> celle marchiate, e se non ci
    /// arriva dice quante ne aveva e quali: un incollaggio che non avviene si legge cosi' invece che
    /// come un impianto che non cresce.
    /// </summary>
    public async Task WaitForMarkedCellsAsync(int expected)
    {
        var ceiling = DateTime.UtcNow.AddSeconds(30);
        var ids = await MarkedCellIdsAsync();
        while (ids.Length != expected && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(SettleInterval);
            ids = await MarkedCellIdsAsync();
        }

        if (ids.Length != expected)
            throw new InvalidOperationException(
                $"Il disegno porta {ids.Length} celle marchiate invece di {expected}: {string.Join(", ", ids)}.");
    }

    /// <summary>
    /// Aspetta che questa cella <b>non sia piu' nel disegno</b> e che le celle marchiate siano
    /// tornate a <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// Esiste per una sola ragione: quando un annullamento toglie l'etichetta invece della sagoma,
    /// "il disegno porta 3 celle invece di 2" non dice che cosa sia successo. Qui il fallimento
    /// porta anche <b>l'etichetta che la cella ha adesso</b>, che e' proprio il segno di un
    /// annullamento arrivato a meta' gesto.
    /// </remarks>
    public async Task WaitForTheCellToBeGoneAsync(string cellId, int expected)
    {
        var ceiling = DateTime.UtcNow.AddSeconds(30);
        var ids = await MarkedCellIdsAsync();
        while ((ids.Contains(cellId) || ids.Length != expected) && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(SettleInterval);
            ids = await MarkedCellIdsAsync();
        }

        if (!ids.Contains(cellId) && ids.Length == expected) return;

        var label = ids.Contains(cellId) ? $" e porta l'etichetta \"{await CellLabelAsync(cellId)}\"" : "";
        throw new InvalidOperationException(
            $"La cella {cellId} e' ancora nel disegno{label}: il disegno porta {ids.Length} celle marchiate " +
            $"invece di {expected} ({string.Join(", ", ids)}). Un annullamento non e' bastato.");
    }

    /// <summary>
    /// How many pages the drawing has, asked of draw.io itself (<c>ui.pages</c>). It is <b>the</b>
    /// fact this batch is anchored to: a measurement that looked for a missing "+" button would go
    /// green by itself the day a class name changed, while this number is what the loss is made of.
    /// </summary>
    public Task<int> PagesInTheDrawingAsync() => InTheGraphAsync<int>("return ui.pages ? ui.pages.length : -1;");

    /// <summary>
    /// The undo stack draw.io's own Ctrl+Z walks (<c>ui.editor.undoManager</c>), one line per entry:
    /// the index, whether the entry is significant, and the kinds of change it carries. The first
    /// line is <c>indexOfNextAdd</c> and the number of entries.
    /// </summary>
    /// <remarks>
    /// It is <b>diagnostic</b> and nothing asserts on it: what the tests assert is what Ctrl+Z does
    /// to the drawing. It is here because "one press was not enough" says nothing about why, and
    /// the stack says everything - which entry was significant, and what sat between two of ours.
    /// </remarks>
    public Task<string[]> UndoStackAsync() => InTheGraphAsync<string[]>(
        """
        const manager = ui.editor.undoManager;
        if (!manager || !manager.history) return ['draw.io has no undoManager to read'];
        const lines = ['indexOfNextAdd=' + manager.indexOfNextAdd + ' of ' + manager.history.length];
        for (let i = 0; i < manager.history.length; i++) {
          const edit = manager.history[i];
          const changes = (edit && edit.changes) ? edit.changes : [];
          const kinds = [];
          for (const change of changes)
            kinds.push(change && change.constructor ? String(change.constructor.name) : String(typeof change));
          const flag = (edit && edit.isSignificant && edit.isSignificant()) ? 'SIG' : 'insig';
          lines.push(i + ':' + flag + '/' + kinds.length + '[' + kinds.join(',') + ']');
        }
        return lines;
        """);

    /// <summary>
    /// The box this cell has in the model, as <c>[x, y]</c>. It is the same place a drag writes
    /// into and the same place an undo puts back, which is what makes it the way to see whether
    /// somebody else's gesture is still there.
    /// </summary>
    public Task<double[]> CellPositionAsync(string cellId) => InTheGraphAsync<double[]>(
        """
        const cell = ui.editor.graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell ' + args);
        if (!cell.geometry) throw new Error('the cell ' + args + ' carries no geometry: there is no position to read');
        return [cell.geometry.x, cell.geometry.y];
        """, cellId);

    /// <summary>
    /// The whole box this cell has in the model, as <c>[x, y, width, height]</c>: the position
    /// alone says nothing about a gesture that resizes.
    /// </summary>
    public Task<double[]> CellGeometryAsync(string cellId) => InTheGraphAsync<double[]>(
        """
        const cell = ui.editor.graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell ' + args);
        if (!cell.geometry) throw new Error('the cell ' + args + ' carries no geometry: there is no box to read');
        return [cell.geometry.x, cell.geometry.y, cell.geometry.width, cell.geometry.height];
        """, cellId);

    /// <summary>
    /// The identifier of the cell's parent in the model - <c>"1"</c> for the default layer, the
    /// group's own identifier once the cell has been put in a group. It is the model, not the
    /// picture: a group that only looked like one would still say <c>"1"</c> here.
    /// </summary>
    public Task<string?> CellParentAsync(string cellId) => InTheGraphAsync<string?>(
        """
        const cell = ui.editor.graph.getModel().getCell(args);
        if (!cell) throw new Error('the drawing has no cell ' + args);
        return cell.parent ? String(cell.parent.id) : null;
        """, cellId);

    /// <summary>
    /// Row 37: every cell this drawing marks <c>wcs="device"</c> or <c>wcs="junction"</c>, with
    /// whether it is visible in draw.io's OWN model (<c>model.isVisible</c>) and whether it is
    /// actually RENDERED on screen (a shape node in the DOM whose computed style is neither
    /// <c>display:none</c> nor <c>visibility:hidden</c>).
    /// </summary>
    /// <remarks>
    /// The two can disagree, and that disagreement is the whole reason both are read instead of
    /// just one: mxGraph keeps a visibility flag PER CELL, but rendering walks the parent chain, so
    /// a cell can answer <c>modelVisible=true</c> - its own flag was never touched - and still be
    /// off the screen because an ANCESTOR (the layer it hangs from) is hidden. A check that read
    /// only <c>model.isVisible</c> would call that cell visible while the operator sees nothing.
    /// </remarks>
    public Task<DrawioCellVisibility[]> MarkedCellVisibilityAsync() => InTheGraphAsync<DrawioCellVisibility[]>(
        """
        const graph = ui.editor.graph;
        const model = graph.getModel();
        const out = [];
        for (const key of Object.keys(model.cells)) {
          const cell = model.cells[key];
          const wcs = cell.value && cell.value.getAttribute ? cell.value.getAttribute('wcs') : null;
          if (wcs !== 'device' && wcs !== 'junction') continue;
          const modelVisible = model.isVisible(cell);
          const state = graph.getView().getState(cell);
          const node = state && state.shape && state.shape.node;
          const rendered = !!node && w.getComputedStyle(node).display !== 'none'
            && w.getComputedStyle(node).visibility !== 'hidden';
          out.push({ id: String(cell.id), mark: wcs, modelVisible, rendered });
        }
        return out;
        """);

    /// <summary>Copies the size of the single selected shape (draw.io's own <c>Alt+Shift+F</c>).</summary>
    public Task CopySizeAsync() => _page.Keyboard.PressAsync("Alt+Shift+F");

    /// <summary>Gives the copied size to whatever is selected (draw.io's own <c>Alt+Shift+V</c>).</summary>
    public Task PasteSizeAsync() => _page.Keyboard.PressAsync("Alt+Shift+V");

    /// <summary>
    /// Arms <b>one</b> unrelated gesture of the operator's, to land in the window between the birth
    /// of a dropped shape and the arrival of the label the page puts on it: the first time a cell is
    /// born in the drawing, the cell named here is moved by <paramref name="dx"/> and
    /// <paramref name="dy"/>, through draw.io's own <c>graph.moveCells</c> - the same road a drag
    /// with the mouse takes, and an undoable edit like any other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is not a real drag.</b> The window it has to fall inside is the creation's HTTP
    /// round trip, measured at about 137 ms for one cell: a mouse drag driven from outside the
    /// browser cannot be aimed at it, and a test that tried would sometimes measure the window and
    /// sometimes measure nothing - the worst kind of green. Here the moment is taken from draw.io's
    /// own CHANGE event, which is the very event the bridge hears the birth on, and the move is put
    /// in a task of its own with <c>setTimeout(0)</c>.
    /// </para>
    /// <para>
    /// <b>Why the timer and not the listener itself.</b> Inside a CHANGE listener the model is still
    /// closing its own update (<c>endingUpdate</c> is true, and a nested <c>endUpdate</c> commits
    /// nothing): a change made there would not become an edit of its own, which is the opposite of
    /// what this arms. A task of its own runs with the model closed and the birth already in the
    /// undo stack.
    /// </para>
    /// <para>
    /// <b>Whether it fired is asked, never assumed</b> - see <see cref="TheUnrelatedGestureAsync"/>.
    /// A test that armed this and did not look would go green on a window that never opened.
    /// </para>
    /// <para>
    /// <b>What "a cell is born" means here is the bridge's own answer, and it used to be a narrower
    /// one.</b> The test was <c>previous == null</c>, which is true of a shape dropped out of the
    /// panel and <b>false of a paste</b>: measured on 2026-09-18, the <c>mxChildChange</c> of a
    /// Ctrl+V arrives with <c>previous</c> set to the temporary parent draw.io decodes the clipboard
    /// into, so this armed nothing at all on a paste (<c>fired=0</c>) while the summary said "the
    /// first time a cell is born". Nothing was measuring it - the only caller drops a shape - which
    /// is exactly how a trap is laid for the next test. It now asks the same question
    /// <c>watchAdditions</c> asks, walked and not looked up: the current parent belongs to this
    /// model and the previous one does not. A move between parents (Ctrl+G) is still not a birth,
    /// because there the previous parent is a layer of this model.
    /// </para>
    /// </remarks>
    public Task<string> ArmAnUnrelatedGestureAtTheNextBirthAsync(string cellId, double dx, double dy) =>
        InTheGraphAsync<string>(
            """
            const graph = ui.editor.graph;
            const model = graph.getModel();
            const w = graph.container.ownerDocument.defaultView;
            const victim = model.getCell(args.cellId);
            if (!victim) throw new Error('the drawing has no cell ' + args.cellId + ' to move');
            if (w.__wcsUnrelatedListener) model.removeListener(w.__wcsUnrelatedListener);
            w.__wcsUnrelatedFired = 0;
            w.__wcsUnrelatedDone = 0;
            w.__wcsUnrelatedError = '';
            // The root is read at every question and not captured once: a reseeding replaces it, and
            // a stale root would answer "not in the model" for every cell of the new drawing.
            const inModel = (cell) => { const root = model.getRoot(); for (let node = cell; node != null; node = node.parent) if (node === root) return true; return false; };
            const listener = function (sender, evt) {
              if (w.__wcsUnrelatedFired) return;
              const edit = evt.getProperty('edit');
              if (!edit || !edit.changes) return;
              let aBirth = false;
              for (const change of edit.changes)
                if (change && change.child && inModel(change.parent) && !inModel(change.previous)) aBirth = true;
              if (!aBirth) return;
              // Claimed BEFORE the timer is set, so a second birth arriving in the meantime cannot
              // arm a second move: this stands for one gesture of the operator's, not for a stream.
              w.__wcsUnrelatedFired = 1;
              w.setTimeout(function () {
                try { graph.moveCells([victim], args.dx, args.dy); w.__wcsUnrelatedDone = 1; }
                catch (e) { w.__wcsUnrelatedError = String(e); }
              }, 0);
            };
            w.__wcsUnrelatedListener = listener;
            model.addListener(w.mxEvent.CHANGE, listener);
            return 'armed on ' + args.cellId;
            """,
            new { cellId, dx, dy });

    /// <summary>
    /// Whether the gesture armed by <see cref="ArmAnUnrelatedGestureAtTheNextBirthAsync"/> has been
    /// seen off (<c>Fired</c>), has really moved the cell (<c>Done</c>), and what it said if it
    /// could not.
    /// </summary>
    public Task<DrawioUnrelatedGesture> TheUnrelatedGestureAsync() => InTheGraphAsync<DrawioUnrelatedGesture>(
        """
        const w = ui.editor.graph.container.ownerDocument.defaultView;
        return { fired: !!w.__wcsUnrelatedFired, done: !!w.__wcsUnrelatedDone, error: String(w.__wcsUnrelatedError || '') };
        """);

    /// <summary>
    /// The state of draw.io's page tab bar, as <c>"display/offsetHeight"</c>, or <c>"absent"</c>
    /// when <c>ui.tabContainer</c> is not there at all. The two answers are deliberately different:
    /// "absent" means the bridge's first move (hiding that very object) quietly did nothing, and
    /// that has to be read as a failure rather than as a bar that is not showing.
    /// </summary>
    public Task<string> TheTabBarAsync() => InTheGraphAsync<string>(
        """
        const w = ui.editor.graph.container.ownerDocument.defaultView;
        if (!ui.tabContainer) return 'absent';
        return w.getComputedStyle(ui.tabContainer).display + '/' + ui.tabContainer.offsetHeight;
        """);

    /// <summary>
    /// Walks every road to a second page that this draw.io exposes and gives back, for each one,
    /// how many pages there were before and after and whether it refused. Every road here was
    /// <b>measured</b> really adding a page before it was shut - the first six on 2026-09-17, the
    /// three that go through <c>importXml</c> on 2026-09-18.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It refuses to measure nothing.</b> Before trying anything it checks that each road still
    /// exists, and throws if one does not: an attempt on a function that is no longer there would
    /// be caught as "refused", and the whole measurement would go green while measuring air. That
    /// is the failure mode this branch produces most often, and here it is closed by hand.
    /// </para>
    /// <para>
    /// The "+" of the tab bar is not among them as a click, and it does not need to be: read out of
    /// the bundle we host, its handler <b>is</b> <c>actions.get("insertPage").funct()</c>, which is
    /// the first road below. Clicking it after the bar has been hidden would measure the hiding,
    /// not the block - see <see cref="TheTabBarAsync"/> for that half.
    /// </para>
    /// <para>
    /// Deleting the only page is one of the roads, and it belongs here: draw.io's own
    /// <c>removePage</c>, taking the last page away, calls <c>insertPage()</c> to put a fresh one in
    /// its place - so a page can be born from a deletion too.
    /// </para>
    /// </remarks>
    public Task<string[]> TryEveryRoadToASecondPageAsync() => InTheGraphAsync<string[]>(
        """
        const roads = [];
        const require = (there, what) => { if (!there) throw new Error('draw.io no longer has ' + what + ': this measurement would go green without measuring anything'); };
        require(ui.actions && ui.actions.get('insertPage'), "the 'insertPage' action (the one behind the + of the tab bar)");
        require(ui.actions.get('duplicatePage'), "the 'duplicatePage' action (the one in a tab's context menu)");
        require(typeof ui.insertPage === 'function', 'ui.insertPage');
        require(typeof ui.duplicatePage === 'function', 'ui.duplicatePage');
        require(typeof ui.removePage === 'function', 'ui.removePage');
        require(typeof ui.importXml === 'function', 'ui.importXml (the road Ctrl+V and a dropped file take)');
        require(typeof ui.pasteXml === 'function', 'ui.pasteXml (what a real Ctrl+V of XML ends in)');
        require(typeof ui.setFileData === 'function', 'ui.setFileData (the road the bridge\'s own load() takes)');
        const manyPages = '<mxfile host="127.0.0.1" pages="2">'
          + '<diagram id="roadA" name="Page-1"><mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel></diagram>'
          + '<diagram id="roadB" name="Page-2"><mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel></diagram></mxfile>';
        const onePage = '<mxfile host="127.0.0.1">'
          + '<diagram id="roadC" name="Page-1"><mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel></diagram></mxfile>';
        const count = () => (ui.pages ? ui.pages.length : -1);
        const attempt = (name, fn) => {
          const before = count();
          let outcome = 'did not refuse';
          try { fn(); } catch (e) { outcome = 'refused: ' + (e && e.message ? e.message : String(e)); }
          roads.push(name + ' | before=' + before + ' after=' + count() + ' | ' + outcome);
        };
        attempt("the + of the tab bar (actions.get('insertPage').funct)", () => ui.actions.get('insertPage').funct());
        attempt("a tab's context menu (actions.get('duplicatePage').funct)", () => ui.actions.get('duplicatePage').funct());
        attempt('ui.insertPage()', () => ui.insertPage());
        attempt('ui.duplicatePage(ui.currentPage)', () => ui.duplicatePage(ui.currentPage));
        attempt('ui.removePage(ui.currentPage)', () => ui.removePage(ui.currentPage));
        // The three that do NOT pass by insertPage, and that were open behind the block for a whole
        // batch: an <mxfile> of two <diagram> made a page each, and a single <diagram> made one of
        // its own as soon as addNewPage was true, which is what holding Ctrl while dropping a file
        // sets. They are expected to add nothing AND not to refuse: here the import goes through,
        // the first page arrives, and only the pages are taken away.
        attempt('ui.importXml(<mxfile> of two <diagram>)', () => ui.importXml(manyPages));
        attempt('ui.importXml(one <diagram>, addNewPage=true)', () => ui.importXml(onePage, 0, 0, true, null, true));
        attempt('ui.pasteXml(<mxfile> of two <diagram>)', () => ui.pasteXml(manyPages, false, true));
        // The road no interface of ours can take away: Extras > Edit Diagram, whose Apply button
        // ends in editor.setGraphXml when what was pasted is an <mxfile> (read out of the bundle we
        // host). It is walked LAST because it replaces the drawing with what is pasted.
        attempt('Extras > Edit Diagram with an <mxfile> of two <diagram> (editor.setGraphXml)', () => {
          const w = ui.editor.graph.container.ownerDocument.defaultView;
          const pasted = w.mxUtils.parseXml('<mxfile><diagram id="a" name="Page-1"><mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel></diagram><diagram id="b" name="Page-2"><mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel></diagram></mxfile>');
          ui.editor.setGraphXml(pasted.documentElement);
        });
        // And the road the bridge's OWN seeding takes, which is the one that was open behind
        // everything above: EditorUi.setFileData builds its pages straight out of the <diagram> it
        // is handed, with no ChangePage anywhere in it. It is walked LAST of all because it throws
        // the drawing away and puts this document in its place.
        attempt('the seeding itself (ui.setFileData with an <mxfile> of two <diagram>)', () => ui.setFileData(manyPages));
        // draw.io hands a refusal of its own to handleError, which opens a dialog over the canvas,
        // and more than one road can leave one behind. They are closed here - one call closes one -
        // so that whatever follows is not clicking through a stack of modals.
        try { for (let i = 0; i < 10 && ui.dialogs && ui.dialogs.length > 0; i++) ui.hideDialog(); } catch (e) { /* none was open */ }
        return roads;
        """);

    /// <summary>
    /// Makes a second page <b>the way nothing in this Editor can any more</b>: through draw.io's
    /// own function taken off the prototype, which the bridge replaces on the instance alone.
    /// Gives back how many pages there are afterwards, because a measurement that assumed the
    /// second page had been made would prove nothing about the save that follows.
    /// </summary>
    /// <remarks>
    /// It stands for what the bridge cannot answer for, and NOT for a particular menu entry: the
    /// line that used to be here named Extras &gt; Edit Diagram, which the same commit had measured
    /// not to make a page, and which this embedded draw.io has no menu bar to reach anyway
    /// (<c>ui.menubar</c> is null, measured 2026-09-18). What is true is that the block is our code
    /// inside somebody else's application: it can fail to install - a console line is all it says -
    /// and the roads it shuts are the roads somebody thought to try. One that nobody had tried, a
    /// plain Ctrl+V of the text of a <c>.drawio</c> file, was open behind that block for a whole
    /// batch and took the drawing from one page to three. That is why the save counts the pages
    /// instead of trusting the interface, and why this makes its page through draw.io's own
    /// prototype rather than through any gesture.
    /// </remarks>
    public Task<int> MakeASecondPageBehindTheBlocksBackAsync() => InTheGraphAsync<int>(
        """
        const w = ui.editor.graph.container.ownerDocument.defaultView;
        if (!w.EditorUi || typeof w.EditorUi.prototype.insertPage !== 'function')
            throw new Error('draw.io no longer carries EditorUi.prototype.insertPage: a second page can no longer be made by hand, and this measurement is no longer measuring anything');
        w.EditorUi.prototype.insertPage.call(ui);
        return ui.pages ? ui.pages.length : -1;
        """);

    /// <summary>
    /// Puts <paramref name="text"/> on the browser's <b>system</b> clipboard, so that the Ctrl+V
    /// that follows is the operator's own gesture and not a call of ours.
    /// </summary>
    /// <remarks>
    /// The permission is granted here and not in <see cref="OpenAsync"/>: a page that could write
    /// the clipboard from the first instant would not be the page the operator opens. Chromium wants
    /// the document focused for <c>navigator.clipboard.writeText</c>, which is why this is called
    /// before the click that moves the focus into draw.io's iframe.
    /// </remarks>
    public async Task PutOnTheSystemClipboardAsync(string text)
    {
        await _context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await _page.EvaluateAsync("async (t) => { await navigator.clipboard.writeText(t); }", text);
    }

    /// <summary>
    /// The text of the dialog draw.io is showing over the canvas, whitespace squeezed, or
    /// <c>"no dialog"</c> when there is none.
    /// </summary>
    /// <remarks>
    /// It reads <c>ui.dialogs</c>, draw.io's own stack, and not a class name: this is what a refusal
    /// or a warning of draw.io's really looks like to the operator, and the batch of 2026-09-17 left
    /// it as a reserve - the block's message was never read off the screen by anything. Here it is.
    /// </remarks>
    public Task<string> TheDialogOverTheCanvasAsync() => InTheGraphAsync<string>(
        """
        if (!ui.dialogs || ui.dialogs.length === 0) return 'no dialog';
        const el = ui.dialogs[ui.dialogs.length - 1].container;
        return (el && el.innerText ? el.innerText : '(empty)').replace(/\s+/g, ' ').trim();
        """);

    /// <summary>
    /// Waits for draw.io to put a dialog over the canvas and gives back its text. Failing, it says
    /// so instead of giving back <c>"no dialog"</c>: a caller that asserted on a string it never
    /// waited for would be measuring how fast the browser is.
    /// </summary>
    public async Task<string> WaitForTheDialogOverTheCanvasAsync()
    {
        var ceiling = DateTime.UtcNow.AddMilliseconds(ConditionCeiling);
        var text = await TheDialogOverTheCanvasAsync();
        while (text == "no dialog" && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(SettleInterval);
            text = await TheDialogOverTheCanvasAsync();
        }

        if (text == "no dialog")
            throw new InvalidOperationException(
                "draw.io non ha mostrato nessun dialogo sulla tela: quello che e' stato lasciato fuori non e' stato dichiarato a nessuno.");
        return text;
    }

    /// <summary>
    /// Waits for a cell carrying this label to be on the canvas, and if it does not come says which
    /// labels are there: "the shape did not arrive" without saying what did arrive sends whoever
    /// reads it back to the beginning.
    /// </summary>
    public async Task WaitForTheLabelInTheDrawingAsync(string label)
    {
        var ceiling = DateTime.UtcNow.AddMilliseconds(ConditionCeiling);
        var labels = await LabelsInTheDrawingAsync();
        while (!labels.Contains(label) && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(SettleInterval);
            labels = await LabelsInTheDrawingAsync();
        }

        if (!labels.Contains(label))
            throw new InvalidOperationException(
                $"Il disegno non porta nessuna cella con l'etichetta \"{label}\": porta {string.Join(", ", labels.Select(one => $"\"{one}\""))}.");
    }

    /// <summary>Closes every dialog draw.io has open, so that what follows is not clicking through a stack of them.</summary>
    public Task<int> CloseEveryDialogAsync() => InTheGraphAsync<int>(
        """
        try { for (let i = 0; i < 10 && ui.dialogs && ui.dialogs.length > 0; i++) ui.hideDialog(); } catch (e) { /* none was open */ }
        return ui.dialogs ? ui.dialogs.length : 0;
        """);

    /// <summary>
    /// The labels of every cell sitting directly on the canvas, as draw.io's model has them. It is
    /// what tells "the first page came in" from "nothing came in", which a page count alone cannot.
    /// </summary>
    public Task<string[]> LabelsInTheDrawingAsync() => InTheGraphAsync<string[]>(
        """
        const model = ui.editor.graph.getModel();
        const parent = ui.editor.graph.getDefaultParent();
        const labels = [];
        for (let i = 0; i < model.getChildCount(parent); i++) {
          const value = model.getValue(model.getChildAt(parent, i));
          if (value == null) { labels.push(''); continue; }
          labels.push(typeof value === 'string' ? value : (value.getAttribute('label') || ''));
        }
        return labels;
        """);

    /// <summary>
    /// Drops a file on the canvas, the way a file manager does: a real <c>File</c> in a real
    /// <c>DataTransfer</c>, on a <c>drop</c> event dispatched at draw.io's own graph container -
    /// which is the element draw.io listens on (<c>mxEvent.addListener(graph.container, "drop", ...)</c>,
    /// read out of the bundle we host).
    /// </summary>
    /// <param name="withCtrl">
    /// Ctrl held down while dropping. It is not a detail: <c>EditorUi.importFile</c> hands
    /// <c>mxEvent.isControlDown(evt)</c> to <c>importXml</c> as its <c>addNewPage</c> argument, so
    /// this is what turned a one-page file into a page of its own - measured, 1 -&gt; 2.
    /// </param>
    /// <remarks>
    /// <b>What this is not:</b> an operating-system drag. Playwright cannot hold a file with the
    /// mouse, so the gesture is reproduced at the edge of the browser instead - and everything past
    /// that edge, draw.io's listener included, is the real thing. What makes it more than a call of
    /// ours is the anchor on the far side: the shape of the dropped file has to ARRIVE on the canvas,
    /// which no amount of dispatching would achieve if the listener were not really handling it.
    /// </remarks>
    public Task<string> DropAFileOnTheCanvasAsync(string name, string content, bool withCtrl) => InTheGraphAsync<string>(
        """
        const w = ui.editor.graph.container.ownerDocument.defaultView;
        const container = ui.editor.graph.container;
        if (!w.Graph.fileSupport)
            throw new Error('this draw.io does not take dropped files any more (Graph.fileSupport is false): dropping one would measure nothing');
        const file = new w.File([args.content], args.name, { type: 'application/xml' });
        const carried = new w.DataTransfer();
        carried.items.add(file);
        if (carried.files.length !== 1)
            throw new Error('the drop event would carry no file: this measurement would go green without measuring anything');
        const box = container.getBoundingClientRect();
        container.dispatchEvent(new w.DragEvent('drop', {
          bubbles: true, cancelable: true, dataTransfer: carried, ctrlKey: args.withCtrl,
          clientX: Math.round(box.left + box.width / 2), clientY: Math.round(box.top + box.height / 2)
        }));
        return args.name + (args.withCtrl ? ' dropped with Ctrl' : ' dropped');
        """,
        new { content, name, withCtrl });

    /// <summary>
    /// Runs code inside draw.io with its own interface (<c>ui</c>) and the argument (<c>args</c>) in
    /// hand, through the very door the bridge goes through: <c>Draw.loadPlugin</c>. It lives here
    /// once, because every copy of this dance is a copy that can drift.
    /// </summary>
    private async Task<T> InTheGraphAsync<T>(string body, object? args = null)
    {
        await WaitForConditionAsync(
            "() => { const f = document.getElementById('drawio-frame');" +
            " try { return !!(f && f.contentWindow && f.contentWindow.Draw && f.contentWindow.Draw.loadPlugin); }" +
            " catch (e) { return false; } }",
            "draw.io non ha mai esposto Draw.loadPlugin nell'iframe: il modello del disegno non si raggiunge");

        return await _page.EvaluateAsync<T>(
            """
            (args) => new Promise((resolve, reject) => {
              const w = document.getElementById('drawio-frame').contentWindow;
              const timer = setTimeout(() => reject(new Error('Draw.loadPlugin never called back')), 30000);
              w.Draw.loadPlugin(function (ui) {
                clearTimeout(timer);
                try { resolve((function () { __BODY__ })()); } catch (e) { reject(e); }
              });
            })
            """.Replace("__BODY__", body),
            args);
    }

    /// <summary>
    /// What draw.io last said was selected, as <see cref="WatchSelectionAsync"/> heard it. Empty
    /// both when nothing is selected and when nothing was ever selected.
    /// </summary>
    public async Task<IReadOnlyList<DrawioSelectedCell>> SelectedCellsAsync() =>
        await _page.EvaluateAsync<DrawioSelectedCell[]>("() => window.__wcsSelection || []");

    /// <summary>
    /// The line the page shows about the selection (<c>#drawio-selection</c>), or null when the page
    /// is showing no line at all - which it does until draw.io has said something.
    /// </summary>
    public Task<string?> SelectionLineAsync() => TextOrNull("#drawio-selection");

    /// <summary>
    /// Waits until the page's selection line says exactly this, and tells what the page was showing
    /// if it never does. Waiting is not optional: what is selected travels from draw.io to the JS
    /// bridge, from there over the Blazor circuit and back as a render, so reading the line straight
    /// after a click would be reading the one before.
    /// </summary>
    public Task WaitForSelectionLineAsync(string expected) => WaitForConditionAsync(
        "() => { const line = document.getElementById('drawio-selection');" +
        $" return !!line && line.textContent.trim() === {JsonSerializer.Serialize(expected)}; }}",
        $"la riga della selezione non e' mai diventata \"{expected}\"");

    /// <summary>
    /// Reloads draw.io INSIDE THE SAME <c>&lt;iframe&gt;</c> element - <c>contentWindow.location.reload()</c>,
    /// not a new element - and returns once the new document has replaced the old one and exposes
    /// <c>Draw.loadPlugin</c> again. What the bridge held just before is kept in the page, for
    /// <see cref="WhatTheBridgeHoldsAfterTheReloadAsync"/> to compare against.
    /// </summary>
    /// <remarks>
    /// The wait is on the DOCUMENT changing, and it has to be: right after <c>reload()</c> the old
    /// document is still there until the navigation commits, with its own <c>Draw</c> and its own
    /// labels on the canvas, so waiting for <c>Draw</c> alone - or for a device - could be answered
    /// by the draw.io that is about to go.
    /// </remarks>
    public async Task ReloadDrawioInTheSameFrameAsync()
    {
        await StepAsync(
            () => _page.EvaluateAsync(
                """
                async () => {
                  const bridge = await import('/_content/Wcs.WebEditor/drawio-bridge.js');
                  const f = document.getElementById('drawio-frame');
                  const target = bridge.liveTarget();
                  if (!target) throw new Error('the bridge held no graph before the reload: nothing to compare against');
                  window.__wcsBeforeReload = {
                    frame: f, window: f.contentWindow, document: f.contentWindow.document, graph: target.graph,
                  };
                  f.contentWindow.location.reload();
                }
                """),
            "draw.io could not be reloaded inside its iframe");

        await WaitForConditionAsync(
            "() => { const f = document.getElementById('drawio-frame'); const b = window.__wcsBeforeReload;" +
            " try { return !!(f && f.contentWindow && f.contentWindow.document !== b.document" +
            " && f.contentWindow.Draw && f.contentWindow.Draw.loadPlugin); }" +
            " catch (e) { return false; } }",
            "the reloaded iframe never brought up a new draw.io exposing Draw.loadPlugin");
    }

    /// <summary>
    /// After <see cref="ReloadDrawioInTheSameFrameAsync"/>: what draw.io itself hands out now - asked
    /// through <c>Draw.loadPlugin</c>, the same door the bridge goes through - against what the
    /// bridge's <c>liveTarget()</c> holds, and against what both held before the reload.
    /// </summary>
    public Task<DrawioReloadedFrame> WhatTheBridgeHoldsAfterTheReloadAsync() => _page.EvaluateAsync<DrawioReloadedFrame>(
        """
        async () => {
          const bridge = await import('/_content/Wcs.WebEditor/drawio-bridge.js');
          const b = window.__wcsBeforeReload;
          const f = document.getElementById('drawio-frame');
          const w = f.contentWindow;
          const fresh = await new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error('Draw.loadPlugin never called back')), 30000);
            w.Draw.loadPlugin((ui) => { clearTimeout(timer); resolve(ui.editor.graph); });
          });
          const target = bridge.liveTarget();
          return {
            sameFrame: f === b.frame,
            sameWindow: w === b.window,
            newDocument: w.document !== b.document,
            drawioHasANewGraph: fresh !== b.graph,
            bridgeHoldsTheNewGraph: !!target && target.graph === fresh,
            bridgeStillHoldsTheOldGraph: !!target && target.graph === b.graph,
          };
        }
        """);

    /// <summary>
    /// The note that says why the properties panel cannot be edited, or <c>null</c> when there is no
    /// panel at all: the way of asking the page "is the panel there?" by reading what the operator
    /// would read, not a mark put there for the test. Its text content: since the passata grafica the
    /// note is behind the "?" of the panel's heading, shut unless the operator opens it, and what it
    /// says does not depend on that.
    /// </summary>
    public Task<string?> PropertyPanelNoticeAsync() => TextContentOfAsync("#drawio-property-subject");

    /// <summary>
    /// Waits until the panel shows this value in this property (the contract's field name, for
    /// instance <c>Name</c>). Waiting is not optional: the trip goes from the click inside draw.io
    /// to the bridge, over the Blazor circuit, to the host for the configuration and back as a
    /// render, so reading the field straight after the click would be reading the one before.
    /// </summary>
    public Task WaitForPropertyAsync(string propertyName, string expected)
    {
        var selector = $"#drawio-property-panel div.property-panel-field[data-property='{propertyName}'] input";
        return WaitForConditionAsync(
            $"() => {{ const field = document.querySelector({JsonSerializer.Serialize(selector)});" +
            $" return !!field && field.value === {JsonSerializer.Serialize(expected)}; }}",
            $"the panel never showed \"{expected}\" in the property {propertyName}");
    }

    /// <summary>
    /// Waits until the properties panel is <b>not there</b>: the other half of the test, and the
    /// half that catches the real defect - a panel left standing on the previous device once the
    /// selection is no longer one device.
    /// </summary>
    public Task WaitForNoPropertyPanelAsync() => WaitForConditionAsync(
        "() => !document.getElementById('drawio-property-panel')",
        "the properties panel never went away");

    /// <summary>
    /// The fields of the panel the operator could really type into, by property name (plus
    /// <c>save</c> when the button that writes is live): <b>empty</b> is what "read only" actually
    /// means. Measured field by field in the DOM, not deduced from the <c>CanEdit</c> handed to the
    /// component - they are sixty fields of five different shapes, and a single one left live would
    /// be a write this batch does not provide for.
    /// </summary>
    public async Task<IReadOnlyList<string>> WriteablePropertyFieldsAsync() =>
        await _page.EvaluateAsync<string[]>(
            """
            () => {
              const panel = document.getElementById('drawio-property-panel');
              if (!panel) return ['(no panel)'];
              const open = [];
              panel.querySelectorAll('input, select').forEach((element) => {
                if (element.disabled) return;
                const field = element.closest('[data-property]');
                open.push(field ? field.getAttribute('data-property') : element.tagName);
              });
              const save = panel.querySelector('#property-panel-save-button');
              if (save && !save.disabled) open.push('save');
              return open;
            }
            """);

    /// <summary>
    /// The categories the panel is showing, as the contract declares them. Read from the
    /// <c>data-category</c> attribute and not from the <c>legend</c>'s text: the stylesheet renders
    /// that in capitals (<c>text-transform</c>) and <c>innerText</c> returns the RENDERED text, so
    /// comparing it with "Basic Option" would fail for the wrong reason.
    /// </summary>
    public async Task<IReadOnlyList<string>> PropertyCategoriesAsync() =>
        await _page.EvaluateAsync<string[]>(
            "() => Array.from(document.querySelectorAll('#drawio-property-panel fieldset.property-panel-group'))" +
            ".map((group) => group.getAttribute('data-category'))");

    /// <summary>
    /// Types a value into one of the panel's fields (batch C), the way a person does: the field is
    /// filled and then LEFT, because Blazor listens to the DOM's <c>change</c> event and filling
    /// alone only produces <c>input</c> - the value would never reach the working copy, and the
    /// confirmation would write what was there before while the screen showed the new text.
    /// </summary>
    public async Task TypePropertyAsync(string propertyName, string value)
    {
        var field = _page.Locator($"#drawio-property-panel div.property-panel-field[data-property='{propertyName}'] input");
        await StepAsync(
            () => field.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ConditionCeiling }),
            $"il campo \"{propertyName}\" non e' comparso nel pannello delle proprieta'");
        await field.FillAsync(value);
        await field.PressAsync("Tab");
    }

    /// <summary>
    /// Confirms the panel: the <c>Salva</c> button of the shared <c>PropertyPanel</c>. The page then
    /// asks for the edit permit and writes - see the page's own note above the fields.
    /// </summary>
    public Task ConfirmPropertiesAsync() => StepAsync(
        () => _page.ClickAsync("#property-panel-save-button", new PageClickOptions { Timeout = ConditionCeiling }),
        "il pulsante di conferma del pannello non si e' lasciato premere");

    /// <summary>
    /// Waits until the panel says that the configuration of <paramref name="naming"/> reached the
    /// plant, and tells what it was saying instead if it never does - which for a refusal is the
    /// line that names the reason.
    /// </summary>
    /// <remarks>
    /// <b>The device's name is part of the condition, and it is not decoration.</b> The written line
    /// stays on screen until the NEXT confirmation is handled on the server, so waiting for "there
    /// is a written line" after a second confirmation reads the FIRST one and returns before
    /// anything has been written - measured: the read-back from the host then found the old value
    /// and the test failed for a reason that was its own.
    /// </remarks>
    public Task WaitForPropertiesWrittenAsync(string naming) => WaitForConditionAsync(
        "() => { const line = document.getElementById('drawio-property-written');" +
        $" return !!line && line.textContent.includes({JsonSerializer.Serialize(naming)}); }}",
        $"il pannello non ha mai dichiarato di aver scritto la configurazione di \"{naming}\"");

    /// <summary>Why the last confirmation did not write, or null when there is no such line.</summary>
    public Task<string?> PropertyWriteErrorAsync() => TextOrNull("#drawio-property-write-error");

    // ---- Row 15: pointing a shape at a device by hand -------------------------------------------

    /// <summary>Whether the reassociation box is on screen at all: it shows up only for a single cell the drawing marks as a device and the plant does not know.</summary>
    public async Task<bool> TheReassociationBoxIsThereAsync() =>
        await _page.Locator("#drawio-reassociate").CountAsync() > 0;

    /// <summary>
    /// Waits for the reassociation box, and says what the column was showing instead when it never
    /// comes - which is the whole diagnosis: the hint means the page did not call the shape an
    /// orphan, the properties panel means it found a device for it.
    /// </summary>
    public Task WaitForTheReassociationBoxAsync() => WaitForConditionAsync(
        "() => !!document.getElementById('drawio-reassociate')",
        "il riquadro per riassociare la sagoma non e' comparso: la pagina non ha riconosciuto la cella come "
        + "una marchiata che l'impianto non conosce");

    /// <summary>The line that says how many devices of the plant have no shape on the canvas - the identifiers this gesture can be aimed at.</summary>
    public Task<string?> ReassociationCandidatesAsync() => TextOrNull("#drawio-reassociate-candidates-summary");

    /// <summary>
    /// Waits until that line says what is expected, which is the only honest way to know the box is
    /// about the canvas <b>as it is now</b>.
    /// </summary>
    /// <remarks>
    /// <b>Measured on 2026-09-20, in the third full run of the suite and not in the first two.</b>
    /// After deleting a device's cell and selecting the orphan again, waiting for the BOX returned
    /// at once - the box was still the one the previous selection had put up, because draw.io's
    /// selection events reach Blazor by their own road and the page had not read the canvas again
    /// yet. The list underneath was therefore the old one, and the test read an empty list of
    /// candidates from a page that was about to show the right one. It is the same trap as the
    /// outcome line (see <see cref="ReassociateAsync"/>): an element that is already there says
    /// nothing about the gesture just made.
    /// </remarks>
    public Task WaitForTheReassociationCandidatesAsync(string containing) => WaitForConditionAsync(
        "() => { const s = document.getElementById('drawio-reassociate-candidates-summary');" +
        $" return !!s && s.textContent.includes({JsonSerializer.Serialize(containing)}); }}",
        $"la riga dei dispositivi senza sagoma non ha mai detto \"{containing}\": il riquadro sta ancora " +
        "parlando della tela di prima");

    /// <summary>
    /// Opens the candidate list, which the page shows shut. It has to be opened to be read: the
    /// lines of a closed <c>&lt;details&gt;</c> are not rendered, and what comes back from them is
    /// the empty string - a test that read them shut would be asserting on nothing.
    /// </summary>
    public async Task OpenTheReassociationCandidatesAsync()
    {
        await StepAsync(
            () => _page.ClickAsync("#drawio-reassociate-candidates-summary", new PageClickOptions { Timeout = ConditionCeiling }),
            "la riga dei dispositivi senza sagoma non si e' lasciata premere");
        await WaitForConditionAsync(
            "() => { const d = document.getElementById('drawio-reassociate-candidates'); return !!d && d.open; }",
            "l'elenco dei dispositivi senza sagoma non si e' aperto");
    }

    /// <summary>The devices offered by name and identifier, one line each. The list has to be open: see <see cref="OpenTheReassociationCandidatesAsync"/>.</summary>
    public Task<IReadOnlyList<string>> ReassociationCandidateLinesAsync() => LinesOf("li.drawio-reassociate-candidate");

    /// <summary>
    /// Types an identifier into the box and presses "Associa", the way a person does. Filled and
    /// then LEFT for the same reason as the properties panel's fields - Blazor listens to the DOM -
    /// except that this field is bound to <c>oninput</c>, so the Tab is belt and braces and costs
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <b>It waits for the page's own ROUND COUNTER to move</b>, not for the outcome line to exist,
    /// and the difference was measured on 2026-09-20 while this row was being built: the line is
    /// still on the page from the gesture before, so a wait on its presence returned at once and
    /// the test read the FIRST refusal as the answer to the SECOND gesture - green on a gesture the
    /// page had not made yet. Waiting for the TEXT to change would not close it either: two
    /// "Associa" can end with the very same sentence. It is the same lesson, and the same shape, as
    /// <see cref="SaveAsync"/>'s counter.
    /// </remarks>
    public async Task ReassociateAsync(string typed)
    {
        var field = _page.Locator("#drawio-reassociate-id");
        await StepAsync(
            () => field.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ConditionCeiling }),
            "la casella dell'identificativo non e' comparsa");
        await field.FillAsync(typed);
        await field.PressAsync("Tab");

        var before = await _page.EvaluateAsync<string?>(
            "() => { const r = document.getElementById('drawio-reassociate-round'); return r ? r.textContent : null; }");
        if (before is null)
            throw new InvalidOperationException(
                "La pagina non porta il contatore dei gesti \"Associa\" (#drawio-reassociate-round): senza di lui " +
                "questa attesa tornerebbe a guardare una riga che c'e' gia', e leggerebbe la risposta di prima.");

        await StepAsync(
            () => _page.ClickAsync("#drawio-reassociate-confirm", new PageClickOptions { Timeout = ConditionCeiling }),
            "il pulsante \"Associa\" non si e' lasciato premere");

        await WaitForConditionAsync(
            "() => { const r = document.getElementById('drawio-reassociate-round');" +
            $" return !!r && r.textContent !== {JsonSerializer.Serialize(before)}; }}",
            $"il gesto \"Associa\" non e' arrivato in fondo: il contatore della pagina e' ancora a {before}");
    }

    /// <summary>
    /// The answer to the last "Associa". The waiting is done by <see cref="ReassociateAsync"/>, on
    /// the round counter, so this is a plain read: a second wait here would be the very thing that
    /// wait exists to replace.
    /// </summary>
    public async Task<string> WaitForTheReassociationOutcomeAsync()
    {
        await WaitForConditionAsync(
            "() => !!document.getElementById('drawio-reassociate-outcome')",
            "il gesto \"Associa\" non ha lasciato nessuna riga di esito: la pagina non ha risposto affatto");
        return (await TextOrNull("#drawio-reassociate-outcome"))!;
    }

    /// <summary>Whether the outcome line is shown in the tone of something that went through: a refusal that read like a success would be the worst line this box can produce.</summary>
    public Task<bool> TheReassociationWentThroughAsync() => _page.EvaluateAsync<bool>(
        "() => { const line = document.getElementById('drawio-reassociate-outcome');" +
        " return !!line && line.classList.contains('drawio-property-written'); }");

    /// <summary>
    /// What the browser really APPLIES to the outcome line of the panel: the separating rule above
    /// it, and whether it may be squeezed. Returned as the browser computes it, not as the
    /// stylesheet reads.
    /// </summary>
    /// <remarks>
    /// <b>It is here because a stylesheet can be read wrong by the browser and by nobody else.</b> A
    /// comment closed halfway above this very rule turned the whole block into an invalid prelude:
    /// the selector produced no rule at all, the line lost its separator and became compressible
    /// again, and everything else on the page went on looking right. No test on this page could have
    /// seen it - all of them look at what is written, none at how it is drawn.
    /// </remarks>
    public async Task<(string BorderTop, string FlexShrink)> PropertyOutcomeStyleAsync()
    {
        var measured = await _page.Locator("#drawio-property-written").EvaluateAsync<string[]>(
            "e => { const s = getComputedStyle(e); return [s.borderTopWidth, s.flexShrink]; }");
        return (measured[0], measured[1]);
    }

    /// <summary>
    /// Waits until the panel says why the confirmation did not write, and that what it says contains
    /// this. Waiting is not optional: between the click and the line there is the Blazor circuit, a
    /// request for the permit and the host's answer.
    /// </summary>
    public Task WaitForPropertyWriteErrorAsync(string containing) => WaitForConditionAsync(
        "() => { const line = document.getElementById('drawio-property-write-error');" +
        $" return !!line && line.textContent.includes({JsonSerializer.Serialize(containing)}); }}",
        $"il pannello non ha mai dichiarato un rifiuto che contenesse \"{containing}\"");

    // ---- The warehouse cells -----------------------------------------------------------------

    /// <summary>
    /// Waits until the summary in the Linked Storage Cells window says this.
    /// </summary>
    public async Task WaitForCellsSummaryAsync(string containing)
    {
        try
        {
            await WaitForConditionAsync(
                "() => { const line = document.getElementById('drawio-cells-summary');" +
                $" return !!line && line.textContent.includes({JsonSerializer.Serialize(containing)}); }}",
                $"the cells line never said \"{containing}\"");
        }
        catch (InvalidOperationException ex)
        {
            // What the section REALLY said, attached to the failure: without it the message only says
            // that something did not arrive, and whoever reads it has to run the test again to know.
            throw new InvalidOperationException($"{ex.Message}{Environment.NewLine}The section said:{Environment.NewLine}{await CellsSectionAsync()}", ex);
        }
    }

    /// <summary>What the cells section says in plain text, for a failure message that can be read.</summary>
    public async Task<string> CellsSectionAsync()
    {
        var section = _page.Locator("#drawio-cells");
        return await section.CountAsync() == 0 ? "(no cells section)" : (await section.InnerTextAsync()).Trim();
    }

    /// <summary>
    /// Opens the dedicated Linked Storage Cells window the way a person does: right-click the device
    /// and choose its entry. Returns only once the windows module has shown it and given it focus.
    /// </summary>
    public Task OpenTheCellsAsync(string deviceName) =>
        OpenContextWindowAsync(deviceName, "Linked Storage Cells", "drawio-linked-storage-cells-window");

    /// <summary>Moves a contextual window to the half of the viewport opposite the named device.</summary>
    public async Task MoveWindowAwayFromDeviceAsync(string windowId, string deviceName)
    {
        var device = await DeviceBoxAsync(deviceName);
        var viewport = _page.ViewportSize ?? throw new InvalidOperationException("The browser viewport has no size.");
        const double width = 440;
        var left = device.X + device.Width / 2 < viewport.Width / 2 ? viewport.Width - width - 16 : 16;
        await PlaceWindowAsync(windowId, left, 180, width, 360);
    }

    /// <summary>Opens one of the device-context windows from draw.io's own menu.</summary>
    public async Task OpenContextWindowAsync(string deviceName, string entry, string windowId)
    {
        await RightClickOnDeviceAsync(deviceName);
        await ClickTheMenuEntryAsync(entry);
        await WaitForConditionAsync(TheWindowHasTheFocus(windowId),
            $"the context-menu entry '{entry}' did not show and focus the window '{windowId}'");
    }

    /// <summary>Ticks a cell's door in the table of the given side, the way a person does.</summary>
    public Task TickDoorAsync(string side, decimal cellId) => StepAsync(
        () => _page.CheckAsync($"#door-{side.ToLowerInvariant()}-{cellId}", new PageCheckOptions { Timeout = ConditionCeiling }),
        $"the box of door {side} of cell {cellId} did not let itself be ticked");

    /// <summary>Confirms the door map: the "Save" button of <c>DoorMappings</c>.</summary>
    public Task ConfirmDoorMappingsAsync() => StepAsync(
        () => _page.ClickAsync("#door-mappings-save-button", new PageClickOptions { Timeout = ConditionCeiling }),
        "the door map's \"Save\" button did not let itself be pressed");

    /// <summary>Chooses the cell to connect in the table of <c>LinkedCells</c>.</summary>
    public Task ChooseCellToLinkAsync(decimal cellId) => StepAsync(
        () => _page.CheckAsync($"#linked-cell-{cellId}", new PageCheckOptions { Timeout = ConditionCeiling }),
        $"the row of cell {cellId} did not let itself be chosen");

    /// <summary>Connects the chosen cell: the "Link" button of <c>LinkedCells</c>.</summary>
    public Task ConfirmCellLinkAsync() => StepAsync(
        () => _page.ClickAsync("#linked-cells-link-button", new PageClickOptions { Timeout = ConditionCeiling }),
        "the \"Link\" button did not let itself be pressed");

    /// <summary>
    /// Waits until the column declares what reached the plant. The device's name is part of the
    /// condition for the same reason as in <see cref="WaitForPropertiesWrittenAsync"/>: the line
    /// stays on screen until the next gesture, and waiting for "there is a line" would read the
    /// previous one.
    /// </summary>
    public Task WaitForCellsWrittenAsync(string naming) => WaitForConditionAsync(
        "() => { const line = document.getElementById('drawio-cells-written');" +
        $" return !!line && line.textContent.includes({JsonSerializer.Serialize(naming)}); }}",
        $"the column never declared it had written the cells of \"{naming}\"");

    /// <summary>Why the last gesture on the cells did not write, or null when there is no such line.</summary>
    public Task<string?> CellsWriteErrorAsync() => TextOrNull("#drawio-cells-write-error");

    /// <summary>
    /// Like <see cref="WaitForPropertyAsync"/>, but for a numeric field and <b>comparing the
    /// number</b>, not the text.
    /// </summary>
    /// <remarks>
    /// MEASURED: the same cell arrives from the warehouse as <c>900202</c> and from the WCS's own
    /// store as <c>900202.0</c> - the panel's converter writes out the scale the column keeps. A
    /// comparison on the text would fall over on that scale, that is on how the cell is stored, and
    /// not on what this test measures: that the reread after the link reached the sixty fields too.
    /// </remarks>
    public Task WaitForPropertyNumberAsync(string propertyName, decimal expected)
    {
        var selector = $"#drawio-property-panel div.property-panel-field[data-property='{propertyName}'] input";
        return WaitForConditionAsync(
            $"() => {{ const field = document.querySelector({JsonSerializer.Serialize(selector)});" +
            $" return !!field && field.value !== '' && Number(field.value) === {expected.ToString(System.Globalization.CultureInfo.InvariantCulture)}; }}",
            $"the field {propertyName} never carried the number {expected}");
    }

    /// <summary>
    /// A screenshot of the page into a file, <b>only when the environment variable
    /// <c>WCS_BROWSER_SHOTS</c> points at a folder</b>: it is for whoever has to LOOK at what the
    /// tests measure, and it measures nothing on its own.
    /// </summary>
    /// <remarks>
    /// Without the variable it does nothing and costs nothing, so it is not an artefact the suite
    /// leaves lying around on every run. It replaces no assertion: a test that settled for having
    /// saved an image would be proving nothing.
    /// </remarks>
    public async Task LookAtItAsync(string name)
    {
        if (Environment.GetEnvironmentVariable("WCS_BROWSER_SHOTS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        await _page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, name + ".png"), FullPage = false });
    }

    /// <summary>
    /// How the cells table really sits inside the 320 px column, measured from the browser: how far
    /// its right edge runs past the column's right edge (zero or less: it does not), whether there is
    /// more to see than what is seen, and what the browser really applies as <c>overflow-x</c>.
    /// </summary>
    /// <remarks>
    /// <b>It is here because it was seen happening.</b> In the first draft the seven columns ran past
    /// their container's right edge and Colonna, Piano and Porta were cut away with nothing saying so: no
    /// test of this batch could have noticed - they all look at what is written, none at how it is
    /// drawn - and only a screenshot showed it. This is the measurement that replaces the screenshot.
    /// </remarks>
    public async Task<(double Overflow, bool HasMoreToScroll, string OverflowX)> CellsTableReachAsync()
    {
        var measured = await _page.EvaluateAsync<string[]>(
            """
            () => {
              const windowContent = document.querySelector('#drawio-linked-storage-cells-window > .drawio-window-content');
              const scroll = document.querySelector('#drawio-cells-details .wms-cells-table-scroll');
              if (!windowContent || !scroll) return ['no table scroller', '', ''];
              const d = windowContent.getBoundingClientRect(), s = scroll.getBoundingClientRect();
              return [String(s.right - d.right), String(scroll.scrollWidth > scroll.clientWidth),
                      getComputedStyle(scroll).overflowX];
            }
            """);

        if (!double.TryParse(measured[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var overflow))
            throw new InvalidOperationException($"The cells table could not be measured: {measured[0]}");

        return (overflow, bool.Parse(measured[1]), measured[2]);
    }

    /// <summary>
    /// The contextual cells window after an attempted extreme resize: actual width and height, the
    /// shortest table viewport, and how far the action row extends below visible window content.
    /// </summary>
    public Task<double[]> CellsWindowUsabilityAsync() => _page.EvaluateAsync<double[]>(
        """
        () => {
          const w = document.getElementById('drawio-linked-storage-cells-window');
          const content = w && w.querySelector(':scope > .drawio-window-content');
          const scrolls = w ? Array.from(w.querySelectorAll('.wms-cells-table-scroll')) : [];
          const actions = w && w.querySelector('.wms-cells-actions');
          if (!w || !content || scrolls.length === 0 || !actions) return [-1, -1, -1, Number.POSITIVE_INFINITY];
          const box = w.getBoundingClientRect(), visible = content.getBoundingClientRect();
          const shortest = Math.min(...scrolls.map((scroll) => scroll.getBoundingClientRect().height));
          return [box.width, box.height, shortest, actions.getBoundingClientRect().bottom - visible.bottom];
        }
        """);

    /// <summary>How many property groups the column is still showing: the cells must not have taken them away.</summary>
    public Task<int> PropertyGroupCountAsync() =>
        _page.Locator("#drawio-property-panel fieldset.property-panel-group").CountAsync();

    /// <summary>
    /// Drops the selection the way a person does it: a <b>real click on the empty canvas</b>. Not
    /// Escape and not a call into draw.io - the gesture this has to prove is the one the operator
    /// makes.
    /// </summary>
    /// <remarks>
    /// The point is inside draw.io's own diagram container (the shapes panel and the format panel
    /// are outside it), and it is CHOSEN rather than fixed: the boxes of the named shapes are read
    /// and the click goes to the first corner of the container far enough from all of them. A fixed
    /// corner would work today and would start clicking on a device the day the seeded plant moves -
    /// and it would fail by selecting something else, which reads exactly like the defect this test
    /// is looking for.
    /// </remarks>
    public async Task ClickEmptyCanvasAsync(params string[] shapesToAvoid)
    {
        var canvas = await Frame.Locator("div.geDiagramContainer").BoundingBoxAsync()
            ?? throw new InvalidOperationException(
                "Il contenitore del disegno di draw.io non ha nessun riquadro sullo schermo: non c'e' nessuna tela da cliccare.");

        var taken = new List<LocatorBoundingBoxResult>();
        foreach (var name in shapesToAvoid) taken.Add(await BoxOf(Shape(name), name));

        (float X, float Y)[] corners =
        [
            (canvas.X + EmptySpotInset, canvas.Y + canvas.Height - EmptySpotInset),
            (canvas.X + canvas.Width - EmptySpotInset, canvas.Y + canvas.Height - EmptySpotInset),
            (canvas.X + EmptySpotInset, canvas.Y + EmptySpotInset),
            (canvas.X + canvas.Width - EmptySpotInset, canvas.Y + EmptySpotInset),
        ];

        foreach (var (x, y) in corners)
        {
            if (taken.Any(box => IsWithinReach(box, x, y))) continue;
            await _page.Mouse.ClickAsync(x, y);
            return;
        }

        throw new InvalidOperationException(
            "Nessuno dei quattro angoli della tela e' libero dalle sagome: non c'e' un punto vuoto da cliccare, " +
            "e un clic altrove selezionerebbe qualcosa invece di deselezionare.");
    }

    private static bool IsWithinReach(LocatorBoundingBoxResult box, float x, float y) =>
        x > box.X - EmptySpotClearance && x < box.X + box.Width + EmptySpotClearance &&
        y > box.Y - EmptySpotClearance && y < box.Y + box.Height + EmptySpotClearance;

    /// <summary>
    /// Aspetta una condizione nel browser, raccontando che cosa ha visto se non arriva (si veda
    /// <see cref="StepAsync"/>).
    /// </summary>
    private Task WaitForConditionAsync(string expression, string what) => StepAsync(
        () => _page.WaitForFunctionAsync(expression, null, new PageWaitForFunctionOptions { Timeout = ConditionCeiling }),
        what);

    /// <summary>
    /// Fa un passo nel browser e, se scade, <b>racconta che cosa ha visto</b>: quello che c'era
    /// scritto sulla pagina e gli errori che il browser aveva gia' segnalato in console. Un
    /// "Timeout 30000ms exceeded" e basta non dice se il guasto sia nella pagina, nel browser o
    /// nell'host, e fa ripartire da zero chiunque lo legga - per questo ci passano tutte le attese,
    /// non solo quelle su una condizione scritta da noi.
    /// </summary>
    private async Task StepAsync(Func<Task> step, string what)
    {
        try
        {
            await step();
        }
        catch (TimeoutException ex)
        {
            var visible = await _page.InnerTextAsync("body");
            throw new InvalidOperationException(
                $"{what} (entro {ConditionCeiling / 1000:0} secondi).{Environment.NewLine}" +
                $"La pagina mostrava:{Environment.NewLine}{visible.Trim()}{Environment.NewLine}" +
                $"Errori in console:{Environment.NewLine}" +
                (_consoleErrors.Count == 0 ? "(nessuno)" : string.Join(Environment.NewLine, _consoleErrors)) +
                $"{Environment.NewLine}Richieste non riuscite o senza tipo:{Environment.NewLine}" +
                (_badResponses.Count == 0 ? "(nessuna)" : string.Join(Environment.NewLine, _badResponses)), ex);
        }
    }

    private IFrameLocator Frame => _page.FrameLocator("#drawio-frame");

    private ILocator Shape(string deviceName) => Frame.Locator(
        "xpath=//*[name()='text'][normalize-space(text())=" + XPathLiteral(deviceName) + "]" +
        "/ancestor::*[name()='g'][2]/preceding-sibling::*[name()='g'][1]//*[name()='image']");

    /// <summary>
    /// Il riquadro della sagoma <b>quando ha smesso di muoversi</b>. Dopo il rilascio del mouse
    /// mxGraph consolida lo spostamento per conto suo, e leggere il riquadro subito dopo
    /// <c>MouseUp</c> era l'unico punto di questa prova senza una condizione esplicita. Quello che
    /// se ne ricava non e' un verde falso - la posizione che conta si rilegge dall'impianto, non
    /// dalla tela - ma un rosso che va e viene, e un rosso intermittente si impara a non guardarlo.
    /// </summary>
    /// <remarks>
    /// Si aspetta che due letture di fila diano lo stesso riquadro, non che il riquadro sia
    /// cambiato: chiedere che sia cambiato darebbe per scontato l'esito del gesto, che e' proprio
    /// cio' che la prova deve poter trovare falso.
    /// </remarks>
    private async Task<LocatorBoundingBoxResult> SettledBoxAsync(string deviceName)
    {
        var giveUpAt = DateTime.UtcNow.AddMilliseconds(ConditionCeiling);
        var previous = await BoxOf(Shape(deviceName), deviceName);
        while (true)
        {
            await Task.Delay(SettleInterval);
            var current = await BoxOf(Shape(deviceName), deviceName);
            if (IsSameBox(previous, current)) return current;
            if (DateTime.UtcNow > giveUpAt)
                throw new InvalidOperationException(
                    $"La sagoma di \"{deviceName}\" non ha smesso di muoversi entro {ConditionCeiling / 1000:0} secondi " +
                    $"dal rilascio del mouse: era a ({previous.X:0.#}, {previous.Y:0.#}) e un istante dopo a " +
                    $"({current.X:0.#}, {current.Y:0.#}). Qualcosa sulla tela e' in movimento continuo, e finche' dura " +
                    "non c'e' nessuna posizione da confrontare.");
            previous = current;
        }
    }

    private static bool IsSameBox(LocatorBoundingBoxResult a, LocatorBoundingBoxResult b) =>
        Math.Abs(a.X - b.X) < SamePixel && Math.Abs(a.Y - b.Y) < SamePixel &&
        Math.Abs(a.Width - b.Width) < SamePixel && Math.Abs(a.Height - b.Height) < SamePixel;

    /// <summary>
    /// The shape's box on screen. A shape draw.io is redrawing - the live overlays right after the
    /// engine starts, a zoom, a scroll - can be in the DOM for an instant with no box at all: that
    /// instant is waited out, up to the ceiling, and only a shape that STAYS without a box is a
    /// failure. Final review: measured once in a three-run suite, on the right-click of
    /// DrawioContextMenuTest just after the engine had been started; the pointer test had met the
    /// same instant after a zoom (DrawioPointerPositionBrowserTest).
    /// </summary>
    private async Task<LocatorBoundingBoxResult> BoxOf(ILocator shape, string deviceName)
    {
        // Playwright's own wait for the shape to be visible (confirmation review, M-B: not a poll of
        // ours), then the box. A redraw can still slip between the two and leave the read without a
        // box: then the read is taken again UNDER the same wait (last cleanup) - every round waits on
        // Playwright's condition, never on a time, and the ceiling bounds the whole.
        var giveUpAt = DateTime.UtcNow.AddMilliseconds(ConditionCeiling);
        while (true)
        {
            await StepAsync(
                () => shape.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ConditionCeiling }),
                $"La sagoma di \"{deviceName}\" non e' diventata visibile");
            if (await shape.BoundingBoxAsync() is { } box) return box;
            if (DateTime.UtcNow > giveUpAt)
                throw new InvalidOperationException(
                    $"La sagoma di \"{deviceName}\" risulta visibile ma non ha avuto nessun riquadro sullo schermo per {ConditionCeiling / 1000:0} secondi: non la si puo' afferrare con il mouse.");
        }
    }

    private async Task<string?> TextOrNull(string selector)
    {
        var locator = _page.Locator(selector);
        return await locator.CountAsync() == 0 ? null : (await locator.InnerTextAsync()).Trim();
    }

    private async Task<IReadOnlyList<string>> LinesOf(string selector) =>
        (await _page.Locator(selector).AllInnerTextsAsync()).Select(t => t.Trim()).ToList();

    /// <summary>
    /// La pagina dichiara da se' quando il ponte con draw.io non si e' caricato (difetto D200: non
    /// tacere). Se lo dice, la prova si ferma su QUEL fatto invece di aspettare un disegno che non
    /// arrivera' mai e poi raccontare un tempo scaduto.
    /// </summary>
    private async Task FailIfBridgeUnavailableAsync()
    {
        var banner = _page.Locator(".drawio-bridge-error");
        if (await banner.CountAsync() == 0) return;
        throw new InvalidOperationException(
            $"La pagina dichiara che il ponte con draw.io non e' utilizzabile: {(await banner.InnerTextAsync()).Trim()}");
    }

    // ---- What the plant has and the drawing does not show ----------------------------------------

    /// <summary>
    /// How many boxes the page is showing about what the drawing cannot show: 1 or 0. The count and
    /// not a boolean, so a failure message can say "two of them" if the markup ever grew a second.
    /// Since the passata grafica (compito 3, 29/9) the box is the window behind the "Details" of its
    /// line in the row under the bar, on the page shut or open.
    /// </summary>
    public Task<int> UndrawnBoxCountAsync() => _page.Locator("#drawio-undrawn").CountAsync();

    /// <summary>
    /// The one line readable with the box SHUT - its line in the row under the bar, on screen - or null
    /// when there is no box at all.
    /// </summary>
    public Task<string?> UndrawnSummaryAsync() => TextOrNull("#drawio-undrawn-summary");

    /// <summary>
    /// Opens it the way a person does - one click on "Details" at the end of its line - rather than
    /// un-hiding the window from JavaScript: the same reason as <see cref="OpenTheChecksAsync"/>, the
    /// gesture under test is the operator's. Returns once the windows module has SHOWN the window (the
    /// focus in it, the last thing <c>show()</c> does), as <see cref="OpenWindowAsync"/> does.
    /// </summary>
    public async Task OpenTheUndrawnBoxAsync()
    {
        await StepAsync(
            () => _page.ClickAsync("#drawio-undrawn-details-toggle", new PageClickOptions { Timeout = ConditionCeiling }),
            "the \"Details\" of the line about what the drawing does not show did not let itself be pressed");
        await WaitForConditionAsync(TheWindowHasTheFocus("drawio-undrawn-details"),
            "the window about what the drawing does not show did not open, or never took the focus, after its \"Details\" was clicked");
    }

    /// <summary>One entry per device the drawing could not show, in the order the page lists them.</summary>
    public Task<IReadOnlyList<string>> UndrawnDeviceLinesAsync() => LinesOf("li.drawio-undrawn-device");

    /// <summary>What the page says about the arrows that went away with those devices, or null when it says nothing.</summary>
    public Task<string?> UndrawnJunctionsLineAsync() => TextOrNull("#drawio-undrawn-junctions");

    // ---- The plant's file: export and import ---------------------------------------------------

    /// <summary>
    /// Opens the plant's file section the way a person does, with one click on its button in the
    /// strip - the same gesture as <see cref="OpenTheChecksAsync"/>, and not a window un-hidden from
    /// JavaScript: shut is how the page is born, and a window forced open would not say that the button
    /// can be pressed. Since 27/9 it is a section of the page's strip, the first one (it used to be a
    /// box of the band above), and since 29/9 it opens in a floating window: a button anywhere else
    /// does not count. Since the passata grafica (29/9) the strip is the page's one bar, whose own
    /// controls come before the sections: the plant file's is the first of the SECTIONS' buttons.
    /// Returns once the windows module has shown the window, as <see cref="OpenWindowAsync"/> does:
    /// Blazor un-hides it a round trip before the module's show(), whose last step gives the window
    /// the focus - a focus the test had put elsewhere meanwhile would be taken from under it (seen on
    /// 30/9: the Import's keyboard ring, read after a Tab from Export, gone in one run of the whole
    /// suite out of three; the trace of a run that passed had show() 4 ms ahead of this return).
    /// </summary>
    public async Task OpenThePlantFileSectionAsync()
    {
        await StepAsync(
            () => _page.ClickAsync("#drawio-plant-file-toggle", new PageClickOptions { Timeout = ConditionCeiling }),
            "the plant file's button did not let itself be pressed");
        await WaitForConditionAsync(
            "() => { const b = document.getElementById('drawio-plant-file-toggle'); " +
            "const d = document.getElementById('drawio-plant-file'); " +
            "return !!b && !!d && !d.hidden && !!b.parentElement && b.parentElement.classList.contains('drawio-sections') " +
            "&& b.parentElement.querySelector(':scope > .drawio-section-toggle') === b; }",
            "the plant file's window did not open from the first section button of the strip (.drawio-sections) after it was clicked");
        await WaitForConditionAsync(TheWindowHasTheFocus("drawio-plant-file"),
            "the plant file's window opened but never took the focus: the windows module did not show it");
    }

    /// <summary>
    /// Opens a section of the strip from the KEYBOARD, the way an operator does: Tab, one press at a
    /// time from wherever the focus is, until its button has it, then Enter. It is the way an operator
    /// reaches another section while the import's confirmation is on screen - its backdrop covers the
    /// whole window and takes every click (a click there cancels the import). Not
    /// <c>FocusAsync</c>: that puts the focus where no key press would, and would not say that the
    /// button can be reached at all. Returns how many presses it took, once the page has opened the
    /// section's window.
    /// </summary>
    public async Task<int> OpenASectionFromTheKeyboardAsync(string sectionId)
    {
        const int MostPresses = 60;
        var target = $"{sectionId}-toggle";
        var path = new List<string>();
        for (var presses = 1; presses <= MostPresses; presses++)
        {
            await _page.Keyboard.PressAsync("Tab");
            var focused = await _page.EvaluateAsync<string>(
                "() => { const e = document.activeElement; return !e ? '(none)' : (e.id || e.tagName.toLowerCase()); }");
            path.Add(focused);
            if (focused != target) continue;

            await _page.Keyboard.PressAsync("Enter");
            await WaitForConditionAsync(
                $"() => {{ const d = document.getElementById('{sectionId}'); return !!d && !d.hidden; }}",
                $"the window of section '{sectionId}' did not open after Enter on its button");
            return presses;
        }
        throw new InvalidOperationException(
            $"The button of section '{sectionId}' was not reached in {MostPresses} presses of Tab. The focus went: {string.Join(" -> ", path)}");
    }

    /// <summary>Opens a section of the strip with one click on its button, and returns once the page has opened its window.</summary>
    public async Task OpenASectionAsync(string sectionId)
    {
        await StepAsync(
            () => _page.ClickAsync($"#{sectionId}-toggle", new PageClickOptions { Timeout = ConditionCeiling }),
            $"the button of section '{sectionId}' did not let itself be pressed - is something covering it?");
        await WaitForConditionAsync(
            $"() => {{ const d = document.getElementById('{sectionId}'); return !!d && !d.hidden; }}",
            $"the window of section '{sectionId}' did not open after its button was clicked");
    }

    /// <summary>
    /// Shuts a section's window with Esc, the way an operator does with the focus in it (spec
    /// 2026-09-29: Esc shuts the window that has the focus). First waits for the focus to be in the
    /// window - the window takes it when it opens - so that a focus left elsewhere fails here, named,
    /// instead of as a window that never shut. It is the one gesture that reaches the plant file's
    /// window while the import's confirmation is up: the confirmation's backdrop covers everything
    /// else, the window's own X and the strip's button included, and a click on it cancels the import.
    /// </summary>
    public async Task ShutTheWindowWithEscapeAsync(string sectionId)
    {
        await WaitForConditionAsync(TheWindowHasTheFocus(sectionId),
            $"the keyboard focus is not in the window of section '{sectionId}', so Esc cannot shut it");
        await _page.Keyboard.PressAsync("Escape");
        await WaitForConditionAsync(
            $"() => {{ const d = document.getElementById('{sectionId}'); return !!d && d.hidden; }}",
            $"the window of section '{sectionId}' did not shut after Esc");
    }

    /// <summary>Whether what a section's window holds is taller than the window shows: it scrolls inside the window.</summary>
    public Task<bool> WindowContentOutgrowsItAsync(string sectionId, string scrollSelector = ".drawio-window-content") =>
        _page.EvaluateAsync<bool>(
            "args => { const c = document.getElementById(args.sectionId).querySelector(args.scrollSelector); " +
            "return !!c && c.scrollHeight > c.clientHeight + 1; }", new { sectionId, scrollSelector });

    /// <summary>Whether a section's window is open right now.</summary>
    public Task<bool> IsTheSectionOpenAsync(string sectionId) =>
        _page.EvaluateAsync<bool>($"() => {{ const d = document.getElementById('{sectionId}'); return !!d && !d.hidden; }}");

    /// <summary>How many elements match a selector in the editor document.</summary>
    public Task<int> ElementCountAsync(string selector) => _page.Locator(selector).CountAsync();

    /// <summary>Detaches an open draw.io window and returns the real browser page that now owns its live DOM.</summary>
    public async Task<IPage> DetachWindowAsync(string windowId)
    {
        var popupTask = _page.WaitForPopupAsync(new PageWaitForPopupOptions { Timeout = ConditionCeiling });
        await _page.ClickAsync($"#{windowId}-popout", new PageClickOptions { Timeout = ConditionCeiling });
        var popup = await popupTask;
        await popup.Locator($"#{windowId}.drawio-window-detached").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = ConditionCeiling,
        });
        await WaitForConditionAsync(
            $"() => !document.getElementById('{windowId}')",
            $"the live window '{windowId}' stayed in the editor after its separate browser window opened");
        return popup;
    }

    /// <summary>Waits until a detached window's live DOM is back in the editor.</summary>
    public Task WaitForDetachedWindowBackAsync(string windowId) => WaitForConditionAsync(
        $"() => !!document.getElementById('{windowId}')",
        $"the live window '{windowId}' did not return to the editor");

    /// <summary>
    /// Waits until a section's window is shut in the editor. Closing it is the page's answer to a
    /// click that goes over the circuit: it comes a round trip after the click, never with it.
    /// </summary>
    public Task WaitForTheWindowShutAsync(string windowId) => WaitForConditionAsync(
        $"() => {{ const d = document.getElementById({JsonSerializer.Serialize(windowId)}); return !!d && d.hidden; }}",
        $"the window '{windowId}' was not shut");

    /// <summary>Waits until the bottom edge of the element a selector finds is at <paramref name="bottom"/> px, within half a pixel.</summary>
    public Task WaitForTheBottomEdgeAtAsync(string selector, double bottom) => WaitForConditionAsync(
        $"() => {{ const e = document.querySelector({JsonSerializer.Serialize(selector)}); return !!e && Math.abs(e.getBoundingClientRect().bottom - {bottom.ToString(System.Globalization.CultureInfo.InvariantCulture)}) <= 0.5; }}",
        $"the bottom edge of '{selector}' did not reach {bottom}px");

    /// <summary>
    /// Gives an element of the editor the keyboard's focus and presses a key on it: the operator's way
    /// to a control under a backdrop that takes every click.
    /// </summary>
    public async Task PressOnAsync(string selector, string key)
    {
        await _page.Locator(selector).FocusAsync(new LocatorFocusOptions { Timeout = ConditionCeiling });
        await _page.Keyboard.PressAsync(key);
    }

    /// <summary>
    /// Starts recording the ids of the targets of the clicks that reach the editor's document - where
    /// Blazor hears every click of the page, a detached window's included.
    /// </summary>
    public Task RecordTheClicksOfTheEditorsDocumentAsync() => _page.EvaluateAsync(
        "() => { window.__wcsEditorClicks = []; document.addEventListener('click', e => window.__wcsEditorClicks.push(e.target?.id ?? ''), true); }");

    /// <summary>The ids recorded since <see cref="RecordTheClicksOfTheEditorsDocumentAsync"/>.</summary>
    public Task<string[]> TheClicksOfTheEditorsDocumentAsync() =>
        _page.EvaluateAsync<string[]>("() => window.__wcsEditorClicks ?? []");

    /// <summary>Simulates the browser policy that refuses a requested separate window.</summary>
    public Task BlockSeparateWindowsAsync() => _page.EvaluateAsync(
        "() => { window.__wcsOriginalOpen = window.open; window.open = () => null; }");

    public Task<string> PopoutStatusAsync(string windowId) =>
        _page.Locator($"#{windowId}-popout-status").InnerTextAsync();

    public Task WaitForVisibleAsync(string selector) => StepAsync(
        () => _page.Locator(selector).WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = ConditionCeiling,
        }),
        $"nothing matching '{selector}' became visible");

    /// <summary>
    /// The plant file's button in the strip: what the operator reads when its window is shut. Since
    /// the passata grafica (29/9) the button shows its name, and with an import waiting a "!" and the
    /// sentence behind a tip that the mouse opens: the sentence is read from the tip (its text is in
    /// the page, hidden until then), the name from the label otherwise. Text content and not the
    /// rendered text: the tip is not shown, and on a narrow window neither is the label.
    /// </summary>
    public Task<string?> PlantFileSummaryAsync() => _page.EvaluateAsync<string?>(
        "() => { const b = document.getElementById('drawio-plant-file-toggle'); if (!b) return null; " +
        "const said = b.querySelector('.drawio-tip') || b.querySelector('.drawio-toggle-label'); " +
        "return said ? said.textContent.trim() : null; }");

    /// <summary>
    /// Whether the import's confirmation is on screen - as the browser lays it out, not whether it is in
    /// the DOM: a confirmation waiting inside a shut section is in the DOM and must not be visible.
    /// </summary>
    public Task<bool> IsTheImportConfirmationVisibleAsync() => _page.Locator("#plant-import-confirm").IsVisibleAsync();

    /// <summary>Presses "Cancel" on the import's confirmation and returns once the confirmation has gone.</summary>
    public async Task CancelTheImportAsync()
    {
        await StepAsync(
            () => _page.ClickAsync("#plant-import-cancel-button", new PageClickOptions { Timeout = ConditionCeiling }),
            "the import's Cancel did not let itself be pressed");
        await WaitForConditionAsync(
            "() => !document.getElementById('plant-import-confirm')",
            "the import's confirmation was still there after Cancel");
    }

    /// <summary>
    /// What the browser really hits at the centre of the import confirmation's Confirm and Cancel
    /// buttons (<c>elementFromPoint</c>, the same test a click makes), one row per button: whether
    /// the hit is inside the dialog, what was hit, and whether that point also lies inside the window
    /// <paramref name="otherWindowId"/> (open) - so a test can show that the other window really lies
    /// over the buttons, and that a hit inside the dialog is not just a window placed elsewhere.
    /// </summary>
    public async Task<IReadOnlyList<DrawioImportButtonHit>> WhatTheImportButtonsHitAsync(string otherWindowId) =>
        await _page.EvaluateAsync<DrawioImportButtonHit[]>(
            """
            (otherId) => {
              const dialog = document.getElementById('plant-import-confirm');
              const other = document.getElementById(otherId);
              __DESCRIBE__
              return ['plant-import-confirm-button', 'plant-import-cancel-button'].map((id) => {
                const b = document.getElementById(id);
                if (!b) return { button: id, inTheDialog: false, hit: '(the button is not in the page)', underTheOtherWindow: false };
                const r = b.getBoundingClientRect();
                const x = r.left + r.width / 2, y = r.top + r.height / 2;
                const hit = document.elementFromPoint(x, y);
                const o = other && !other.hidden ? other.getBoundingClientRect() : null;
                return {
                  button: id,
                  inTheDialog: !!hit && !!dialog && dialog.contains(hit),
                  hit: describe(hit),
                  underTheOtherWindow: !!o && x >= o.left && x <= o.right && y >= o.top && y <= o.bottom,
                };
              });
            }
            """.Replace("__DESCRIBE__", DescribeAnElement),
            otherWindowId);

    /// <summary>
    /// A line of JavaScript for the scripts that say what the browser hit at a point: <c>describe(e)</c>,
    /// an element as its tag, id and classes, and the page's floating window it is in, if any.
    /// </summary>
    private const string DescribeAnElement =
        """
        const describe = (e) => !e ? '(nothing)'
          : (e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : '')
             + (e.closest('.drawio-window') ? ' in window #' + e.closest('.drawio-window').id : ''));
        """;

    /// <summary>A window's z-index as the module wrote it (inline) and as the browser stacks it (computed).</summary>
    public async Task<DrawioWindowStacking> WindowStackingAsync(string windowId) =>
        await _page.EvaluateAsync<DrawioWindowStacking>(
            "(id) => { const w = document.getElementById(id); " +
            "return { inline: w ? w.style.zIndex : '(no window)', computed: w ? getComputedStyle(w).zIndex : '(no window)' }; }",
            windowId);

    // ---- The floating windows (spec 2026-09-29) ------------------------------------------------

    /// <summary>
    /// Waits, inside the page, until the strip of section buttons has stopped moving - its bottom edge
    /// the same over three frames in a row - and then for one more frame: the windows module answers a
    /// move of the strip from a ResizeObserver, whose notifications come after the layout of a frame,
    /// so by the next frame they have been handled. A frame is the page's own clock, not a time chosen
    /// here; the 600 frames are a ceiling, so that a strip that never settles is read as it is.
    /// </summary>
    private const string SettleTheStrip =
        """
        async () => {
          const frame = () => new Promise((r) => requestAnimationFrame(() => r()));
          const strip = document.querySelector('.drawio-sections');
          if (!strip) return;
          let last = strip.getBoundingClientRect().bottom, same = 0;
          for (let frames = 0; same < 3 && frames < 600; frames++) {
            await frame();
            const bottom = strip.getBoundingClientRect().bottom;
            same = bottom === last ? same + 1 : 0;
            last = bottom;
          }
          await frame();
        }
        """;

    /// <summary>
    /// Waits until the element is both wider and taller than the given size: an auto-sized window
    /// takes its new size from drawio-windows.js one frame after the panel shows, so a box read at
    /// once can still be the old one.
    /// </summary>
    public Task WaitForBoxLargerThanAsync(string selector, double width, double height) => WaitForConditionAsync(
        $"() => {{ const r = document.querySelector({JsonSerializer.Serialize(selector)})?.getBoundingClientRect(); return !!r && r.width > {width.ToString(CultureInfo.InvariantCulture)} && r.height > {height.ToString(CultureInfo.InvariantCulture)}; }}",
        $"{selector} never grew beyond {width:0}x{height:0}");

    /// <summary>The on-screen box of the page's element <paramref name="selector"/> finds, as <c>[x, y, width, height]</c> (<c>getBoundingClientRect</c>).</summary>
    public async Task<double[]> BoxOfAsync(string selector) =>
        await _page.EvaluateAsync<double[]?>(
            "(s) => { const e = document.querySelector(s); if (!e) return null; " +
            "const r = e.getBoundingClientRect(); return [r.left, r.top, r.width, r.height]; }",
            selector)
        ?? throw new InvalidOperationException($"Nothing on the page matches {selector}, so it has no box.");

    /// <summary>Measures containment without scrolling or focusing the element on the test's behalf.</summary>
    public Task<bool> IsInsideRegionAsync(string selector, string regionSelector, bool focused = false) =>
        _page.EvaluateAsync<bool>(
            """
            args => {
              const element = document.querySelector(args.selector);
              const region = document.querySelector(args.regionSelector);
              if (!element || !region) return false;
              const child = element.getBoundingClientRect(), box = region.getBoundingClientRect();
              return child.left >= box.left && child.right <= box.right
                && child.top >= box.top && child.bottom <= box.bottom
                && (!args.focused || document.activeElement === element);
            }
            """, new { selector, regionSelector, focused });

    public Task WaitForFocusedPlcIdAsync() => WaitForConditionAsync(
        "() => document.activeElement === document.querySelector('#drawio-plc-table tbody tr:last-child input[data-field=Id]')",
        "the newly added PLC row's Id did not receive focus");

    /// <summary>Measures the operational windows, including the children that can overflow a toolbar.</summary>
    public Task<DrawioOperationalWindowLayout> OperationalWindowLayoutAsync(
        string windowId, string? toolbarSelector = null, string? gridSelector = null,
        string? pagerSelector = null, string? cardsSelector = null) =>
        _page.EvaluateAsync<DrawioOperationalWindowLayout>(
            """
            args => {
              const popup = document.getElementById(args.windowId);
              const content = popup.querySelector('.drawio-window-content');
              const toolbar = args.toolbarSelector ? popup.querySelector(args.toolbarSelector) : null;
              const grid = args.gridSelector ? popup.querySelector(args.gridSelector) : null;
              const pager = args.pagerSelector ? popup.querySelector(args.pagerSelector) : null;
              const header = grid?.querySelector('thead th');
              const cards = args.cardsSelector ? popup.querySelector(args.cardsSelector) : null;
              const box = popup.getBoundingClientRect();
              const inside = element => {
                const r = element.getBoundingClientRect();
                return r.left >= box.left - 1 && r.right <= box.right + 1
                  && r.top >= box.top - 1 && r.bottom <= box.bottom + 1;
              };
              const regionInside = element => !!element && inside(element)
                && Array.from(element.children).every(inside)
                && Array.from(element.querySelectorAll('button, input, select')).every(inside);
              return {
                width: box.width, height: box.height,
                toolsInside: regionInside(toolbar), pagerInside: regionInside(pager),
                controlsFitHorizontally: Array.from(popup.querySelectorAll('button, input, select')).every(element => {
                  if (!element.getClientRects().length) return true;
                  const r = element.getBoundingClientRect();
                  return r.left >= box.left - 1 && r.right <= box.right + 1;
                }),
                contentOverflowY: getComputedStyle(content).overflowY,
                contentScrolls: content.scrollHeight > content.clientHeight + 1,
                gridHeight: grid?.clientHeight ?? 0,
                gridOverflowX: grid ? getComputedStyle(grid).overflowX : '',
                gridOverflowY: grid ? getComputedStyle(grid).overflowY : '',
                horizontalOverflow: !!grid && grid.scrollWidth > grid.clientWidth + 1,
                headerPosition: header ? getComputedStyle(header).position : '',
                cardColumns: cards ? getComputedStyle(cards).gridTemplateColumns.split(' ').length : 0,
                columns: grid ? Array.from(grid.querySelectorAll('thead th'))
                  .filter(cell => cell.getClientRects().length && cell.getBoundingClientRect().width > 0
                    && getComputedStyle(cell).visibility !== 'hidden')
                  .map(cell => cell.textContent.trim()) : []
              };
            }
            """, new { windowId, toolbarSelector, gridSelector, pagerSelector, cardsSelector });

    /// <summary>The mission grid's real layout: the properties bUnit cannot measure.</summary>
    public Task<DrawioMissionLayout> MissionLayoutAsync() => _page.EvaluateAsync<DrawioMissionLayout>(
        """
        () => {
          const windowBox = document.getElementById('drawio-mission-list').getBoundingClientRect();
          const grid = document.querySelector('#drawio-mission-list .drawio-mission-grid-scroll');
          const pager = document.querySelector('#drawio-mission-list .drawio-mission-pager');
          const header = document.querySelector('#drawio-mission-list-table thead th:not([hidden])');
          const tools = document.querySelector('#drawio-mission-list .drawio-device-list-tools');
          const inside = (r) => r.left >= windowBox.left - 1 && r.right <= windowBox.right + 1;
          return {
            visibleColumns: document.querySelectorAll('#drawio-mission-list-table thead th:not([hidden])').length,
            gridOverflowY: grid ? getComputedStyle(grid).overflowY : '',
            headerPosition: header ? getComputedStyle(header).position : '',
            pagerInside: !!pager && pager.getBoundingClientRect().bottom <= windowBox.bottom + 1,
            horizontalOverflow: !!grid && grid.scrollWidth > grid.clientWidth,
            toolsInside: !!tools && Array.from(tools.children).every((child) => inside(child.getBoundingClientRect())),
          };
        }
        """);

    /// <summary>The real layout of a paged Devices or Junctions grid: what bUnit cannot measure.</summary>
    public Task<DrawioDataGridLayout> DataGridLayoutAsync(string windowId) =>
        _page.EvaluateAsync<DrawioDataGridLayout>(
            """
            (windowId) => {
              const windowElement = document.getElementById(windowId);
              const windowBox = windowElement.getBoundingClientRect();
              const grid = windowElement.querySelector('.drawio-data-grid-scroll');
              const pager = windowElement.querySelector('.drawio-data-pager');
              const header = windowElement.querySelector('.drawio-data-table thead th');
              const tools = windowElement.querySelector('.drawio-device-list-tools');
              const inside = (r) => r.left >= windowBox.left - 1 && r.right <= windowBox.right + 1;
              return {
                visibleRows: windowElement.querySelectorAll('.drawio-data-table tbody tr').length,
                gridOverflowY: grid ? getComputedStyle(grid).overflowY : '',
                headerPosition: header ? getComputedStyle(header).position : '',
                pagerInside: !!pager && pager.getBoundingClientRect().bottom <= windowBox.bottom + 1,
                horizontalOverflow: !!grid && grid.scrollWidth > grid.clientWidth,
                toolsInside: !!tools && Array.from(tools.children).every((child) => inside(child.getBoundingClientRect())),
              };
            }
            """,
            windowId);

    /// <summary>Waits until a paged Devices or Junctions grid has rendered its fixed pager.</summary>
    public Task WaitForDataGridAsync(string windowId) => WaitForConditionAsync(
        $"() => !!document.querySelector('#{windowId} .drawio-data-pager')",
        $"the {windowId} pager did not appear");

    /// <summary>
    /// Opens a section's window with one click on its button in the strip (<see cref="OpenASectionAsync"/>)
    /// and returns once the windows module has SHOWN it - brought it inside the viewport and to the front,
    /// and given it the focus, which is the last thing <c>show()</c> does. Un-hidden is not enough for a
    /// test that reads the window's place or its order: Blazor un-hides it before the module's call.
    /// </summary>
    public async Task OpenWindowAsync(string windowId)
    {
        await OpenASectionAsync(windowId);
        await WaitForConditionAsync(TheWindowHasTheFocus(windowId),
            $"the window '{windowId}' opened but never took the focus: the windows module did not show it");
    }

    /// <summary>
    /// A condition for <see cref="WaitForConditionAsync"/>: the window is open and the keyboard focus
    /// is in it - where the windows module's show() leaves it, and where Esc reaches it.
    /// </summary>
    private static string TheWindowHasTheFocus(string windowId) =>
        $"() => {{ const d = document.getElementById({JsonSerializer.Serialize(windowId)}); return !!d && !d.hidden && d.contains(document.activeElement); }}";

    /// <summary>
    /// Waits for a window the page opens by itself - from the memory of the windows open last time - to be
    /// open and shown by the windows module (it has a z-index of its own only once <c>show()</c> or a
    /// touch has put it in front).
    /// </summary>
    public Task WaitForTheWindowShownAsync(string windowId) => WaitForConditionAsync(
        $"() => {{ const d = document.getElementById({JsonSerializer.Serialize(windowId)}); return !!d && !d.hidden && d.style.zIndex !== ''; }}",
        $"the window '{windowId}' was not opened and shown again from the memory of the windows open last time: " +
        "it is shut, or it has no inline z-index (style.zIndex), which only the windows module's show() - or a touch - writes");

    /// <summary>
    /// Drags the page's element <paramref name="selector"/> finds with the real mouse: pressed at its
    /// middle, moved by <paramref name="dx"/>, <paramref name="dy"/> in <paramref name="steps"/> steps,
    /// let go. The end may be outside the browser's window: with the pointer captured, the element
    /// that captured it keeps getting the moves.
    /// </summary>
    public async Task DragAsync(string selector, double dx, double dy, int steps = 10)
    {
        var box = await BoxOfAsync(selector);
        var x = box[0] + box[2] / 2;
        var y = box[1] + box[3] / 2;
        await _page.Mouse.MoveAsync((float)x, (float)y);
        await _page.Mouse.DownAsync();
        await _page.Mouse.MoveAsync((float)(x + dx), (float)(y + dy), new MouseMoveOptions { Steps = steps });
        await _page.Mouse.UpAsync();
    }

    /// <summary>
    /// Puts an open window at this place and size with the real mouse, as the operator would: the grip
    /// first takes it down to its smallest size, the title bar then moves it to
    /// <paramref name="left"/>, <paramref name="top"/>, and the grip makes it
    /// <paramref name="width"/> x <paramref name="height"/>. The window shrinks first so that the move
    /// cannot run it into an edge of the browser, which would stop it short. Returns the box the window
    /// has in the end, as <see cref="BoxOfAsync"/> reads it: whoever stages windows checks where they
    /// landed, because the windows module keeps them below the floor and inside the browser.
    /// </summary>
    public async Task<double[]> PlaceWindowAsync(string windowId, double left, double top, double width, double height)
    {
        await DragAsync($"#{windowId} .drawio-window-grip", -2000, -2000);
        var small = await BoxOfAsync($"#{windowId}");
        await DragAsync($"#{windowId} .drawio-window-title", left - small[0], top - small[1]);
        var moved = await BoxOfAsync($"#{windowId}");
        await DragAsync($"#{windowId} .drawio-window-grip", width - moved[2], height - moved[3]);
        return await BoxOfAsync($"#{windowId}");
    }

    /// <summary>
    /// Makes the browser's window this size, and returns once the page has it (<c>innerWidth</c> and
    /// <c>innerHeight</c>) and the strip of section buttons has settled in it - with the windows brought
    /// back inside by then.
    /// </summary>
    public async Task SetViewportAsync(int width, int height)
    {
        await _page.SetViewportSizeAsync(width, height);
        await WaitForConditionAsync(
            $"() => window.innerWidth === {width} && window.innerHeight === {height}",
            $"the page never saw its window become {width}x{height}");
        await _page.EvaluateAsync(SettleTheStrip);
    }

    /// <summary>
    /// The keys of THIS browser's memory that belong to the floating windows (<c>wcs.drawio.window.*</c>
    /// and <c>wcs.drawio.windows.open</c>): empty in a browser that has never moved or opened one.
    /// </summary>
    public Task<string[]> WindowMemoryAsync() => _page.EvaluateAsync<string[]>(
        "() => Object.keys(localStorage).filter((k) => k.startsWith('wcs.drawio.window'))");

    /// <summary>
    /// Once the strip has stopped moving, where its bottom edge is, where the floor of the windows is
    /// (the top of <c>.drawio-body</c>, below the bar and the lines under it), where each of these
    /// windows is, and what the browser really hits at the middle of each button of the bar - the seven
    /// that open a window, the plant checks' among them, and the engine's - and of the Rulers switch
    /// (<c>elementFromPoint</c>, the same test a click makes).
    /// </summary>
    public async Task<DrawioStripAndWindows> TheStripAndItsWindowsAsync(params string[] windowIds)
    {
        await _page.EvaluateAsync(SettleTheStrip);
        return await _page.EvaluateAsync<DrawioStripAndWindows>(
            """
            (ids) => {
              __DESCRIBE__
              const strip = document.querySelector('.drawio-sections');
              const body = document.querySelector('.drawio-body');
              const hitOf = (b) => {
                const r = b.getBoundingClientRect();
                const x = r.left + r.width / 2, y = r.top + r.height / 2;
                const hit = document.elementFromPoint(x, y);
                return { id: b.id, x, y, hitsIt: !!hit && (hit === b || b.contains(hit)), hit: describe(hit) };
              };
              const rulers = document.getElementById('drawio-ruler-switch');
              return {
                stripBottom: strip ? strip.getBoundingClientRect().bottom : -1,
                stripHeight: strip ? strip.getBoundingClientRect().height : -1,
                floor: body ? body.getBoundingClientRect().top : -1,
                rulers: rulers ? hitOf(rulers) : { id: '(no Rulers switch on the page)', x: -1, y: -1, hitsIt: false, hit: '' },
                windows: ids.map((id) => {
                  const w = document.getElementById(id);
                  const r = w ? w.getBoundingClientRect() : { left: -1, top: -1, width: -1, height: -1 };
                  return { id, open: !!w && !w.hidden, left: r.left, top: r.top, width: r.width, height: r.height };
                }),
                buttons: Array.from(document.querySelectorAll('.drawio-sections > :is(.drawio-checks-toggle, .drawio-section-toggle), .drawio-sections > .drawio-engine-switch button')).map(hitOf),
              };
            }
            """.Replace("__DESCRIBE__", DescribeAnElement),
            windowIds);
    }

    /// <summary>
    /// Chooses the device list's row of this device the way a person does - one click on its name, in
    /// the device list's window - and returns once the page marks the row as the device selected on
    /// the drawing, which it does only after draw.io has told it over the circuit.
    /// </summary>
    public async Task ChooseDeviceListRowAsync(string deviceName)
    {
        var name = _page.Locator("#drawio-device-list-table td[data-property='Name']").Filter(new LocatorFilterOptions
        {
            HasTextRegex = new System.Text.RegularExpressions.Regex($"^\\s*{System.Text.RegularExpressions.Regex.Escape(deviceName)}\\s*$"),
        });
        await StepAsync(
            () => name.ClickAsync(new LocatorClickOptions { Timeout = ConditionCeiling }),
            $"the device list's row of \"{deviceName}\" did not let itself be clicked - is something covering it?");
        await WaitForConditionAsync(
            "() => Array.from(document.querySelectorAll(\"#drawio-device-list-table td[data-property='Name']\"))" +
            $".some((td) => td.textContent.trim() === {JsonSerializer.Serialize(deviceName)} && td.closest('tr').getAttribute('aria-selected') === 'true')",
            $"the device list's row of \"{deviceName}\" was never marked as the device selected on the drawing");
    }

    /// <summary>The inline width and height the windows module wrote on a window (<c>style.width</c>, <c>style.height</c>), as written.</summary>
    public Task<string[]> InlineSizeOfAsync(string windowId) => _page.EvaluateAsync<string[]>(
        "(id) => { const w = document.getElementById(id); return w ? [w.style.width, w.style.height] : ['(no window)', '(no window)']; }",
        windowId);

    /// <summary>
    /// Presses the strip's button of a window that is open, and returns once the page has answered:
    /// the window has shut, or it is the one in front of the open windows (the highest inline z-index,
    /// the windows module's own order) - whichever the button did.
    /// </summary>
    public async Task PressTheButtonOfAnOpenWindowAsync(string windowId)
    {
        if (!await IsTheSectionOpenAsync(windowId))
            throw new InvalidOperationException($"The window '{windowId}' is not open: this presses the button of an open window.");
        var before = (await WindowStackingAsync(windowId)).Inline;
        await ClickAsync($"#{windowId}-toggle");
        await WaitForConditionAsync(
            $$"""
            () => {
              const d = document.getElementById({{JsonSerializer.Serialize(windowId)}});
              if (!d) return false;
              if (d.hidden) return true;
              const z = (w) => Number.parseInt(w.style.zIndex, 10) || 0;
              const others = Array.from(document.querySelectorAll('.drawio-window')).filter((w) => w !== d && !w.hidden);
              return d.style.zIndex !== {{JsonSerializer.Serialize(before)}} && others.every((w) => z(w) < z(d));
            }
            """,
            $"the window '{windowId}' neither shut nor came to the front of the open windows after its button was pressed");
    }

    /// <summary>Presses the X of a window, and returns once the window has shut.</summary>
    public async Task CloseTheWindowAsync(string windowId)
    {
        await ClickAsync($"#{windowId}-close");
        await WaitForTheSectionAsync(windowId, open: false);
    }

    /// <summary>
    /// Calls the windows module's <c>show()</c> once more on the open window with this id, from the
    /// page, and returns what has the keyboard focus afterwards. The module is the one the page
    /// imported - DrawioWindow's path, <c>./_content/Wcs.WebEditor/drawio-windows.js</c>, resolved
    /// against the page's base as Blazor resolves it - so the call reaches the instance that attached
    /// the window. That is checked, not assumed: the window is first put 500 px out past the browser's
    /// left edge, and show() brings a window it knows back inside; a copy of the module that does not
    /// know it would leave it out there, and the focus where it was for that reason alone.
    /// </summary>
    public async Task<DrawioShownAgain> ShowTheWindowAgainAsync(string windowId) =>
        await _page.EvaluateAsync<DrawioShownAgain>(
            """
            async (id) => {
              __DESCRIBE__
              const module = await import(new URL('./_content/Wcs.WebEditor/drawio-windows.js', document.baseURI).href);
              const w = document.getElementById(id);
              w.style.left = '-500px';
              module.show(w);
              const focused = document.activeElement;
              return { left: w.getBoundingClientRect().left, focusedId: focused ? focused.id : '', focused: describe(focused) };
            }
            """.Replace("__DESCRIBE__", DescribeAnElement),
            windowId);

    /// <summary>
    /// Stages the page opening again the windows of the last visit on the windows open now, with the
    /// operator's focus coming in between the windows module's show() calls. From the page, on the
    /// module the page imported (as <see cref="ShowTheWindowAgainAsync"/>), and in one script, so that
    /// nothing of the page's own comes in between: the module is told that the page is opening again
    /// the windows of the last visit (<c>openedLastTime()</c>, which reads the memory the openings of
    /// the windows wrote); show() is called on each window of <paramref name="shownBefore"/>; the element
    /// with the id <paramref name="focusedId"/> is given the focus, as a click in it gives it (the
    /// module's focusin brings its window in front); and show() is called on each window of
    /// <paramref name="shownAfter"/>. Each window is put 500 px out past the browser's left edge right
    /// before its show(), which brings a window it knows back inside: that the calls reached the instance
    /// that attached the windows is checked, not assumed. Returned: the memory the reopening went by,
    /// the windows show() left outside, what has the focus, what the browser hits at the middle of the
    /// element given the focus (<c>elementFromPoint</c>, the same test a click makes), and the other
    /// open windows whose box holds that point - those that would hide it, in front of its own.
    /// </summary>
    public async Task<DrawioReopening> ReopenTheOpenWindowsAsync(string[] shownBefore, string focusedId, string[] shownAfter) =>
        await _page.EvaluateAsync<DrawioReopening>(
            """
            async ([before, focusedId, after]) => {
              __DESCRIBE__
              const module = await import(new URL('./_content/Wcs.WebEditor/drawio-windows.js', document.baseURI).href);
              const order = module.openedLastTime();
              const outside = [];
              const show = (id) => {
                const w = document.getElementById(id);
                w.style.left = '-500px';
                module.show(w);
                if (w.getBoundingClientRect().left < 0) outside.push(id);
              };
              before.forEach(show);
              const field = document.getElementById(focusedId);
              field.focus();
              after.forEach(show);
              const r = field.getBoundingClientRect();
              const x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y);
              const own = field.closest('.drawio-window');
              const over = [...document.querySelectorAll('.drawio-window')]
                .filter((w) => w !== own && !w.hidden)
                .filter((w) => { const b = w.getBoundingClientRect(); return x >= b.left && x <= b.right && y >= b.top && y <= b.bottom; })
                .map((w) => w.id);
              const focused = document.activeElement;
              return {
                order, outside, focusedId: focused ? focused.id : '', focused: describe(focused),
                fieldOnTop: !!hit && (hit === field || field.contains(hit)), hit: describe(hit), windowsOverTheField: over,
              };
            }
            """.Replace("__DESCRIBE__", DescribeAnElement),
            new object[] { shownBefore, focusedId, shownAfter });

    /// <summary>Puts the keyboard focus on the page's element <paramref name="selector"/> finds, as a click or a Tab would.</summary>
    public Task FocusAsync(string selector) => _page.Locator(selector).FocusAsync();

    /// <summary>Presses one key on whatever has the keyboard focus (Playwright's names: "Escape", "Enter"...).</summary>
    public Task PressAsync(string key) => _page.Keyboard.PressAsync(key);

    /// <summary>What has the keyboard focus, described as <c>DescribeAnElement</c> describes an element.</summary>
    public Task<string> FocusedElementAsync() => _page.EvaluateAsync<string>(
        "() => { __DESCRIBE__ return describe(document.activeElement); }".Replace("__DESCRIBE__", DescribeAnElement));

    /// <summary>
    /// Waits for the keyboard focus to be on the element with this id, and says where it is instead
    /// if it never gets there.
    /// </summary>
    public async Task WaitForTheFocusOnAsync(string elementId)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                $"() => !!document.activeElement && document.activeElement.id === {JsonSerializer.Serialize(elementId)}",
                null, new PageWaitForFunctionOptions { Timeout = ConditionCeiling });
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"The keyboard focus never reached #{elementId} (within {ConditionCeiling / 1000:0} seconds): it is on {await FocusedElementAsync()}.");
        }
    }

    /// <summary>Waits for the keyboard focus to be in the window with this id (open), and says where it is instead if it never gets there.</summary>
    public async Task WaitForTheFocusInTheWindowAsync(string windowId)
    {
        try
        {
            await _page.WaitForFunctionAsync(TheWindowHasTheFocus(windowId), null, new PageWaitForFunctionOptions { Timeout = ConditionCeiling });
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"The keyboard focus never came into the window '{windowId}' (within {ConditionCeiling / 1000:0} seconds): it is on {await FocusedElementAsync()}.");
        }
    }

    /// <summary>
    /// Queue item 26: from the next opening of the page in this context on - a reload - draw.io's first
    /// answer of each of <paramref name="events"/> ('init', 'load') is held at the page's window and
    /// does not reach the bridge until <see cref="ReleaseDrawiosAnswerAsync"/> lets it go. Held by a
    /// listener of the page's own window in the capture phase, which runs before the bridge's (both are
    /// on the window itself) and stops it; installed through <c>AddInitScriptAsync</c>, so it is there
    /// before draw.io can answer anything. The gap between draw.io's doing and the bridge's hearing of
    /// it is then a condition of the test, not a race: what happens in it is the test's own gesture.
    /// </summary>
    public Task HoldDrawiosAnswersFromTheNextOpeningAsync(params string[] events) => _context.AddInitScriptAsync(
        """
        (() => {
          if (window !== window.top) return;
          const toHold = new Set(__EVENTS__);
          const held = {};
          window.__wcsHeldDrawioAnswers = held;
          window.addEventListener('message', (e) => {
            if (typeof e.data !== 'string') return;
            let m;
            try { m = JSON.parse(e.data); } catch { return; }
            if (!m || !toHold.has(m.event)) return;
            toHold.delete(m.event);
            e.stopImmediatePropagation();
            held[m.event] = { data: e.data, origin: e.origin, source: e.source };
          }, true);
        })();
        """.Replace("__EVENTS__", JsonSerializer.Serialize(events)));

    /// <summary>Waits until draw.io has given its answer <paramref name="name"/> and the page holds it (<see cref="HoldDrawiosAnswersFromTheNextOpeningAsync"/>).</summary>
    public Task WaitForDrawiosAnswerHeldAsync(string name) => WaitForConditionAsync(
        $"() => !!window.__wcsHeldDrawioAnswers && !!window.__wcsHeldDrawioAnswers[{JsonSerializer.Serialize(name)}]",
        $"draw.io's answer '{name}' never arrived at the page to be held");

    /// <summary>
    /// Lets draw.io's held answer <paramref name="name"/> reach the bridge - the same data, from the same
    /// window - and says where the keyboard focus is right after the bridge has handled it, in the same
    /// task: the bridge's handling of 'init' and 'load' runs to its end before the dispatch returns.
    /// </summary>
    public Task<DrawioFocusAfterTheAnswer> ReleaseDrawiosAnswerAsync(string name) => _page.EvaluateAsync<DrawioFocusAfterTheAnswer>(
        """
        (name) => {
          __DESCRIBE__
          const held = window.__wcsHeldDrawioAnswers && window.__wcsHeldDrawioAnswers[name];
          if (!held) throw new Error('the answer ' + name + ' of draw.io is not held');
          delete window.__wcsHeldDrawioAnswers[name];
          window.dispatchEvent(new MessageEvent('message', held));
          const active = document.activeElement;
          const inAWindow = active ? active.closest('.drawio-window') : null;
          return { onTheFrame: !!active && active.id === 'drawio-frame', window: inAWindow ? inAWindow.id : '', focused: describe(active) };
        }
        """.Replace("__DESCRIBE__", DescribeAnElement),
        name);

    /// <summary>Whether the keyboard focus of the page is on draw.io's iframe - inside draw.io, as far as the page can see.</summary>
    public Task<bool> TheFocusIsInDrawiosFrameAsync() => _page.EvaluateAsync<bool>(
        "() => !!document.activeElement && document.activeElement.id === 'drawio-frame'");

    /// <summary>
    /// A real click on the drawing's area where none of the page's windows - and none of draw.io's
    /// panels - covers it: the first of four points near the corners of draw.io's diagram container
    /// that <see cref="WhatIsHitAtAsync"/> says is the drawing's area. Whatever is under it - empty
    /// canvas or a shape - is the canvas the operator presses on.
    /// </summary>
    public async Task ClickTheCanvasWhereNoWindowCoversItAsync()
    {
        var canvas = await Frame.Locator("div.geDiagramContainer").BoundingBoxAsync()
            ?? throw new InvalidOperationException("draw.io's diagram container has no box on the screen: there is no canvas to click.");

        (float X, float Y)[] corners =
        [
            (canvas.X + EmptySpotInset, canvas.Y + canvas.Height - EmptySpotInset),
            (canvas.X + canvas.Width - EmptySpotInset, canvas.Y + canvas.Height - EmptySpotInset),
            (canvas.X + EmptySpotInset, canvas.Y + EmptySpotInset),
            (canvas.X + canvas.Width - EmptySpotInset, canvas.Y + EmptySpotInset),
        ];

        var seen = new List<string>();
        foreach (var (x, y) in corners)
        {
            var hit = await WhatIsHitAtAsync(x, y);
            if (hit.TheFrame && hit.OnTheCanvas && !hit.InAWindow)
            {
                await _page.Mouse.ClickAsync(x, y);
                return;
            }
            seen.Add(FormattableString.Invariant($"({x:0},{y:0}): {hit}"));
        }

        throw new InvalidOperationException(
            "None of the four points near the corners of the canvas is the drawing's area: " + string.Join("; ", seen));
    }

    /// <summary>
    /// What the browser hits at this point of the page (<c>elementFromPoint</c>, the same test a click
    /// makes): whether it is draw.io's iframe, whether it is inside one of the page's floating windows
    /// and which, and what it is. When it is the iframe, the same question is asked of draw.io's own
    /// document at that point: whether what is there is the drawing's area (draw.io's
    /// <c>div.geDiagramContainer</c>, mxGraph's container) and not one of draw.io's own panels over it
    /// (its shapes and format panels, its floating windows) - the toolbar and the panels beside the
    /// drawing are in the iframe too, and a cell under them is not seen either.
    /// </summary>
    public async Task<DrawioPointHit> WhatIsHitAtAsync(double x, double y) =>
        await _page.EvaluateAsync<DrawioPointHit>(
            """
            ([x, y]) => {
              __DESCRIBE__
              const hit = document.elementFromPoint(x, y);
              const window = hit ? hit.closest('.drawio-window') : null;
              const theFrame = !!hit && hit.id === 'drawio-frame';
              let onTheCanvas = false;
              let inside = '';
              if (theFrame && hit.contentDocument) {
                const r = hit.getBoundingClientRect();
                const there = hit.contentDocument.elementFromPoint(x - r.left - hit.clientLeft, y - r.top - hit.clientTop);
                onTheCanvas = !!there && !!there.closest('div.geDiagramContainer') && !there.closest('.mxWindow, .geSidebarContainer');
                inside = there ? ' > ' + describe(there) : ' > (nothing)';
              }
              return { theFrame, inAWindow: !!window, window: window ? window.id : '', onTheCanvas, hit: describe(hit) + inside };
            }
            """.Replace("__DESCRIBE__", DescribeAnElement),
            new[] { x, y });

    /// <summary>
    /// Makes draw.io's graph refuse to bring a cell into view from now on - its own
    /// <c>scrollCellToVisible</c> and <c>scrollRectToVisible</c> throw, which are the two roads the
    /// bridge's centring takes (centreCellInTheFreeCanvas) - for the one test that has to see a
    /// duplicate drawn and selected and then not centred. On the graph instance of this page only; the
    /// page is closed at the end of that test.
    /// </summary>
    public Task MakeBringingACellIntoViewFailAsync() => InTheGraphAsync<bool>(
        "const refuse = function () { throw new Error('bringing a cell into view refused on purpose (browser test)'); };" +
        " ui.editor.graph.scrollCellToVisible = refuse; ui.editor.graph.scrollRectToVisible = refuse; return true;");

    /// <summary>
    /// How many lines the page's stylesheet shows for <paramref name="text"/> - lines joined with line
    /// breaks, as the page writes them - in an element like the one <paramref name="selector"/> finds:
    /// a copy of it (same tag and classes, no id, nothing of its content) is put right after it with
    /// that text, its rendered text read (<c>innerText</c>, which follows the stylesheet's white-space)
    /// and the copy taken away. The element itself is Blazor's and is not touched.
    /// </summary>
    public Task<int> LinesShownForAsync(string selector, string text) => _page.EvaluateAsync<int>(
        """
        ([selector, text]) => {
          const element = document.querySelector(selector);
          if (!element) throw new Error('nothing on the page matches ' + selector);
          const copy = element.cloneNode(false);
          copy.removeAttribute('id');
          copy.textContent = text;
          element.after(copy);
          try {
            return copy.innerText.split('\n').filter((line) => line.trim() !== '').length;
          } finally {
            copy.remove();
          }
        }
        """,
        new[] { selector, text });

    /// <summary>
    /// Makes the column above the drawing taller, so that it is <paramref name="beyond"/> px taller than
    /// the browser and the page scrolls - what a bar wrapped onto several lines, or many lines under it,
    /// do; here in one step: a <c>min-height</c> on the engine switch, an element Blazor
    /// renders with no style of its own. Since the passata grafica (29/9) the switch is in the bar, so
    /// it is the bar that grows (it was the band above it). The drawing's area gives its height first
    /// (it is the one child of the column that shrinks), so the switch grows by that height and then by
    /// <paramref name="beyond"/>. Returns how far the page can now scroll, once the strip has settled.
    /// </summary>
    public async Task<double> OverflowThePageByAsync(double beyond)
    {
        await _page.EvaluateAsync(
            """
            (beyond) => {
              const band = document.getElementById('drawio-engine-switch');
              const body = document.querySelector('.drawio-body');
              if (!band || !body) throw new Error('the page has no engine switch or no .drawio-body');
              band.style.minHeight = (band.getBoundingClientRect().height + body.getBoundingClientRect().height + beyond) + 'px';
            }
            """,
            beyond);
        await _page.EvaluateAsync(SettleTheStrip);
        return await _page.EvaluateAsync<double>("() => document.documentElement.scrollHeight - window.innerHeight");
    }

    /// <summary>
    /// Makes the bar <paramref name="extra"/> px taller than it is - what it does when it wraps onto a
    /// second line, here in one step and by a known height: a <c>min-height</c> on the bar
    /// (<c>.drawio-sections</c>), which Blazor renders with no style of its own. Its controls stay in
    /// it; the drawing's area below it goes down by as much. Returns once the strip has settled.
    /// </summary>
    public async Task GrowTheBarByAsync(double extra)
    {
        await _page.EvaluateAsync(
            """
            (extra) => {
              const bar = document.querySelector('.drawio-sections');
              if (!bar) throw new Error('the page has no bar (.drawio-sections)');
              bar.style.minHeight = (bar.getBoundingClientRect().height + extra) + 'px';
            }
            """,
            extra);
        await _page.EvaluateAsync(SettleTheStrip);
    }

    /// <summary>Scrolls the page itself to this height, and returns once the strip has settled where the scroll put it.</summary>
    public async Task ScrollThePageToAsync(double y)
    {
        await _page.EvaluateAsync("(y) => window.scrollTo(0, y)", y);
        await WaitForConditionAsync(
            $"() => Math.abs(window.scrollY - {y.ToString(System.Globalization.CultureInfo.InvariantCulture)}) < 1",
            $"the page never scrolled to {y.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        await _page.EvaluateAsync(SettleTheStrip);
    }

    // ---- The bar as a sighted operator gets it: its tips, its one line ------------------------------

    /// <summary>
    /// Moves the real mouse onto the middle of the element <paramref name="selector"/> finds, and
    /// returns once the browser has it under the mouse (<c>:hover</c>). That is true whatever the page's
    /// CSS does with it, so what is read next - a tip shown or not - is read at once, and a tip that
    /// never opens is a wrong answer and not a timeout.
    /// </summary>
    public async Task PutTheMouseOnAsync(string selector)
    {
        await StepAsync(
            () => _page.HoverAsync(selector, new PageHoverOptions { Timeout = ConditionCeiling }),
            $"the mouse could not be put on {selector} - is something covering it?");
        await WaitForConditionAsync(
            $"() => {{ const e = document.querySelector({JsonSerializer.Serialize(selector)}); return !!e && e.matches(':hover'); }}",
            $"{selector} is not under the mouse after the mouse was put on it");
    }

    /// <summary>
    /// The mouse on the bar's control <paramref name="selector"/> finds, and what the browser then shows
    /// of the sentence behind it (<see cref="TipOnScreenAsync"/>).
    /// </summary>
    public async Task<string?> TipUnderTheMouseAsync(string selector)
    {
        await PutTheMouseOnAsync(selector);
        return await TipOnScreenAsync(selector);
    }

    /// <summary>
    /// The text of the <c>.drawio-tip</c> of the control <paramref name="selector"/> finds as the browser
    /// lays it out now, or null when it is not on screen (<c>checkVisibility</c>: its display and every
    /// ancestor's). Not its text content: that is in the page all along, shown or not.
    /// </summary>
    public Task<string?> TipOnScreenAsync(string selector) => _page.EvaluateAsync<string?>(
        "(s) => { const t = document.querySelector(s + ' > .drawio-tip'); return t && t.checkVisibility() ? t.innerText.trim() : null; }",
        selector);

    /// <summary>
    /// The whole text of the element <paramref name="selector"/> finds, as the page holds it, or null when
    /// there is no such element: its text content, not what is laid out of it - a line cut short with an
    /// ellipsis is still whole here.
    /// </summary>
    public Task<string?> TextContentOfAsync(string selector) => _page.EvaluateAsync<string?>(
        "(s) => { const e = document.querySelector(s); return e ? e.textContent.trim() : null; }",
        selector);

    /// <summary>
    /// The mouse on the bar's button <paramref name="buttonId"/>, and how the browser then shows its name
    /// (<see cref="NameOnScreenAsync"/>).
    /// </summary>
    public async Task<DrawioBarButtonName> NameUnderTheMouseAsync(string buttonId)
    {
        await PutTheMouseOnAsync($"#{buttonId}");
        return await NameOnScreenAsync(buttonId);
    }

    /// <summary>
    /// How the browser shows the name of the bar's button <paramref name="buttonId"/> now: its word
    /// (<c>.drawio-toggle-label</c>) if it is laid out, and the text of the tip generated under it (its
    /// <c>::after</c>) if one is drawn. The generated tip is read with <c>getComputedStyle</c>: a
    /// pseudo-element has no box of its own to ask for, and it is drawn when its display is not none and
    /// it has a content.
    /// </summary>
    public Task<DrawioBarButtonName> NameOnScreenAsync(string buttonId) => _page.EvaluateAsync<DrawioBarButtonName>(
        """
        (id) => {
          const b = document.getElementById(id);
          if (!b) return { id, found: false, label: null, generated: null };
          const label = b.querySelector(':scope > .drawio-toggle-label');
          const after = getComputedStyle(b, '::after');
          const drawn = after.display !== 'none' && after.content !== 'none' && after.content !== 'normal';
          return {
            id,
            found: true,
            label: label && label.checkVisibility() ? label.innerText.trim() : null,
            generated: drawn ? after.content : null,
          };
        }
        """,
        buttonId);

    /// <summary>The bar's height now, once it has stopped moving.</summary>
    public async Task<double> BarHeightAsync()
    {
        await _page.EvaluateAsync(SettleTheStrip);
        return await _page.EvaluateAsync<double>(
            "() => { const bar = document.querySelector('.drawio-sections'); if (!bar) throw new Error('the page has no bar (.drawio-sections)'); return bar.getBoundingClientRect().height; }");
    }

    /// <summary>
    /// From now on, every change inside the bar that leaves something on its way in it - the engine's
    /// pending line (<c>#drawio-engine-pending</c>), or the plant checks saying that the configuration
    /// check is running - is written down with the bar's height at that moment, read back with
    /// <see cref="BarMomentsAsync"/>. The height is read in the MutationObserver's callback, that is
    /// right after the change and with the layout it made: however short the state lasts, it is seen.
    /// </summary>
    public Task WatchTheBarWhileSomethingIsOnItsWayAsync() => _page.EvaluateAsync(
        """
        () => {
          const bar = document.querySelector('.drawio-sections');
          if (!bar) throw new Error('the page has no bar (.drawio-sections)');
          window.__wcsBarMoments = [];
          // The width the bar's children would take on one line: their boxes, the gaps between them and
          // the bar's own padding and border (the windows left out: fixed to the viewport, they take no room).
          const needed = () => {
            const style = getComputedStyle(bar);
            const shown = [...bar.children].filter((c) => !c.classList.contains('drawio-window') && c.checkVisibility());
            const px = (v) => parseFloat(v) || 0;
            return shown.reduce((sum, c) => sum + c.getBoundingClientRect().width, 0)
              + px(style.columnGap) * Math.max(0, shown.length - 1)
              + px(style.paddingLeft) + px(style.paddingRight) + px(style.borderLeftWidth) + px(style.borderRightWidth);
          };
          const look = () => {
            const pending = document.getElementById('drawio-engine-pending');
            const summary = document.getElementById('drawio-checks-summary');
            const running = !!summary && summary.textContent.includes('is running');
            if (!pending && !running) return;
            const state = document.getElementById('drawio-engine-state');
            window.__wcsBarMoments.push({
              engine: state ? state.textContent.trim() : '(no engine state)',
              pending: pending ? pending.textContent.trim() : null,
              checkRunning: running,
              height: bar.getBoundingClientRect().height,
              needed: needed(),
            });
          };
          new MutationObserver(look).observe(bar, { childList: true, subtree: true, characterData: true });
        }
        """);

    /// <summary>What <see cref="WatchTheBarWhileSomethingIsOnItsWayAsync"/> has written down so far, in order.</summary>
    public Task<DrawioBarMoment[]> BarMomentsAsync() =>
        _page.EvaluateAsync<DrawioBarMoment[]>("() => window.__wcsBarMoments || []");

    /// <summary>
    /// How the browser lays out the line of text <paramref name="selector"/> finds, measured against the
    /// bar: whether it is shown and inside the bar, where its box is, whether its box holds all of its
    /// text (no overflow either way - a line cut short with an ellipsis overflows its box to the
    /// right), and whether the mouse would reach it (<c>elementFromPoint</c> near its start: nothing
    /// covers it).
    /// </summary>
    public Task<DrawioTextLine> TextLineAsync(string selector) => _page.EvaluateAsync<DrawioTextLine>(
        """
        (s) => {
          const e = document.querySelector(s);
          const bar = document.querySelector('.drawio-sections');
          if (!bar) throw new Error('the page has no bar (.drawio-sections)');
          if (!e) return { selector: s, found: false };
          const r = e.getBoundingClientRect();
          const hit = document.elementFromPoint(r.left + Math.min(r.width / 2, 40), r.top + r.height / 2);
          return {
            selector: s,
            found: true,
            shown: e.checkVisibility(),
            inTheBar: !!e.closest('.drawio-sections'),
            top: r.top,
            left: r.left,
            right: r.right,
            barBottom: bar.getBoundingClientRect().bottom,
            viewportWidth: window.innerWidth,
            overflowX: e.scrollWidth - e.clientWidth,
            overflowY: e.scrollHeight - e.clientHeight,
            reached: !!hit && (hit === e || e.contains(hit)),
          };
        }
        """,
        selector);

    /// <summary>
    /// The buttons of the bar - and of the engine's confirmation box, which hangs from it - that the
    /// browser shows now in a strong colour, by id. "Strong" is read off the painted background, not
    /// off the page's classes or rules: a background at least half opaque whose red, green and blue
    /// lie 100 or more apart. The palette's blue (0,120,212) and red (185,28,28) are, with their hover
    /// and pressed shades; the greys and the light blue of a lit button (222,236,249) are not. The
    /// buttons of the windows are left out (a shut window shows none).
    /// </summary>
    public Task<string[]> ColouredButtonsOfTheBarAsync() => _page.EvaluateAsync<string[]>(
        """
        () => [...document.querySelectorAll('.drawio-sections button')]
          .filter(b => !b.closest('.drawio-window') && b.checkVisibility())
          .filter(b => {
            const [r, g, bl, a = 1] = getComputedStyle(b).backgroundColor.match(/[\d.]+/g).map(Number);
            return a >= 0.5 && Math.max(r, g, bl) - Math.min(r, g, bl) >= 100;
          })
          .map(b => b.id || `<button class="${b.className}">`)
        """);

    /// <summary>
    /// The ids of the page's buttons whose accessible name - the name a screen reader says, as the
    /// browser computes it (Playwright's role locator) - is exactly <paramref name="name"/>.
    /// </summary>
    public Task<string[]> ButtonsNamedAsync(string name) =>
        _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = name, Exact = true })
            .EvaluateAllAsync<string[]>("(buttons) => buttons.map((b) => b.id)");

    // ---- The look of the page: the bar's one line, the drawing's height, the "?" (compito 5) --------

    /// <summary>
    /// Waits until the bar's live link is up and well: the quiet "Live" pill (<c>drawio-live-on</c>),
    /// with no sentence behind it. Until then the pill says "Connecting", some 45 px wider, and the
    /// page is not yet at rest.
    /// </summary>
    public Task WaitForTheLiveLinkAsync() => WaitForConditionAsync(
        "() => { const p = document.getElementById('drawio-live-status'); return !!p && p.classList.contains('drawio-live-on'); }",
        "the bar's live link never came up: its pill never became the quiet \"Live\" (drawio-live-on)");

    /// <summary>
    /// Once the bar has stopped moving, its box and each of its children the browser shows, in the
    /// page's order, with its box - the windows left out: they are rendered inside the bar but fixed to
    /// the viewport, and take no room in its line.
    /// </summary>
    public async Task<DrawioBarLine> BarLineAsync()
    {
        await _page.EvaluateAsync(SettleTheStrip);
        return await _page.EvaluateAsync<DrawioBarLine>(
            """
            () => {
              __DESCRIBE__
              const bar = document.querySelector('.drawio-sections');
              if (!bar) throw new Error('the page has no bar (.drawio-sections)');
              const r = bar.getBoundingClientRect();
              return {
                top: r.top,
                bottom: r.bottom,
                viewportWidth: window.innerWidth,
                children: [...bar.children]
                  .filter((c) => !c.classList.contains('drawio-window') && c.checkVisibility())
                  .map((c) => {
                    const b = c.getBoundingClientRect();
                    return { key: c.id || c.classList[0] || c.tagName.toLowerCase(), what: describe(c), top: b.top, bottom: b.bottom, left: b.left, right: b.right };
                  }),
              };
            }
            """.Replace("__DESCRIBE__", DescribeAnElement));
    }

    /// <summary>
    /// How the bar names its buttons that open a window - every <c>-toggle</c> of the bar, the plant
    /// checks' among them - with each one's aria-label and whether its icon is seen; and every word of
    /// the bar's buttons (<c>.drawio-toggle-label</c>, windows left out) with its computed display, the
    /// width the browser lays it out at and whether it is seen. Seen is <c>checkVisibility</c> with the
    /// opacity and the visibility too: a word laid out but transparent or hidden takes room and says
    /// nothing.
    /// </summary>
    public Task<DrawioBarNames> BarNamesAsync() => _page.EvaluateAsync<DrawioBarNames>(
        """
        () => {
          const seen = (e) => !!e && e.checkVisibility({ opacityProperty: true, visibilityProperty: true });
          return {
            toggles: [...document.querySelectorAll('.drawio-sections > [id$="-toggle"]')]
              .map((b) => {
                const icon = b.querySelector(':scope > svg.drawio-icon');
                return { id: b.id, ariaLabel: b.getAttribute('aria-label'), iconSeen: seen(icon), iconWidth: icon ? icon.getBoundingClientRect().width : 0 };
              }),
            labels: [...document.querySelectorAll('.drawio-sections .drawio-toggle-label')]
              .filter((l) => !l.closest('.drawio-window'))
              .map((l) => ({ owner: l.parentElement ? l.parentElement.id : '', display: getComputedStyle(l).display, width: l.getBoundingClientRect().width, seen: seen(l) })),
          };
        }
        """);

    /// <summary>
    /// What the browser hits at the middle of each control of the bar it shows (<c>elementFromPoint</c>,
    /// the same test a click makes): every button, field and focusable element, and every pill - the
    /// windows and whatever is in them left out.
    /// </summary>
    public async Task<DrawioStripButtonHit[]> BarControlsHitAsync()
    {
        await _page.EvaluateAsync(SettleTheStrip);
        return await _page.EvaluateAsync<DrawioStripButtonHit[]>(
            """
            () => {
              __DESCRIBE__
              return [...document.querySelectorAll('.drawio-sections :is(button, input, select, textarea, [tabindex], .drawio-pill)')]
                .filter((e) => !e.closest('.drawio-window') && e.checkVisibility())
                .map((e) => {
                  const r = e.getBoundingClientRect();
                  const x = r.left + r.width / 2, y = r.top + r.height / 2;
                  const hit = document.elementFromPoint(x, y);
                  return { id: e.id || describe(e), x, y, hitsIt: !!hit && (hit === e || e.contains(hit)), hit: describe(hit) };
                });
            }
            """.Replace("__DESCRIBE__", DescribeAnElement));
    }

    /// <summary>Whether the page's element <paramref name="selector"/> finds is laid out on screen now (<c>checkVisibility</c>): false when there is none.</summary>
    public Task<bool> IsShownAsync(string selector) => _page.EvaluateAsync<bool>(
        "(s) => { const e = document.querySelector(s); return !!e && e.checkVisibility(); }",
        selector);

    /// <summary>
    /// Where the text of the page's element <paramref name="selector"/> finds is on screen - the box of
    /// its words (a range over its contents), not of the element, which may be wider than they are - as
    /// left, top, width and height.
    /// </summary>
    public async Task<double[]> TextBoxOfAsync(string selector) =>
        await _page.EvaluateAsync<double[]?>(
            "(s) => { const e = document.querySelector(s); if (!e) return null; const r = document.createRange(); r.selectNodeContents(e); const b = r.getBoundingClientRect(); return [b.left, b.top, b.width, b.height]; }",
            selector)
        ?? throw new InvalidOperationException($"Nothing on the page matches {selector}, so it has no text to measure.");

    /// <summary>Scrolls whatever box holds the page's element <paramref name="selector"/> finds until the element is in view, as a click would.</summary>
    public Task ScrollIntoViewAsync(string selector) => StepAsync(
        () => _page.Locator(selector).ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = ConditionCeiling }),
        $"{selector} could not be scrolled into view");

    /// <summary>Waits until the page's element <paramref name="selector"/> finds is laid out on screen (<c>checkVisibility</c>).</summary>
    public Task WaitForShownAsync(string selector, string what) => WaitForConditionAsync(
        $"() => {{ const e = document.querySelector({JsonSerializer.Serialize(selector)}); return !!e && e.checkVisibility(); }}",
        $"{what} ({selector}) never came on screen");

    /// <summary>
    /// One computed style of the page's element <paramref name="selector"/> finds (<c>getComputedStyle</c>,
    /// the property's CSS name: "background-color", "outline-style"), as the browser writes it.
    /// </summary>
    public async Task<string> ComputedStyleAsync(string selector, string property) =>
        await _page.EvaluateAsync<string?>(
            "([s, p]) => { const e = document.querySelector(s); return e ? getComputedStyle(e).getPropertyValue(p) : null; }",
            new[] { selector, property })
        ?? throw new InvalidOperationException($"Nothing on the page matches {selector}, so it has no {property}.");

    /// <summary>
    /// The elements of the page's column that carry the <c>hidden</c> attribute and are laid out all the
    /// same, described: a shut window, a shut note, a line behind "+N more" that a rule of the page's own
    /// display shows. Empty when [hidden] hides everything it is on.
    /// </summary>
    public Task<string[]> HiddenButShownAsync() => _page.EvaluateAsync<string[]>(
        """
        () => {
          __DESCRIBE__
          return [...document.querySelectorAll('.drawio-shell [hidden]')]
            .filter((e) => getComputedStyle(e).display !== 'none')
            .map((e) => describe(e));
        }
        """.Replace("__DESCRIBE__", DescribeAnElement));

    /// <summary>
    /// Puts the keyboard focus on the page's element <paramref name="from"/> finds, presses Tab - a key,
    /// so that the browser counts the focus that follows as the keyboard's (<c>:focus-visible</c>) - and
    /// returns once the focus is on the element with the id <paramref name="toId"/>.
    /// </summary>
    public async Task TabFromAsync(string from, string toId)
    {
        await FocusAsync(from);
        await _page.Keyboard.PressAsync("Tab");
        await WaitForTheFocusOnAsync(toId);
    }

    /// <summary>
    /// Stages the live link's pill in one of its troubles, the way DrawioLiveStatus renders it - its word
    /// and the tone's class in place of the quiet "Live" - with nothing else of the page touched. What
    /// the browser then does with the bar's width is the page's stylesheet's; that the pill says these
    /// words in these tones is DrawioTopBarTest's (bUnit) to prove. A browser test cannot make the host's
    /// live channel reconnect, or drop, on cue: the page opens it by itself and it comes up at once.
    /// </summary>
    public Task StageTheLiveLinkAsync(string word, string tone) => _page.EvaluateAsync(
        """
        ([word, tone]) => {
          const pill = document.getElementById('drawio-live-status');
          if (!pill) throw new Error('the bar has no live link pill (#drawio-live-status)');
          const text = [...pill.childNodes].find((n) => n.nodeType === Node.TEXT_NODE && n.textContent.trim());
          if (!text) throw new Error('the live link pill has no word to stage');
          text.textContent = word;
          pill.classList.remove('drawio-live-on', 'drawio-pill-neutral', 'drawio-pill-warn', 'drawio-pill-bad');
          pill.classList.add(tone, 'drawio-has-tip');
        }
        """,
        new[] { word, tone });

    /// <summary>
    /// Stages the plant checks' button as DrawioChecksDock renders it while the configuration check runs:
    /// its longer words, the same words as its name (aria-label), and the blue dot - with nothing else
    /// of the page touched. The host answers the check at once, far too soon for a mouse to be put on
    /// the button meanwhile; that the button says these words and wears this dot while the check runs is
    /// DrawioTopBarTest's (bUnit) to prove.
    /// </summary>
    public Task StageTheRunningCheckAsync() => _page.EvaluateAsync(
        """
        () => {
          const words = 'Plant checks - the configuration check is running...';
          const button = document.getElementById('drawio-checks-toggle');
          const summary = document.getElementById('drawio-checks-summary');
          if (!button || !summary) throw new Error('the bar has no plant checks button (#drawio-checks-toggle, #drawio-checks-summary)');
          summary.textContent = words;
          button.setAttribute('aria-label', words);
          const dot = document.createElement('span');
          dot.className = 'drawio-badge drawio-badge-dot drawio-badge-running';
          dot.setAttribute('aria-hidden', 'true');
          button.appendChild(dot);
        }
        """);

    /// <summary>Whether the text of the page's element <paramref name="selector"/> finds runs past its box (<c>scrollWidth</c> over <c>clientWidth</c>): cut short, with an ellipsis or not.</summary>
    public Task<bool> IsCutShortAsync(string selector) => _page.EvaluateAsync<bool>(
        "(s) => { const e = document.querySelector(s); return !!e && e.scrollWidth > e.clientWidth; }",
        selector);

    /// <summary>
    /// Where the open note of the "?" <paramref name="helpId"/> (<c>{id}-body</c>) is on screen, against
    /// the browser's window, and whether the browser hits the note near its start and near its end on
    /// its first line - a note cut by a box that clips it, or past the browser's edge, is not hit there.
    /// </summary>
    public Task<DrawioNoteOnScreen> NoteOnScreenAsync(string helpId) => _page.EvaluateAsync<DrawioNoteOnScreen>(
        """
        (id) => {
          const note = document.getElementById(id + '-body');
          if (!note || !note.checkVisibility()) return { id, shown: false };
          const r = note.getBoundingClientRect();
          const y = r.top + Math.min(r.height / 2, 14);
          const hitAt = (x) => { const h = document.elementFromPoint(x, y); return !!h && (h === note || note.contains(h)); };
          return {
            id, shown: true, left: r.left, top: r.top, right: r.right, bottom: r.bottom,
            viewportWidth: window.innerWidth, viewportHeight: window.innerHeight,
            startReached: hitAt(r.left + 6), endReached: hitAt(r.right - 6),
          };
        }
        """,
        helpId);

    /// <summary>
    /// A "?" of the page (DrawioHelp's button, by its id) and its note (<c>{id}-body</c>) as the browser
    /// has them now: what the "?" says (<c>aria-expanded</c>), whether the note is laid out on screen,
    /// and what has the keyboard focus.
    /// </summary>
    public Task<DrawioHelpState> HelpStateAsync(string helpId) => _page.EvaluateAsync<DrawioHelpState>(
        """
        (id) => {
          __DESCRIBE__
          const help = document.getElementById(id);
          const note = document.getElementById(id + '-body');
          return {
            id,
            found: !!help && !!note,
            expanded: help ? help.getAttribute('aria-expanded') : null,
            noteShown: !!note && note.checkVisibility(),
            focus: describe(document.activeElement),
          };
        }
        """.Replace("__DESCRIBE__", DescribeAnElement),
        helpId);

    /// <summary>
    /// Waits until the "?" <paramref name="helpId"/> and its note agree on being <paramref name="open"/>:
    /// the "?" says so (<c>aria-expanded</c>) and the note is on screen - or neither.
    /// </summary>
    public async Task WaitForTheHelpAsync(string helpId, bool open, string after)
    {
        var expanded = open ? "true" : "false";
        try
        {
            await _page.WaitForFunctionAsync(
                "([id, expanded, open]) => { const h = document.getElementById(id); const n = document.getElementById(id + '-body');" +
                " return !!h && !!n && h.getAttribute('aria-expanded') === expanded && n.checkVisibility() === open; }",
                new object[] { helpId, expanded, open }, new PageWaitForFunctionOptions { Timeout = ConditionCeiling });
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"The note of #{helpId} did not {(open ? "open" : "shut")} {after} (within {ConditionCeiling / 1000:0} seconds): {await HelpStateAsync(helpId)}.");
        }
    }

    /// <summary>
    /// Waits until drawio-help.js watches the open note of the "?" <paramref name="helpId"/>. The page
    /// fetches the module at the note's first opening, and opens the note only once the module watches
    /// it (since 30/9: it used to tell the module to watch a few round trips after the note was on
    /// screen, and a press meanwhile - outside the note or on its text - was only what the rest of the
    /// page made of it: seen once in a run of the whole suite and every time with the module's answer
    /// held back, a press on the note's text right after its first opening left the focus on the "?" -
    /// the title bar's drag, which cancels the press, had it). Asked with an Esc that
    /// reaches the note's box and nothing else - a keydown that does not bubble, dispatched on the box:
    /// Blazor listens on the document without capturing, and the windows module on the window - which
    /// only the module marks as handled, and only while the note is open.
    /// </summary>
    public Task WaitForTheNoteToBeWatchedAsync(string helpId) => WaitForConditionAsync(
        $"() => {{ const help = document.getElementById({JsonSerializer.Serialize(helpId)}); const box = help && help.closest('.drawio-help-wrap');" +
        " return !!box && !box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: false, cancelable: true })); }",
        $"drawio-help.js never watched the note of #{helpId}: an Esc on its box was never marked as handled - the note is shut, or the module was not fetched or not told to watch");

    /// <summary>
    /// Clicks the "?" <paramref name="helpId"/> for the first time on this page with the browser's
    /// request for drawio-help.js held back, as a slow link would hold it: the page fetches the module
    /// at the first opening of a "?", and it is the module that places the note. Once the page has
    /// asked for it - by then the click has been taken, and whatever the click made the page show is
    /// on screen - <paramref name="whileHeld"/> looks at the page; then the module goes through. Fails
    /// when the page does not ask for it: a "?" opened earlier on this page has fetched it already.
    /// </summary>
    public async Task ClickTheHelpWithItsModuleHeldBackAsync(string helpId, Func<Task> whileHeld)
    {
        const string Module = "**/_content/Wcs.WebEditor/drawio-help*.js";
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var through = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task HoldAsync(IRoute route)
        {
            asked.TrySetResult();
            await release.Task;
            try { await route.ContinueAsync(); }
            finally { through.TrySetResult(); }
        }

        await _page.RouteAsync(Module, HoldAsync);
        try
        {
            await ClickAsync($"#{helpId}");
            try
            {
                await asked.Task.WaitAsync(TimeSpan.FromMilliseconds(ConditionCeiling));
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $"The page never asked for drawio-help.js after a click on #{helpId} (within {ConditionCeiling / 1000:0} seconds): " +
                    $"a \"?\" opened earlier on this page has fetched it, or the click did not reach the \"?\" ({await HelpStateAsync(helpId)}).");
            }
            await whileHeld();
        }
        finally
        {
            release.TrySetResult();
            if (asked.Task.IsCompleted) await through.Task;
            await _page.UnrouteAsync(Module, HoldAsync);
        }
    }

    /// <summary>
    /// Clicks, with the real mouse, the text of the open note of the "?" <paramref name="helpId"/>
    /// (<c>{id}-body</c>): the middle of the first line of its words where the browser hits the note or
    /// something in it that is not a link, a button or a field - the press an operator makes to select
    /// its words. Not a point of the note's box: its padding is the note too, but not its text.
    /// Returns where it clicked and what was there.
    /// </summary>
    public async Task<string> ClickTheNoteOfAsync(string helpId)
    {
        var spot = await _page.EvaluateAsync<DrawioNoteSpot?>(
            """
            (id) => {
              __DESCRIBE__
              const note = document.getElementById(id + '-body');
              if (!note || !note.checkVisibility()) return null;
              const words = document.createTreeWalker(note, NodeFilter.SHOW_TEXT,
                { acceptNode: (t) => t.textContent.trim() ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT });
              for (let t = words.nextNode(); t; t = words.nextNode()) {
                const range = document.createRange();
                range.selectNodeContents(t);
                for (const line of range.getClientRects()) {
                  const x = line.left + line.width / 2, y = line.top + line.height / 2;
                  const hit = document.elementFromPoint(x, y);
                  if (hit && note.contains(hit) && !hit.closest('a, button, input, select, textarea')) return { x, y, what: describe(hit) };
                }
              }
              return null;
            }
            """.Replace("__DESCRIBE__", DescribeAnElement),
            helpId)
            ?? throw new InvalidOperationException(
                $"The note of #{helpId} has no words to press: it is not on screen, or no line of its text is where the browser hits it bare ({await HelpStateAsync(helpId)}).");
        await _page.Mouse.ClickAsync((float)spot.X, (float)spot.Y);
        return $"the note of #{helpId} at {spot}";
    }

    /// <summary>
    /// Sorts the list table <paramref name="tableId"/> by its first column WITHOUT A PRESS: a click
    /// event dispatched on the column's header, which Blazor takes like any click, while the browser
    /// sends no <c>pointerdown</c> and moves no focus. Returns once the header says the new order
    /// (<c>aria-sort</c>), that is once the page has rendered it, and what that order is. The page's
    /// circuit takes what the browser sends in the order it was sent - the gestures and the calls of
    /// the page's own modules alike - and renders it in that order: by the time the sort is on screen,
    /// whatever an earlier gesture made the page do is on screen too. For a test that has to see
    /// something NOT happen, or not happen on the way - a "?"'s note left open under a press on its own
    /// text, or opened again after the click that shut it - where a real press, or a move of the
    /// focus, would be one more gesture on the note.
    /// </summary>
    public async Task<string> SortTheTableWithoutAPressAsync(string tableId)
    {
        var sorted = await _page.EvaluateAsync<string[]?>(
            """
            (id) => {
              const th = document.querySelector('#' + CSS.escape(id) + ' thead th');
              if (!th) return null;
              const before = th.getAttribute('aria-sort') || '';
              th.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return [th.dataset.property || th.textContent.trim(), before];
            }
            """,
            tableId)
            ?? throw new InvalidOperationException($"The table #{tableId} is not in the page, or has no column header to sort it by.");
        var (column, before) = (sorted[0], sorted[1]);
        await WaitForConditionAsync(
            $"() => {{ const th = document.querySelector('#' + CSS.escape({JsonSerializer.Serialize(tableId)}) + ' thead th'); " +
            $"return !!th && (th.getAttribute('aria-sort') || '') !== {JsonSerializer.Serialize(before)}; }}",
            $"the table #{tableId} was not sorted again by {column} (its header still says aria-sort \"{before}\"): the page never rendered the click dispatched on it");
        var after = await _page.EvaluateAsync<string>(
            "(id) => document.querySelector('#' + CSS.escape(id) + ' thead th').getAttribute('aria-sort') || ''", tableId);
        return $"#{tableId} sorted by {column}, aria-sort \"{before}\" -> \"{after}\"";
    }

    /// <summary>
    /// Clicks, with the real mouse, a spot of the drawing that nothing covers, far from
    /// <paramref name="shapesToAvoid"/>: where the page shows draw.io's frame - none of the page's
    /// windows is open over it - and draw.io shows its drawing - none of its own panels is over it.
    /// Returns where it clicked and what was there. Not <see cref="ClickEmptyCanvasAsync"/>'s corners:
    /// measured on 29/9 at 1366x768, all four of them are on something of draw.io's own that lies over
    /// its drawing's container - its shapes panel, its format panel, a field and a bare box - and draw.io
    /// cancels a press on its shapes panel: a cancelled press moves no focus at all, so a test of what a
    /// click on the drawing does would be testing something else.
    /// </summary>
    public async Task<string> ClickTheDrawingWhereNothingCoversItAsync(params string[] shapesToAvoid)
    {
        var taken = new List<LocatorBoundingBoxResult>();
        foreach (var name in shapesToAvoid) taken.Add(await BoxOf(Shape(name), name));

        var spots = await UncoveredSpotsOfTheFrameAsync("div.geDiagramContainer", ".mxWindow, .geSidebarContainer");
        foreach (var spot in spots)
        {
            if (taken.Any(box => IsWithinReach(box, (float)spot.X, (float)spot.Y))) continue;
            await _page.Mouse.ClickAsync((float)spot.X, (float)spot.Y);
            return $"the drawing at {spot}";
        }

        throw new InvalidOperationException(spots.Length == 0
            ? "No spot of the drawing is uncovered: of the 81 looked at, none showed it - under a window of the page or a panel of draw.io, or draw.io has no drawing."
            : $"No spot of the drawing is uncovered and clear of the shapes: of the 81 looked at, {spots.Length} showed the drawing, and every one of them was near a shape.");
    }

    /// <summary>
    /// Clicks, with the real mouse, a spot of draw.io's shapes panel (on the left, not the format panel
    /// on the right) that nothing of the page covers, and that is the panel's own and nothing in it: not
    /// an entry - a click on one would put its shape on the drawing - nor a section's title, which would
    /// open or shut the section, nor a field. It is the press an operator makes on the way to dragging a
    /// device onto the drawing, and draw.io cancels it on the whole panel, entries included (measured on
    /// 30/9: <c>mousedown</c> <c>defaultPrevented</c>, the page's focus left where it was). Returns where
    /// it clicked and what was there.
    /// </summary>
    public async Task<string> ClickTheShapesPanelWhereNothingCoversItAsync()
    {
        var spots = await UncoveredSpotsOfTheFrameAsync(
            ".geSidebarContainer:not(.geFormatContainer)", "a, .geTitle, input, select, textarea, button");
        if (spots.Length == 0)
            throw new InvalidOperationException(
                "No spot of draw.io's shapes panel is uncovered and bare: of the 81 looked at, every one was under a window of the page, on an entry, a title or a field, or off the panel.");
        await _page.Mouse.ClickAsync((float)spots[0].X, (float)spots[0].Y);
        return $"draw.io's shapes panel at {spots[0]}";
    }

    /// <summary>
    /// The spots of draw.io's element <paramref name="within"/> (the first one its frame's document
    /// has) where nothing covers it: the page hits draw.io's frame there - none of the page's windows is
    /// open over it - and draw.io hits something inside <paramref name="within"/> and not inside
    /// <paramref name="notOn"/>. Looked for on a grid of the element, row by row from the top, at every
    /// tenth of its width and height; in the page's coordinates. Empty when draw.io has no such element.
    /// </summary>
    private Task<DrawioFrameSpot[]> UncoveredSpotsOfTheFrameAsync(string within, string notOn) =>
        _page.EvaluateAsync<DrawioFrameSpot[]>(
            """
            ([within, notOn]) => {
              const f = document.getElementById('drawio-frame');
              const d = f && f.contentDocument;
              const box = d && d.querySelector(within);
              if (!box) return [];
              const r = f.getBoundingClientRect();
              const b = box.getBoundingClientRect();
              const left = r.left + f.clientLeft + b.left, top = r.top + f.clientTop + b.top;
              const spots = [];
              for (let row = 1; row <= 9; row++) {
                for (let column = 1; column <= 9; column++) {
                  const x = left + b.width * column / 10, y = top + b.height * row / 10;
                  if (document.elementFromPoint(x, y) !== f) continue;
                  const e = d.elementFromPoint(x - r.left - f.clientLeft, y - r.top - f.clientTop);
                  if (!e || !e.closest(within) || e.closest(notOn)) continue;
                  const what = e.tagName.toLowerCase() + (typeof e.className === 'string' && e.className.trim() ? '.' + e.className.trim().split(/\s+/).join('.') : '');
                  spots.push({ x, y, what });
                }
              }
              return spots;
            }
            """,
            new[] { within, notOn });

    /// <summary>
    /// Drags an open window by its title bar, with the real mouse, so that its left edge goes to
    /// <paramref name="left"/> and its top as far up as it goes - the gesture ends past the top of the
    /// browser: up against the floor, if the floor holds. The window is taken where its title bar is its
    /// own (<see cref="TitleBarGripAsync"/>).
    /// </summary>
    public async Task DragTheWindowUpAsync(string windowId, double left)
    {
        var box = await BoxOfAsync($"#{windowId}");
        var (x, y) = await TitleBarGripAsync(windowId);
        await _page.Mouse.MoveAsync(x, y);
        await _page.Mouse.DownAsync();
        await _page.Mouse.MoveAsync((float)(x + left - box[0]), (float)(y - box[1] - 200), new MouseMoveOptions { Steps = 10 });
        await _page.Mouse.UpAsync();
    }

    /// <summary>
    /// Drags an open window by a safe, uncovered point of its title bar until its top-left corner is
    /// at the requested viewport coordinates.
    /// </summary>
    public async Task DragTheWindowToAsync(string windowId, double left, double top)
    {
        var box = await BoxOfAsync($"#{windowId}");
        var (x, y) = await TitleBarGripAsync(windowId);
        await _page.Mouse.MoveAsync(x, y);
        await _page.Mouse.DownAsync();
        await _page.Mouse.MoveAsync(
            (float)(x + left - box[0]),
            (float)(y + top - box[1]),
            new MouseMoveOptions { Steps = 10 });
        await _page.Mouse.UpAsync();
    }

    /// <summary>
    /// Clicks, with the real mouse, the title bar of an open window where it is its own
    /// (<see cref="TitleBarGripAsync"/>): the press a drag of the window begins with, let go where it
    /// was pressed, so the window does not move. The windows module cancels that press
    /// (<c>preventDefault</c> on its <c>pointerdown</c>, drawio-windows.js), and the browser then sends
    /// no <c>mousedown</c> and moves no focus - measured on 30/9, the focus stayed on the "?" of the
    /// window. Returns where it clicked.
    /// </summary>
    public async Task<string> ClickTheTitleBarAsync(string windowId)
    {
        var (x, y) = await TitleBarGripAsync(windowId);
        await _page.Mouse.ClickAsync(x, y);
        return FormattableString.Invariant($"the title bar of #{windowId} at {x:0},{y:0}");
    }

    /// <summary>
    /// Where an open window's title bar is its own: the first point along its middle line, from the
    /// left, where the browser hits the title bar and not one of its buttons, nor the "?" with its note,
    /// nor another window over it - with several windows open, the middle of a title bar may well be
    /// under another one, and a press there would take that one.
    /// </summary>
    private async Task<(float X, float Y)> TitleBarGripAsync(string windowId)
    {
        var grip = await _page.EvaluateAsync<double[]?>(
            """
            (id) => {
              const title = document.getElementById(id)?.querySelector('.drawio-window-title');
              if (!title) return null;
              const r = title.getBoundingClientRect();
              const y = r.top + r.height / 2;
              for (let x = r.left + 4; x < r.right - 4; x += 4) {
                const hit = document.elementFromPoint(x, y);
                if (hit && title.contains(hit) && !hit.closest('button, .drawio-help-wrap')) return [x, y];
              }
              return null;
            }
            """,
            windowId)
            ?? throw new InvalidOperationException(
                $"The title bar of the window '{windowId}' is nowhere its own: covered by other windows, or all buttons - there is no point to take it by.");
        return ((float)grip[0], (float)grip[1]);
    }

    /// <summary>
    /// Where the browser lays out the properties panel's actions (<c>.drawio-property-actions</c>, the
    /// first thing in <c>#drawio-property-panel</c>): the box of the row, and the box of each button
    /// shown in it, in the page's order.
    /// </summary>
    public Task<DrawioActionsRow> PanelActionsAsync() => _page.EvaluateAsync<DrawioActionsRow>(
        """
        () => {
          const row = document.querySelector('#drawio-property-panel > .drawio-property-actions');
          if (!row) return { found: false, top: 0, bottom: 0, buttons: [] };
          const r = row.getBoundingClientRect();
          const buttons = [...row.querySelectorAll('button')].filter(b => b.checkVisibility()).map(b => {
            const box = b.getBoundingClientRect();
            return { id: b.id, top: box.top, bottom: box.bottom, left: box.left, right: box.right };
          });
          return { found: true, top: r.top, bottom: r.bottom, buttons };
        }
        """);

    /// <summary>
    /// How far what the element <paramref name="selector"/> finds holds runs past its box to the right
    /// (<c>scrollWidth - clientWidth</c>): 0 when nothing does, and then a box that scrolls shows no
    /// scrollbar across.
    /// </summary>
    public async Task<double> OverflowAcrossAsync(string selector) =>
        await _page.EvaluateAsync<double?>(
            "(s) => { const e = document.querySelector(s); return e ? e.scrollWidth - e.clientWidth : null; }",
            selector)
        ?? throw new InvalidOperationException($"Nothing on the page matches {selector}, so nothing can overflow it.");

    /// <summary>
    /// How the browser paints the button - or the label drawn as one - that <paramref name="selector"/>
    /// finds: its corners, its border and the weight of its text at rest, and its background and text
    /// at rest, with the mouse on it and pressed. The mouse starts on the middle of <paramref name="elsewhere"/> (the rest), and
    /// the press is let go there too: the press and the release are on two different elements, so the
    /// browser makes no click of them on the button - nothing is exported, imported, confirmed,
    /// cancelled or gone to. Each state is read once the browser says it holds (<c>:hover</c>,
    /// <c>:active</c>), not after a pause.
    /// </summary>
    public async Task<DrawioButtonLook> ButtonLookAsync(string selector, string elsewhere)
    {
        const string Paint =
            "(s) => { const e = document.querySelector(s); if (!e) return null; const c = getComputedStyle(e); " +
            "return [c.backgroundColor, c.color, c.borderTopLeftRadius, c.borderTopColor, c.fontWeight]; }";
        var target = JsonSerializer.Serialize(selector);
        async Task<string[]> PaintAsync() => await _page.EvaluateAsync<string[]?>(Paint, selector)
            ?? throw new InvalidOperationException($"Nothing on the page matches {selector}, so it has no look.");

        var away = await BoxOfAsync(elsewhere);
        var box = await BoxOfAsync(selector);
        var awayX = (float)(away[0] + away[2] / 2);
        var awayY = (float)(away[1] + away[3] / 2);

        await _page.Mouse.MoveAsync(awayX, awayY);
        await WaitForConditionAsync(
            $"() => {{ const e = document.querySelector({target}); return !!e && !e.matches(':hover'); }}",
            $"{selector} is still under the mouse with the mouse on {elsewhere}");
        var rest = await PaintAsync();

        await _page.Mouse.MoveAsync((float)(box[0] + box[2] / 2), (float)(box[1] + box[3] / 2));
        await WaitForConditionAsync(
            $"() => {{ const e = document.querySelector({target}); return !!e && e.matches(':hover'); }}",
            $"{selector} is not under the mouse after the mouse was put on its middle - is something covering it?");
        var hover = await PaintAsync();

        await _page.Mouse.DownAsync();
        await WaitForConditionAsync(
            $"() => {{ const e = document.querySelector({target}); return !!e && e.matches(':active'); }}",
            $"{selector} is not pressed with the mouse held down on it");
        var pressed = await PaintAsync();
        await _page.Mouse.MoveAsync(awayX, awayY);
        await _page.Mouse.UpAsync();

        return new DrawioButtonLook
        {
            Selector = selector,
            Corners = rest[2],
            Border = rest[3],
            Weight = rest[4],
            Rest = rest[0],
            RestText = rest[1],
            Hover = hover[0],
            Pressed = pressed[0],
            PressedText = pressed[1],
        };
    }

    /// <summary>The corners of the element <paramref name="selector"/> finds, as the browser computes them (<c>border-top-left-radius</c>).</summary>
    public async Task<string> CornersOfAsync(string selector) =>
        await _page.EvaluateAsync<string?>(
            "(s) => { const e = document.querySelector(s); return e ? getComputedStyle(e).borderTopLeftRadius : null; }",
            selector)
        ?? throw new InvalidOperationException($"Nothing on the page matches {selector}, so it has no corners.");

    /// <summary>
    /// Waits until the engine's switch offers the gesture <paramref name="buttonId"/>
    /// (<c>drawio-engine-start</c> or <c>drawio-engine-stop</c>) and can be pressed: nothing pending,
    /// the button there and switched on.
    /// </summary>
    public Task WaitForTheEngineToOfferAsync(string buttonId) => WaitForConditionAsync(
        $"() => {{ const b = document.getElementById({JsonSerializer.Serialize(buttonId)});" +
        " return !!b && !b.disabled && !document.getElementById('drawio-engine-pending'); }",
        $"the engine's switch never offered {buttonId} with nothing pending");

    /// <summary>
    /// Presses "Export" and saves what the browser really downloads, returning the file's path.
    /// It goes through the browser's own download, not through a second call to the API: what is
    /// being measured is the file an operator ends up with on his disk.
    /// </summary>
    public async Task<string> ExportToAFileAsync(string directory)
    {
        var download = await _page.RunAndWaitForDownloadAsync(async () =>
        {
            await _page.ClickAsync("#plant-export-button", new PageClickOptions { Timeout = ConditionCeiling });
        }, new PageRunAndWaitForDownloadOptions { Timeout = ConditionCeiling });

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, download.SuggestedFilename);
        await download.SaveAsAsync(path);
        return path;
    }

    /// <summary>
    /// Hands a file to the import picker, as choosing one in the native dialog does, and waits for
    /// the confirmation to be on screen: the page reads the file and the plant before it can say
    /// anything, so returning earlier would race the dialog.
    /// </summary>
    public async Task ChooseTheFileToImportAsync(string path)
    {
        await StepAsync(
            () => _page.SetInputFilesAsync("#plant-import-input", path, new PageSetInputFilesOptions { Timeout = ConditionCeiling }),
            $"the import picker did not take the file '{path}'");
        await WaitForConditionAsync(
            "() => !!document.getElementById('plant-import-confirm')",
            "the import confirmation never showed up after the file was chosen - see #plant-io-error for what the page said");
    }

    /// <summary>What the confirmation says about the free drawing: the one sentence that tells the two kinds of file apart.</summary>
    public async Task<string> WhatTheImportSaysAboutTheDrawingAsync() =>
        (await _page.Locator("#plant-import-decoration").InnerTextAsync()).Trim();

    /// <summary>The whole confirmation dialog in plain text, for a failure message that can be read.</summary>
    public async Task<string> TheImportDialogAsync() =>
        (await _page.Locator("#plant-import-confirm").InnerTextAsync()).Trim();

    /// <summary>
    /// Confirms the import and comes back when the page is up again with the drawing on it. A
    /// confirmed import reloads the WHOLE page (see ImportExport), which takes down the draw.io
    /// iframe with it and kills the Blazor circuit: the key has to be given again, exactly as at
    /// the first opening, and nothing can be read off the page until it is.
    /// </summary>
    /// <param name="ceiling">
    /// How long to wait for the page to go down and come up again, in milliseconds. The default is
    /// the usual one, and a caller passes more only when it has MEASURED that it needs more. The
    /// whole-round test does: on its own this step takes about a second, and in a full suite run -
    /// several hosts and several Chromiums at once on one machine - it went past 60 s in two runs
    /// out of three, always here and nowhere else. That is the machine and not the product: the
    /// plant being replaced holds four devices.
    /// </param>
    public async Task ConfirmTheImportAndWaitForTheReloadAsync(string apiKey, float? ceiling = null)
    {
        var wait = ceiling ?? ConditionCeiling;
        await StepAsync(
            () => ClickAndWaitForTheNextLoadAsync("#plant-import-confirm-button", wait),
            "the import confirmation did not take the page down and up again - see #plant-io-error for what it said instead");

        await PassTheKeyGateAsync(apiKey);
    }

    /// <summary>
    /// Clicks <paramref name="selector"/> and comes back when the main frame has loaded a NEW document,
    /// all within <paramref name="ceiling"/> milliseconds - what the deprecated
    /// <c>RunAndWaitForNavigationAsync</c> did here (its default waits for the "load" event). Its
    /// suggested successor, <c>WaitForURLAsync</c>, cannot stand in: the import reloads the page on
    /// the URL it is already on, so a wait on the URL would be satisfied before the reload began. The
    /// page's own <c>Load</c> event is listened to before the click, so a fast reload cannot slip by.
    /// A ceiling that runs out throws the same <see cref="TimeoutException"/> Playwright throws.
    /// </summary>
    private async Task ClickAndWaitForTheNextLoadAsync(string selector, float ceiling)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoad(object? sender, IPage page) => loaded.TrySetResult();

        _page.Load += OnLoad;
        try
        {
            var reload = loaded.Task.WaitAsync(TimeSpan.FromMilliseconds(ceiling));
            await _page.ClickAsync(selector, new PageClickOptions { Timeout = ceiling });
            await reload;
        }
        finally
        {
            _page.Load -= OnLoad;
        }
    }

    /// <summary>What the import said when it refused, or null when it said nothing.</summary>
    public Task<string?> ImportErrorAsync() => TextOrNull("#plant-io-error");

    // ---- The plant's checks --------------------------------------------------------------------

    /// <summary>
    /// Opens the plant checks the way a person does: one click on their button in the bar, and the
    /// window the page opens for them (until the passata grafica of 29/9, a click on the
    /// <c>&lt;summary&gt;</c> line of a box above the strip). Not by un-hiding the window from
    /// JavaScript - the gesture under test is the operator's, and a window forced open would not say
    /// that the button can be pressed.
    /// </summary>
    public Task OpenTheChecksAsync() => OpenASectionAsync("drawio-checks");

    /// <summary>What the checks box says in plain text, for a failure message that can be read.</summary>
    public async Task<string> ChecksBoxAsync()
    {
        var box = _page.Locator("#drawio-checks");
        return await box.CountAsync() == 0 ? "(no checks box)" : (await box.InnerTextAsync()).Trim();
    }

    /// <summary>Presses "Check Junctions" - the panel's own button, used as it is.</summary>
    public Task RunTheJunctionCheckAsync() => StepAsync(
        () => _page.ClickAsync("#check-junctions-button", new PageClickOptions { Timeout = ConditionCeiling }),
        "the \"Check Junctions\" button did not let itself be pressed");

    /// <summary>Presses "Check Configuration".</summary>
    public Task RunTheConfigurationCheckAsync() => StepAsync(
        () => _page.ClickAsync("#check-config-button", new PageClickOptions { Timeout = ConditionCeiling }),
        "the \"Check Configuration\" button did not let itself be pressed");

    /// <summary>The lines the junction check put on screen, one per warning.</summary>
    public Task<IReadOnlyList<string>> JunctionWarningsAsync() => LinesOf("#junction-check-result li");

    /// <summary>What the junction check said in whole, or null when it has not been run.</summary>
    public Task<string?> JunctionCheckResultAsync() => TextOrNull("#junction-check-result");

    /// <summary>
    /// The words of the plant checks' button in the bar - what can be read with their window shut (the
    /// summary line of their box until the passata grafica of 29/9, the same words).
    /// </summary>
    public Task<string?> ChecksSummaryAsync() => TextOrNull("#drawio-checks-summary");

    /// <summary>
    /// Waits until the summary line is back to its resting text, that is until it has <b>stopped</b>
    /// saying that the configuration check is running.
    /// </summary>
    /// <remarks>
    /// The whole text is compared and not a substring: "Plant checks" is inside the working line too
    /// ("Plant checks - the configuration check is running..."), so a substring would be satisfied by
    /// the very state this waits to leave.
    /// </remarks>
    public Task WaitForTheSummaryToRestAsync() => WaitForConditionAsync(
        "() => { const line = document.getElementById('drawio-checks-summary');" +
        " return !!line && line.textContent.trim() === 'Plant checks'; }",
        "the checks summary line never went back to \"Plant checks\": it was left saying that the configuration check is still running");

    /// <summary>
    /// What "Check Configuration" said, whichever of its two answers came back - the failure line or
    /// the result - or null when nobody has pressed it.
    /// </summary>
    public async Task<string?> ConfigCheckOutcomeAsync() =>
        await TextOrNull("#config-check-error") ?? await TextOrNull("#config-check-result");

    /// <summary>
    /// Waits until the configuration check has SAID something at all - its rows, "verificata con
    /// successo", or a failure - and hands back what it said.
    /// </summary>
    public async Task<string> WaitForAConfigurationCheckOutcomeAsync()
    {
        try
        {
            await WaitForConditionAsync(
                "() => !!document.getElementById('config-check-error') || !!document.getElementById('config-check-result')",
                "the configuration check never said anything at all: neither a result nor a failure reached the screen");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}{Environment.NewLine}The checks box said:{Environment.NewLine}{await ChecksBoxAsync()}", ex);
        }

        return await ConfigCheckOutcomeAsync()
            ?? throw new InvalidOperationException("The configuration check's answer was on the page an instant ago and is not there now.");
    }

    /// <summary>The labels of the "go to" buttons the page is offering, in the order it draws them.</summary>
    public Task<IReadOnlyList<string>> CheckTargetsAsync() => LinesOf("button.drawio-checks-target");

    /// <summary>
    /// Waits until the page is offering a way to reach this device. Waiting is not optional: the
    /// click on "Check Junctions" travels over the Blazor circuit and the list comes back as a render.
    /// </summary>
    public async Task WaitForCheckTargetAsync(string deviceName)
    {
        try
        {
            await WaitForConditionAsync(
                "() => Array.from(document.querySelectorAll('button.drawio-checks-target'))" +
                $".some(b => b.textContent.trim() === {JsonSerializer.Serialize($"Go to {deviceName}")})",
                $"the checks never offered a way to reach \"{deviceName}\"");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}{Environment.NewLine}The checks box said:{Environment.NewLine}{await ChecksBoxAsync()}", ex);
        }
    }

    /// <summary>Goes to a device the way a person does: one click on its button.</summary>
    public Task GoToTheDeviceAsync(string deviceName) => StepAsync(
        () => _page.Locator("button.drawio-checks-target")
            .Filter(new LocatorFilterOptions { HasTextString = $"Go to {deviceName}" })
            .ClickAsync(new LocatorClickOptions { Timeout = ConditionCeiling }),
        $"the button that reaches \"{deviceName}\" did not let itself be pressed");

    /// <summary>What the page said about the last "go to", or null when nobody has asked for one.</summary>
    public Task<string?> GoToOutcomeAsync() => TextOrNull("#drawio-checks-goto-outcome");

    /// <summary>
    /// Which of the two colours that line is wearing, read off its class: <c>drawio-checks-goto-reached</c>
    /// or <c>drawio-checks-goto-missed</c>. Null when there is no line.
    /// </summary>
    /// <remarks>
    /// The WORDS and the COLOUR are two separate claims, and only the words used to be read here: a
    /// "nothing was selected" painted in the green of a success says two opposite things at once, and
    /// that is what a miss after a hit looked like until this was asserted.
    /// </remarks>
    public async Task<string?> GoToOutcomeToneAsync()
    {
        var line = _page.Locator("#drawio-checks-goto-outcome");
        return await line.CountAsync() == 0 ? null : (await line.GetAttributeAsync("class"))?.Trim();
    }

    /// <summary>Waits until the page has said something about the last "go to" and that it contains this.</summary>
    public async Task WaitForGoToOutcomeAsync(string containing)
    {
        try
        {
            await WaitForConditionAsync(
                "() => { const line = document.getElementById('drawio-checks-goto-outcome');" +
                $" return !!line && line.textContent.includes({JsonSerializer.Serialize(containing)}); }}",
                $"the page never said \"{containing}\" about the last \"go to\"");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}{Environment.NewLine}The checks box said:{Environment.NewLine}{await ChecksBoxAsync()}", ex);
        }
    }

    /// <summary>
    /// What draw.io itself has selected right now, read <b>out of its own graph</b> and not out of
    /// the page: the identifiers of the selected cells.
    /// </summary>
    /// <remarks>
    /// Different from <see cref="SelectedCellsAsync"/>, which reports what the page was TOLD through
    /// the bridge. Here the question is whether the selection really landed inside draw.io, so the
    /// answer has to come from draw.io - the page's own line could be right for a reason that has
    /// nothing to do with the canvas.
    /// </remarks>
    public Task<string[]> SelectedInTheDrawingAsync() => InTheGraphAsync<string[]>(
        "return ui.editor.graph.getSelectionCells().map((cell) => (cell.id == null ? '' : String(cell.id)));");

    /// <summary>
    /// The undo manager's two numbers, as <c>[indexOfNextAdd, history.length]</c>: what a Ctrl+Z would
    /// walk into, and how much there is. It is the direct measurement of the question this batch is
    /// shaped by - whether a selection costs the operator a press.
    /// </summary>
    public Task<int[]> UndoDepthAsync() => InTheGraphAsync<int[]>(
        """
        const manager = ui.editor.undoManager;
        if (!manager || !manager.history) throw new Error('draw.io handed over no undoManager to read');
        return [manager.indexOfNextAdd, manager.history.length];
        """);

    /// <summary>Un letterale XPath sicuro anche quando il testo contiene un apice.</summary>
    private static string XPathLiteral(string value) =>
        !value.Contains('\'') ? $"'{value}'"
        : !value.Contains('"') ? $"\"{value}\""
        : "concat('" + value.Replace("'", "', \"'\", '") + "')";
}

/// <summary>
/// One cell of draw.io's selection, as the parent page reads it. <see cref="Id"/> is the cell's own
/// identifier - for a device drawn by <c>DrawioDocument</c> that IS the plant item's guid - and
/// <see cref="Wcs"/> is our own mark, <c>"device"</c> or <c>"junction"</c>, absent on anything the
/// operator drew themselves.
/// </summary>
public sealed class DrawioSelectedCell
{
    /// <summary>The mxGraph cell's own identifier: for a device, the plant item's guid.</summary>
    public string Id { get; set; } = "";

    /// <summary>The label draw.io draws under the shape - the device's name.</summary>
    public string? Label { get; set; }

    /// <summary>Our own mark: <c>"device"</c>, <c>"junction"</c>, or absent on the operator's own drawing.</summary>
    public string? Wcs { get; set; }
}

/// <summary>
/// What <see cref="DrawioEditorPage.WhatTheBridgeHoldsAfterTheReloadAsync"/> read after draw.io was
/// reloaded inside the same iframe element. The first three say the reload was the one the test
/// means - same element, same WindowProxy, new document - and the fourth that draw.io really started
/// over; only then do the last two say anything about the bridge.
/// </summary>
public sealed class DrawioReloadedFrame
{
    /// <summary>The <c>&lt;iframe&gt;</c> element is the one that was there before the reload.</summary>
    public bool SameFrame { get; set; }

    /// <summary><c>iframe.contentWindow</c> is the very object it was before: a WindowProxy survives the reload.</summary>
    public bool SameWindow { get; set; }

    /// <summary>The iframe holds a different document from the one before the reload.</summary>
    public bool NewDocument { get; set; }

    /// <summary><c>Draw.loadPlugin</c> now hands out a graph that is not the one from before.</summary>
    public bool DrawioHasANewGraph { get; set; }

    /// <summary>The bridge's <c>liveTarget()</c> is on the graph draw.io hands out now.</summary>
    public bool BridgeHoldsTheNewGraph { get; set; }

    /// <summary>The bridge's <c>liveTarget()</c> is still on the graph of the document that was reloaded away.</summary>
    public bool BridgeStillHoldsTheOldGraph { get; set; }

    /// <summary>All six in one line, for a failure message that can be read without a debugger.</summary>
    public override string ToString() =>
        $"sameFrame={SameFrame}, sameWindow={SameWindow}, newDocument={NewDocument}, " +
        $"drawioHasANewGraph={DrawioHasANewGraph}, bridgeHoldsTheNewGraph={BridgeHoldsTheNewGraph}, " +
        $"bridgeStillHoldsTheOldGraph={BridgeStillHoldsTheOldGraph}";
}

/// <summary>
/// What came of closing the tab through <see cref="DrawioEditorPage.CloseTheTabAsync"/>.
/// </summary>
/// <param name="Asked">The browser asked "Leave site?" before closing.</param>
/// <param name="DialogType">The type of the question, <c>beforeunload</c> when it is the one meant; null when nothing was asked.</param>
/// <param name="StillOpen">After the question was refused, the page was still there and answering.</param>
public sealed record DrawioTabClosing(bool Asked, string? DialogType, bool StillOpen);

/// <summary>
/// One marked cell of the drawing, as <see cref="DrawioEditorPage.MarkedCellVisibilityAsync"/> read
/// it: see that method's remarks for why <see cref="ModelVisible"/> and <see cref="Rendered"/> are
/// two different questions and not one asked twice.
/// </summary>
/// <remarks>
/// A plain class with settable properties, not a positional record: this is deserialized straight
/// out of the JSON <c>page.EvaluateAsync</c> hands back (like <see cref="DrawioSelectedCell"/>
/// above), and a record's constructor is not what that deserialization goes through.
/// </remarks>
public sealed class DrawioCellVisibility
{
    public string Id { get; set; } = "";
    public string Mark { get; set; } = "";
    public bool ModelVisible { get; set; }
    public bool Rendered { get; set; }

    public override string ToString() => $"{Id} ({Mark}): modelVisible={ModelVisible}, rendered={Rendered}";
}

/// <summary>
/// What <see cref="DrawioEditorPage.SaveButtonReachAsync"/> found under draw.io's Save button.
/// </summary>
public sealed class DrawioSaveButtonReach
{
    /// <summary>draw.io's own document finds the button (or something inside it) under its middle.</summary>
    public bool InsideTheFrame { get; set; }

    /// <summary>The page finds the iframe at that same spot on the screen: nothing of the page lies over it.</summary>
    public bool OnThePage { get; set; }

    public string UnderTheButton { get; set; } = "";
    public string OnThePageAtTheButton { get; set; } = "";
    public string Button { get; set; } = "";
    public int FrameHeight { get; set; }
    public int BodyHeight { get; set; }

    /// <summary>
    /// The height the window leaves the drawing once every other strip of the page has had its own,
    /// counted whether or not those strips still fit in the window: the drawing's whole share, which
    /// it must take - no more (the page would scroll), no less.
    /// </summary>
    public int WindowRoom { get; set; }
    public int PageScrollHeight { get; set; }
    public int WindowHeight { get; set; }
    public DrawioWindowBox[] DrawioWindows { get; set; } = [];

    /// <summary>The page's own floating windows open at the time (spec 2026-09-29), by id, in the order of the strip.</summary>
    public string[] OpenWindows { get; set; } = [];

    /// <summary>Where draw.io's frame starts, in the page's window: a section's window opening must not move it.</summary>
    public int FrameTop { get; set; }

    /// <summary>A hand reaches it: nothing covers it, neither in draw.io nor on the page.</summary>
    public bool Reachable => InsideTheFrame && OnThePage;

    public override string ToString() =>
        $"windows open: {(OpenWindows.Length == 0 ? "(none)" : string.Join(", ", OpenWindows))}; " +
        $"reachable={Reachable}; under the button: {UnderTheButton}; on the page there: {OnThePageAtTheButton}; " +
        $"button (x,y,w,h in the frame) {Button}; frame at {FrameTop}, {FrameHeight} tall, drawing area {BodyHeight} (the window leaves it {WindowRoom}), " +
        $"page {PageScrollHeight} tall in a window of {WindowHeight}; draw.io windows: " +
        (DrawioWindows.Length == 0 ? "(none)" : string.Join("; ", DrawioWindows.Select(w => w.ToString())));
}

/// <summary>One of draw.io's own floating windows (Shapes, Format...), where it is inside draw.io's frame.</summary>
public sealed class DrawioWindowBox
{
    public string Title { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public override string ToString() => $"\"{Title}\" at {X},{Y} {Width}x{Height}";
}

/// <summary>
/// Where the drag of <see cref="DrawioEditorPage.DragAnArrowByHandAsync"/> ends. They are three
/// different gestures and the save treats each one differently, so none of them is the default of
/// the others: the caller says which one it means to measure.
/// </summary>
public enum DrawioArrowLanding
{
    /// <summary>On the shape's left-hand connection point: the arrow hooks a port.</summary>
    OnThePort,

    /// <summary>In the middle of the shape: the arrow hooks the whole cell, with no port.</summary>
    OnTheMiddle,

    /// <summary>
    /// Beside the shape, on empty canvas: the aim was missed altogether and the arrow is left with
    /// one end hanging from nothing. It is the third of the three landings an operator can produce
    /// with the same drag, and the only one that used to pass in silence.
    /// </summary>
    BesideTheShape,
}

/// <summary>
/// One edge of draw.io's model, whoever made it.
/// </summary>
/// <remarks>
/// <see cref="Wcs"/> is our own mark and <see cref="ValueKind"/> is what the cell's value actually
/// is: a bare string ("string"), nothing at all ("none"), or an XML element ("element:object"). The
/// two together tell an unmarked <c>&lt;object&gt;</c> from a cell that could not carry a mark at
/// all, which is the difference a repair would have to close.
/// </remarks>
public sealed class DrawioEdge
{
    /// <summary>The identifier draw.io chose for the arrow.</summary>
    public string Id { get; set; } = "";

    /// <summary>The cell the arrow starts from, or null when it hangs in the air.</summary>
    public string? Source { get; set; }

    /// <summary>The cell the arrow ends on, or null when it hangs in the air.</summary>
    public string? Target { get; set; }

    /// <summary>Our own mark, <c>"junction"</c> on an arrow the plant will read as one.</summary>
    public string? Wcs { get; set; }

    /// <summary>What kind of thing the cell's value is: <c>none</c>, <c>string</c>, or <c>element:&lt;name&gt;</c>.</summary>
    public string ValueKind { get; set; } = "";

    /// <summary>The arrow's style, ports included when it is hooked to any.</summary>
    public string Style { get; set; } = "";

    /// <summary>A whole line about this arrow, for a failure message that can be read without a debugger.</summary>
    public override string ToString() =>
        $"{Id}: {Source ?? "(nessuna)"} -> {Target ?? "(nessuna)"}, marchio {Wcs ?? "(nessuno)"}, " +
        $"valore {ValueKind}, stile \"{Style}\"";
}

/// <summary>
/// What came of a drag of the mouse meant to draw an arrow: see
/// <see cref="DrawioEditorPage.DragAnArrowByHandAsync"/>.
/// </summary>
public sealed class DrawioHandDrawnArrow
{
    /// <summary>The edges that were not in the model before the gesture. Empty when the gesture drew nothing.</summary>
    public IReadOnlyList<DrawioEdge> NewEdges { get; set; } = [];

    /// <summary>
    /// The shape pressed on changed place: the press became a MOVE instead of a connection, and
    /// whatever else this measurement says about the plant, it did not measure an arrow.
    /// </summary>
    public bool SourceShapeMoved { get; set; }

    /// <summary>Where the mouse pressed and where it let go, in plain words, for a failure that can be read.</summary>
    public string Account { get; set; } = "";
}

/// <summary>
/// What became of the one unrelated gesture armed by
/// <see cref="DrawioEditorPage.ArmAnUnrelatedGestureAtTheNextBirthAsync"/>.
/// </summary>
/// <remarks>
/// The two flags are deliberately separate: <see cref="Fired"/> says the window really opened - a
/// cell was born and the move was scheduled - and <see cref="Done"/> says the move really went
/// through. A test that only looked at the first would go green on a <c>moveCells</c> that threw,
/// with nothing between the birth and the label at all.
/// </remarks>
public sealed class DrawioUnrelatedGesture
{
    /// <summary>The birth was seen and the move was scheduled.</summary>
    public bool Fired { get; set; }

    /// <summary>The move went through: the cell really did change place.</summary>
    public bool Done { get; set; }

    /// <summary>What the move said if it could not happen, empty when nothing went wrong.</summary>
    public string Error { get; set; } = "";
}

/// <summary>What the browser hits at the centre of one of the import confirmation's buttons (<see cref="DrawioEditorPage.WhatTheImportButtonsHitAsync"/>).</summary>
public sealed class DrawioImportButtonHit
{
    public string Button { get; set; } = "";

    /// <summary>Whether the element hit is inside the confirmation dialog: a click there reaches the dialog.</summary>
    public bool InTheDialog { get; set; }

    public string Hit { get; set; } = "";

    /// <summary>Whether the point also lies inside the other window asked about (open).</summary>
    public bool UnderTheOtherWindow { get; set; }

    public override string ToString() =>
        $"{Button}: hit {Hit} ({(InTheDialog ? "inside" : "OUTSIDE")} the dialog; {(UnderTheOtherWindow ? "under" : "not under")} the other window)";
}

/// <summary>The properties panel's row of actions as the browser lays it out (<see cref="DrawioEditorPage.PanelActionsAsync"/>).</summary>
public sealed class DrawioActionsRow
{
    public bool Found { get; set; }
    public double Top { get; set; }
    public double Bottom { get; set; }

    /// <summary>The buttons shown in the row, in the page's order.</summary>
    public DrawioButtonBox[] Buttons { get; set; } = [];

    /// <summary>How many lines the buttons take: tops more than a pixel apart are two lines.</summary>
    public int Lines
    {
        get
        {
            var lines = 0;
            var last = double.NegativeInfinity;
            foreach (var top in Buttons.Select(b => b.Top).Order())
            {
                if (top - last > 1) lines++;
                last = top;
            }

            return lines;
        }
    }

    public override string ToString() => !Found
        ? "no row of actions"
        : FormattableString.Invariant($"the row from {Top:0.##} to {Bottom:0.##}, {Lines} line(s): ") +
          string.Join("; ", Buttons.Select(b => b.ToString()));
}

/// <summary>Where one button is on the screen.</summary>
public sealed class DrawioButtonBox
{
    public string Id { get; set; } = "";
    public double Top { get; set; }
    public double Bottom { get; set; }
    public double Left { get; set; }
    public double Right { get; set; }

    public override string ToString() =>
        FormattableString.Invariant($"{Id} at x {Left:0.##}-{Right:0.##}, y {Top:0.##}-{Bottom:0.##}");
}

/// <summary>How the browser paints a button at rest, under the mouse and pressed (<see cref="DrawioEditorPage.ButtonLookAsync"/>), as computed colours.</summary>
public sealed class DrawioButtonLook
{
    public string Selector { get; set; } = "";

    /// <summary>The computed <c>border-top-left-radius</c>, "6px" for instance.</summary>
    public string Corners { get; set; } = "";

    /// <summary>The computed <c>border-top-color</c> at rest.</summary>
    public string Border { get; set; } = "";

    /// <summary>The computed <c>font-weight</c> at rest: "400" for the normal weight, "600" for the coloured buttons'.</summary>
    public string Weight { get; set; } = "";

    public string Rest { get; set; } = "";
    public string RestText { get; set; } = "";
    public string Hover { get; set; } = "";
    public string Pressed { get; set; } = "";
    public string PressedText { get; set; } = "";

    public override string ToString() =>
        $"{Selector}: corners {Corners}; border {Border}; text of weight {Weight}; at rest {Rest} with text {RestText}; under the mouse {Hover}; pressed {Pressed} with text {PressedText}";
}

/// <summary>A window's z-index as drawio-windows.js wrote it inline and as the browser computes it.</summary>
public sealed class DrawioWindowStacking
{
    public string Inline { get; set; } = "";
    public string Computed { get; set; } = "";

    public override string ToString() => $"inline {Inline}, computed {Computed}";
}

/// <summary>
/// The strip of section buttons and the floating windows, read in one go once the strip has settled
/// (<see cref="DrawioEditorPage.TheStripAndItsWindowsAsync"/>).
/// </summary>
public sealed class DrawioStripAndWindows
{
    /// <summary>The strip's bottom edge on the screen, in px from the top of the viewport.</summary>
    public double StripBottom { get; set; }

    /// <summary>The strip's height: it does not change when what grows is the row of outcomes under it.</summary>
    public double StripHeight { get; set; }

    /// <summary>
    /// The top of <c>.drawio-body</c> on the screen: the floor of the windows - no window may go above
    /// it, so that none covers the bar (the Rulers switch is in it since the passata grafica) or the
    /// lines under it (the engine's refusal, the row of outcomes).
    /// </summary>
    public double Floor { get; set; }

    public DrawioStripButtonHit Rulers { get; set; } = new();
    public DrawioWindowPlaceOnScreen[] Windows { get; set; } = [];
    public DrawioStripButtonHit[] Buttons { get; set; } = [];

    public override string ToString() =>
        FormattableString.Invariant($"strip bottom at {StripBottom:0.##} ({StripHeight:0.##} high), floor at {Floor:0.##}; ") +
        $"windows: {string.Join("; ", Windows.Select(w => w.ToString()))}; buttons: {string.Join("; ", Buttons.Select(b => b.ToString()))}; Rulers: {Rulers}";
}

/// <summary>Where a floating window is on the screen, and whether it is open.</summary>
public sealed class DrawioWindowPlaceOnScreen
{
    public string Id { get; set; } = "";
    public bool Open { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public override string ToString() =>
        FormattableString.Invariant($"{Id} {(Open ? "open" : "shut")} at {Left:0.##},{Top:0.##} {Width:0.##}x{Height:0.##}");
}

/// <summary>What the browser hits at the middle of a button of the strip (or of the Rulers switch), and where that middle is.</summary>
public sealed class DrawioStripButtonHit
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public bool HitsIt { get; set; }
    public string Hit { get; set; } = "";

    public override string ToString() => $"{Id}: {(HitsIt ? "reached" : "COVERED by " + Hit)}";
}

/// <summary>
/// How the browser shows the name of a button of the bar (<see cref="DrawioEditorPage.NameOnScreenAsync"/>):
/// its word as laid out, and the text of the tip generated under it - each null when it is not shown.
/// </summary>
public sealed class DrawioBarButtonName
{
    public string Id { get; set; } = "";
    public bool Found { get; set; }

    /// <summary>The button's word (<c>.drawio-toggle-label</c>) as laid out, or null when it is not.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// The computed <c>content</c> of the button's <c>::after</c> when the browser draws it, as the
    /// browser writes it (a CSS string, in quotes), or null when it draws none.
    /// </summary>
    public string? Generated { get; set; }

    /// <summary><see cref="Generated"/> without the quotes of a CSS string, or null.</summary>
    public string? GeneratedText =>
        Generated is { Length: >= 2 } g && g[0] == '"' && g[^1] == '"' ? JsonSerializer.Deserialize<string>(g) : Generated;

    public override string ToString() =>
        !Found ? $"{Id}: not on the page" : $"{Id}: word {(Label is null ? "not shown" : $"\"{Label}\"")}, tip {Generated ?? "none"}";
}

/// <summary>
/// One moment of the bar with something on its way in it, as
/// <see cref="DrawioEditorPage.WatchTheBarWhileSomethingIsOnItsWayAsync"/> wrote it down.
/// </summary>
public sealed class DrawioBarMoment
{
    /// <summary>The engine's pill at that moment ("Engine: Stop", "Engine: Run").</summary>
    public string Engine { get; set; } = "";

    /// <summary>The engine's pending line, or null when there was none.</summary>
    public string? Pending { get; set; }

    /// <summary>Whether the plant checks said that the configuration check was running.</summary>
    public bool CheckRunning { get; set; }

    public double Height { get; set; }

    /// <summary>The width the bar's children would take on one line at that moment, gaps and padding included.</summary>
    public double Needed { get; set; }

    public override string ToString() =>
        FormattableString.Invariant($"[{Engine}; {(Pending is null ? "nothing pending" : $"\"{Pending}\"")}; {(CheckRunning ? "the configuration check running" : "no check running")}; bar {Height:0.##} px high, {Needed:0.#} px needed on one line]");
}

/// <summary>How the browser lays out a line of text of the page, against the bar (<see cref="DrawioEditorPage.TextLineAsync"/>).</summary>
public sealed class DrawioTextLine
{
    public string Selector { get; set; } = "";
    public bool Found { get; set; }

    /// <summary><c>checkVisibility()</c>: its display, and every ancestor's.</summary>
    public bool Shown { get; set; }

    /// <summary>Whether it is inside the bar (<c>.drawio-sections</c>).</summary>
    public bool InTheBar { get; set; }

    public double Top { get; set; }
    public double Left { get; set; }
    public double Right { get; set; }
    public double BarBottom { get; set; }
    public double ViewportWidth { get; set; }

    /// <summary>How far its text runs past its box to the right (<c>scrollWidth - clientWidth</c>): 0 when the box holds it.</summary>
    public double OverflowX { get; set; }

    /// <summary>How far its text runs past its box downwards (<c>scrollHeight - clientHeight</c>): 0 when the box holds it.</summary>
    public double OverflowY { get; set; }

    /// <summary>Whether the point near its start hits it: nothing covers it there.</summary>
    public bool Reached { get; set; }

    public override string ToString() => !Found
        ? $"{Selector}: not on the page"
        : FormattableString.Invariant(
            $"{Selector}: {(Shown ? "shown" : "NOT shown")}, {(InTheBar ? "inside the bar" : "outside the bar")}, top {Top:0.##} (the bar ends at {BarBottom:0.##}), x {Left:0.##}-{Right:0.##} of {ViewportWidth:0.##}, text past its box by {OverflowX:0.##} px across and {OverflowY:0.##} px down, {(Reached ? "reached" : "COVERED")}");
}

/// <summary>What the browser hits at one point of the page (<see cref="DrawioEditorPage.WhatIsHitAtAsync"/>).</summary>
public sealed class DrawioPointHit
{
    /// <summary>The hit is draw.io's iframe itself: what the page shows there is the drawing.</summary>
    public bool TheFrame { get; set; }

    /// <summary>The hit is inside one of the page's floating windows.</summary>
    public bool InAWindow { get; set; }

    /// <summary>The id of the page's floating window the hit is inside; empty when it is in none.</summary>
    public string Window { get; set; } = "";

    /// <summary>
    /// The hit is draw.io's iframe, and inside it the drawing's area (mxGraph's container), not
    /// draw.io's toolbar or one of its panels: there the drawing can be seen.
    /// </summary>
    public bool OnTheCanvas { get; set; }

    public string Hit { get; set; } = "";

    public override string ToString() =>
        $"{Hit}{(TheFrame ? " (draw.io's frame" + (OnTheCanvas ? ", the drawing's area)" : ", NOT the drawing's area)") : "")}{(InAWindow ? $" (in the window #{Window})" : "")}";
}

/// <summary>Where the keyboard focus is right after the bridge has handled an answer of draw.io's (<see cref="DrawioEditorPage.ReleaseDrawiosAnswerAsync"/>).</summary>
public sealed class DrawioFocusAfterTheAnswer
{
    /// <summary>The focus is on draw.io's iframe: inside draw.io.</summary>
    public bool OnTheFrame { get; set; }

    /// <summary>The id of the page's floating window the focus is in; empty when it is in none.</summary>
    public string Window { get; set; } = "";

    public string Focused { get; set; } = "";

    public override string ToString() => Focused + (Window.Length > 0 ? $" (in the window #{Window})" : "");
}

/// <summary>
/// A window shown again by the windows module (<see cref="DrawioEditorPage.ShowTheWindowAgainAsync"/>):
/// its left edge afterwards, and what has the keyboard focus.
/// </summary>
public sealed class DrawioShownAgain
{
    public double Left { get; set; }

    public string FocusedId { get; set; } = "";

    public string Focused { get; set; } = "";

    public override string ToString() =>
        FormattableString.Invariant($"the window's left edge at {Left:0.##}, the focus on {Focused}");
}

/// <summary>An operational window's measured cards, controls and scrolling regions.</summary>
public sealed class DrawioOperationalWindowLayout
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool ToolsInside { get; set; }
    public bool PagerInside { get; set; }
    public bool ControlsFitHorizontally { get; set; }
    public string ContentOverflowY { get; set; } = "";
    public bool ContentScrolls { get; set; }
    public double GridHeight { get; set; }
    public string GridOverflowX { get; set; } = "";
    public string GridOverflowY { get; set; } = "";
    public bool HorizontalOverflow { get; set; }
    public string HeaderPosition { get; set; } = "";
    public int CardColumns { get; set; }
    public string[] Columns { get; set; } = [];
}

/// <summary>The mission window's measured grid, pager and toolbar layout.</summary>
public sealed class DrawioMissionLayout
{
    public int VisibleColumns { get; set; }
    public string GridOverflowY { get; set; } = "";
    public string HeaderPosition { get; set; } = "";
    public bool PagerInside { get; set; }
    public bool HorizontalOverflow { get; set; }
    public bool ToolsInside { get; set; }
}

/// <summary>A paged Devices or Junctions grid measured by a real browser.</summary>
public sealed class DrawioDataGridLayout
{
    public int VisibleRows { get; set; }
    public string GridOverflowY { get; set; } = "";
    public string HeaderPosition { get; set; } = "";
    public bool PagerInside { get; set; }
    public bool HorizontalOverflow { get; set; }
    public bool ToolsInside { get; set; }
}

/// <summary>
/// The page's windows opened again from the memory, with the operator's focus coming in between
/// (<see cref="DrawioEditorPage.ReopenTheOpenWindowsAsync"/>): the memory the reopening went by, the
/// windows show() left outside the browser, what has the focus, and at the middle of the element
/// given the focus, whether the browser hits that element, what it hits, and which other windows'
/// boxes hold that point.
/// </summary>
public sealed class DrawioReopening
{
    public string[] Order { get; set; } = [];

    public string[] Outside { get; set; } = [];

    public string FocusedId { get; set; } = "";

    public string Focused { get; set; } = "";

    public bool FieldOnTop { get; set; }

    public string Hit { get; set; } = "";

    public string[] WindowsOverTheField { get; set; } = [];

    public override string ToString() =>
        $"opened again by the memory [{string.Join(", ", Order)}], left outside [{string.Join(", ", Outside)}], the focus on {Focused}; "
        + $"at the middle of the field given the focus the browser hits {Hit}{(FieldOnTop ? " (the field)" : "")}, and the boxes of [{string.Join(", ", WindowsOverTheField)}] hold that point";
}

/// <summary>
/// A spot of the page where draw.io's frame shows, uncovered by the page's windows - on its drawing
/// (<see cref="DrawioEditorPage.ClickTheDrawingWhereNothingCoversItAsync"/>) or its shapes panel
/// (<see cref="DrawioEditorPage.ClickTheShapesPanelWhereNothingCoversItAsync"/>).
/// </summary>
public sealed class DrawioFrameSpot
{
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>What draw.io has there, inside its frame.</summary>
    public string What { get; set; } = "";

    public override string ToString() => FormattableString.Invariant($"{X:0},{Y:0} ({What} inside draw.io's frame)");
}

/// <summary>A spot of the text of a "?"'s open note, in the page's coordinates (<see cref="DrawioEditorPage.ClickTheNoteOfAsync"/>).</summary>
public sealed class DrawioNoteSpot
{
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>What the browser hits there, described.</summary>
    public string What { get; set; } = "";

    public override string ToString() => FormattableString.Invariant($"{X:0},{Y:0} ({What})");
}

/// <summary>The bar and the children it shows, as the browser lays them out once it has stopped moving (<see cref="DrawioEditorPage.BarLineAsync"/>).</summary>
public sealed class DrawioBarLine
{
    public double Top { get; set; }
    public double Bottom { get; set; }
    public double ViewportWidth { get; set; }
    public DrawioBarChild[] Children { get; set; } = [];

    public override string ToString() =>
        FormattableString.Invariant($"the bar at {Top:0.##}-{Bottom:0.##} in a window {ViewportWidth:0} px wide: ") +
        string.Join("; ", Children.Select(c => c.ToString()));
}

/// <summary>One child of the bar the browser shows, with its box.</summary>
public sealed class DrawioBarChild
{
    /// <summary>Its id, or its first class, or its tag: what names it in the bar.</summary>
    public string Key { get; set; } = "";

    public string What { get; set; } = "";
    public double Top { get; set; }
    public double Bottom { get; set; }
    public double Left { get; set; }
    public double Right { get; set; }

    public override string ToString() =>
        FormattableString.Invariant($"{What} at x {Left:0.##}-{Right:0.##}, y {Top:0.##}-{Bottom:0.##}");
}

/// <summary>How the bar names its buttons that open a window, and how it lays out their words (<see cref="DrawioEditorPage.BarNamesAsync"/>).</summary>
public sealed class DrawioBarNames
{
    public DrawioBarToggle[] Toggles { get; set; } = [];
    public DrawioBarLabel[] Labels { get; set; } = [];

    public override string ToString() =>
        $"buttons: {string.Join("; ", Toggles.Select(t => t.ToString()))}; words: {string.Join("; ", Labels.Select(l => l.ToString()))}";
}

/// <summary>
/// A button of the bar that opens a window, with the name a screen reader says (<c>aria-label</c>, null
/// when it has none), and its icon: whether it is seen, and how wide it is laid out.
/// </summary>
public sealed class DrawioBarToggle
{
    public string Id { get; set; } = "";
    public string? AriaLabel { get; set; }
    public bool IconSeen { get; set; }
    public double IconWidth { get; set; }

    public override string ToString() =>
        FormattableString.Invariant($"{Id} named {(AriaLabel is null ? "(no aria-label)" : $"\"{AriaLabel}\"")}, its icon {(IconSeen ? "seen" : "NOT seen")}, {IconWidth:0.##} px wide");
}

/// <summary>The word of a button of the bar (<c>.drawio-toggle-label</c>) with its computed display and its laid-out width.</summary>
public sealed class DrawioBarLabel
{
    /// <summary>The id of the button the word is in.</summary>
    public string Owner { get; set; } = "";

    public string Display { get; set; } = "";

    /// <summary>How wide the browser lays the word out, in px: 0 when it takes no room.</summary>
    public double Width { get; set; }

    /// <summary>Whether it is seen: laid out, not transparent and not hidden (<c>checkVisibility</c> with the opacity and the visibility).</summary>
    public bool Seen { get; set; }

    public override string ToString() => FormattableString.Invariant($"the word of {Owner}: display {Display}, {Width:0.##} px wide, {(Seen ? "seen" : "NOT seen")}");
}

/// <summary>Where the open note of a "?" is on screen (<see cref="DrawioEditorPage.NoteOnScreenAsync"/>).</summary>
public sealed class DrawioNoteOnScreen
{
    public string Id { get; set; } = "";
    public bool Shown { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }
    public double ViewportWidth { get; set; }
    public double ViewportHeight { get; set; }

    /// <summary>The browser hits the note near the start of its first line: nothing cuts it or covers it there.</summary>
    public bool StartReached { get; set; }

    /// <summary>The browser hits the note near the end of its first line.</summary>
    public bool EndReached { get; set; }

    /// <summary>The whole of the note's box is inside the browser's window.</summary>
    public bool Inside => Shown && Left >= 0 && Top >= 0 && Right <= ViewportWidth && Bottom <= ViewportHeight;

    public override string ToString() => !Shown
        ? $"the note of #{Id}: not on screen"
        : FormattableString.Invariant(
            $"the note of #{Id} at x {Left:0.##}-{Right:0.##}, y {Top:0.##}-{Bottom:0.##} in a window of {ViewportWidth:0}x{ViewportHeight:0}, its start {(StartReached ? "reached" : "NOT reached")}, its end {(EndReached ? "reached" : "NOT reached")}");
}

/// <summary>A "?" of the page and its note as the browser has them (<see cref="DrawioEditorPage.HelpStateAsync"/>).</summary>
public sealed class DrawioHelpState
{
    public string Id { get; set; } = "";

    /// <summary>Both the "?" and its note (<c>{id}-body</c>) are in the page.</summary>
    public bool Found { get; set; }

    /// <summary>What the "?" says of its note (<c>aria-expanded</c>): "true", "false", or null when there is no "?".</summary>
    public string? Expanded { get; set; }

    /// <summary>The note is laid out on screen (<c>checkVisibility</c>).</summary>
    public bool NoteShown { get; set; }

    /// <summary>What has the keyboard focus, described.</summary>
    public string Focus { get; set; } = "";

    public override string ToString() => !Found
        ? $"#{Id}: no \"?\" or no note in the page (the focus on {Focus})"
        : $"#{Id}: aria-expanded {Expanded}, the note {(NoteShown ? "on screen" : "not on screen")}, the focus on {Focus}";
}

/// <summary>A custom WCS command icon as draw.io rendered and decoded it in its popup menu.</summary>
public sealed class DrawioContextMenuIcon
{
    public string Source { get; set; } = "";
    public string? Alt { get; set; }
    public string? AriaHidden { get; set; }
    public string? Role { get; set; }
    public bool Complete { get; set; }
    public int NaturalWidth { get; set; }
    public int NaturalHeight { get; set; }
}
