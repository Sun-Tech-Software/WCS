using System.Globalization;
using System.Text.RegularExpressions;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Un parser e un riempitore SVG ridotti all'osso, solo per <c>PlantCanvasTest</c> (compito B2a,
/// seconda correzione): non e' pensato per disegnare, solo per rispondere a due domande su un
/// tracciato - il suo riquadro, e se un punto dato cade dentro il riempimento con la regola
/// "non-zero" che un browser userebbe. Comandi gestiti: <c>M</c>, <c>H</c>, <c>V</c>, <c>L</c>,
/// <c>A</c>, <c>Z</c> - i cinque che <c>Wcs.Engine.Api.DeviceTypeRegistry</c> scrive oggi, piu' <c>L</c>
/// per le due diagonali della X di <c>PlantCanvas.UnknownDeviceType</c> (locale a questa tela, non nel
/// registro); un comando diverso solleva, cosi' un tracciato futuro che ne usasse uno non gestito
/// fallisce rumorosamente invece di essere ignorato in silenzio.
/// <para>
/// Duplicato di proposito, non condiviso: e' lo stesso codice di
/// <c>Wcs.Engine.Test.SvgPathFill</c>, ripetuto perche' <c>Wcs.WebEditor.Test</c> non puo'
/// referenziare <c>Wcs.Engine</c> ne' i suoi test (il confine e' <c>BoundaryTest</c>, che guarda
/// l'assembly di produzione <c>Wcs.WebEditor</c>, non questo progetto di test - ma condividere questo
/// file fra i due progetti richiederebbe comunque un riferimento che quel confine non vuole). La
/// stessa duplicazione gia' dichiarata per <c>AssertArcSweepFlagsAgree</c> prima di questa
/// correzione, per la stessa ragione.
/// </para>
/// </summary>
internal static class SvgPathFill
{
    public static List<List<(double X, double Y)>> ParseToPolygons(string d)
    {
        var tokens = Tokenize(d);
        var subpaths = new List<List<(double X, double Y)>>();
        List<(double X, double Y)>? current = null;
        double x = 0, y = 0;
        var i = 0;

        while (i < tokens.Count)
        {
            var cmd = tokens[i++];
            switch (cmd)
            {
                case "M":
                    x = ReadNumber(tokens, ref i);
                    y = ReadNumber(tokens, ref i);
                    current = [(x, y)];
                    subpaths.Add(current);
                    break;
                case "H":
                    x = ReadNumber(tokens, ref i);
                    Current(current).Add((x, y));
                    break;
                case "V":
                    y = ReadNumber(tokens, ref i);
                    Current(current).Add((x, y));
                    break;
                case "L":
                    x = ReadNumber(tokens, ref i);
                    y = ReadNumber(tokens, ref i);
                    Current(current).Add((x, y));
                    break;
                case "A":
                    var rx = ReadNumber(tokens, ref i);
                    var ry = ReadNumber(tokens, ref i);
                    _ = ReadNumber(tokens, ref i); // x-axis-rotation: sempre 0 nel registro di oggi, si veda AppendArc
                    var largeArc = ReadNumber(tokens, ref i) != 0;
                    var sweep = ReadNumber(tokens, ref i) != 0;
                    var endX = ReadNumber(tokens, ref i);
                    var endY = ReadNumber(tokens, ref i);
                    AppendArc(Current(current), x, y, rx, ry, largeArc, sweep, endX, endY);
                    x = endX;
                    y = endY;
                    break;
                case "Z":
                    // Chiusura implicita: WindingNumber collega gia' l'ultimo punto della poligonale
                    // al primo, non serve aggiungere un punto di chiusura esplicito.
                    break;
                default:
                    throw new InvalidOperationException(
                        $"SvgPathFill non gestisce il comando '{cmd}' (tracciato: \"{d}\") - e' un parser di test, non di produzione.");
            }
        }

        return subpaths;
    }

    private static List<(double X, double Y)> Current(List<(double X, double Y)>? current) =>
        current ?? throw new InvalidOperationException("comando di disegno prima di un M: nessun sotto-tracciato aperto");

