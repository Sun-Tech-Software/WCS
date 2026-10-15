using Wcs.WebEditor.Drawing;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// La correzione della revisione: il riquadro migrato da <c>PlantItemDto</c> (X,Y,Width,Height) e'
/// l'AABB di Syncfusion **dopo** la rotazione, non le dimensioni proprie del nodo, e Syncfusion
/// ruota attorno al centro di quel riquadro, non attorno all'angolo in alto a sinistra.
/// </summary>
/// <remarks>
/// I quattro riquadri usati come dati di partenza (rotazione 0, 90, 180, -90) non sono inventati:
/// sono la lettura vera di <c>Syncfusion.Diagram.Windows 34.2.7</c> (la versione di
/// <c>Wcs.Editor.csproj</c>) dopo aver costruito <c>new Rectangle(new RectangleF(100, 50, 80, 20))</c>
/// e impostato <c>RotationAngle</c>, serializzato con <c>ISerializable.GetObjectData</c> - lo stesso
/// meccanismo che scrive il campo <c>BoundingRect</c> che <c>DiagramGeometryReader</c> legge dal
/// disegno vero. Il caso a 35 gradi e' lo stesso rettangolo 80x20, e riproduce l'unico angolo non
/// retto visto sui 51 dispositivi del disegno dimostrativo.
/// </remarks>
public sealed class NodePlacementTest
{
    [Theory]
    [InlineData(100, 50, 80, 20, 0)]
    [InlineData(129.99998, 19.999996, 20, 79.999985, 90)]
    [InlineData(100, 49.999992, 80, 20, 180)]
    [InlineData(129.99997, 19.999985, 20.000046, 80.000015, -90)]
    [InlineData(101.49815, 28.865425, 77.00368, 62.269142, 35)]
    public void TheShapeEndsUpCenteredOnThePinPointNotOnTheCorner(
        double x, double y, double width, double height, double rotation)
    {
        // Il centro del riquadro migrato e' invariante rispetto alla rotazione - e' il PinPoint di
        // Syncfusion - qualunque sia l'angolo (qui solo per documentare quale, tra i cinque
        // riquadri, appartiene a quale rotazione): (140, 60) in tutti e cinque i casi.
        _ = rotation;
        var (centerX, centerY) = NodePlacement.WorldCenter(x, y, width, height);
        Assert.Equal(140, centerX, 1);
        Assert.Equal(60, centerY, 1);
    }

    [Theory]
    [InlineData(100, 50, 80, 20, 0, 80, 20)]
    [InlineData(129.99998, 19.999996, 20, 79.999985, 90, 80, 20)]
    [InlineData(100, 49.999992, 80, 20, 180, 80, 20)]
    [InlineData(129.99997, 19.999985, 20.000046, 80.000015, -90, 80, 20)]
    [InlineData(101.49815, 28.865425, 77.00368, 62.269142, 35, 80, 20)]
    public void LocalSizeRecoversTheDimensionsBeforeTheRotation(
        double x, double y, double width, double height, double rotation,
        double expectedWidth0, double expectedHeight0)
    {
        // Le dimensioni proprie sono quelle che Syncfusion aveva PRIMA di ruotare - 80x20 in ogni
        // caso - non il riquadro (Width, Height) migrato, che a 90/-90 le scambia e a 35 le gonfia.
        // x e y non servono a LocalSize (dipende solo da Width, Height e Rotation): restano nella
        // firma solo per riusare lo stesso quintetto di dati degli altri test di questo file.
        _ = x;
        _ = y;
        var (width0, height0) = NodePlacement.LocalSize(width, height, rotation);
        Assert.Equal(expectedWidth0, width0, 0);
        Assert.Equal(expectedHeight0, height0, 0);
    }

