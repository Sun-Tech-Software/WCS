using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Cancella un dispositivo (compito 7): <c>DELETE /plant/items/{id}</c> porta via anche le
/// giunzioni in cui il dispositivo e' origine o destinazione (<c>PlantStore.DeleteItemAsync</c>,
/// <c>WCS/Wcs.Engine/src/Persistence/PlantStore.cs</c>) - non solo la riga del dispositivo. Chi
/// costruisce questo comando (<c>PlantCanvas</c>) deve quindi catturare, prima di cancellare, tutto
/// quello che la cancellazione porta via: la configurazione completa (<see cref="Config"/>, letta
/// con <c>GetItemConfigurationAsync</c> perche' <c>PlantItemDto</c> non la porta tutta), la
/// geometria (<see cref="Geometry"/>) e le giunzioni toccate (<see cref="Junctions"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>L'annulla rimette la forma del grafo, non l'identita' delle giunzioni.</b>
/// <c>JunctionRequest</c> non porta un identificativo (<c>Guid SourceItemId, Guid DestinationItemId, int Priority</c>):
/// una giunzione ricreata con <c>POST /plant/junctions</c> riceve un <c>JunctionId</c> nuovo, mai
/// quello che aveva prima di essere cancellata. <see cref="UndoAsync"/> rimette quindi gli stessi
/// estremi e la stessa priorita' di ogni giunzione in <see cref="Junctions"/>, ma con un
/// identificativo diverso - una dichiarazione, non un difetto nascosto: chi tenesse altrove un
/// riferimento al vecchio <see cref="JunctionDto.JunctionId"/> lo troverebbe non piu' valido dopo
/// un annulla.
/// </para>
/// <para>
/// <b>L'annulla e' tre passi, non transazionali insieme - reso tollerante a un tentativo ripetuto.</b>
/// Ricreare il dispositivo (creazione, poi geometria) e ricreare ciascuna giunzione sono
/// chiamate HTTP indipendenti: se una di mezzo fallisce (permesso perso, host irraggiungibile),
/// <see cref="UndoAsync"/> solleva e <see cref="CommandStack"/> lascia il comando in pila per un
/// nuovo tentativo (contratto gia' vero per ogni comando, non nuovo qui). Un secondo tentativo
/// ripartirebbe pero' da capo se non guardasse quello che il primo ha gia' scritto: quando il
/// dispositivo e la sua geometria sono gia' stati confermati, <see cref="UndoAsync"/> li ha aggiunti
/// insieme al grafo locale e il tentativo successivo salta entrambi. La scrittura di geometria non
/// e' piu' un upsert ripetibile: porta il token appena letto dopo la creazione e una sua replica con
/// quel token, ormai consumato, sarebbe correttamente rifiutata. Anche la ricreazione non e'
/// ripetibile: <c>POST /plant/items</c> sul dispositivo gia' tornato risponde <c>409</c> - si veda
/// <see cref="CreateAsync"/>, che dichiara che cosa fa allora.
/// Rifare <c>AddJunctionAsync</c> e' innocuo sull'host vero - ma per un motivo diverso da quello che
/// questo commento dichiarava, ed e' falso: <b>una seconda <c>POST</c> per
/// la stessa giunzione non la duplica.</b> <c>PlantQueries.AddJunctionAsync</c>
/// (<c>WCS/Wcs.Engine/src/Api/PlantQueries.cs</c>) collassa sulla coppia (origine,
/// destinazione) e, se la trova gia' presente, restituisce quella esistente invece di crearne
/// un'altra. La guardia che <see cref="UndoAsync"/> applica comunque <b>prima</b> di chiamare
/// <c>AddJunctionAsync</c> non serve a proteggere l'host da un duplicato che non creerebbe mai.
/// Non protegge piu' nemmeno il
/// <b>modello locale</b>. Lo avrebbe fatto prima che <see cref="PlantGraph.AddJunction"/> imparasse a
/// deduplicare: senza guardia, una giunzione
/// gia' ripristinata sarebbe stata rimandata all'host una seconda volta, l'host avrebbe restituito
/// la giunzione esistente (stesso <c>JunctionId</c> della prima POST), e
/// <see cref="PlantGraph.AddJunction"/> - che allora accodava senza deduplicare - l'avrebbe infilata
/// una seconda volta nella lista locale. Da quando (<c>PlantGraph.cs:101-106</c>: <c>AddJunction</c>
/// sostituisce per <c>JunctionId</c>, come gia' fa <see cref="PlantGraph.AddItem"/> per
/// <c>ItemId</c>) quella divergenza non puo' piu' accadere: una seconda <c>AddJunction</c> con lo
/// stesso <c>JunctionId</c> rimpiazza la voce esistente invece di accodarla - si veda il commento di
/// <see cref="PlantGraph.AddJunction"/>, che oggi e' la protezione vera. Sull'host vero la guardia
/// resta quindi solo un'<b>ottimizzazione</b>: una <c>POST</c> HTTP in meno per ogni giunzione gia'
/// ripristinata. Contro il gestore finto di questa suite, deliberatamente piu' permissivo di quello
/// vero (non collassa sulla coppia da solo, <c>PlantMutationHandler.cs:100-111</c>), la guardia
/// resta invece l'unica cosa che impedisca la doppia <c>POST</c> - e' per questo che
/// <c>ARetriedUndoSkipsWhatAnEarlierPartialAttemptAlreadyRestored</c> diventerebbe rosso togliendola,
/// pur provando il finto e non il vero. <see cref="UndoAsync"/>
/// guarda quindi <paramref name="graph"/> prima di ogni passo: ricrea il dispositivo solo se non c'e'
/// gia', e salta ogni giunzione la cui forma (origine, destinazione, priorita') e' gia' presente. Il
/// confronto e' per forma, non per identita' - <see cref="JunctionDto.JunctionId"/> cambia a ogni
/// ricreazione, come detto sopra - e per questo resta un'euristica: due giunzioni con la stessa
/// forma esatta (stessa origine, stessa destinazione, stessa priorita') di cui una sola ancora da
/// rimettere dopo un tentativo interrotto a meta' sarebbero indistinguibili. Nessun test di questo
/// compito crea due giunzioni cosi' identiche - il caso non e' provato, e non e' dichiarato chiuso.
/// </para>
/// <para>
/// <b>La garanzia "una coppia, una giunzione" e' dell'host, non di questo comando, ed e' piu' larga
/// di quanto serva qui: sicurezza per accordo fra piu' posti, scritta prima in
/// nessuno.</b> Tre meccanismi indipendenti collassano sulla coppia (origine, destinazione),
/// ignorando la priorita', e nessuno e' un vincolo del database (l'indice a
/// <c>WcsDbContext.cs:131</c> non e' <c>IsUnique()</c>): <c>PlantQueries.AddJunctionAsync</c> (sopra),
/// <c>PlantStore.UpsertJunctionAsync</c> (<c>WCS/Wcs.Engine/src/Persistence/PlantStore.cs:316-321</c>,
/// cerca per identificativo <b>o</b> per coppia, e sovrascrive), e l'importatore interno (non pubblicato) dal disegno
/// (<c>DiagramReader.JunctionIdOf</c> deriva il <c>JunctionId</c> dalla coppia per SHA-256,
/// <c>DiagramReader.cs:101</c> dell'importatore interno, non pubblicato, e <c>Resolve</c> tiene un <c>HashSet</c> sulla
/// coppia, <c>:112</c>, che collassa due linee identiche in una). C'e' pero' un quarto ingresso che a
/// quell'accordo <b>non</b> partecipa: <c>Importer.ReadJunctionsFromTableAsync</c>
/// (<c>Importer.cs:255-272</c> dell'importatore interno, non pubblicato) conserva il <c>JUNCTION_ID</c> legacy di
/// <c>NAVIGATOR_JUNCTIONS</c> e aggiunge le righe con <c>AddRange</c>, senza collasso sulla coppia:
/// un impianto migrato dalla tabella (non dal disegno) puo' quindi portare due giunzioni sulla stessa
/// coppia. Se hanno la stessa priorita' l'euristica di <see cref="UndoAsync"/> funziona per caso e
/// bene (la seconda viene saltata, e l'host ne avrebbe comunque tenuta una sola). Se hanno priorita'
/// diverse l'euristica non salta, la seconda POST parte davvero, l'host restituisce la giunzione gia'
/// esistente (con la priorita' della prima) - ed e' esattamente il caso in cui
/// <see cref="PlantGraph.AddJunction"/>, corretto per deduplicare per <c>JunctionId</c> come gia'
/// fa <see cref="PlantGraph.AddItem"/>, evita che il modello locale la infili due
/// volte.
/// </para>
/// </remarks>
public sealed record DeleteItemCommand : EditCommand
{
    public PlantItemConfigDto Config { get; }
    public PlantItemGeometryDto Geometry { get; }
    public IReadOnlyList<JunctionDto> Junctions { get; }

