using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>Che cosa e' stato trovato nel disegno, compreso quello che non e' entrato.</summary>
/// <param name="PartiallyPlaced">
/// Quanti dei <see cref="Geometries"/> portano un riquadro letto solo in parte (uno, due o tre dei
/// quattro numeri <c>x</c>/<c>y</c>/<c>width</c>/<c>height</c>, mai tutti e quattro): entrano fra i
/// collocati con la geometria a meta', e questo e' l'unico modo in cui il verbale lo dice - dopo la
/// marcatura non c'e' una seconda occasione per accorgersene.
/// </param>
public sealed record DiagramGeometryResult(
    IReadOnlyDictionary<Guid, ItemGeometry> Geometries,
    int ElementsWithoutGuid,
    int GuidsWithoutGeometry,
    int PartiallyPlaced);

/// <summary>
/// Estrae dal disegno di Syncfusion WinForms la collocazione di ogni dispositivo. Funzione pura:
/// prende i byte del blob e restituisce il risultato, senza toccare database ne' Navigator.
/// </summary>
/// <remarks>
/// Il blob e' XML serializzato con DataContract: <b>non serve Syncfusion per leggerlo</b>, bastano
/// il <c>Tag</c> (che porta il GUID del dispositivo, si veda
/// <c>Wcs.Editor/src/Managers/DiagramHandler.Private.cs:290</c>), il <c>BoundingRect</c> e il
/// <c>RotationAngle</c>. I connettori non si guardano: nel disegno vero non c'e' un solo
/// <c>LinePoints</c> valorizzato, e le giunzioni stanno gia' in <c>PlantJunctions</c>.
/// <para>
/// Il serializzatore DataContract scrive ogni oggetto una volta sola: la prima occorrenza porta
/// <c>z:Id</c>, le successive un <c>z:Ref</c> che rimanda a quel numero. Vale per il <c>Tag</c> (il
/// Guid e' un oggetto condiviso, non ripetuto) e per il <c>PropertyBag</c>. Nel disegno vero
/// nessun <c>Tag</c> porta un Guid leggibile alla lettera: sono tutti <c>i:nil</c> oppure
/// <c>z:Ref</c>. Per questo i nodi si raccolgono tutti in una prima passata e si risolvono in una
/// seconda - non si risolve mentre si scorre - con la stessa tecnica dell'importatore interno degli impianti
/// (non pubblicato), che questo formato l'ha gia' affrontato per le giunzioni.
/// </para>
/// <para>
/// Non ogni <c>Tag</c> e' un dispositivo: il disegno porta anche le giunzioni e le decorazioni
/// (centinaia di nodi <c>i:nil</c> che non sono ne' l'uno ne' l'altro). Si filtra dal
/// <c>PropertyBag</c> - <c>OrchestraType</c> o, in sua assenza, <c>DiagramType</c>, uguale a
/// <c>ITEM</c> senza guardare le maiuscole - esattamente come fa l'Editor in
/// <c>DiagramManager.ReadDiagramType</c>. Senza questo filtro i due contatori onesti diventerebbero
/// un verbale illeggibile, gonfiato dalle decorazioni che non sono mai stati un dispositivo.
/// </para>
/// <para>
/// Un dispositivo e' spesso un gruppo con dei figli, e il gruppo e i figli condividono lo stesso
/// <c>Tag</c> (lo stesso Guid, con lo stesso gioco di <c>z:Id</c>/<c>z:Ref</c>) - e talvolta anche
/// lo stesso <c>PropertyBag</c>. Percio' la decisione "collocato o mancante" non si prende alla
/// prima occorrenza incontrata: si raccolgono tutte le occorrenze di un Guid, e quel Guid e' "senza
/// geometria" solo se <b>nessuna</b> delle sue occorrenze porta un riquadro; se almeno una lo porta,
/// quel Guid e' collocato e basta - mai anche fra i mancanti allo stesso tempo. Fra le occorrenze
/// con un riquadro vince quella completa, se ce n'e' una; altrimenti si ripiega sulla prima
/// occorrenza parziale incontrata - una posizione a meta' e' comunque meglio di nessuna, ma non deve
/// scavalcare una geometria intera trovata su un'altra occorrenza dello stesso dispositivo.
/// </para>
/// I due contatori esistono perche' una migrazione che gira una volta sola in casa d'altri deve
/// poter dire che cosa non ha portato via.
/// <para>
/// I quattro numeri del riquadro non si chiamano <c>X</c>/<c>Y</c>/<c>Width</c>/<c>Height</c>
/// come ci si aspetterebbe da un nodo del disegno: sul file vero sono <c>x</c>/<c>y</c>/
/// <c>width</c>/<c>height</c> minuscoli, in un altro spazio dei nomi
/// (<c>2004/07/System.Drawing</c>) e in ordine alfabetico. Non e' una scelta del disegno: e'
/// <c>RectangleF</c> di <c>System.Drawing</c> serializzato con i nomi dei suoi campi, un tipo
/// che non appartiene a Syncfusion. <c>RotationAngle</c> invece e' scritto dal nodo del disegno
/// stesso, in PascalCase come il resto. Per questo il confronto dei nomi ignora le maiuscole:
/// senza, i quattro numeri del riquadro risultavano sempre nulli, in silenzio, e i 51
/// dispositivi del disegno vero tornavano tutti "senza posizione" pur avendo un riquadro.
/// </para>
/// </remarks>
public static class DiagramGeometryReader
{
    public static DiagramGeometryResult Read(byte[] diagramXml)
    {
        if (diagramXml.Length == 0)
            return new DiagramGeometryResult(new Dictionary<Guid, ItemGeometry>(), 0, 0, 0);

        using var stream = new MemoryStream(diagramXml);
        return Read(stream);
    }