    [Theory]
    [InlineData(100, 50, 80, 20, 0, 180, 60)]
    [InlineData(129.99998, 19.999996, 20, 79.999985, 90, 140, 100)]
    [InlineData(100, 49.999992, 80, 20, 180, 100, 60)]
    [InlineData(129.99997, 19.999985, 20.000046, 80.000015, -90, 140, 20)]
    [InlineData(101.49815, 28.865425, 77.00368, 62.269142, 35, 172.8, 82.9)]
    // -180: stesso riquadro del caso a 180 (un rettangolo ruotato di mezzo giro attorno al proprio
    // centro torna sempre sullo stesso AABB, qualunque sia il verso), ma qui - a differenza di
    // TheShapesOwnCenterMapsExactlyToTheWorldCenterAtAnyRotation, che usa il punto centrale (50,50)
    // e passerebbe anche con una matrice di rotazione sbagliata (dx=dy=0 la rende quasi
    // tautologica) - il punto della porta non e' centrale (100,50): dx=40 esercita davvero cos/sin
    // di -180, non solo l'invarianza del centro. Atteso calcolato con la stessa composizione degli
    // altri casi, non copiato dal caso 180: (100, 60) - identico perche' -180 e 180 sono la stessa
    // rotazione fisica, a 360 gradi di distanza.
    [InlineData(100, 49.999992, 80, 20, -180, 100, 60)]
    public void APortEndsUpOnTheRotatedEdgeNotOffsetByTheCorner(
        double x, double y, double width, double height, double rotation,
        double expectedPortX, double expectedPortY)
    {
        // Porta "out" sul bordo destro, a meta' altezza (100, 50) nel riquadro unitario del tipo:
        // con le dimensioni proprie 80x20 e il centro a (140, 60), deve finire sul bordo della
        // sagoma ruotata - non spostata della quantita' che il riquadro dell'angolo introdurrebbe.
        var (portX, portY) = NodePlacement.WorldPoint(100, 50, x, y, width, height, rotation);
        Assert.Equal(expectedPortX, portX, 1);
        Assert.Equal(expectedPortY, portY, 1);
    }

    [Fact]
    public void TheShapesOwnCenterMapsExactlyToTheWorldCenterAtAnyRotation()
    {
        // Il punto (50,50) del riquadro unitario - il centro della sagoma - deve mappare
        // esattamente sul centro nel mondo, qualunque sia l'angolo: e' il punto che non si sposta
        // mai quando si ruota attorno al proprio centro.
        foreach (var rotation in new double[] { -180, -90, 0, 35, 90 })
        {
            var (x, y) = NodePlacement.WorldPoint(50, 50, 10, 20, 80, 20, rotation);
            Assert.Equal(10 + 40, x, 6);
            Assert.Equal(20 + 10, y, 6);
        }
    }

    [Theory]
    [InlineData(45)]
    [InlineData(-45)]
    [InlineData(135)]
    [InlineData(225)]
    [InlineData(-135)]
    public void RotationsAt45DegreeMultiplesAreDegenerate(double rotation) =>
        Assert.True(NodePlacement.IsDegenerateRotation(rotation));

    [Theory]
    [InlineData(-180)]
    [InlineData(-90)]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(90)]
    public void RotationsAwayFrom45DegreeMultiplesAreNotDegenerate(double rotation) =>
        Assert.False(NodePlacement.IsDegenerateRotation(rotation));

    [Fact]
    public void ADegenerateRotationMakesLocalSizeFallBackToTheMigratedRectangleUnchanged()
    {
        // A 45 gradi il determinante si annulla: LocalSize non inverte piu' nulla e ripiega sul
        // riquadro migrato cosi' com'e' - lo stesso comportamento sbagliato di prima della
        // correzione (l'AABB al posto delle dimensioni proprie), ma ora IsDegenerateRotation lo
        // rende scopribile dal chiamante invece di restare un ripiego muto.
        Assert.True(NodePlacement.IsDegenerateRotation(45));
        var (width0, height0) = NodePlacement.LocalSize(80, 20, 45);
        Assert.Equal(80, width0, 6);
        Assert.Equal(20, height0, 6);
    }

    [Fact]
    public void AZeroWidthWithAPartialGeometryCanProduceANegativeLocalSizeAMirroredShape()
    {
        // DiagramGeometryReader ammette esplicitamente geometrie parziali (uno o piu' dei quattro
        // numeri del riquadro mancanti finiscono a zero in PlantCanvas.Transform via `?? 0`): un
        // riquadro con width a zero, a una rotazione non degenere (35 gradi, l'unico angolo non
        // retto visto sui 51 dispositivi veri), non produce un fallback ne' un'eccezione, ma una
        // dimensione propria negativa - una sagoma specchiata, non invisibile e non segnalata.
        var (width0, _) = NodePlacement.LocalSize(0, 100, 35);
        Assert.True(width0 < 0, $"width0 atteso negativo (sagoma specchiata), trovato {width0}");
    }
}