    public DeleteItemCommand(PlantItemConfigDto config, PlantItemGeometryDto geometry, IReadOnlyList<JunctionDto> junctions)
    {
        if (geometry.ItemId != config.ItemId)
            throw new ArgumentException(
                $"La geometria porta l'ItemId {geometry.ItemId}, la configurazione {config.ItemId}: devono essere lo stesso dispositivo.");

        Config = config;
        Geometry = geometry;
        Junctions = junctions;
    }

    public override string Description => Config.Name is { Length: > 0 } name ? $"Cancella {name}" : "Cancella dispositivo";

    /// <summary>La cancellazione vera: una sola chiamata, che porta via anche le giunzioni sull'host - il modello locale segue con <see cref="PlantGraph.RemoveItem"/> e <see cref="PlantGraph.RemoveJunctionsTouching"/>, nello stesso ordine.</summary>
    public override async Task ApplyAsync(WcsClient client, PlantGraph graph)
    {
        await client.DeleteItemAsync(Config.ItemId);
        graph.RemoveItem(Config.ItemId);
        graph.RemoveJunctionsTouching(Config.ItemId);
    }

    public override async Task UndoAsync(WcsClient client, PlantGraph graph)
    {
        // Passo 1: il dispositivo. Se un tentativo precedente e' arrivato fin qui (e poi e' fallito
        // su una giunzione), non lo ricrea una seconda volta: la rotta della creazione risponderebbe
        // 409 (si veda sotto), e il passo 2 resta capace di distinguere "il dispositivo manca ancora"
        // da "il dispositivo c'e', resta solo qualche giunzione".
        if (graph.Items.All(i => i.ItemId != Config.ItemId))
        {
            await CreateAsync(client);
            // Queue item 21: the device is back with this configuration from here on.
            graph.ApplyConfiguration(Config);
            var recreated = await client.GetItemAsync(Config.ItemId)
                ?? throw new InvalidOperationException(
                    $"Il dispositivo appena ricreato ({Config.ItemId}) non risulta piu' sull'host: annullamento incoerente.");
            var confirmation = await client.SetItemGeometryAsync(
                GeometryWriter.ToUpdate(Geometry, recreated.GeometryVersion));
            graph.AddItem(GeometryWriter.ApplyConfirmation(recreated, Geometry, confirmation));
        }

        // Passo 2: le giunzioni non ancora ripristinate, per forma (vedi il remarks della classe).
        foreach (var junction in Junctions)
        {
            var alreadyRestored = graph.Junctions.Any(j =>
                j.SourceItemId == junction.SourceItemId &&
                j.DestinationItemId == junction.DestinationItemId &&
                j.Priority == junction.Priority);
            if (alreadyRestored) continue;

            var recreated = await client.AddJunctionAsync(new JunctionRequest(junction.SourceItemId, junction.DestinationItemId, junction.Priority));
            graph.AddJunction(recreated);
        }
    }

