using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Session;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Base comune a ogni classe di prova di questo progetto (compito B3a): da questo compito
/// <c>PlantCanvas.OnAfterRenderAsync</c> importa un modulo JS (<c>plant-canvas-size.js</c>) per
/// misurare la tela vera nel browser - la prima interoperabilita' JS che questa libreria di
/// componenti Razor porta. bUnit, in modalita' predefinita (<c>JSRuntimeMode.Strict</c>), rifiuta
/// qualunque chiamata JS che nessuno ha configurato (<c>JSRuntimeUnhandledInvocationException</c>):
/// senza questa base, ogni prova che rende <c>PlantCanvas</c> - direttamente o attraverso
/// <c>Editor</c> - falliva qui, non per un difetto della tela ma per l'assenza di un motore JS che
/// l'ambiente di prova non ha mai promesso di avere.
/// <para>
/// <c>Loose</c>, non un <c>SetupModule</c> puntuale: questo progetto non ha ancora nessun'altra
/// interoperabilita' JS da configurare (e' la prima), quindi non c'e' nessuna chiamata che una
/// modalita' permissiva rischierebbe di lasciar passare per sbaglio al posto di un mock scelto
/// apposta. <c>PlantCanvas.OnAfterRenderAsync</c> gestisce gia' da solo il caso "nessuna
/// interoperabilita' disponibile" (il modulo torna nullo, il resto del metodo si ferma li'): la
/// prova che vuole verificare l'effetto di una misura del browser (<c>CanvasSizeTest</c>) non la
/// simula da qui, chiama direttamente il metodo <c>[JSInvokable]</c> <c>OnCanvasSizeChanged</c>
/// sull'istanza gia' resa - lo stesso principio con cui <c>UndoTest</c> richiama
/// <c>OnKeyDown</c> a mano invece di affidarsi al fuoco vero della tastiera (si veda il commento di
/// quel metodo in PlantCanvas.razor).
/// </para>
/// </summary>
public abstract class PlantCanvasBunitContext : BunitContext
{
    protected PlantCanvasBunitContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        // I servizi di Fluent UI Blazor: da quando la barra e' fatta con i suoi controlli, ogni resa
        // che arriva a Toolbar (diretta, o attraverso PlantCanvas/Editor) ne ha bisogno -
        // FluentButton e FluentTooltip risolvono LibraryConfiguration e il servizio dei suggerimenti
        // dal contenitore. Senza, non fallirebbe un'asserzione ma la resa stessa, e per un motivo che
        // non c'entra con cio' che la prova vuole misurare. E' la stessa registrazione che
        // AddWebEditor fa per l'applicazione vera.
        // NON registra <FluentMenuProvider/>: quel provider e' un componente, non un servizio, e
        // nessun componente di questa libreria rende piu' un <FluentMenu> (compito barre-usabili: il
        // menu del traboccamento della barra, l'unico che lo usava, e' sparito insieme a
        // FluentOverflow).
        Services.AddFluentUIComponents();

