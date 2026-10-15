using System.Globalization;
using AngleSharp.Dom;

namespace Wcs.WebEditor.Test;

/// <summary>
/// La stessa conversione pixel-tela -&gt; unita'-disegno di <c>PlantCanvas.ScreenPixelsToViewBoxUnits</c>,
/// formattata come <c>PlantCanvas.Fmt</c> - una lunghezza, non un punto: nessuna banda da sottrarre, lo
/// stesso motivo per cui <c>DragTest.ToViewDelta</c> non la sottrae per un delta.
/// </summary>
/// <remarks>
/// Correzione: prima di questa correzione ogni suite che ne aveva bisogno portava la propria copia,
/// identica, con le dimensioni della tela (960x600) scritte a mano invece che lette dal markup reso -
/// due copie potevano divergere in silenzio se quelle dimensioni fossero cambiate. Qui una sola
/// copia, e le dimensioni si leggono dagli attributi <c>width</c>/<c>height</c> dell'<c>&lt;svg&gt;</c>
/// passato, non da una costante: l'<c>&lt;svg&gt;</c> che il test interroga le porta gia'. Scelta che
/// si e' rivelata utile anche oltre la ragione per cui e' nata: dal compito B3a quelle dimensioni non
/// sono nemmeno piu' una costante (<c>PlantCanvas._canvasPixelWidth</c>/<c>_canvasPixelHeight</c>,
/// scritte dalla misura vera del browser) - questo aiutante non ha dovuto cambiare di una riga,
/// perche' non aveva mai letto la costante direttamente.
/// </remarks>
internal static class ViewLengths
{
    public static string ToViewLength(IElement svg, double pixels)
    {
        var viewBoxParts = svg.GetAttribute("viewBox")!.Split(' ');
        var viewWidth = double.Parse(viewBoxParts[2], CultureInfo.InvariantCulture);
        var viewHeight = double.Parse(viewBoxParts[3], CultureInfo.InvariantCulture);
        var canvasWidth = double.Parse(svg.GetAttribute("width")!, CultureInfo.InvariantCulture);
        var canvasHeight = double.Parse(svg.GetAttribute("height")!, CultureInfo.InvariantCulture);
        var unitsPerPixel = Math.Max(viewWidth / canvasWidth, viewHeight / canvasHeight);
        return (pixels * unitsPerPixel).ToString("0.##", CultureInfo.InvariantCulture);
    }
}