    /// <summary>
    /// Ricrea il dispositivo con <c>POST /plant/items</c>, e su <c>409</c> si ferma invece di
    /// scrivere.
    /// </summary>
    /// <remarks>
    /// <b>Che cosa vuol dire un 409 qui: due cose, non una.</b> <c>POST /plant/items</c> risponde
    /// <c>409</c> sia quando quell'identificativo e' gia' occupato, sia quando il <b>motore e' in
    /// Run</b> - la guardia <c>PlantQueries.RequireNotRunning</c> viene prima del controllo di
    /// esistenza, e <c>EngineExceptionHandler</c> traduce la sua <c>EngineBusyException</c> nello
    /// stesso codice (lo prova <c>Wcs.Host.Test/WritingOnAMissingDeviceTest</c>). Le due cause hanno
    /// due rimedi opposti - fermare il motore, oppure non fare niente - quindi
    /// <see cref="DescribeConflictAsync"/> le distingue chiedendo all'host il suo stato, invece di
    /// dare per scontata la prima e mandare l'operatore a rimettere a mano giunzioni che nessuno ha
    /// perso.
    /// <para>
    /// <b>Con il motore in Run</b> l'annulla non ha scritto niente - la creazione e' il suo primo
    /// gesto - e si ripete tale e quale a motore fermo. Il costo dichiarato: se il motore si
    /// fermasse proprio fra il <c>409</c> e la lettura dello stato, il messaggio ricadrebbe su
    /// quello dell'identificativo occupato. E' una finestra di una chiamata, e il rimedio che
    /// suggerisce (riprovare l'annulla) resta quello giusto.
    /// </para>
    /// <para>
    /// <b>Con l'identificativo occupato</b> vuol dire che un dispositivo con <b>questo</b>
    /// identificativo e' tornato sull'impianto mentre l'annulla non era ancora stato dato. Il
    /// permesso di modifica e' esclusivo, quindi non e' un altro editor che disegna: per riportare
    /// proprio quel <c>Guid</c> ci vuole una sostituzione dell'impianto intero
    /// (<c>PUT /plant/graph</c>, l'importazione) o il ripristino di un backup - l'impianto sotto i
    /// piedi non e' piu' quello da cui il dispositivo era stato cancellato.
    /// </para>
    /// <para>
    /// Le due strade scartate, e perche'. <b>Sovrascrivere</b> con <c>PUT .../config</c> rimetterebbe
    /// le ~60 proprieta' di questo comando sopra quelle del dispositivo che c'e' ora, che sono di chi
    /// lo ha riportato: e' esattamente la perdita silenziosa che il 404 della PUT e' stato introdotto
    /// per chiudere. <b>Adottarlo</b> - rileggerlo, metterlo nel grafo e tirare dritto con le
    /// giunzioni - rimetterebbe archi vecchi su un impianto che nel frattempo e' cambiato, e
    /// chiamerebbe «annullato» un gesto che ha restituito un dispositivo diverso da quello
    /// cancellato.
    /// </para>
    /// <para>
    /// <b>Il costo, dichiarato:</b> l'eccezione risale a <c>CommandStack.UndoAsync</c>, che lascia il
    /// comando in pila (contratto suo, non nuovo qui), e un secondo annulla trovera' lo stesso
    /// <c>409</c> finche' quell'identificativo resta occupato. Il dispositivo pero' <b>e'</b>
    /// sull'impianto, non perso: quello che non torna e' la sua vecchia configurazione, e le
    /// giunzioni che questo comando aveva catturato - che il messaggio nomina, perche' l'operatore
    /// sappia che cosa ha ancora in mano.
    /// </para>
    /// </remarks>
    private async Task CreateAsync(WcsClient client)
    {
        try
        {
            await client.CreateItemAsync(Config);
        }
        catch (WcsApiException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(await DescribeConflictAsync(client), conflict);
        }
    }

