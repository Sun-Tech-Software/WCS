using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Un host del WCS avviato come <b>processo vero</b>, con il suo Kestrel su una porta libera, per
/// il tempo di una classe di prove.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perche' non <c>WebApplicationFactory</c>.</b> L'host di prova delle altre suite gira in
/// memoria, senza nessuna porta TCP: un browser non ha niente a cui collegarsi. Qui serve il
/// processo, e serve costruito da qualcun altro - il percorso arriva dalla variabile d'ambiente
/// <see cref="ExecutableVariable"/>. Non c'e' nessun <c>ProjectReference</c> a <c>Wcs.Host</c> e
/// non si lancia <c>dotnet run</c>: entrambi ricostruirebbero l'host, che sulla macchina di chi
/// sviluppa e' spesso in esecuzione, e la copia in uso non si lascia sovrascrivere.
/// </para>
/// <para>
/// <b>Perche' il motore acceso.</b> Con <c>Wcs:StartEngine</c> falso le rotte dell'impianto
/// rispondono 503 (lo decide il filtro del sottogruppo "engine" in <c>Program.cs</c>) e la pagina
/// non avrebbe niente da mostrare; e <c>Wcs:DatabaseDirectory</c> viene assegnato dentro
/// <c>EngineHostedService</c>, quindi a motore spento l'host scriverebbe accanto al proprio
/// eseguibile invece che nella cartella temporanea di questa prova. <c>Wcs:AutoRun</c> resta falso
/// perche' l'Editor scrive solo con il motore fermo.
/// </para>
/// <para>
/// <b>Perche' nessun gestionale.</b> L'host pretende che <c>Wms:ApiAddress</c> ci sia e sia un
/// http assoluto, non che risponda (<c>EngineHostedService.CreateWarehouse</c>): un indirizzo
/// formalmente valido e irraggiungibile basta, e la prova resta autonoma.
/// </para>
/// </remarks>
public sealed class WcsHostProcess : IAsyncDisposable
{
    /// <summary>La variabile d'ambiente che porta il percorso dell'eseguibile gia' costruito.</summary>
    public const string ExecutableVariable = "WCS_HOST_EXE";

    /// <summary>La riga che Kestrel stampa quando ha preso la porta ED e' pronto a servire.</summary>
    private const string ListeningPrefix = "Now listening on: ";

    /// <summary>
    /// Quante righe del registro dell'host si tengono per raccontare un avvio andato male. Non
    /// serve tutto: l'eccezione che impedisce l'avvio e' sempre in coda.
    /// </summary>
    private const int LogTailLines = 60;

    /// <summary>
    /// Il tetto all'attesa dell'avvio. <b>Non e' un'attesa a tempo</b>: l'attesa vera e' la riga
    /// "<c>Now listening on:</c>" sullo stdout, e la fine del processo la interrompe subito. Questo
    /// e' solo il limite oltre il quale un host che non stampa ne' vive ne' muore diventa un
    /// fallimento che si legge, invece di una prova appesa per sempre.
    /// </summary>
    private static readonly TimeSpan StartCeiling = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Quante porte si provano prima di arrendersi. Fra il momento in cui <see cref="FreePort"/>
    /// lascia una porta e quello in cui l'host la apre c'e' una finestra in cui qualcun altro puo'
    /// prenderla: e' una corsa vera, e si chiude riprovando su un'altra porta invece di far
    /// fallire una prova che non c'entra niente. Poche, pero': se capita tre volte di fila non e'
    /// piu' una corsa, ed e' giusto che si veda.
    /// </summary>
    private const int PortAttempts = 3;

    /// <summary>
    /// Come Kestrel dice che la porta e' gia' di qualcun altro. E' testo suo, non una diagnosi
    /// nostra: si riconosce quella riga e basta, cosi' un host che non parte per un motivo SUO non
    /// viene riavviato tre volte raccontando una corsa che non c'e' stata.
    /// </summary>
    private const string PortTakenMarker = "address already in use";

    private readonly Process _process;
    private readonly string _dataDirectory;
    private readonly ConcurrentQueue<string> _log;

    private WcsHostProcess(Process process, string dataDirectory, ConcurrentQueue<string> log, Uri baseAddress, string apiKey, string declaration)
    {
        _process = process;
        _dataDirectory = dataDirectory;
        _log = log;
        BaseAddress = baseAddress;
        ApiKey = apiKey;
        Declaration = declaration;
    }

