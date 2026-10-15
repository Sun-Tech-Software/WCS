using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>Che cosa ha fatto la migrazione, e che cosa non ha portato via.</summary>
/// <remarks>
/// I numeri non sono una partizione: un GUID noto la cui geometria e' illeggibile in tutte le sue
/// occorrenze conta sia in <see cref="ItemsWithoutPosition"/> (e' un <c>PlantItem</c> senza
/// posizione, qualunque ne sia la ragione) sia in <see cref="GuidsWithoutGeometry"/> (la ragione
/// era proprio quella). E' voluto: prima che <see cref="GuidsWithoutGeometry"/> esistesse, un GUID
/// sconosciuto - non un <c>PlantItem</c> - con la geometria illeggibile in tutte le sue occorrenze
/// non compariva in nessuno dei quattro numeri di allora: non fra i collocati, non fra quelli senza
/// posizione (quel conteggio scorre solo gli identificativi noti), non fra le identita' sconosciute
/// (calcolate solo su cio' che e' finito in <see cref="DiagramGeometryResult.Geometries"/>, dove
/// un GUID cosi' non entra mai). Spariva dal verbale, l'unica cosa che si vede quando questo codice
/// gira in casa d'altri. <see cref="GuidsWithoutGeometry"/> e' il totale che
/// <see cref="DiagramGeometryReader"/> gia' contava e non arrivava mai fin qui: con questo, quel
/// GUID torna visibile, anche se non e' un <c>PlantItem</c>.
/// </remarks>
/// <param name="PartiallyPlaced">
/// Quanti dei <see cref="Placed"/> portano un riquadro letto solo in parte (si veda
/// <see cref="DiagramGeometryResult.PartiallyPlaced"/>): sono fra i collocati, ma con la geometria
/// a meta', e dopo questa migrazione non c'e' una seconda occasione per accorgersene.
/// </param>
public sealed record GeometryMigrationReport(
    bool Executed, int Placed, int ItemsWithoutPosition, int ElementsWithoutGuid, int UnknownGuids,
    int GuidsWithoutGeometry, int PartiallyPlaced);

/// <summary>
/// Porta una volta sola la collocazione dei dispositivi dal blob del disegno alle colonne del
/// modello. Idempotente: la seconda volta non fa nulla, perche' da quel momento la verita' e' il
/// modello e il disegno e' congelato.
/// </summary>
/// <remarks>
/// Un impianto nuovo, senza blob, si marca lo stesso senza leggere niente: non c'e' geometria da
/// recuperare e il congelamento vale ugualmente.
/// <para>
/// <see cref="DiagramGeometryReader.Read"/> solleva se l'XML e' malformato, e qui non viene
/// intercettata apposta: non e' compito di questo metodo decidere se un blob illeggibile debba
/// fermare l'host, e il chiamante (<c>Navigator.OpenDatabases</c>) intercetta a sua volta, registra
/// l'errore e prosegue - il motore non legge mai la geometria, e' un dato di chi disegna, e un
/// impianto che non movimenta bancali perche' un file di disegno e' illeggibile sarebbe un modo
/// nuovo di fermarlo. Qui basta non mentire: siccome
/// <see cref="PlantDiagram.GeometryMigratedAt"/> si valorizza solo <b>dopo</b> la lettura riuscita
/// (righe piu' sotto), un'eccezione a meta' strada non lo tocca. Il disegno resta percio' non
/// congelato - il vecchio Editor continua a funzionare e chi installa puo' correggere il blob - e il
/// prossimo avvio puo' riprovare la migrazione da capo.
/// </para>
/// <para>
/// Stessa sorte per un blob presente e XML valido che pero' non colloca <b>nessuno</b> dei
/// dispositivi noti: non e' un impianto nuovo (quello ha il blob vuoto, gestito sopra), e' un
/// disegno che questo lettore non sa interpretare - scritto da una versione che non mette
/// <c>OrchestraType</c>/<c>DiagramType</c> nel <c>PropertyBag</c>, o un formato diverso. Marcare
/// qui congelerebbe il disegno con tutti i dispositivi "senza posizione" e nessun modo di
/// riprovare se non una <c>UPDATE</c> a mano su <see cref="PlantDiagram.GeometryMigratedAt"/>. Si
/// solleva anche qui, cosi' il chiamante la tratta come l'XML malformato: registrata come errore,
/// non marcata, riprovata al prossimo avvio.
/// </para>
/// <para>
/// C'e' un caso legittimo che cade nello stesso ramo e si ripete a ogni avvio senza essere un
/// guasto: un impianto i cui dispositivi noti sono tutti piu' recenti del blob - per esempio dopo
/// "Nuovo diagramma", che svuota <see cref="PlantItem"/> ma non <see cref="PlantDiagram"/>, seguito
/// da creazioni via API senza un salvataggio del disegno - non colloca nessuno perche' nel blob
/// quei dispositivi non ci sono ancora, e questo metodo solleva a ogni avvio finche' non arriva un
/// salvataggio del disegno: a quel punto il blob torna a contenere i dispositivi correnti e la
/// migrazione, ancora ammessa, si marca da sola. E' il verso giusto dell'errore - non un guasto da
/// correggere - ma chi lo vede ripetersi senza conoscere questo caso lo scambiera' per uno.
/// </para>
/// </remarks>
public static class GeometryMigration
{
    public static async Task<GeometryMigrationReport> RunOnceAsync(WcsDatabase database)
    {
        await using var context = database.CreateContext();
        var diagram = await PlantStore.GetOrCreateDiagramAsync(context);

        if (diagram.GeometryMigratedAt != null)
            return new GeometryMigrationReport(false, 0, 0, 0, 0, 0, 0);

        var read = DiagramGeometryReader.Read(diagram.DiagramXml ?? []);

        var knownIds = await context.PlantItems.Select(i => i.ItemId).ToListAsync();
        var placed = 0;
        foreach (var (itemId, geometry) in read.Geometries)
            if (await PlantStore.SetGeometryAsync(context, itemId, geometry))
                placed++;

        var unknownGuids = read.Geometries.Count - placed;
        var withoutPosition = knownIds.Count(id => !read.Geometries.ContainsKey(id));

        // Il blob c'e' ed e' XML valido (altrimenti Read avrebbe gia' sollevato), ma non ha
        // collocato nessuno dei dispositivi noti: non e' un impianto nuovo (blob vuoto, gestito
        // sopra da diagramXml.Length == 0), e' un disegno che questo lettore non riconosce. Si
        // veda il remarks della classe.
        if (diagram.DiagramXml is { Length: > 0 } && knownIds.Count > 0 && placed == 0)
            throw new InvalidOperationException(
                $"Il disegno ha {knownIds.Count} dispositivi noti ma non ne ha collocato nessuno: " +
                "probabile formato non riconosciuto (PropertyBag senza OrchestraType/DiagramType), " +
                "non un impianto vuoto. Non marcato, si riprova al prossimo avvio.");

        diagram.GeometryMigratedAt = DateTime.Now;
        await context.SaveChangesAsync();

        return new GeometryMigrationReport(
            true, placed, withoutPosition, read.ElementsWithoutGuid, unknownGuids, read.GuidsWithoutGeometry,
            read.PartiallyPlaced);
    }
}
