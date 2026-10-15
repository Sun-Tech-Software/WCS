using System.Text;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// L'estrattore della geometria dal disegno di Syncfusion WinForms.
/// </summary>
/// <remarks>
/// Il campione e' piccolo e fatto a mano: il disegno vero della condizione dimostrativa pesa 29 MB
/// e in un repository non ci va. Ha la stessa forma del disegno vero - stesso spazio dei nomi
/// DataContract, stesso gioco di <c>z:Id</c>/<c>z:Ref</c> per il Tag e per il PropertyBag, stesso
/// filtro <c>OrchestraType=ITEM</c>, e lo stesso <c>BoundingRect</c> copiato dal file vero: figli
/// minuscoli (<c>x</c>/<c>y</c>/<c>width</c>/<c>height</c>), in ordine alfabetico, con il proprio
/// <c>xmlns:a</c> di <c>System.Drawing</c> che ridefinisce il prefisso <c>a:</c> gia' usato altrove
/// per gli Arrays - e copre i casi che contano: l'elemento con il GUID e la geometria, l'elemento
/// senza Tag del tutto, il GUID senza geometria, il gruppo con un figlio che condivide il suo Guid
/// e il suo PropertyBag, la decorazione che non e' un dispositivo, e il riquadro con i nomi
/// sbagliati che non deve piazzare il dispositivo con quattro nulli silenziosi. La prova sul
/// disegno vero la fa il compito 8 e non e' un test automatico.
/// </remarks>
public sealed class DiagramGeometryReaderTest
{
    private const string Ns = "http://schemas.datacontract.org/2004/07/Syncfusion.Windows.Forms.Diagram";
    private const string ArraysNs = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";

    private static byte[] Sample(string body) => Encoding.UTF8.GetBytes(
        $"""
         <DiagramDocumentWrapper xmlns="{Ns}" xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
             xmlns:z="http://schemas.microsoft.com/2003/10/Serialization/" xmlns:a="{ArraysNs}">
           <Model><Nodes>{body}</Nodes></Model>
         </DiagramDocumentWrapper>
         """);

    /// <summary>Il PropertyBag di un dispositivo: <c>OrchestraType=ITEM</c>, come lo scrive l'Editor.</summary>
    private static string ItemBag(string? zId = null) =>
        $"""
         <PropertyBag{(zId == null ? "" : $" z:Id=\"{zId}\"")}>
           <a:KeyValueOfstringanyType>
             <a:Key>OrchestraType</a:Key>
             <a:Value i:type="a:string">ITEM</a:Value>
           </a:KeyValueOfstringanyType>
         </PropertyBag>
         """;

    /// <summary>Il PropertyBag condiviso: un figlio del gruppo che rimanda al bag gia' scritto.</summary>
    private static string BagRef(string zId) => $"<PropertyBag z:Ref=\"{zId}\"/>";

    /// <summary>Il PropertyBag di una giunzione: non e' un dispositivo.</summary>
    private static string JunctionBag() =>
        """
        <PropertyBag>
          <a:KeyValueOfstringanyType>
            <a:Key>OrchestraType</a:Key>
            <a:Value i:type="a:string">JUNCTION</a:Value>
          </a:KeyValueOfstringanyType>
        </PropertyBag>
        """;

    private static string TagWithId(string zId, Guid itemId) => $"<Tag z:Id=\"{zId}\">{itemId}</Tag>";
    private static string TagRef(string zId) => $"<Tag z:Ref=\"{zId}\"/>";
    private static string TagNil() => "<Tag i:nil=\"true\"/>";

    private static string Node(string propertyBag, string? tag, string? rect, string? rotazione) =>
        $"""
         <Node>
           {propertyBag}
           {tag ?? ""}
           {rect ?? ""}
           {(rotazione == null ? "" : $"<RotationAngle>{rotazione}</RotationAngle>")}
         </Node>
         """;

    /// <summary>
    /// Il BoundingRect vero: figli minuscoli, in ordine alfabetico, nello spazio dei nomi di
    /// <c>System.Drawing</c> - copiato di peso dal disegno vero, non inventato. Il suo
    /// <c>xmlns:a</c> ridefinisce, solo dentro questo elemento, il prefisso <c>a:</c> gia' usato
    /// altrove per gli Arrays: e' il motivo per cui il lettore deve guardare il nome locale e non
    /// il prefisso.
    /// </summary>
    private static string Rect(double x, double y, double w, double h)
    {
        var i = System.Globalization.CultureInfo.InvariantCulture;
        return $"""
                <BoundingRect xmlns:a="http://schemas.datacontract.org/2004/07/System.Drawing"><a:height>{h.ToString(i)}</a:height><a:width>{w.ToString(i)}</a:width><a:x>{x.ToString(i)}</a:x><a:y>{y.ToString(i)}</a:y></BoundingRect>
                """;
    }