    /// <summary>L'origine su cui questo host ascolta davvero, letta dal suo stdout - non quella che gli era stata chiesta.</summary>
    public Uri BaseAddress { get; }

    /// <summary>La chiave dell'API di questo host, generata al momento e mai scritta in un file del repository.</summary>
    public string ApiKey { get; }

    /// <summary>
    /// Su quale binario questa prova sta girando, con le sue date: va nel verbale <b>anche quando
    /// la prova passa</b>. Si veda <see cref="ProductBinary"/> per il perche'.
    /// </summary>
    public string Declaration { get; }

    /// <summary>
    /// The last lines the host wrote (stdout and stderr), for a failure message: a page whose circuit
    /// dies tells the browser only that it died, and the reason is on the server's side.
    /// </summary>
    public string LogTail() => Tail(_log);

    /// <summary>
    /// Avvia l'host e torna quando ha dichiarato di ascoltare. Solleva - nominando la variabile e
    /// il perche' - se l'eseguibile non e' stato indicato, se non esiste, se e' piu' vecchio dei
    /// sorgenti dell'Editor web (<see cref="ProductBinary"/>), o se il processo muore prima di
    /// annunciarsi (in quel caso il messaggio porta le ultime righe del suo registro, non un tempo
    /// scaduto e muto).
    /// </summary>
    /// <param name="warehouseAddress">
    /// The address to give <c>Wms:ApiAddress</c> in place of the unreachable one it uses by default:
    /// it is for the one test class that needs <c>GET /wms/cells</c> to ANSWER (see
    /// <see cref="StubWarehouse"/>). Null - the case of every other one - leaves the address that
    /// does not answer, and the host stays as self-contained as before.
    /// </param>
    public static async Task<WcsHostProcess> StartAsync(Uri? warehouseAddress = null)
    {
        var executable = Environment.GetEnvironmentVariable(ExecutableVariable);
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException(
                $"La variabile d'ambiente {ExecutableVariable} non e' impostata: queste prove guidano un browser vero " +
                "contro un host vero, e l'host dev'essere un eseguibile GIA' costruito. Si imposta al percorso di " +
                "Wcs.Host.exe (per esempio ...\\Wcs.Host\\src\\bin\\Debug\\net10.0\\Wcs.Host.exe). Non lo si ricostruisce " +
                "da qui apposta: sulla macchina di chi sviluppa l'host e' spesso in esecuzione e i suoi file sono in uso.");

        if (!File.Exists(executable))
            throw new InvalidOperationException(
                $"{ExecutableVariable} indica \"{executable}\", che non esiste. Va costruito prima (dotnet build in Debug " +
                "su WCS/Wcs.Host/src) e la variabile deve puntare all'eseguibile, non alla cartella.");

        // Il controllo che manca a MSBuild: qui non c'e' nessun ProjectReference verso l'Editor web,
        // quindi niente lega queste prove ai suoi sorgenti e un binario vecchio passerebbe per
        // buono. MISURATO, si veda ProductBinary.
        var binary = ProductBinary.Inspect(executable);
        binary.RefuseIfStale();

        PortTakenException? conflict = null;
        for (var attempt = 1; attempt <= PortAttempts; attempt++)
        {
            try
            {
                return await StartOnAFreePortAsync(executable, binary.Declaration, warehouseAddress);
            }
            catch (PortTakenException ex)
            {
                conflict = ex;
            }
        }

        throw new InvalidOperationException(
            $"L'host non e' riuscito ad aprire nessuna delle {PortAttempts} porte provate, e ognuna era libera un " +
            "istante prima di passargliela. Non e' piu' la corsa che il ritentativo copre: qualcosa sta prendendo le " +
            $"porte a mano a mano che si liberano.{Environment.NewLine}{conflict!.Message}", conflict);
    }

