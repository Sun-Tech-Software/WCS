using Wcs.WebEditor.Drawing;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="NodePlacement.Envelope"/> e <see cref="NodePlacement.Rotate"/> (compito B4): l'andata
/// che i gesti di ridimensionamento e rotazione con maniglia devono usare per ricostruire il
/// riquadro migrato (<c>Width</c>/<c>Height</c> di <c>PlantItemDto</c>) da dimensione propria e
/// rotazione, senza mai spostare il riquadro della stessa quantita' della dimensione propria - la
/// trappola misurata tre volte in questa tappa.
/// </summary>
/// <remarks>
/// Gli stessi cinque riquadri di <c>NodePlacementTest</c> (rotazione -180, -90, 0, 35, 90), letti
/// davvero da Syncfusion su un rettangolo proprio 80x20: <see cref="NodePlacement.Envelope"/> deve
/// tornare al riquadro migrato di partenza quando applicato alla dimensione propria che
/// <see cref="NodePlacement.LocalSize"/> ricava da quello stesso riquadro - un giro di andata e
/// ritorno, non un valore inventato.
/// </remarks>
public sealed class NodePlacementEnvelopeTest
{
    [Theory]
    [InlineData(100, 50, 80, 20, 0)]
    [InlineData(129.99998, 19.999996, 20, 79.999985, 90)]
    [InlineData(100, 49.999992, 80, 20, 180)]
    [InlineData(129.99997, 19.999985, 20.000046, 80.000015, -90)]
    [InlineData(101.49815, 28.865425, 77.00368, 62.269142, 35)]
    public void EnvelopeRoundTripsWithLocalSize(double x, double y, double width, double height, double rotation)
    {
        _ = x;
        _ = y;
        var (width0, height0) = NodePlacement.LocalSize(width, height, rotation);
        var (envelopeWidth, envelopeHeight) = NodePlacement.Envelope(width0, height0, rotation);

        Assert.Equal(width, envelopeWidth, 1);
        Assert.Equal(height, envelopeHeight, 1);
    }

    [Fact]
    public void RotatingA90DegreeMultipleSwapsTheEnvelopeWithoutChangingOwnSize()
    {
        // La stessa dimensione propria (80x20), solo la rotazione cambia: l'inviluppo scambia
        // larghezza e altezza a 90 gradi, non le lascia invariate - il secondo effetto diretto
        // della trappola ("ruotare cambia Width e Height anche a dimensione invariata").
        var (widthAt0, heightAt0) = NodePlacement.Envelope(80, 20, 0);
        var (widthAt90, heightAt90) = NodePlacement.Envelope(80, 20, 90);

        Assert.Equal(80, widthAt0, 6);
        Assert.Equal(20, heightAt0, 6);
        Assert.Equal(20, widthAt90, 6);
        Assert.Equal(80, heightAt90, 6);
    }

    [Fact]
    public void ResizingAt35DegreesDoesNotMoveTheEnvelopeByTheSameAmountAsTheOwnSize()
    {
        // Il primo effetto diretto della trappola: a un angolo non retto un cambio di dimensione
        // propria non sposta l'inviluppo della stessa quantita'. Da 80x20 a 100x20 (solo la
        // larghezza propria cresce di 20) a 35 gradi l'inviluppo NON cresce di 20 su entrambi gli
        // assi - la formula sbagliata (Width += 20, Height invariata) e quella giusta
        // (ricalcolare da capo con Envelope) danno risultati diversi proprio perche' la rotazione
        // non e' zero e il riquadro non e' quadrato.
        var (widthBefore, heightBefore) = NodePlacement.Envelope(80, 20, 35);
        var (widthAfter, heightAfter) = NodePlacement.Envelope(100, 20, 35);

        var wrongWidth = widthBefore + 20;
        var wrongHeight = heightBefore;

        Assert.NotEqual(wrongWidth, widthAfter, 1);
        Assert.NotEqual(wrongHeight, heightAfter, 1);
    }

