using System.Runtime.CompilerServices;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Il binario del prodotto che queste prove guidano davvero, messo a confronto con i sorgenti da
/// cui dovrebbe essere nato.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perche' esiste, MISURATO.</b> Spegnendo una riga di <c>DrawioDelta.cs</c> - cioe' spegnendo
/// gli spostamenti - e lanciando queste prove <b>senza ricostruire l'host</b>, restavano tutte e
/// due verdi; ricostruito l'host, il giro del trascinamento e' diventato rosso come doveva. La
/// rete e' vera, ma <b>vede il binario, non il sorgente</b>: questo progetto non ha nessun
/// <c>ProjectReference</c> verso l'Editor web - lo guida attraverso un processo gia' costruito -
/// quindi MSBuild non lo lega a quei sorgenti e non ricostruisce niente. Chi tocca il delta o la
/// pagina e lancia solo queste prove vedrebbe verde il codice di prima. E' la famiglia di "una
/// build fallita lascia il binario vecchio", per una via diversa: qui la build non fallisce, non
/// avviene proprio.
/// </para>
/// <para>
/// <b>Due riferimenti, non uno, e MISURATO perche'.</b> Un <c>.cs</c> o un <c>.razor</c> finisce
/// dentro <c>Wcs.WebEditor.dll</c>, quindi la data della libreria dice se e' entrato. Un
/// <c>.js</c> o un <c>.css</c> no: quando cambia solo una risorsa statica <b>la libreria non si
/// muove affatto</b>, e confrontarla con lei dava un rifiuto <b>senza uscita</b> - si ricostruiva
/// l'host esattamente come il messaggio chiedeva e il rifiuto tornava identico, per sempre. Cio'
/// che la ricostruzione rifa' e' il manifesto delle risorse statiche accanto all'eseguibile:
/// toccato <c>wwwroot/drawio-bridge.js</c> alle 12:31:26, la build lo ha riscritto alle 12:31:49
/// mentre <c>Wcs.WebEditor.dll</c> restava alle 12:09:53; stessa misura toccando
/// <c>wwwroot/wcs-webeditor.css</c> e un file del draw.io vendorizzato. E' quello il riferimento
/// giusto per le risorse statiche, ed e' cio' che rende il rifiuto togliibile ricostruendo.
/// </para>
/// <para>
/// <b>Attenzione a come si legge quel "la build lo riscrive".</b> Non lo riscrive <b>ogni</b>
/// build: due build a vuoto di fila lo hanno lasciato fermo allo stesso millesimo (12:29:26.408
/// tutte e due le volte). Lo riscrive quando una risorsa statica e' piu' recente di lui, che e'
/// esattamente il caso in cui questo controllo si lamenta. Le due cose combaciano per
/// costruzione: finche' c'e' un rifiuto da togliere, la ricostruzione non e' a vuoto.
/// </para>
/// <para>
/// <b>Le date, non i contenuti.</b> Non si ricompila e non si confronta nessuna impronta: si
/// guarda quando e' stato scritto l'ultimo sorgente e quando il riferimento che gli tocca. E' un
/// confronto grezzo e puo' chiedere una ricostruzione che sarebbe bastato non fare; il caso
/// contrario - lasciar passare un binario vecchio - costa un verde falso su <b>l'unica</b> rete
/// che veda draw.io.
/// </para>
/// <para>
/// <b>Niente si spegne da solo.</b> Libreria che non c'e', manifesto che non c'e', cartella dei
/// sorgenti che non si trova, nessun sorgente di una delle due famiglie dentro: sono motivi per
/// fermarsi dicendolo, non per saltare il controllo. Un controllo che si spegne da solo e' peggio
/// di nessun controllo, perche' chi legge crede che sia acceso.
/// </para>
/// </remarks>
public sealed class ProductBinary
{
    /// <summary>
    /// La libreria dell'Editor web. Sta accanto all'eseguibile dell'host ed e' <b>lei</b> il
    /// prodotto che queste prove guidano: l'eseguibile dell'host non contiene ne' la pagina ne' il
    /// delta.
    /// </summary>
    public const string LibraryName = "Wcs.WebEditor.dll";

    /// <summary>
    /// Come si chiama il manifesto delle risorse statiche: il nome dell'eseguibile senza
    /// estensione, piu' questo. Si ricava dall'eseguibile invece di essere scritto per intero
    /// perche' e' ASP.NET a chiamarlo cosi', a partire dall'assembly d'ingresso: chi cambiasse
    /// nome all'host troverebbe il controllo ancora acceso invece di un file che non c'e'.
    /// </summary>
    public const string ManifestSuffix = ".staticwebassets.endpoints.json";