    /// <summary>
    /// Un tentativo di avvio su una porta scelta libera adesso. Solleva
    /// <see cref="PortTakenException"/> - e solo quella - quando l'host esce perche' la porta era
    /// gia' di qualcun altro: e' il solo guasto che valga la pena riprovare.
    /// </summary>
    private static async Task<WcsHostProcess> StartOnAFreePortAsync(string executable, string declaration, Uri? warehouseAddress)
    {
        // Ogni avvio ha la sua cartella e la sua chiave: due esecuzioni in parallelo non si toccano,
        // e nessuna tocca il database dell'host che sta usando qualcun altro.
        var dataDirectory = Path.Combine(Path.GetTempPath(), "wcs-browser-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var apiKey = "browser-test-" + Guid.NewGuid().ToString("N");

        var start = new ProcessStartInfo(executable)
        {
            // La cartella di lavoro e' quella dell'eseguibile perche' e' li' che stanno il suo
            // appsettings.json e le sue risorse statiche.
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Development, e non e' un dettaglio: MISURATO in un browser vero. Le risorse statiche
        // della libreria di componenti (il foglio di stile, drawio-bridge.js, draw.io intero,
        // blazor.web.js) NON stanno accanto all'eseguibile di una compilazione - il manifesto
        // .staticwebassets.runtime.json le fa puntare alle cartelle dei progetti - e ASP.NET Core
        // carica quel manifesto SOLO in ambiente di sviluppo. In Production l'host risponde 200
        // con un corpo VUOTO e senza tipo a ogni /_content e /_framework: la pagina arriva, il
        // circuito Blazor non parte mai, e non lo dice nessuno. E' la stessa famiglia della voce
        // D200 del registro, un piano piu' in basso. Un host PUBBLICATO (dotnet publish) non ha
        // questo problema, perche' le risorse gli finiscono accanto: qui si guida una
        // compilazione, quindi serve questo.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Wcs__ApiKey"] = apiKey;
        start.Environment["Wcs__DatabaseDirectory"] = dataDirectory;
        start.Environment["Wcs__StartEngine"] = "true";
        start.Environment["Wcs__AutoRun"] = "false";
        // By default an address that is formally valid and unreachable: the host insists there be
        // one, not that it answer. A single test class passes one that really answers, because the
        // cells panel has nothing to show without the warehouse.
        start.Environment["Wms__ApiAddress"] = warehouseAddress?.ToString() ?? "http://127.0.0.1:1";
        start.Environment["Wms__ApiKey"] = "browser-test-no-wms";
        // La porta la sceglie il sistema, non una costante in questo file: una porta fissa e' una
        // prova che fallisce quando qualcun altro ha gia' quella porta.
        // NON si scrive pero' "porta 0" nella configurazione, e il motivo e' misurato, non
        // teorico: l'Editor web ricava l'indirizzo con cui chiama l'host DA QUESTA STESSA VOCE
        // (WebEditorExtensions.ResolveHostAddress legge Kestrel:Endpoints:Http:Url), quindi con
        // uno 0 la pagina finirebbe per chiamare "http://127.0.0.1:0", che non e' nessuno. Kestrel
        // partirebbe lo stesso, su una porta vera, e solo la pagina resterebbe muta. Si prende
        // quindi una porta libera qui e gliela si dice per nome.
        var port = FreePort();
        start.Environment["Kestrel__Endpoints__Http__Url"] = $"http://127.0.0.1:{port}";
        // La riga dell'annuncio arriva da Microsoft.Hosting.Lifetime: si chiede esplicitamente il
        // suo livello, cosi' un appsettings.json che alzasse il livello predefinito non lascerebbe
        // questa prova ad aspettare una riga che non verra' mai stampata.
        start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        // NOT set here, and on purpose: why SignalR closed a connection - a message from the browser
        // over the circuit's ceiling, for one - is written by Microsoft.AspNetCore.SignalR.
        // HubConnectionHandler at Debug, and the host's own appsettings.json turns that on since queue
        // item 13 (owner's decision of 28/9). This host reads the same file, so the failure messages
        // that carry LogTail() show the reason as the installed host would log it.

        var log = new ConcurrentQueue<string>();
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };

        void Record(string? line)
        {
            if (line is null) return;
            log.Enqueue(line);
            while (log.Count > LogTailLines) log.TryDequeue(out _);

            var at = line.IndexOf(ListeningPrefix, StringComparison.Ordinal);
            if (at < 0) return;
            var url = line[(at + ListeningPrefix.Length)..].Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out var address)) listening.TrySetResult(address);
        }

