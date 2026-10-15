using Xunit;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// La rete della rete: <see cref="ProductBinary"/> e' cio' che impedisce a queste prove di girare
/// contro un binario vecchio, e se si spegnesse nessuno se ne accorgerebbe - il suo silenzio ha
/// esattamente la forma del verde.
/// </summary>
/// <remarks>
/// <para>
/// Queste prove non avviano niente: nessun host, nessun browser. Girano ovunque, e il loro rosso
/// dice che il controllo sull'eta' del binario non funziona piu'.
/// </para>
/// <para>
/// <b>Due versi, non uno.</b> Un controllo che si limitasse a scattare sarebbe meta' controllo:
/// deve anche <b>smettere di scattare</b> quando si fa cio' che il suo messaggio prescrive,
/// altrimenti l'unica uscita per chi ci sbatte e' allentarlo - cioe' disfare proprio cio' che
/// protegge. Qui sotto il secondo verso e' provato per tutte e due le famiglie di sorgenti.
/// </para>
/// </remarks>
public sealed class ProductBinaryTest : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wcs-product-binary-" + Guid.NewGuid().ToString("N"));
    private readonly string _binDirectory;
    private readonly string _sourceDirectory;
    private readonly string _executable;
    private readonly string _library;
    private readonly string _manifest;

    public ProductBinaryTest()
    {
        _binDirectory = Path.Combine(_root, "bin");
        _sourceDirectory = Path.Combine(_root, "src");
        Directory.CreateDirectory(_binDirectory);
        Directory.CreateDirectory(_sourceDirectory);
        _executable = Write(Path.Combine(_binDirectory, "Wcs.Host.exe"), Noon);
        _library = Write(Path.Combine(_binDirectory, ProductBinary.LibraryName), Noon);
        _manifest = Write(Path.Combine(_binDirectory, "Wcs.Host" + ProductBinary.ManifestSuffix), Noon);
    }

    /// <summary>Un'ora qualunque ma fissa: le prove qui sotto si muovono attorno a lei, non attorno ad "adesso".</summary>
    private static readonly DateTime Noon = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void RefusesWhenACompiledSourceIsNewerThanTheLibrary()
    {
        WriteCompiled(Noon.AddMinutes(1));
        WriteStaticAsset(Noon.AddMinutes(-1));

        var binary = ProductBinary.Inspect(_executable, _sourceDirectory);

        Assert.True(binary.IsCompiledStale, $"Il sorgente e' un minuto piu' recente della libreria, e non risulta. {binary.Declaration}");
        Assert.False(binary.IsStaticAssetStale, $"La risorsa statica e' piu' vecchia del manifesto e risulta arretrata. {binary.Declaration}");
        var refusal = Assert.Throws<InvalidOperationException>(binary.RefuseIfStale);
        // Il messaggio deve portare le tre cose che servono a chi lo legge: QUALE file, contro
        // QUALE riferimento, e COME si rimedia. Un rifiuto che dicesse solo "qualcosa non va"
        // farebbe ripartire da zero.
        Assert.Contains("DrawioDelta.cs", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(ProductBinary.LibraryName, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("-c Debug", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Il caso che il confronto con la sola libreria lasciava passare: una risorsa statica cambiata
    /// dopo l'ultima scrittura del manifesto, ma prima dell'ultima compilazione della libreria. Il
    /// browser eseguirebbe il JavaScript che il manifesto non ha ancora visto, e queste prove sono
    /// le uniche che quel JavaScript lo eseguono davvero.
    /// </summary>
    [Fact]
    public void RefusesWhenAStaticAssetIsNewerThanTheManifest()
    {
        Touch(_library, Noon.AddMinutes(5));
        WriteCompiled(Noon.AddMinutes(-1));
        WriteStaticAsset(Noon.AddMinutes(1));

        var binary = ProductBinary.Inspect(_executable, _sourceDirectory);

        Assert.True(binary.IsStaticAssetStale, $"La risorsa statica e' piu' recente del manifesto e non risulta. {binary.Declaration}");
        Assert.False(binary.IsCompiledStale, $"Il sorgente compilato e' piu' vecchio della libreria e risulta arretrato. {binary.Declaration}");
        var refusal = Assert.Throws<InvalidOperationException>(binary.RefuseIfStale);
        Assert.Contains("drawio-bridge.js", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(_manifest), refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Il secondo verso, per una risorsa statica.</b> E' il difetto che questa classe ha avuto:
    /// il rifiuto scattava e non se ne andava piu', perche' una modifica a un <c>.js</c> non
    /// ricompila la libreria e la ricostruzione prescritta dal messaggio non muoveva niente di
    /// cio' che il controllo guardava. Qui si riproduce cio' che la build fa <b>davvero</b>
    /// (MISURATO: manifesto riscritto, libreria ferma) e si pretende che il rifiuto sparisca.
    /// </summary>
    [Fact]
    public void ARebuildClearsTheRefusalOverAStaticAsset()
    {
        WriteCompiled(Noon.AddMinutes(-1));
        WriteStaticAsset(Noon.AddMinutes(1));
        Assert.True(ProductBinary.Inspect(_executable, _sourceDirectory).IsStale,
            "Prima della ricostruzione il rifiuto deve esserci, altrimenti il secondo verso non prova niente.");

        // La ricostruzione, com'e' stata misurata: il manifesto viene riscritto, la libreria non si
        // muove di un millesimo perche' nessun .cs e' cambiato.
        Touch(_manifest, Noon.AddMinutes(2));

        var binary = ProductBinary.Inspect(_executable, _sourceDirectory);

        Assert.Equal(Noon, binary.LibraryWrittenAt);
        Assert.False(binary.IsStale,
            $"Ricostruito l'host, il rifiuto e' rimasto: il rimedio che il messaggio prescrive non funziona. {binary.Declaration}");
        binary.RefuseIfStale();
    }

    /// <summary>
    /// <b>Il secondo verso, per un sorgente compilato.</b> La' la ricostruzione muove la libreria.
    /// </summary>
    [Fact]
    public void ARebuildClearsTheRefusalOverACompiledSource()
    {
        WriteCompiled(Noon.AddMinutes(1));
        WriteStaticAsset(Noon.AddMinutes(-1));
        Assert.True(ProductBinary.Inspect(_executable, _sourceDirectory).IsStale,
            "Prima della ricostruzione il rifiuto deve esserci, altrimenti il secondo verso non prova niente.");

        Touch(_library, Noon.AddMinutes(2));
        Touch(_manifest, Noon.AddMinutes(2));

        var binary = ProductBinary.Inspect(_executable, _sourceDirectory);

        Assert.False(binary.IsStale, $"Ricostruito l'host, il rifiuto e' rimasto. {binary.Declaration}");
        binary.RefuseIfStale();
    }

    /// <summary>
    /// Il manifesto e' piu' recente di tutto, ma un <c>.cs</c> non e' entrato nella libreria: il
    /// rifiuto deve restare. E' la guardia contro la semplificazione sbagliata - un riferimento
    /// solo per tutti - che rimetterebbe in piedi il verde falso da cui questa classe e' nata.
    /// </summary>
    [Fact]
    public void AFreshManifestDoesNotForgiveACompiledSource()
    {
        Touch(_manifest, Noon.AddMinutes(5));
        WriteCompiled(Noon.AddMinutes(1));
        WriteStaticAsset(Noon.AddMinutes(-1));

        var binary = ProductBinary.Inspect(_executable, _sourceDirectory);

        Assert.True(binary.IsStale, $"Un manifesto fresco ha coperto un .cs non compilato. {binary.Declaration}");
    }

    [Fact]
    public void AcceptsWhenEverySourceIsOlderThanItsReference()
    {
        WriteCompiled(Noon.AddMinutes(-1));
        WriteStaticAsset(Noon.AddSeconds(-1));

        var binary = ProductBinary.Inspect(_executable, _sourceDirectory);

        Assert.False(binary.IsStale, $"Tutti i sorgenti sono piu' vecchi del loro riferimento e il controllo si lamenta. {binary.Declaration}");
        binary.RefuseIfStale();
        // I sorgenti nominati sono i PIU' RECENTI della loro famiglia, non i primi che si
        // incontrano: sono quelli che decidono l'esito, e il verbale deve dire su che cosa si e'
        // deciso.
        Assert.Contains("DrawioDelta.cs", binary.NewestCompiledSourcePath, StringComparison.Ordinal);
        Assert.Contains("drawio-bridge.js", binary.NewestStaticAssetPath, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaresBothReferencesAndBothNewestSourcesWithTheirDates()
    {
        Write(Path.Combine(_sourceDirectory, "Pages", "DrawioEditor.razor"), Noon.AddMinutes(-5));
        WriteStaticAsset(Noon.AddMinutes(-5));

        var declaration = ProductBinary.Inspect(_executable, _sourceDirectory).Declaration;

        Assert.Contains("Wcs.Host.exe", declaration, StringComparison.Ordinal);
        Assert.Contains(ProductBinary.LibraryName, declaration, StringComparison.Ordinal);
        // Il manifesto e' l'altro riferimento del confronto: un verbale che lo tacesse non
        // permetterebbe di rifare a mano meta' del controllo.
        Assert.Contains(Path.GetFileName(_manifest), declaration, StringComparison.Ordinal);
        Assert.Contains("DrawioEditor.razor", declaration, StringComparison.Ordinal);
        Assert.Contains("drawio-bridge.js", declaration, StringComparison.Ordinal);
        // Le date, non solo i nomi: un verbale che dicesse quale file senza dire di quando non
        // permetterebbe a chi legge di rifare il confronto.
        Assert.Contains(Noon.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), declaration, StringComparison.Ordinal);
        Assert.Contains(Noon.AddMinutes(-5).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), declaration, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoresWhatMsbuildWritesUnderBinAndObj()
    {
        WriteCompiled(Noon.AddMinutes(-1));
        WriteStaticAsset(Noon.AddMinutes(-1));
        // Scritti dalla build stessa, e piu' recenti dei riferimenti per costruzione: se
        // contassero, il controllo si lamenterebbe subito dopo ogni ricostruzione riuscita - cioe'
        // sempre - e chi lo legge imparerebbe a ignorarlo.
        Write(Path.Combine(_sourceDirectory, "obj", "Debug", "net10.0", "Wcs.WebEditor.AssemblyInfo.cs"), Noon.AddHours(1));
        Write(Path.Combine(_sourceDirectory, "bin", "Debug", "net10.0", "wwwroot", "drawio-bridge.js"), Noon.AddHours(1));

        var binary = ProductBinary.Inspect(_executable, _sourceDirectory);

        Assert.False(binary.IsStale, $"Un file generato dalla build ha fatto scattare il controllo. {binary.Declaration}");
        Assert.Contains("DrawioDelta.cs", binary.NewestCompiledSourcePath, StringComparison.Ordinal);
        Assert.Contains("wwwroot", binary.NewestStaticAssetPath, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.Combine("bin", "Debug"), binary.NewestStaticAssetPath, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenTheSourceDirectoryIsNotThere()
    {
        var missing = Path.Combine(_root, "src-che-non-c-e");

        var refusal = Assert.Throws<InvalidOperationException>(() => ProductBinary.Inspect(_executable, missing));

        Assert.Contains(missing, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenTheSourceDirectoryHoldsNoCompiledSource()
    {
        // La cartella c'e' e non e' vuota, ma dentro non c'e' niente che finisca nella libreria:
        // e' il caso in cui un controllo scritto male direbbe "va bene" senza aver guardato meta'
        // di quello che doveva.
        WriteStaticAsset(Noon.AddHours(1));

        var refusal = Assert.Throws<InvalidOperationException>(() => ProductBinary.Inspect(_executable, _sourceDirectory));

        Assert.Contains(_sourceDirectory, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(".razor", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenTheSourceDirectoryHoldsNoStaticAsset()
    {
        WriteCompiled(Noon.AddHours(1));

        var refusal = Assert.Throws<InvalidOperationException>(() => ProductBinary.Inspect(_executable, _sourceDirectory));

        Assert.Contains(_sourceDirectory, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(".js", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenTheLibraryIsNotBesideTheExecutable()
    {
        File.Delete(_library);
        WriteCompiled(Noon);
        WriteStaticAsset(Noon);

        var refusal = Assert.Throws<InvalidOperationException>(() => ProductBinary.Inspect(_executable, _sourceDirectory));

        Assert.Contains(ProductBinary.LibraryName, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Senza manifesto non c'e' il riferimento delle risorse statiche. Fermarsi e' l'unica via che
    /// non mente: prendere la data della libreria al suo posto rimetterebbe in piedi il rifiuto
    /// senza uscita, e ignorare le risorse statiche spegnerebbe meta' controllo in silenzio.
    /// </summary>
    [Fact]
    public void SaysSoWhenTheManifestIsNotBesideTheExecutable()
    {
        File.Delete(_manifest);
        WriteCompiled(Noon);
        WriteStaticAsset(Noon);

        var refusal = Assert.Throws<InvalidOperationException>(() => ProductBinary.Inspect(_executable, _sourceDirectory));

        Assert.Contains(Path.GetFileName(_manifest), refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Il percorso dei sorgenti veri, quello che il controllo usa quando gira sul serio. Tutte le
    /// prove qui sopra lavorano su cartelle finte e resterebbero verdi anche se questo percorso
    /// puntasse nel vuoto: la' il controllo si fermerebbe a ogni avvio, qui si vede subito.
    /// </summary>
    [Fact]
    public void TheRealSourceDirectoryIsWhereItIsExpected()
    {
        var sources = ProductBinary.SourceDirectory();

        Assert.True(Directory.Exists(sources), $"I sorgenti dell'Editor web non stanno in \"{sources}\".");
        Assert.True(File.Exists(Path.Combine(sources, "Drawio", "DrawioDelta.cs")),
            $"In \"{sources}\" non c'e' Drawio\\DrawioDelta.cs: il percorso porta da qualche altra parte.");
        Assert.True(File.Exists(Path.Combine(sources, "Pages", "DrawioEditor.razor")),
            $"In \"{sources}\" non c'e' Pages\\DrawioEditor.razor: il percorso porta da qualche altra parte.");
        Assert.True(File.Exists(Path.Combine(sources, "wwwroot", "drawio-bridge.js")),
            $"In \"{sources}\" non c'e' wwwroot\\drawio-bridge.js: il percorso porta da qualche altra parte.");
    }

    /// <summary>
    /// La prova qui sopra, chiamando dal proprio file, non distingue il percorso di
    /// <c>ProductBinary.cs</c> da quello di <c>ProductBinaryTest.cs</c>: stanno nella stessa
    /// cartella e i due conti danno lo stesso risultato, quindi resterebbe verde anche se il
    /// percorso lo desse <b>chi chiama</b> - e il giorno in cui uno dei due file si spostasse, il
    /// controllo vero si romperebbe in silenzio. Qui la stessa domanda arriva da una cartella piu'
    /// in basso, dove le due risposte non possono coincidere per caso.
    /// </summary>
    [Fact]
    public void TheRealSourceDirectoryDoesNotDependOnWhoAsks()
    {
        var fromHere = ProductBinary.SourceDirectory();
        var fromElsewhere = Elsewhere.AskedFromAnotherFolder.SourceDirectory();

        Assert.True(string.Equals(fromHere, fromElsewhere, StringComparison.OrdinalIgnoreCase),
            $"La cartella dei sorgenti cambia con chi la chiede: da qui \"{fromHere}\", da un'altra cartella " +
            $"\"{fromElsewhere}\". Il percorso viene dal file del chiamante invece che da quello di ProductBinary.");
    }

    private void WriteCompiled(DateTime writtenAtUtc) =>
        Write(Path.Combine(_sourceDirectory, "Drawio", "DrawioDelta.cs"), writtenAtUtc);

    private void WriteStaticAsset(DateTime writtenAtUtc) =>
        Write(Path.Combine(_sourceDirectory, "wwwroot", "drawio-bridge.js"), writtenAtUtc);

    private static string Write(string path, DateTime writtenAtUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "contenuto qualunque: il controllo guarda le date, non i contenuti");
        File.SetLastWriteTimeUtc(path, writtenAtUtc);
        return path;
    }

    private static void Touch(string path, DateTime writtenAtUtc) => File.SetLastWriteTimeUtc(path, writtenAtUtc);
}