    [Fact]
    public void RotateAppliesTheSameMatrixWorldPointUsesForAPortOnTheEdge()
    {
        // Stessa composizione di WorldPoint (una porta "out" sul bordo destro, riquadro 80x20,
        // rotazione 35): Rotate esposto separatamente deve dare lo stesso spostamento dal centro
        // che WorldPoint calcola internamente, non una matrice diversa.
        var (portX, portY) = NodePlacement.WorldPoint(100, 50, 101.49815, 28.865425, 77.00368, 62.269142, 35);
        var (centerX, centerY) = NodePlacement.WorldCenter(101.49815, 28.865425, 77.00368, 62.269142);
        var (width0, height0) = NodePlacement.LocalSize(77.00368, 62.269142, 35);

        var (rotatedX, rotatedY) = NodePlacement.Rotate((100 - 50) * width0 / 100, (50 - 50) * height0 / 100, 35);

        Assert.Equal(portX, centerX + rotatedX, 6);
        Assert.Equal(portY, centerY + rotatedY, 6);
    }

    /// <summary>
    /// I due riquadri interi (compito 4): <see cref="NodePlacement.OwnBox"/> porta il riquadro
    /// migrato nello spazio di chi ruota da se' - draw.io, la tela SVG - e
    /// <see cref="NodePlacement.EnvelopeBox"/> lo riporta indietro. Il giro completo deve tornare
    /// al punto di partenza su tutti e quattro i numeri, non solo sulla dimensione: e' su questo
    /// giro che poggia il delta, che confronta all'andata e riscrive l'impianto al ritorno. Un
    /// ritorno che sbaglia di poco non si vede al primo salvataggio e sposta i dispositivi al
    /// centesimo a ogni salvataggio successivo.
    /// </summary>
    [Theory]
    [InlineData(10, 20, 130, 184, 0)]
    [InlineData(10, 20, 130, 184, 35)]
    [InlineData(10, 20, 130, 184, -90)]
    [InlineData(10, 20, 130, 184, 90)]
    [InlineData(10, 20, 130, 184, -180)]
    [InlineData(101.49815, 28.865425, 77.00368, 62.269142, 35)]
    public void OwnBoxAndEnvelopeBoxAreATrueRoundTrip(double x, double y, double width, double height, double rotation)
    {
        var own = NodePlacement.OwnBox(x, y, width, height, rotation);
        var back = NodePlacement.EnvelopeBox(own.X, own.Y, own.Width, own.Height, rotation);

        Assert.Equal(x, back.X, 6);
        Assert.Equal(y, back.Y, 6);
        Assert.Equal(width, back.Width, 6);
        Assert.Equal(height, back.Height, 6);
    }

    /// <summary>
    /// Il caso in cui il giro NON torna, misurato invece che dato per scontato: a 45 gradi
    /// <see cref="NodePlacement.LocalSize"/> non sa invertire l'inviluppo e
    /// <see cref="NodePlacement.OwnBox"/> ripiega sul riquadro cosi' com'e', percio' il ritorno
    /// restituisce un riquadro piu' GRANDE di quello di partenza - un dispositivo che ingrassa di
    /// un fattore radice di 2 a ogni salvataggio. E' il motivo per cui <c>DrawioDelta</c> chiede
    /// <see cref="NodePlacement.IsDegenerateRotation"/> prima di riscrivere, invece di fidarsi.
    /// </summary>
    [Fact]
    public void AtFortyFiveDegreesTheRoundTripDoesNotCloseAndTheCallerMustAsk()
    {
        Assert.True(NodePlacement.IsDegenerateRotation(45));

        var own = NodePlacement.OwnBox(10, 20, 100, 100, 45);
        var back = NodePlacement.EnvelopeBox(own.X, own.Y, own.Width, own.Height, 45);

        Assert.Equal(100 * Math.Sqrt(2), back.Width, 6);
        Assert.NotEqual(100, back.Width, 1);
    }
}