        process.OutputDataReceived += (_, e) => Record(e.Data);
        process.ErrorDataReceived += (_, e) => Record(e.Data);
        process.Exited += (_, _) =>
        {
            // WaitForExit() senza tempo, a processo gia' uscito, non aspetta il processo: aspetta
            // che i due lettori asincroni abbiano finito di consegnare. Senza, il registro qui
            // sotto puo' essere ancora a meta' - e la riga che dice PERCHE' l'host e' morto e'
            // proprio l'ultima.
            process.WaitForExit();
            var tail = Tail(log);
            var why =
                $"L'host ({executable}) e' uscito con codice {process.ExitCode} prima di dichiarare su quale porta " +
                $"ascolta. Le sue ultime righe:{Environment.NewLine}{tail}";
            listening.TrySetException(LooksLikePortConflict(tail)
                ? new PortTakenException(why)
                : new InvalidOperationException(why));
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            var address = await listening.Task.WaitAsync(StartCeiling);
            if (address.Port != port)
                throw new InvalidOperationException(
                    $"L'host dice di ascoltare sulla porta {address.Port}, ma la configurazione gli chiedeva la {port}. " +
                    "L'Editor web ricava da quella configurazione l'indirizzo con cui chiama l'host, quindi da qui in " +
                    "avanti la pagina parlerebbe con un'altra porta: la prova si ferma invece di provare un giro " +
                    "che non e' quello vero.");
            return new WcsHostProcess(process, dataDirectory, log, address, apiKey, declaration);
        }
        catch (TimeoutException)
        {
            await new WcsHostProcess(process, dataDirectory, log, new Uri("http://127.0.0.1"), apiKey, declaration).DisposeAsync();
            throw new InvalidOperationException(
                $"L'host ({executable}) non ha dichiarato la propria porta entro {StartCeiling.TotalSeconds:0} secondi e " +
                $"non e' nemmeno uscito. Le sue ultime righe:{Environment.NewLine}{Tail(log)}");
        }
        catch
        {
            // L'host e' uscito da solo (l'eccezione viene dal gestore di Exited, che ha gia' messo
            // nel messaggio le righe del suo registro) oppure ascolta su una porta che non e'
            // quella chiesta: in tutti e due i casi resta solo da fermarlo e portare via la
            // cartella, senza riscrivere un messaggio che c'e' gia'.
            await new WcsHostProcess(process, dataDirectory, log, new Uri("http://127.0.0.1"), apiKey, declaration).DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Se queste righe dell'host dicono che la porta era gia' occupata. Si riconosce il testo di
    /// Kestrel, e nient'altro: leggere ogni avvio fallito come una corsa sulla porta lo farebbe
    /// riprovare tre volte e poi racconterebbe la corsa al posto del guasto vero.
    /// </summary>
    internal static bool LooksLikePortConflict(string log) =>
        log.Contains(PortTakenMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// L'host e' uscito perche' la porta era gia' di qualcun altro. Porta gia' il messaggio per
    /// intero: se e' l'ultimo tentativo esce cosi' com'e', senza che nessuno lo riscriva.
    /// </summary>
    private sealed class PortTakenException(string message) : InvalidOperationException(message);

    /// <summary>Ferma l'host e porta via la sua cartella temporanea.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // Il processo era gia' andato: niente da fermare.
        }

        _process.Dispose();

        // La cartella e' sotto la temporanea del sistema e porta un Guid nel nome: se SQLite tiene
        // ancora un file aperto per un istante dopo l'uscita, lasciarla li' non sporca niente di
        // condiviso. Non si solleva da un Dispose: mascherebbe il fallimento vero della prova.
        try
        {
            if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Le ultime righe del registro dell'host: quello che si mette in un messaggio di fallimento.</summary>
    private static string Tail(ConcurrentQueue<string> log) => string.Join(Environment.NewLine, log);

    /// <summary>
    /// Una porta libera, presa lasciandola scegliere al sistema e restituendola subito. Fra il
    /// rilascio e l'avvio dell'host resta una finestra in cui qualcun altro potrebbe prenderla: se
    /// capita, l'host esce dicendo che la porta e' occupata, <see cref="StartAsync"/> lo riconosce
    /// (<see cref="LooksLikePortConflict"/>) e riprova su un'altra porta fino a
    /// <see cref="PortAttempts"/> volte. La finestra resta - non si puo' chiudere tenendo la porta,
    /// perche' andrebbe tenuta proprio mentre l'host prova ad aprirla - ma non fa piu' fallire una
    /// prova che non c'entra niente.
    /// </summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>L'indirizzo di una pagina dell'host, dal percorso relativo.</summary>
    public string PageUrl(string relativePath) =>
        new Uri(BaseAddress, relativePath).ToString();
}
