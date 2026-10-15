using System.Xml.Linq;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La porta chiusa verso il gestionale: il motore non deve poter chiamare i servizi del WMS
/// nemmeno per sbaglio, ne' aprire il suo SQL Server, e questa e' la prova che chi ci prova non
/// compila. Nessun progetto sotto <c>WCS/</c> referenzia piu' i progetti del gestionale — anche
/// l'ultimo residuo, <c>WMS.Services.Interface</c>, e' sparito — e il motore non referenzia piu'
/// nemmeno <c>MsSqlHelper</c>: con il compito 16 del piano «Il motore senza SQL Server» sono
/// uscite l'ultima query diretta (<c>GetDatiPostazione</c> su <c>POSTAZIONI</c>),
/// <c>Navigator.Database</c> e la chiave <c>Wms:ConnectionString</c>.
/// </summary>
/// <remarks>
/// <para>
/// Il primo test legge i <c>.csproj</c> di tutta l'area, perche' le assembly referenziate da
/// <c>Navigator</c> non dicono niente dell'Editor o dell'host (voce D140); gli altri tre guardano
/// il motore compilato.
/// </para>
/// <para>
/// <b>La verifica a mano che accompagna questi test</b>, e che non e' un'asserzione perche'
/// guarderebbe i sorgenti invece del compilato:
/// <code>
/// git grep -n "SqlConnection\|SqlCommand\|SqlDataReader\|MsSqlHelper\|Wms:ConnectionString\|Wms__ConnectionString" -- 'WCS/*/src/*' 'WCS/*/tests/*'
/// </code>
/// <b>I terminatori <c>/*</c> servono</b>: in git il <c>*</c> del pathspec attraversa le
/// <c>/</c> e il modello deve agganciare il percorso <b>intero</b>, quindi <c>WCS/*/src</c> da
/// solo non aggancia nessun file e darebbe zero righe anche con il codice SQL al suo posto —
/// un verde che non puo' fallire. Con la forma qui sopra <b>il risultato atteso non e' vuoto</b>:
/// sono solo commenti (questo, quelli dei <c>.csproj</c> e quelli delle prove che raccontano
/// che cosa e' uscito) piu' le <b>due asserzioni</b> di
/// <see cref="TheEngineDoesNotOpenSqlServerEither"/> qui sotto. <b>Zero righe di codice</b>: e'
/// quello che si guarda, non il conteggio. Fuori da quei percorsi restano, e sono legittimi:
/// i documenti sotto <c>doc/</c>, che raccontano anche com'era.
/// </para>
/// </remarks>
public sealed class EngineDependenciesTest
{
    /// <summary>
    /// Il confine per tutta l'area, letto dai file di progetto: nessun <c>.csproj</c> non di
    /// collaudo sotto <c>WCS/</c> ha un <c>ProjectReference</c> verso i servizi del gestionale,
    /// cioe' un progetto <c>WMS.Services*</c> (oggi in <c>Common/</c>) o qualunque cosa sotto una
    /// cartella <c>Services\</c> (dove stavano nel monorepo di provenienza). I progetti di collaudo
    /// sono quelli sotto una cartella <c>tests</c>: <c>Wcs.Connection.Test</c> e' la connessione
    /// simulata, non un collaudo, e conta. Il messaggio nomina il progetto che sgarra.
    /// </summary>
    [Fact]
    public void NoProjectUnderWcsReferencesTheWarehouseManagementServices()
    {
        var root = FindAreaRoot();
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !HasSegment(p, "bin") && !HasSegment(p, "obj") && !HasSegment(p, "tests"))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // L'enumerazione deve aver trovato l'area: un elenco vuoto o sbagliato darebbe verde.
        Assert.Contains(projects, p => Path.GetFileName(p) == "Wcs.Engine.csproj");
        Assert.Contains(projects, p => Path.GetFileName(p) == "Wcs.Host.csproj");