    /// <summary>
    /// Un riquadro presente ma con nomi che il lettore non riconosce affatto (non e' un problema
    /// di maiuscole): serve a provare che un BoundingRect illeggibile non piazza il dispositivo
    /// con quattro nulli silenziosi.
    /// </summary>
    private static string RectWithUnrecognizedNames() =>
        "<BoundingRect><Left>1</Left><Top>2</Top><Cx>3</Cx><Cy>4</Cy></BoundingRect>";

    [Fact]
    public void ReadsPositionSizeAndAngleOfADevice()
    {
        var itemId = Guid.NewGuid();
        var result = DiagramGeometryReader.Read(Sample(
            Node(ItemBag(), TagWithId("1", itemId), Rect(120.5, 340.25, 80, 20), "37.5")));

        Assert.Equal(new ItemGeometry(120.5, 340.25, 80, 20, 37.5), result.Geometries[itemId]);
        Assert.Equal(0, result.ElementsWithoutGuid);
        Assert.Equal(0, result.GuidsWithoutGeometry);
    }

    [Fact]
    public void AnElementWithANilTagIsCountedAndSkipped()
    {
        var itemId = Guid.NewGuid();
        var result = DiagramGeometryReader.Read(Sample(
            Node(ItemBag(), TagWithId("1", itemId), Rect(1, 2, 3, 4), "0") +
            Node(ItemBag(), TagNil(), Rect(9, 9, 9, 9), "0")));

        Assert.Single(result.Geometries);
        Assert.Equal(1, result.ElementsWithoutGuid);
    }

    [Fact]
    public void AnElementWithoutATagAtAllIsCountedAndSkipped()
    {
        // Il campione vecchio copriva solo il caso i:nil: nel disegno vero un dispositivo puo'
        // anche non avere l'elemento Tag del tutto.
        var itemId = Guid.NewGuid();
        var result = DiagramGeometryReader.Read(Sample(
            Node(ItemBag(), TagWithId("1", itemId), Rect(1, 2, 3, 4), "0") +
            Node(ItemBag(), tag: null, rect: Rect(9, 9, 9, 9), rotazione: "0")));

        Assert.Single(result.Geometries);
        Assert.Equal(1, result.ElementsWithoutGuid);
    }

    [Fact]
    public void AGuidWithoutABoundingRectIsCountedAndLeftOut()
    {
        var itemId = Guid.NewGuid();
        var result = DiagramGeometryReader.Read(Sample(
            Node(ItemBag(), TagWithId("1", itemId), null, null)));

        Assert.Empty(result.Geometries);
        Assert.Equal(1, result.GuidsWithoutGeometry);
    }

    [Fact]
    public void ABoundingRectWithUnrecognizedFieldNamesDoesNotSilentlyPlaceTheDevice()
    {
        // Il difetto vero: il lettore cercava X/Y/Width/Height e sul disegno vero, dove i nomi
        // sono minuscoli, non li trovava mai - restituendo quattro nulli come se il dispositivo
        // fosse collocato con posizione ignota, invece di dire onestamente che la geometria manca.
        // Un riquadro presente ma illeggibile deve contare nella stessa categoria di chi il
        // riquadro non lo porta affatto: GuidsWithoutGeometry, non Geometries con valori nulli.
        var itemId = Guid.NewGuid();
        var result = DiagramGeometryReader.Read(Sample(
            Node(ItemBag(), TagWithId("1", itemId), RectWithUnrecognizedNames(), "0")));

        Assert.Empty(result.Geometries);
        Assert.Equal(1, result.GuidsWithoutGeometry);
        Assert.Equal(0, result.ElementsWithoutGuid);
    }

    [Fact]
    public void AMissingAngleDoesNotBecomeZero()
    {
        // Regola di casa: nessuno zero che finge un dato. Un nodo senza angolo ha l'angolo nullo.
        var itemId = Guid.NewGuid();
        var result = DiagramGeometryReader.Read(Sample(
            Node(ItemBag(), TagWithId("1", itemId), Rect(1, 2, 3, 4), null)));

        Assert.Null(result.Geometries[itemId].Rotation);
    }