    /// <summary>
    /// Che cosa finisce dentro la libreria. Per questi il riferimento e' <see cref="LibraryPath"/>:
    /// se la libreria e' piu' vecchia, la modifica non e' stata compilata.
    /// </summary>
    private static readonly string[] CompiledExtensions = [".cs", ".razor"];

    /// <summary>
    /// Che cosa <b>non</b> finisce dentro la libreria ma e' esattamente cio' che il browser
    /// esegue, cioe' cio' che queste prove provano. Per questi il riferimento e'
    /// <see cref="ManifestPath"/>: la libreria, con loro, non si muove.
    /// </summary>
    private static readonly string[] StaticAssetExtensions = [".js", ".css"];

    /// <summary>
    /// Le cartelle di lavoro di MSBuild sotto i sorgenti. Dentro ci sono file scritti dalla build
    /// stessa (l'assembly info generato, le copie in <c>bin</c>): guardarli vorrebbe dire
    /// confrontare una build con se' stessa, e l'esito dipenderebbe da quale file MSBuild ha
    /// toccato per ultimo.
    /// </summary>
    private static readonly string[] BuildDirectories = ["bin", "obj"];

    /// <summary>Un sorgente e quando e' stato scritto. Esiste solo per non passare in giro coppie sciolte.</summary>
    private sealed record SourceFile(string Path, DateTime WrittenAt);

    private ProductBinary(
        string executablePath, DateTime executableWrittenAt,
        string libraryPath, DateTime libraryWrittenAt,
        string manifestPath, DateTime manifestWrittenAt,
        SourceFile newestCompiled, SourceFile newestStaticAsset)
    {
        ExecutablePath = executablePath;
        ExecutableWrittenAt = executableWrittenAt;
        LibraryPath = libraryPath;
        LibraryWrittenAt = libraryWrittenAt;
        ManifestPath = manifestPath;
        ManifestWrittenAt = manifestWrittenAt;
        NewestCompiledSourcePath = newestCompiled.Path;
        NewestCompiledSourceWrittenAt = newestCompiled.WrittenAt;
        NewestStaticAssetPath = newestStaticAsset.Path;
        NewestStaticAssetWrittenAt = newestStaticAsset.WrittenAt;
    }

    /// <summary>L'eseguibile che verra' avviato.</summary>
    public string ExecutablePath { get; }

    /// <summary>Quando e' stato scritto l'eseguibile.</summary>
    public DateTime ExecutableWrittenAt { get; }

    /// <summary>La libreria dell'Editor web accanto a quell'eseguibile.</summary>
    public string LibraryPath { get; }

    /// <summary>Quando e' stata scritta quella libreria.</summary>
    public DateTime LibraryWrittenAt { get; }

    /// <summary>Il manifesto delle risorse statiche accanto a quell'eseguibile.</summary>
    public string ManifestPath { get; }

    /// <summary>Quando e' stato scritto quel manifesto.</summary>
    public DateTime ManifestWrittenAt { get; }

    /// <summary>Il sorgente compilato dell'Editor web scritto piu' di recente.</summary>
    public string NewestCompiledSourcePath { get; }

    /// <summary>Quando e' stato scritto quel sorgente.</summary>
    public DateTime NewestCompiledSourceWrittenAt { get; }

    /// <summary>La risorsa statica dell'Editor web scritta piu' di recente.</summary>
    public string NewestStaticAssetPath { get; }

    /// <summary>Quando e' stata scritta quella risorsa.</summary>
    public DateTime NewestStaticAssetWrittenAt { get; }

    /// <summary>Vero quando la libreria non porta l'ultima modifica al codice o ai componenti.</summary>
    public bool IsCompiledStale => NewestCompiledSourceWrittenAt > LibraryWrittenAt;

    /// <summary>Vero quando il manifesto non ha ancora visto l'ultima modifica a una risorsa statica.</summary>
    public bool IsStaticAssetStale => NewestStaticAssetWrittenAt > ManifestWrittenAt;

    /// <summary>Vero quando almeno uno dei due riferimenti e' rimasto indietro.</summary>
    public bool IsStale => IsCompiledStale || IsStaticAssetStale;

    /// <summary>
    /// La riga che va nel verbale della prova <b>anche quando la prova passa</b>: una prova che non
    /// dice su quale binario ha girato non e' verificabile da chi la legge, e il verde non dice
    /// niente finche' non si sa di che cosa e' il verde. Ci sono tutti e quattro i pezzi del
    /// confronto - i due riferimenti e i due sorgenti piu' recenti - perche' chi legge possa
    /// rifarlo a mano.
    /// </summary>
    public string Declaration =>
        $"Provato contro l'host \"{ExecutablePath}\" del {Show(ExecutableWrittenAt)}, " +
        $"con {LibraryName} del {Show(LibraryWrittenAt)} " +
        $"e \"{Path.GetFileName(ManifestPath)}\" del {Show(ManifestWrittenAt)}. " +
        $"Sorgente compilato piu' recente: \"{NewestCompiledSourcePath}\" del {Show(NewestCompiledSourceWrittenAt)}. " +
        $"Risorsa statica piu' recente: \"{NewestStaticAssetPath}\" del {Show(NewestStaticAssetWrittenAt)}.";

