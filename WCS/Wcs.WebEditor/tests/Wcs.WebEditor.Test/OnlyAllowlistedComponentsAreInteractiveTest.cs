using System.Text.RegularExpressions;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// La rete larga sul canale <c>/_blazor</c>, rimessa in revisione accanto a
/// <see cref="WithoutAKeyNothingIsReachableTest"/> - non al suo posto. Quella prova e' locale a
/// <c>Editor.razor</c>: garantisce che quel componente, senza chiave, non mostri l'impianto. Ma un
/// componente interattivo futuro che non passasse da <see cref="Wcs.WebEditor.Session.EditorSessionState"/>
/// - per esempio uno che chiama l'host direttamente, o che dimentica il controllo - non verrebbe
/// intercettato da niente: nessuno lo saprebbe finche' non fosse gia' in produzione.
/// </summary>
/// <remarks>
/// Questo test scandisce di nuovo tutti i <c>.razor</c> della libreria (era
/// <c>InteractiveRenderModeSentinelTest</c>, compito 1, sostituito - non integrato - dal compito 2:
/// l'errore che questa revisione corregge), ma non pretende piu' che <b>nessun</b> componente sia
/// interattivo: da <c>Editor.razor</c> in poi sarebbe falso. Al suo posto confronta l'elenco trovato
/// con <see cref="AllowedInteractiveComponents"/>, dichiarato qui sotto. Aggiungere un componente
/// interattivo alla libreria senza aggiungerlo a questo elenco fa fallire il test: chi lo aggiunge e'
/// costretto a una decisione consapevole - "questo componente nuovo nega l'impianto a chi non ha la
/// chiave? come?" - invece di scoprirlo (o non scoprirlo mai) in un secondo momento. L'elenco non va
/// allargato per farlo passare senza aver risposto a quella domanda.
/// </remarks>
public sealed class OnlyAllowlistedComponentsAreInteractiveTest
{
    /// <summary>
    /// I soli componenti che possono dichiarare una modalita' di rendering interattiva oggi.
    /// <c>Editor.razor</c> puo' perche' e' lui stesso a negare l'impianto a chi non ha la chiave
    /// (vedi <see cref="WithoutAKeyNothingIsReachableTest"/>): <c>&lt;Key&gt;</c>, che vive dentro di
    /// lui, resta statico - eredita l'interattivita' del padre, non ne dichiara una propria.
    /// <c>DrawioEditor.razor</c> e' rimasto nell'elenco dal compito 1, ma la ragione e' cambiata dal
    /// compito 5 - quella di allora ("non nega niente perche' oggi non c'e' niente da negare", nessun
    /// <c>@inject</c> di <see cref="Wcs.WebEditor.Session.EditorSessionState"/>) e' finita esattamente
    /// come questo commento gia' preannunciava: da quando carica l'impianto vero
    /// (<c>PlantGraph.LoadAsync</c>, lo stesso ponte di <c>Editor.razor</c>) la sua unica interattivita'
    /// e' ammessa per la STESSA ragione di <c>Editor.razor</c> - nega lei stessa l'impianto a chi non
    /// ha la chiave, mostrando solo <c>&lt;Key&gt;</c> quando <c>EditorSessionState.HasKey</c> e'
    /// falso. La prova locale e' <c>DrawioEditorPageTest.WithoutAKeyOnlyTheKeyFormIsReachable</c>,
    /// gemella di <see cref="WithoutAKeyNothingIsReachableTest"/>.
    /// </summary>
    // UNA SOLA VOCE da quando la vecchia tela e' stata smontata: "Editor.razor" stava qui perche'
    // era lui a negare l'impianto a chi non ha la chiave, e quel file non esiste piu'. Tolta e non
    // lasciata per prudenza: un elenco che nomina un file inesistente permette a un componente
    // futuro di prendersi quel nome e passare senza che nessuno lo guardi.
    private static readonly string[] AllowedInteractiveComponents = ["DrawioEditor.razor"];

    private static readonly Regex DeclaresAnInteractiveRenderMode =
        new(@"@rendermode\b|@attribute\s*\[\s*RenderMode", RegexOptions.Compiled);

    [Fact]
    public void NoRazorComponentOutsideTheAllowlistDeclaresAnInteractiveRenderMode()
    {
        var srcRoot = FindWebEditorSourceRoot();
        var razorFiles = Directory.EnumerateFiles(srcRoot, "*.razor", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // L'enumerazione deve aver trovato la libreria vera: un elenco vuoto o sbagliato darebbe
        // verde perche' non c'e' niente da scandire, non perche' tutto e' in regola.
        Assert.Contains(razorFiles, p => Path.GetFileName(p) == "DrawioEditor.razor");
        Assert.Contains(razorFiles, p => Path.GetFileName(p) == "App.razor");

        var offenders = razorFiles
            .Where(p => DeclaresAnInteractiveRenderMode.IsMatch(File.ReadAllText(p)))
            .Select(p => Path.GetFileName(p))
            .Where(name => !AllowedInteractiveComponents.Contains(name))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Componenti interattivi non nell'elenco ammesso: " + string.Join(", ", offenders) + "." +
            Environment.NewLine +
            "Un componente interattivo puo' essere raggiunto attraverso /_blazor prima che chi lo " +
            "guarda abbia dato la chiave (ApiKeyMiddleware lascia passare quel canale per " +
            "necessita': la pagina che la chiede e' essa stessa una pagina Blazor). Prima di " +
            "aggiungerlo a AllowedInteractiveComponents in questo file, verifica che neghi " +
            "l'impianto a una sessione senza EditorSessionState.HasKey, con un test locale come " +
            "WithoutAKeyNothingIsReachableTest.");
    }

    /// <summary>
    /// La cartella <c>src</c> della libreria: sorella di <c>tests</c>, sotto l'antenata che si
    /// chiama <c>Wcs.WebEditor</c> - stesso schema di ricerca usato da
    /// <c>Wcs.Engine.Test.EngineDependenciesTest.FindAreaRoot</c>.
    /// </summary>
    private static string FindWebEditorSourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (dir.Name != "Wcs.WebEditor") continue;
            var src = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(src)) return src;
        }

        throw new InvalidOperationException(
            $"la cartella Wcs.WebEditor/src non e' fra gli antenati di {AppContext.BaseDirectory}");
    }
}