        // Un EditorSessionState minimo (compito barre-usabili): ImportExport.razor e' da questo
        // compito l'ottava barra del dock, non piu' un fratello di PlantCanvas sotto Editor.razor -
        // con lei ogni resa di Toolbar (anche da sola, come in ToolbarTest) raggiunge un componente
        // che inietta EditorSessionState nella disposizione predefinita. Un URI qualunque, nessun
        // gestore HTTP: nessuna prova di questa base tocca State.Client, solo chi clicca
        // Export/Import lo farebbe - e quelle prove (ImportExportTest, DragTest,
        // EditorEditLockTest, ...) registrano gia' il proprio EditorSessionState con un gestore vero
        // nel proprio costruttore, DOPO questo: l'ultima registrazione vince nel contenitore di
        // bUnit, quindi la loro sostituisce questa senza conflitto.
        Services.AddSingleton(new EditorSessionState(new Uri("http://localhost:5580")));
    }

    /// <summary>
    /// Configura il modulo JS che <c>PlantCanvas.OnKeyDown</c> interroga (plant-canvas-keyboard.js)
    /// perche' risponda sempre <paramref name="isTyping"/> - lo stesso che
    /// <c>document.activeElement</c> direbbe in un browser vero col fuoco su un campo (o non su un
    /// campo). Va chiamato PRIMA di rendere <c>PlantCanvas</c>: <c>OnAfterRenderAsync</c> importa il
    /// modulo alla prima resa, e un modulo configurato dopo non lo sostituirebbe piu'.
    /// <para>
    /// <b>Corretto in revisione (rilievo I1): il modulo NON resta nullo senza chiamare questo
    /// metodo, e la guardia "nel dubbio blocca" non e' esercitata a riposo - misurato, non dedotto.</b>
    /// La modalita' <c>Loose</c> di questa base non rifiuta un <c>import</c> non pianificato: torna un
    /// modulo LASCO, e una chiamata non pianificata su quel modulo -
    /// <c>InvokeAsync&lt;bool&gt;("isTypingTarget")</c> - torna <c>default(bool)</c>, cioe'
    /// <c>false</c>: "non sto scrivendo". Prova usa e getta, cancellata dopo la misura: un
    /// <c>KeyDown</c> con permesso di modifica e selezione presenti, SENZA chiamare
    /// <c>SetupTypingTarget</c>, ha scritto (<c>Expected: 0, Actual: 1</c>) - la freccia si muove.
    /// Il ramo "nel dubbio blocca" di <c>IsTypingInFieldAsync</c> (modulo nullo, o la chiamata che
    /// solleva <c>JSException</c>/<c>JSDisconnectedException</c>/<c>TaskCanceledException</c>) NON e'
    /// quindi esercitato da questa base a riposo, ed e' rimasto non provato dopo la correzione che lo
    /// ha introdotto: per esercitarlo davvero serve un modulo che SOLLEVA
    /// (<see cref="SetupTypingTargetToThrow"/>), un modulo assente non basta.
    /// Ogni prova che manda un <c>KeyDown</c> e si aspetta una scrittura vera (freccia che sposta,
    /// Ctrl+Z/Y/Shift+Z che annulla/rifa') chiama comunque <c>SetupTypingTarget(false)</c>: non
    /// perche' il ripiego bloccherebbe altrimenti (non lo fa), ma per essere esplicita sul presupposto
    /// invece di appoggiarsi al comportamento non dichiarato di un modulo lasco.
    /// </para>
    /// </summary>
    protected BunitJSModuleInterop SetupTypingTarget(bool isTyping)
    {
        var module = JSInterop.SetupModule("./_content/Wcs.WebEditor/plant-canvas-keyboard.js");
        module.Setup<bool>("isTypingTarget").SetResult(isTyping);
        return module;
    }

    /// <summary>
    /// L'unico modo, in questa base, di esercitare davvero il ramo "nel dubbio blocca" di
    /// <c>IsTypingInFieldAsync</c> (revisione B4bis, rilievo I1): un modulo che SOLLEVA quando
    /// interrogato, non un modulo assente - si veda il commento di <see cref="SetupTypingTarget"/> per
    /// la misura che lo dimostra.
    /// </summary>
    protected BunitJSModuleInterop SetupTypingTargetToThrow()
    {
        var module = JSInterop.SetupModule("./_content/Wcs.WebEditor/plant-canvas-keyboard.js");
        module.Setup<bool>("isTypingTarget").SetException(new JSDisconnectedException("circuito in chiusura (prova)"));
        return module;
    }
}

/// <summary>
/// Aiutanti geometrici condivisi (correzione in revisione): <c>TransformHandlesTest</c> e
/// <c>DisplayGeometryTest</c> ne tenevano ciascuno una copia quasi letterale - il commento di
/// <c>TransformHandlesTest</c> prometteva proprio il contrario, "se un giorno quei dati cambiassero,
/// cambierebbero insieme anche qui", ma con due copie potevano divergere in silenzio. Una classe
/// statica accanto a <see cref="PlantCanvasBunitContext"/>, non membri protetti su quella base: la
/// base e' ereditata da OGNI classe di prova del progetto, e alcune di loro tengono gia' propri
/// aiutanti privati con questi stessi nomi (<c>RotatedX</c>, <c>Item</c>, <c>Node</c>...) - membri
/// protetti sulla base li avrebbero nascosti in silenzio (<c>CS0108</c>) invece di restare quello che
/// sono, copie locali indipendenti di classi che questo compito non tocca. <c>using static</c>, letta
/// da entrambi i file interessati, tiene la stessa sintassi di chiamata (<c>RotatedItem(id)</c>, non
/// <c>RotatedFixture.RotatedItem(id)</c>) senza toccare nessun'altra classe di prova.
/// </summary>
public static class RotatedFixture
{
    /// <summary>
    /// Il riquadro migrato di un dispositivo proprio 80x20 ruotato di 35 gradi - la lettura vera di
    /// Syncfusion (si veda il remarks di <see cref="NodePlacement"/>), non un valore inventato: a
    /// rotazione zero e riquadro quadrato la formula giusta e quella sbagliata darebbero lo stesso
    /// risultato, e non proverebbero niente.
    /// </summary>
    public const double RotatedX = 101.49815;
    public const double RotatedY = 28.865425;
    public const double RotatedWidth = 77.00368;
    public const double RotatedHeight = 62.269142;
    public const double RotatedRotation = 35;

    public static PlantItemDto RotatedItem(Guid id) => new(
        id, "x", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0,
        default!, X: RotatedX, Y: RotatedY, Width: RotatedWidth, Height: RotatedHeight, Rotation: RotatedRotation);

    public static PlantItemDto Item(Guid id, double x, double y, double width, double height, double rotation) => new(
        id, "x", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0,
        default!, X: x, Y: y, Width: width, Height: height, Rotation: rotation);

    // Gli aiutanti che rendevano la vecchia tela SVG sono usciti di qui con lei (Node,
    // SelectByClick, ParseViewBox, ToCanvasPixels, ToViewDelta): parlavano di PlantCanvas e di
    // ViewBox, che non esistono piu'. Quello che resta e' la base comune di TUTTE le classi di
    // prova - la modalita' JS permissiva e i servizi - ed e' il motivo per cui questo file
    // sopravvive al vecchio Editor pur portandone ancora il nome.
}
