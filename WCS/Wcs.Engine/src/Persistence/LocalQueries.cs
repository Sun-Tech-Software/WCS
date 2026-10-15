using Wcs.Engine.ORM;
using Wcs.Wms.Contracts;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Le domande del motore a cui si risponde con il <b>database del WCS</b>, e non con una query al
/// gestionale: il posto dove finiscono i metodi di <c>DbInfo</c> via via che il piano «Il motore
/// senza SQL Server» toglie loro il T-SQL sulle tabelle del gestionale. Quando <c>DBInfo*.cs</c>
/// sara' vuoto (compito 16) questa classe sara' tutto quello che resta.
/// </summary>
internal static class LocalQueries
{
    /// <summary>
    /// Le aree automatiche di deposito di un magazzino che hanno almeno una cella collegata a un
    /// dispositivo. Le celle collegate si leggono dalla mappa porte nel database del WCS, le aree
    /// e a che area appartiene ogni cella li dice il gestionale
    /// (<see cref="IWarehouse.GetAreaTopologyAsync"/>): l'incrocio si fa qui, in memoria.
    /// </summary>
    /// <remarks>
    /// <b>Era un <c>JOIN</c> solo, ed era di la' dal confine.</b> Una <c>SELECT DISTINCT</c> su
    /// <c>AREE_MAGAZZINO</c> e <c>CELLE_AREE</c> - tabelle del gestionale - con gli identificativi
    /// delle celle mappate <b>interpolati nel testo</b>, perche' quelli vengono dal database del
    /// WCS e nessuna delle due parti puo' unire le tabelle dell'altra. Sono le due meta' che non
    /// stanno nello stesso database: ognuna la chiede a chi la possiede, e l'intersezione e' tre
    /// righe di LINQ. Nessuna query, qui dentro, in nessun ramo.
    /// <para>
    /// <b>La topologia e' una chiave sola per magazzino</b>, e <c>CellToArea</c> e' un dizionario
    /// senza perdere niente: <c>PK_CELLE_AREE</c> e' su <c>ID_CELLA</c> (verificato su
    /// DEMOPLANT_WMS), quindi una cella sta in una e una sola area e non ci sono coppie da
    /// schiacciare.
    /// </para>
    /// <para>
    /// <b>L'ordine delle tre risposte non e' indifferente.</b> Il magazzino zero non e' un
    /// magazzino: nessuna domanda. Poi le celle mappate, che stanno nel database del WCS: se non
    /// ce n'e' nessuna l'intersezione e' vuota qualunque cosa risponda il gestionale, la risposta
    /// e' gia' nota, e chiederla sarebbe una domanda in piu' per ogni giro del ciclo di estrazione
    /// di ogni magazzino. Non e' una lista vuota che finge un dato: e' un dato che il motore
    /// possiede per intero. Solo dopo si chiede la topologia, e li' il gestionale che non risponde
    /// solleva <see cref="WarehouseUnavailableException"/> - non «nessuna area», come faceva la
    /// query fallita, che scriveva una riga nel registro e rispondeva <c>[]</c>.
    /// <br/>
    /// L'unico caso in cui i due ordini danno risposte diverse e' «nessuna cella mappata <b>e</b>
    /// gestionale giu'»: qui risponde <c>[]</c>, con l'ordine del brief solleverebbe. E' innocuo
    /// perche' quel caso non arriva fin qui: senza celle mappate la cella collegata al dispositivo
    /// e' 0, quindi il magazzino e' 0, e tutt'e tre i chiamanti escono dal guardiano dello zero
    /// prima di chiedere le aree.
    /// </para>
    /// <para>
    /// <b>Non si filtrano le capacita' dell'area</b> (mensola, multiprofondita', picking): non e'
    /// una dimenticanza, e' ridondante con quello che fa gia' il gestionale piu' avanti nella
    /// stessa richiesta. Il risultato di questo metodo finisce solo in
    /// <c>ReserveStorageRequest.SearchAreaIds</c> (i tre chiamanti sono
    /// <c>AutomationDeviceStorageManager.MoveObstacleLu</c>, <c>PathMission.FindRouteAllocateLu</c>
    /// e <c>DbInfo.RecoverPickUpQueue</c>), che <c>IWarehouse.ReserveStorageAsync</c> rispedisce al
    /// gestionale. Li' <c>MagazzinoPerWcs.ReserveStorageAsync</c> mette quegli id in
    /// <c>OpzioneRicercaCelle.ListaIdAreeRicerca</c>, e <c>CelleMagazzino.CercaDestinazioneUdc</c>
    /// li passa a <c>getDtCELLE_DISPONIBILI</c> insieme ai tre flag di stoccaggio fissi a 1 —
    /// esattamente <c>CapacitaArea.Stoccaggio</c> — che <c>getSqlORD_AREE</c> traduce in
    /// <c>(TAM.STOCCAGGIO_UDC_MENSOLA = 1 OR TAM.STOCCAGGIO_UDC_MULTIPROFONDITA = 1 OR
    /// TAM.PICKING = 1)</c> ed entra come <c>INNER JOIN</c> in entrambi i rami della stored
    /// procedure. La lista che il motore manda e' quindi una restrizione ulteriore in AND, non
    /// l'unico filtro: allargarla con aree senza queste capacita' non allarga mai il risultato
    /// finale. Il contributo che solo il motore puo' dare e' l'altro pezzo della vecchia
    /// <c>WHERE</c> - <c>AM.AUTOMATICA = 1</c> e «l'area ha almeno una cella mappata su un
    /// dispositivo del WCS» - che il gestionale non conosce: il primo e' adesso
    /// <see cref="AreaTopology.AutomaticAreaIds"/>, il secondo resta di qua.
    /// </para>
    /// </remarks>
    internal static async Task<List<decimal>> GetAutomaticAreasByWarehouseId(decimal warehouseId)
    {
        if (warehouseId == 0) return [];

        var mapped = await ExtractionQueue.ReadAsync(DoorMappingStore.MappedCellIdsAsync, new List<decimal>());
        if (mapped.Count == 0) return [];

        var topology = await Navigator
            .RequireWarehouse($"GetAutomaticAreasByWarehouseId sul magazzino {warehouseId}")
            .GetAreaTopologyAsync(warehouseId);

        // Le celle mappate sono tante quante le porte dell'impianto e si cercano una volta per
        // cella della topologia: l'insieme evita la scansione lineare. Le aree automatiche sono
        // una manciata (sei su DEMOPLANT) e restano la lista che il confine ha risposto.
        var mappedCells = mapped.ToHashSet();

        return topology.CellToArea
            .Where(cell => mappedCells.Contains(cell.Key) && topology.AutomaticAreaIds.Contains(cell.Value))
            .Select(cell => cell.Value)
            .Distinct()
            .OrderBy(areaId => areaId)
            .ToList();
    }
}
