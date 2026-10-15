using Wcs.Common;
using Wcs.Engine.Persistence;

namespace Wcs.Engine.ORM;

/// <summary>
/// Le scritture della missione sul database del WCS: un'istantanea presa ora, messa in coda allo
/// scrittore di stato, che la applica con <see cref="MissionStore"/> in ordine con le altre.
/// </summary>
/// <remarks>
/// Prima erano quattro comandi T-SQL su <c>MISSION_LU</c>, eseguiti in modo sincrono dal thread che
/// passava. Ora la riga sta in <c>wcs.db</c> e SQL Server non viene piu' scritto: tappa 2 del
/// database del WCS, taglio netto sul ramo per decisione del responsabile del prodotto. Senza scrittore —
/// motore non configurato, test — la scrittura va direttamente sul database del WCS, se c'e';
/// altrimenti non c'e' nulla su cui scrivere.
/// </remarks>
internal static class PathMission
{
    /// <summary>
    /// Sonda per i test: se e' impostata, <see cref="Update"/> la chiama al posto della scrittura
    /// e torna. Vede la missione nello stato in cui l'istantanea sarebbe stata presa. In esercizio
    /// e' sempre nulla.
    /// </summary>
    internal static Action<Items.PathMission>? SondaScrittura;

    #region Internal Methods

    internal static void Add(Items.PathMission? value)
    {
        if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Recover || value == null || value.MissionId == Guid.Empty)
            return;

        Upsert(value);
    }

    internal static void Remove(Items.PathMission? value)
    {
        if (value == null)
            return;

        Remove(value.MissionId);
    }

    internal static void Remove(Guid missionId)
    {
        if (missionId == Guid.Empty)
            return;

        Write(context => MissionStore.DeleteAsync(context, missionId));
    }

    internal static void Update(Items.PathMission value)
    {
        if (!value.IsDatabaseWriteSynchronized)
            throw new InvalidOperationException(
                "La scrittura della missione deve essere composta sotto il lock della missione.");

        var sonda = SondaScrittura;
        if (sonda != null)
        {
            sonda(value);
            return;
        }

        if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Recover || value.MissionId == Guid.Empty)
            return;

        Upsert(value);
    }

    /// <summary>
    /// Come <see cref="Update"/>, ma per chi deve sapere che la riga e' scritta prima di andare avanti:
    /// il compito che torna si completa quando l'istantanea e' nel database del WCS, e fallisce se la
    /// scrittura non e' riuscita o la coda non l'ha accettata. Serve al segno delle segnalazioni del
    /// cambio cella (<see cref="Items.PathMission.MarkTheChangeOfCellReportsAsync"/>), che deve stare
    /// nel database prima che la prima segnalazione parta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composta sotto il lock della missione come <see cref="Update"/>: l'istantanea si prende e si
    /// mette in coda li', nell'ordine delle altre scritture della missione. L'attesa la fa chi chiama,
    /// fuori dal lock.
    /// </para>
    /// <para>
    /// <b>Scrive anche in Recover</b>, a differenza di <see cref="Update"/>: li' le scritture si saltano
    /// perche' la ricarica rimette i campi dalla riga stessa, ma un cambio dell'operatore durante la
    /// ricarica manda le segnalazioni al gestionale lo stesso, e senza il segno nel database un riavvio
    /// rimetterebbe in piedi l'estrazione che il gestionale non ha piu'.
    /// </para>
    /// <para>
    /// <paramref name="together"/> e' una scrittura che va nella stessa transazione della riga della
    /// missione, dopo di lei: la riga della coda del fatto «l'estrazione prelevata e' diventata un
    /// deposito» (<c>Items.PathMission.BecomeADepositOfAPickedExtraction</c>, voce 45). O tutt'e due nel
    /// database, o nessuna. Con la sonda dei test non si scrive nessuna delle due.
    /// </para>
    /// </remarks>
    internal static Task UpdateAndWaitAsync(Items.PathMission value, WcsWrite? together = null)
    {
        if (!value.IsDatabaseWriteSynchronized)
            throw new InvalidOperationException(
                "La scrittura della missione deve essere composta sotto il lock della missione.");

        var sonda = SondaScrittura;
        if (sonda != null)
        {
            sonda(value);
            return Task.CompletedTask;
        }

        if (value.MissionId == Guid.Empty)
            return Task.CompletedTask;

        var snapshot = MissionStore.Snapshot(value);
        WcsWrite write = together == null
            ? context => MissionStore.UpsertAsync(context, snapshot)
            : async context =>
            {
                await MissionStore.UpsertAsync(context, snapshot);
                await together(context);
            };

        if (Navigator.StateWriter is { } writer)
            return writer.EnqueueAndWaitAsync(write);

        var store = Navigator.Store;
        if (store == null)
            return Task.CompletedTask;

        try
        {
            using var context = store.CreateContext();
            write(context).GetAwaiter().GetResult();
            context.SaveChanges();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    /// <summary>
    /// La posizione e' un campo della stessa riga: si scrive l'istantanea intera, come per ogni
    /// altro setter. Il nome resta per i chiamanti.
    /// </summary>
    internal static void UpdatePosition(Items.PathMission value)
    {
        if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Recover)
            return;

        Upsert(value);
    }

    #endregion Internal Methods

    #region Private Methods

    private static void Upsert(Items.PathMission value)
    {
        // A mission that has left the engine keeps its row deleted: work that still holds the object
        // would otherwise insert it again (Items.PathMission.MarkRemovedFromTheEngine).
        if (value.IsRemovedFromTheEngine)
            return;

        // L'istantanea si prende QUI, sotto il lock per missione di chi chiama: i valori sono
        // quelli del momento dell'evento, e l'ordine della coda e' l'ordine delle scritture.
        var snapshot = MissionStore.Snapshot(value);
        Write(context => MissionStore.UpsertAsync(context, snapshot));
    }

    private static void Write(WcsWrite write)
    {
        if (Navigator.StateWriter is { } writer && writer.EnqueueOrWait(write))
            return;

        var store = Navigator.Store;
        if (store == null)
            return;

        using var context = store.CreateContext();
        write(context).GetAwaiter().GetResult();
        context.SaveChanges();
    }

    #endregion Private Methods
}