    public static (double MinX, double MinY, double MaxX, double MaxY) BoundingBox(List<List<(double X, double Y)>> subpaths)
    {
        var points = subpaths.SelectMany(p => p).ToList();
        return (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    /// <summary>Regola di riempimento "non-zero", la stessa che SVG usa quando <c>fill-rule</c> non e' dichiarato (nessuna sagoma del registro lo dichiara).</summary>
    public static bool IsPointFilled(List<List<(double X, double Y)>> subpaths, double px, double py)
    {
        var winding = 0;
        foreach (var polygon in subpaths)
        {
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                if (a.Y <= py)
                {
                    if (b.Y > py && IsLeft(a, b, px, py) > 0) winding++;
                }
                else
                {
                    if (b.Y <= py && IsLeft(a, b, px, py) < 0) winding--;
                }
            }
        }

        return winding != 0;
    }

    private static double IsLeft((double X, double Y) a, (double X, double Y) b, double px, double py) =>
        (b.X - a.X) * (py - a.Y) - (px - a.X) * (b.Y - a.Y);

    private static List<string> Tokenize(string d)
    {
        // Le virgole sono un separatore legale in SVG quanto lo spazio (si veda il controesempio
        // della revisione, "A 42,42 0 1,1 ..."): normalizzarle a spazi prima di leggere i numeri
        // evita di doverle riconoscere in ogni punto del parser.
        var normalized = d.Replace(',', ' ');
        return Regex.Matches(normalized, @"[MHVLAZmhvlaz]|-?\d*\.?\d+(?:[eE][-+]?\d+)?")
            .Select(m => m.Value)
            .ToList();
    }

    private static double ReadNumber(List<string> tokens, ref int i) =>
        double.Parse(tokens[i++], CultureInfo.InvariantCulture);

    /// <summary>
    /// Converte un comando <c>A</c> (arco ellittico, parametrizzato dagli estremi) in una poligonale,
    /// con la conversione da estremi a centro della specifica SVG 1.1 (appendice F.6.5) -
    /// semplificata per <c>x-axis-rotation = 0</c>: nessun tracciato del registro di oggi ruota
    /// l'ellisse stessa (la rotazione del dispositivo ruota il &lt;g&gt; del nodo, non l'arco dentro
    /// il suo riquadro unitario), quindi il termine che quella rotazione introdurrebbe sarebbe sempre
    /// l'identita' - ometterlo tiene il parser piu' semplice senza perdere nessun caso che questo
    /// registro possa scrivere oggi.
    /// </summary>
    private static void AppendArc(List<(double X, double Y)> points, double x1, double y1,
        double rx, double ry, bool largeArc, bool sweep, double x2, double y2)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            points.Add((x2, y2));
            return;
        }

        var x1P = (x1 - x2) / 2;
        var y1P = (y1 - y2) / 2;

        var lambda = x1P * x1P / (rx * rx) + y1P * y1P / (ry * ry);
        if (lambda > 1)
        {
            var scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        var sign = largeArc != sweep ? 1 : -1;
        var numerator = rx * rx * ry * ry - rx * rx * y1P * y1P - ry * ry * x1P * x1P;
        var denominator = rx * rx * y1P * y1P + ry * ry * x1P * x1P;
        var co = sign * Math.Sqrt(Math.Max(0, numerator) / denominator);
        var cxP = co * rx * y1P / ry;
        var cyP = -co * ry * x1P / rx;

        var cx = cxP + (x1 + x2) / 2;
        var cy = cyP + (y1 + y2) / 2;

        var theta1 = AngleBetween(1, 0, (x1P - cxP) / rx, (y1P - cyP) / ry);
        var deltaTheta = AngleBetween((x1P - cxP) / rx, (y1P - cyP) / ry, (-x1P - cxP) / rx, (-y1P - cyP) / ry);
        if (!sweep && deltaTheta > 0) deltaTheta -= 2 * Math.PI;
        if (sweep && deltaTheta < 0) deltaTheta += 2 * Math.PI;

        const int segments = 64; // basta a restare fedele a un cerchio per questa prova su un punto
        for (var s = 1; s <= segments; s++)
        {
            var theta = theta1 + deltaTheta * s / segments;
            points.Add((cx + rx * Math.Cos(theta), cy + ry * Math.Sin(theta)));
        }
    }

    private static double AngleBetween(double ux, double uy, double vx, double vy)
    {
        var dot = ux * vx + uy * vy;
        var length = Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy);
        var angle = Math.Acos(Math.Clamp(dot / length, -1, 1));
        return ux * vy - uy * vx < 0 ? -angle : angle;
    }
}