        var violations = projects
            .SelectMany(p => XDocument.Load(p).Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => (string?)e.Attribute("Include") ?? "")
                .Where(PointsToTheWarehouseManagementServices)
                .Select(include => $"{Path.GetRelativePath(root, p)} -> {include}"))
            .ToList();

        Assert.True(violations.Count == 0,
            "Progetti sotto WCS/ che referenziano i servizi del gestionale:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    /// <summary>La cartella <c>WCS/</c> e' un antenato della cartella dei test: la prima che si chiama cosi' e contiene <c>Wcs.Engine</c>.</summary>
    private static string FindAreaRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (dir.Name == "WCS" && Directory.Exists(Path.Combine(dir.FullName, "Wcs.Engine")))
                return dir.FullName;
        throw new InvalidOperationException($"la cartella WCS/ non e' fra gli antenati di {AppContext.BaseDirectory}");
    }

    private static bool HasSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);

    private static bool PointsToTheWarehouseManagementServices(string include)
    {
        var segments = include.Split('\\', '/');
        return segments[^1].StartsWith("WMS.Services", StringComparison.OrdinalIgnoreCase)
            || segments.Contains("Services", StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] Referenced =
        typeof(Navigator).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    /// <summary>
    /// <c>WMS.Services</c> e' lo strato dei servizi del gestionale: aree, ORM, sessione verso il
    /// WCS. Il motore non ne usa piu' nessun tipo, e il progetto non lo referenzia piu'. Le due
    /// asserzioni dicono cose diverse, e servono tutte e due: un riferimento non usato non compare
    /// fra le assembly del manifest — quindi la prima da' verde anche a riferimento presente — ma
    /// resta a disposizione del prossimo che scrive una riga, e la seconda e' quella che lo vede,
    /// perche' senza <c>ProjectReference</c> la libreria non arriva nella cartella dei test.
    /// </summary>
    [Fact]
    public void TheEngineDoesNotReferenceTheWarehouseManagementSystem()
    {
        Assert.DoesNotContain("WMS.Services", Referenced);
        Assert.Empty(Directory.GetFiles(AppContext.BaseDirectory, "WMS.Services.dll"));
    }

    /// <summary>
    /// Il residuo e' sparito insieme al generatore che lo giustificava:
    /// <c>GetAutomaticAreasByWarehouseId</c> non interpola piu' nessun frammento SQL di
    /// <c>Enumeratori.TipoArea.CondizioneCapacita</c>, perche' la condizione era ridondante — il
    /// gestionale la riapplica gia' in AND dentro <c>getDtCELLE_DISPONIBILI</c>. Con il compito 10
    /// quel metodo non interpola piu' proprio niente: non ha piu' nessuna query, e' un'intersezione
    /// in memoria in <c>Persistence/LocalQueries.cs</c> (si veda il suo <c>remarks</c>). Stesse due
    /// asserzioni del test sopra, stesso motivo per averle entrambe.
    /// </summary>
    [Fact]
    public void TheEngineDoesNotReferenceTheInterfaceProjectEither()
    {
        Assert.DoesNotContain("WMS.Services.Interface", Referenced);
        Assert.Empty(Directory.GetFiles(AppContext.BaseDirectory, "WMS.Services.Interface.dll"));
    }

    /// <summary>
    /// Il motore non apre piu' SQL Server del gestionale: MsSqlHelper non e' fra le assembly del
    /// manifest e la sua DLL non arriva nella cartella dei test. Doppia asserzione per la stessa
    /// ragione delle altre due: un ProjectReference inutilizzato non compare nel manifest.
    /// </summary>
    [Fact]
    public void TheEngineDoesNotOpenSqlServerEither()
    {
        Assert.DoesNotContain("MsSqlHelper", Referenced);
        Assert.Empty(Directory.GetFiles(AppContext.BaseDirectory, "MsSqlHelper.dll"));
    }

    /// <summary>
    /// The engine names no field driver: the Sun Tech driver and its protocol are plugged in by the
    /// host through <c>IConnectionFactory</c>, so the engine builds and runs without them (and the
    /// public copy, which does not ship them, builds at all). The same two checks as above (manifest and output folder), over the
    /// whole Wcs.Connection.* family; the folder check needs a clean output folder, since an incremental build does
    /// not delete a DLL a previous build copied there.
    /// </summary>
    [Fact]
    public void TheEngineDoesNotReferenceAFieldDriver()
    {
        string[] allowed = ["Wcs.Connection.Interface", "Wcs.Connection.Test"];
        // The enumeration must see the family, or the two checks below pass on nothing.
        Assert.Contains("Wcs.Connection.Interface", Referenced);
        Assert.DoesNotContain(Referenced,
            n => n.StartsWith("Wcs.Connection.", StringComparison.Ordinal) && !allowed.Contains(n));
        Assert.DoesNotContain(Directory.GetFiles(AppContext.BaseDirectory, "Wcs.Connection.*.dll")
            .Select(Path.GetFileNameWithoutExtension), n => !allowed.Contains(n));
    }
}