    /// <summary>
    /// Come <see cref="Read(byte[])"/>, da uno stream invece che da un array gia' in memoria
    /// (correzione compito 12, Importante 4 della revisione): <c>POST
    /// /plant/graph/legacy-geometry</c> lo chiama passando il corpo della richiesta cosi' com'e',
    /// senza prima bufferizzarlo in un <c>MemoryStream</c> e poi in un secondo array con
    /// <c>ToArray()</c> - il commento di quella rotta gia' prometteva un solo giro sui byte, questo
    /// e' quello che lo mantiene vero. Non gestisce uno stream vuoto come collocazione zero
    /// (<see cref="XDocument.Load(Stream)"/> solleverebbe su un XML vuoto): quel caso resta di
    /// <see cref="Read(byte[])"/>, l'unico chiamato con un blob potenzialmente vuoto (il disegno
    /// migrato, si veda <c>GeometryMigration</c>).
    /// </summary>
    public static DiagramGeometryResult Read(Stream diagramXml)
    {
        var document = XDocument.Load(diagramXml);

        var byId = IndexById(document);

        // Prima passata: ogni occorrenza di un dispositivo (il gruppo, ciascun figlio), col Guid
        // risolto fra z:Ref e z:Id, e il riquadro se questa particolare occorrenza lo porta - con
        // il segno di quanti dei suoi quattro numeri si sono letti davvero.
        var occurrences = new List<(Guid ItemId, ItemGeometry? Geometry, bool Partial)>();
        var elementsWithoutGuid = 0;

        foreach (var element in document.Descendants())
        {
            if (!IsDevice(element, byId)) continue;

            var tag = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Tag");
            var text = tag == null ? null : TextOf(tag, byId);

            if (!Guid.TryParse(text, out var itemId) || itemId == Guid.Empty)
            {
                elementsWithoutGuid++;
                continue;
            }

            var rect = element.Elements().FirstOrDefault(e => e.Name.LocalName == "BoundingRect");
            var (geometry, partial) = ReadGeometry(rect, element);
            occurrences.Add((itemId, geometry, partial));
        }

        // Seconda passata: un Guid e' collocato se una qualsiasi delle sue occorrenze porta un
        // riquadro (vale la prima incontrata), mancante solo se nessuna lo porta - mai le due cose
        // insieme per lo stesso dispositivo.
        var geometries = new Dictionary<Guid, ItemGeometry>();
        var guidsWithoutGeometry = 0;
        var partiallyPlaced = 0;

        foreach (var group in occurrences.GroupBy(o => o.ItemId))
        {
            var withGeometry = group.Where(o => o.Geometry != null).ToList();
            if (withGeometry.Count == 0)
            {
                guidsWithoutGeometry++;
                continue;
            }

            // Fra le occorrenze con un riquadro vince quella completa, se ce n'e' una; solo se
            // sono tutte parziali si ripiega sulla prima incontrata - mai una parziale trovata
            // prima scavalca una geometria intera trovata dopo, sullo stesso dispositivo.
            var complete = withGeometry.FirstOrDefault(o => !o.Partial);
            var chosen = complete.Geometry != null ? complete : withGeometry[0];

            geometries[group.Key] = chosen.Geometry!;
            if (chosen.Partial) partiallyPlaced++;
        }

        return new DiagramGeometryResult(geometries, elementsWithoutGuid, guidsWithoutGeometry, partiallyPlaced);
    }