    /// <summary>
    /// La cartella dei sorgenti dell'Editor web, ricavata dalla posizione di <b>questo file al
    /// momento della compilazione</b>: due cartelle sopra questa, poi <c>src</c>. Non da una
    /// variabile d'ambiente - sarebbe una cosa in piu' da impostare, cioe' da dimenticare - e non
    /// dalla cartella di lavoro, che quando le prove girano e' quella del <c>bin</c>.
    /// </summary>
    /// <remarks>
    /// Il percorso lo chiede <see cref="ThisFile"/>, che sta <b>qui dentro</b>. Se il parametro
    /// <c>[CallerFilePath]</c> stesse su questo metodo, lo riempirebbe il file di <b>chi
    /// chiama</b>: la prova che controlla il percorso vero, chiamandolo dal proprio file,
    /// misurerebbe se stessa. Oggi i due file stanno nella stessa cartella e il risultato
    /// coincide - il giorno in cui uno dei due si sposta, il controllo vero si romperebbe e la
    /// prova resterebbe verde.
    /// </remarks>
    public static string SourceDirectory() =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "src"));

    /// <summary>Guarda l'eseguibile indicato contro i sorgenti veri dell'Editor web.</summary>
    public static ProductBinary Inspect(string executable) => Inspect(executable, SourceDirectory());

    /// <summary>
    /// Guarda un eseguibile contro una cartella di sorgenti qualsiasi. Esiste separata perche' e'
    /// la forma che si puo' mettere sotto prova: una prova che ricalcolasse qui le date da se' non
    /// proverebbe niente.
    /// </summary>
    public static ProductBinary Inspect(string executable, string sourceDirectory)
    {
        var fullExecutable = Path.GetFullPath(executable);
        var directory = Path.GetDirectoryName(fullExecutable);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException(
                $"\"{executable}\" non ha una cartella attorno: {WcsHostProcess.ExecutableVariable} deve portare il " +
                "percorso dell'eseguibile dell'host, non un nome nudo.");

        var libraryPath = Path.Combine(directory, LibraryName);
        if (!File.Exists(libraryPath))
            throw new InvalidOperationException(
                $"Accanto a \"{fullExecutable}\" non c'e' nessun {LibraryName}: quell'host non porta con se' l'Editor " +
                "web, quindi la pagina che queste prove guidano non esisterebbe. Va ricostruito Wcs.Host in Debug e " +
                $"{WcsHostProcess.ExecutableVariable} deve puntare a quella compilazione.");

        var manifestPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(fullExecutable) + ManifestSuffix);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException(
                $"Accanto a \"{fullExecutable}\" non c'e' nessun \"{Path.GetFileName(manifestPath)}\": e' il manifesto " +
                "che dice all'host quali risorse statiche servire e da dove, cioe' quello che porta il JavaScript e il " +
                "foglio di stile dell'Editor fino al browser. Senza di lui non si puo' dire se le risorse statiche " +
                "siano aggiornate - e con ogni probabilita' la pagina non funzionerebbe nemmeno. Va ricostruito " +
                "Wcs.Host in Debug.");

        if (!Directory.Exists(sourceDirectory))
            throw new InvalidOperationException(
                $"I sorgenti dell'Editor web non si trovano in \"{sourceDirectory}\", e senza di loro non si puo' dire " +
                $"se {LibraryName} porti l'ultima modifica. Il percorso si ricava dalla posizione del file di questa " +
                "classe al momento della compilazione, quindi o le prove sono state compilate su un altro albero o " +
                "l'albero e' stato spostato dopo. Si dice, invece di lasciar correre senza controllo.");

        SourceFile? newestCompiled = null;
        SourceFile? newestStaticAsset = null;
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if (IsInsideABuildDirectory(sourceDirectory, file)) continue;
            var extension = Path.GetExtension(file);
            if (CompiledExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                newestCompiled = KeepTheNewer(newestCompiled, file);
            else if (StaticAssetExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                newestStaticAsset = KeepTheNewer(newestStaticAsset, file);
        }

        // Le due famiglie si pretendono tutte e due presenti. Una cartella con i .cs e senza un
        // solo .js non e' quella dell'Editor web, e accontentarsi vorrebbe dire lasciare meta'
        // controllo spento senza che si veda.
        if (newestCompiled is null) throw NothingOfThatKind(sourceDirectory, "nessun sorgente compilato", CompiledExtensions);
        if (newestStaticAsset is null) throw NothingOfThatKind(sourceDirectory, "nessuna risorsa statica", StaticAssetExtensions);

        return new ProductBinary(
            fullExecutable, File.GetLastWriteTimeUtc(fullExecutable),
            libraryPath, File.GetLastWriteTimeUtc(libraryPath),
            manifestPath, File.GetLastWriteTimeUtc(manifestPath),
            newestCompiled, newestStaticAsset);
    }

    /// <summary>
    /// Si rifiuta di far partire la prova quando un riferimento e' rimasto indietro. Il messaggio
    /// nomina <b>quale</b> file, <b>contro quale</b> dei due riferimenti e <b>come</b> rimediare:
    /// un rifiuto che dicesse solo che qualcosa non va lascerebbe a chi legge tutto il lavoro di
    /// capire cosa.
    /// </summary>
    public void RefuseIfStale()
    {
        if (!IsStale) return;

        var complaints = new List<string>();
        if (IsCompiledStale)
            complaints.Add(
                $"Il sorgente \"{NewestCompiledSourcePath}\" ({Show(NewestCompiledSourceWrittenAt)}) e' piu' recente " +
                $"di \"{LibraryPath}\" ({Show(LibraryWrittenAt)}), la libreria dell'Editor web dentro l'host.");
        if (IsStaticAssetStale)
            complaints.Add(
                $"La risorsa statica \"{NewestStaticAssetPath}\" ({Show(NewestStaticAssetWrittenAt)}) e' piu' recente " +
                $"di \"{ManifestPath}\" ({Show(ManifestWrittenAt)}), il manifesto che dice all'host quali risorse " +
                "servire e da dove.");

        throw new InvalidOperationException(
            string.Join(Environment.NewLine, complaints) + Environment.NewLine +
            "L'host che queste prove stanno per guidare non porta quella modifica: girerebbero contro il codice di " +
            "PRIMA, e passerebbero anche su una modifica rotta. Sono la sola rete automatica che veda draw.io girare " +
            "davvero, quindi un verde falso qui non lo prende nessun altro." + Environment.NewLine +
            "Rimedio: ricostruire l'host in Debug con" + Environment.NewLine +
            "    dotnet build WCS\\Wcs.Host\\src\\Wcs.Host.csproj -c Debug" + Environment.NewLine +
            "e rilanciare queste prove. Quella ricostruzione rifa' tutti e due i riferimenti - ricompila la libreria " +
            "se e' cambiato un .cs o un .razor, riscrive il manifesto se e' cambiata una risorsa statica - quindi " +
            "toglie il rifiuto qualunque dei due lo abbia fatto scattare. MISURATO in tutti e due i versi.");
    }

    /// <summary>Il percorso di questo file al momento della compilazione. Si veda <see cref="SourceDirectory"/> per il perche' sta a parte.</summary>
    private static string ThisFile([CallerFilePath] string path = "")
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                "Il compilatore non ha dato il percorso di questo file, quindi non si sa dove stiano i sorgenti " +
                "dell'Editor web. Senza quel percorso il controllo sull'eta' del binario non puo' essere fatto, e " +
                "queste prove sono la sola rete che vede draw.io: ci si ferma invece di saltarlo.");
        return path;
    }

    /// <summary>Fra quello che si aveva e questo file, tiene il piu' recente.</summary>
    private static SourceFile KeepTheNewer(SourceFile? sofar, string file)
    {
        var writtenAt = File.GetLastWriteTimeUtc(file);
        return sofar is not null && writtenAt <= sofar.WrittenAt ? sofar : new SourceFile(file, writtenAt);
    }

    private static InvalidOperationException NothingOfThatKind(string sourceDirectory, string family, string[] extensions) =>
        new($"In \"{sourceDirectory}\" non c'e' {family} dell'Editor web ({string.Join(" ", extensions)}): la cartella " +
            "c'e' ma non e' quella giusta, e il confronto con la data del riferimento direbbe sempre di si' senza " +
            "guardare niente.");

    /// <summary>Vero per un file dentro una cartella di lavoro di MSBuild, qualunque sia la sua estensione.</summary>
    private static bool IsInsideABuildDirectory(string sourceDirectory, string file)
    {
        var relative = Path.GetRelativePath(sourceDirectory, file);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // L'ultimo segmento e' il nome del file: un file che si chiamasse "bin" non e' una cartella.
        return segments[..^1].Any(segment => BuildDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Una data leggibile da chi legge il verbale, nell'ora locale: le date si confrontano in UTC -
    /// l'ora legale sposterebbe il confronto due volte l'anno - ma si mostrano come le mostra
    /// l'esploratore dei file, altrimenti chi le legge non le riconosce.
    /// </summary>
    private static string Show(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