    [Fact]
    public void AGroupAndItsChildShareTheGuidAndTheGeometryCountsOnceAndOnlyAsPlaced()
    {
        // Il gruppo (bag con z:Id="b1", tag con z:Id="t1") non porta il riquadro - lo porta il
        // figlio, che condivide sia il PropertyBag (z:Ref="b1") sia il Tag (z:Ref="t1"). Prima
        // della riparazione il gruppo, incontrato per primo e senza riquadro, faceva scattare
        // GuidsWithoutGeometry; e la stessa geometria, trovata poi sul figlio, faceva anche
        // scattare Geometries: lo stesso dispositivo in due conti opposti.
        var itemId = Guid.NewGuid();
        var group = Node(ItemBag("b1"), TagWithId("t1", itemId), rect: null, rotazione: null);
        var child = Node(BagRef("b1"), TagRef("t1"), Rect(5, 6, 7, 8), "12");

        var result = DiagramGeometryReader.Read(Sample(group + child));

        Assert.Equal(new ItemGeometry(5, 6, 7, 8, 12), result.Geometries[itemId]);
        Assert.Equal(0, result.GuidsWithoutGeometry);
        Assert.Equal(0, result.ElementsWithoutGuid);
    }

    [Fact]
    public void ANonItemElementIsNotCountedAtAll()
    {
        // Una giunzione (o una decorazione) con un Tag nil non e' un dispositivo mancante: non e'
        // un dispositivo. Un contatore che la includesse sarebbe gonfiato e illeggibile.
        var itemId = Guid.NewGuid();
        var result = DiagramGeometryReader.Read(Sample(
            Node(ItemBag(), TagWithId("1", itemId), Rect(1, 2, 3, 4), "0") +
            Node(JunctionBag(), TagNil(), rect: null, rotazione: null)));

        Assert.Single(result.Geometries);
        Assert.Equal(0, result.ElementsWithoutGuid);
        Assert.Equal(0, result.GuidsWithoutGeometry);
    }

    [Fact]
    public void AnEmptyBlobDoesNotThrow()
    {
        var result = DiagramGeometryReader.Read([]);
        Assert.Empty(result.Geometries);
    }

    /// <summary>
    /// Correzione della revisione d'insieme: un riquadro con uno, due o tre dei quattro numeri
    /// leggibili (non tutti e quattro) entra fra i collocati con la geometria a meta' - una
    /// posizione parziale e' comunque piu' utile di nessuna - ma <see cref="DiagramGeometryResult.PartiallyPlaced"/>
    /// lo dice, perche' prima nessun contatore lo distingueva da un riquadro letto per intero.
    /// </summary>
    private static string RectWithOnlyXAndY(double x, double y)
    {
        var i = System.Globalization.CultureInfo.InvariantCulture;
        return $"""
                <BoundingRect xmlns:a="http://schemas.datacontract.org/2004/07/System.Drawing"><a:x>{x.ToString(i)}</a:x><a:y>{y.ToString(i)}</a:y></BoundingRect>
                """;
    }

    [Fact]
    public void ABoundingRectWithSomeButNotAllFourNumbersIsPlacedAndCountedAsPartial()
    {
        var itemId = Guid.NewGuid();
        var result = DiagramGeometryReader.Read(Sample(
            Node(ItemBag(), TagWithId("1", itemId), RectWithOnlyXAndY(10, 20), "0")));

        Assert.Equal(new ItemGeometry(10, 20, null, null, 0), result.Geometries[itemId]);
        Assert.Equal(0, result.GuidsWithoutGeometry);
        Assert.Equal(1, result.PartiallyPlaced);
    }

    [Fact]
    public void ABoundingRectWithAllFourNumbersIsNotCountedAsPartial()
    {
        var itemId = Guid.NewGuid();
        var result = DiagramGeometryReader.Read(Sample(
            Node(ItemBag(), TagWithId("1", itemId), Rect(1, 2, 3, 4), "0")));

        Assert.Single(result.Geometries);
        Assert.Equal(0, result.PartiallyPlaced);
    }

    [Fact]
    public void AGroupWithAPartialFirstOccurrenceAndACompleteLaterOneIsPlacedAsComplete()
    {
        // Il difetto vero: fra piu' occorrenze dello stesso Guid la scelta era la prima con un
        // riquadro, parziale o no. Un gruppo la cui prima occorrenza porta un riquadro a meta' e
        // una successiva lo porta intero deve tenere quella intera - e non contare fra i
        // PartiallyPlaced, che altrimenti direbbe "letto solo in parte" di un dispositivo la cui
        // geometria completa era nello stesso disegno.
        var itemId = Guid.NewGuid();
        var partialFirst = Node(ItemBag(), TagWithId("1", itemId), RectWithOnlyXAndY(10, 20), "0");
        var completeLater = Node(ItemBag(), TagWithId("2", itemId), Rect(1, 2, 3, 4), "0");

        var result = DiagramGeometryReader.Read(Sample(partialFirst + completeLater));

        Assert.Equal(new ItemGeometry(1, 2, 3, 4, 0), result.Geometries[itemId]);
        Assert.Equal(0, result.PartiallyPlaced);
    }
}