    /// <summary>
    /// Un dispositivo, secondo il <c>PropertyBag</c>: <c>OrchestraType</c> o, in sua assenza,
    /// <c>DiagramType</c>, uguale a <c>ITEM</c> senza guardare le maiuscole - come fa
    /// <c>DiagramManager.ReadDiagramType</c> nell'Editor. Le giunzioni (<c>JUNCTION</c>) e le
    /// decorazioni senza PropertyBag restano fuori.
    /// </summary>
    private static bool IsDevice(XElement element, Dictionary<string, XElement> byId)
    {
        var type = PropertyBagValue(element, "OrchestraType", byId) ?? PropertyBagValue(element, "DiagramType", byId);
        return string.Equals(type, "ITEM", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Il valore di una chiave nel PropertyBag del nodo, seguendo lo z:Ref quando il bag e' condiviso con un altro nodo.</summary>
    private static string? PropertyBagValue(XElement element, string propertyName, Dictionary<string, XElement> byId)
    {
        var bag = element.Elements().FirstOrDefault(e => e.Name.LocalName == "PropertyBag");
        if (bag == null) return null;

        var bagReference = bag.Attributes().FirstOrDefault(a => a.Name.LocalName == "Ref")?.Value;
        if (bagReference != null && !byId.TryGetValue(bagReference, out bag!)) return null;

        foreach (var entry in bag.Elements().Where(e => e.Name.LocalName == "KeyValueOfstringanyType"))
        {
            var keyElement = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Key");
            if (keyElement == null || !string.Equals(TextOf(keyElement, byId), propertyName, StringComparison.Ordinal))
                continue;

            var valueElement = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Value");
            return valueElement == null ? null : TextOf(valueElement, byId);
        }

        return null;
    }

    /// <summary>
    /// L'indice dei <c>z:Id</c>: serve a sciogliere i <c>z:Ref</c>. Tecnica presa dall'importatore interno degli impianti
    /// (non pubblicato), che l'ha gia' risolta per le giunzioni.
    /// </summary>
    private static Dictionary<string, XElement> IndexById(XDocument document)
    {
        var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in document.Descendants())
        {
            var id = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Id")?.Value;
            if (id != null) byId[id] = element;
        }

        return byId;
    }

    /// <summary>Il testo di un elemento, seguendo il <c>z:Ref</c> quando rimanda a un'altra scrittura.</summary>
    private static string? TextOf(XElement element, Dictionary<string, XElement> byId)
    {
        var reference = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Ref")?.Value;
        if (reference != null) return byId.TryGetValue(reference, out var target) ? Value(target) : null;

        return Value(element);

        static string? Value(XElement e) => e.Value.Length == 0 ? null : e.Value;
    }

    /// <summary>
    /// La geometria di un'occorrenza, o nullo se il riquadro non c'e' o non si riesce a leggere
    /// nessuno dei suoi quattro numeri. Un <c>BoundingRect</c> presente ma illeggibile in <b>tutti</b>
    /// i suoi numeri (nomi dei figli non riconosciuti, per esempio) non e' un dispositivo collocato:
    /// e' un dispositivo la cui geometria manca, esattamente come se il riquadro non ci fosse
    /// affatto - altrimenti finirebbe fra i collocati con quattro nulli silenziosi, mentendo sul
    /// fatto di sapere dove sta.
    /// <para>
    /// Se invece se ne legge uno, due o tre ma non tutti e quattro, il dispositivo <b>entra</b> fra
    /// i collocati - una posizione a meta' e' comunque piu' utile di nessuna - ma <c>Partial</c>
    /// torna vero: e' il segno che il chiamante conta in <see cref="DiagramGeometryResult.PartiallyPlaced"/>,
    /// perche' dopo la marcatura della migrazione non c'e' una seconda occasione per accorgersi che
    /// quella geometria era monca.
    /// </para>
    /// </summary>
    private static (ItemGeometry? Geometry, bool Partial) ReadGeometry(XElement? rect, XElement element)
    {
        if (rect == null) return (null, false);

        var x = Number(rect, "X");
        var y = Number(rect, "Y");
        var width = Number(rect, "Width");
        var height = Number(rect, "Height");

        var readCount = new[] { x, y, width, height }.Count(n => n != null);
        if (readCount == 0) return (null, false);

        var geometry = new ItemGeometry(x, y, width, height, Number(element, "RotationAngle"));
        return (geometry, readCount < 4);
    }

    /// <summary>
    /// Il numero di un figlio diretto, o nullo se il figlio non c'e' o non e' un numero. Nullo e
    /// non zero: un valore assente non e' un valore. Il confronto del nome ignora le maiuscole:
    /// i figli del riquadro (<c>x</c>/<c>y</c>/<c>width</c>/<c>height</c>) vengono da
    /// <c>RectangleF</c> di <c>System.Drawing</c>, minuscoli per conto loro, non dal disegno.
    /// </summary>
    private static double? Number(XElement parent, string localName)
    {
        var child = parent.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        if (child == null) return null;
        return double.TryParse(child.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