    /// <summary>
    /// Quale delle due cause del <c>409</c> e' stata, e che cosa deve fare l'operatore. Si veda il
    /// <c>remarks</c> di <see cref="CreateAsync"/>.
    /// </summary>
    private async Task<string> DescribeConflictAsync(WcsClient client) =>
        await IsEngineRunningAsync(client)
            ? "Il motore e' in Run: mentre gira l'impianto non si cambia, e questo annulla non ha scritto niente - " +
              $"il dispositivo {Config.ItemId} non e' stato ricreato e nessuna giunzione e' stata toccata. " +
              "Fermare il motore e ridare l'annulla."
            : $"Il dispositivo {Config.ItemId} e' tornato sull'impianto mentre questo annulla non era ancora stato dato: " +
              "l'annulla non lo ha toccato, per non scrivere la configurazione di prima sopra quella che c'e' adesso. " +
              (Junctions.Count > 0
                  ? $"Restano da rimettere a mano {Junctions.Count} giunzione/i di quel dispositivo."
                  : "Quel dispositivo non aveva giunzioni da rimettere.");

    /// <summary>
    /// Lo stato del motore secondo l'host. Un guasto nel chiederlo non deve sostituire il messaggio
    /// del <c>409</c> con un'eccezione sua: si risponde «non in Run», cioe' si ricade sull'altra
    /// causa, che e' quella che l'operatore puo' comunque controllare guardando l'impianto.
    /// </summary>
    private static async Task<bool> IsEngineRunningAsync(WcsClient client)
    {
        try
        {
            return (await client.GetStatusAsync()).NavStatus == NavigatorStatusTypes.Run;
        }
        catch (Exception failure) when (failure is WcsApiException or HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
