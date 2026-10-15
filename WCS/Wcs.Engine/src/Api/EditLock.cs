namespace Wcs.Engine.Api;

/// <summary>
/// Lo stato del permesso cosi' come lo conosce il motore: chi lo tiene, da quando, fino a quando.
/// Non e' <c>Wcs.Api.Contracts.Dtos.EditLockDto</c> - il motore non referenzia quel progetto, come
/// <see cref="DeviceTypeDescriptor"/> non e' <c>DeviceTypeDto</c> - e non porta <c>IsMine</c>: quel
/// campo dipende da chi chiede, cosa che questa classe non sa: lo aggiunge la rotta che la chiama,
/// mappando questo record sul DTO di rete.
/// </summary>
public sealed record EditLockState(string Holder, DateTime SinceLocal, DateTime ExpiresLocal);

/// <summary>
/// Il permesso di modifica dell'impianto: un solo editor per volta puo' cambiarne la forma. Lo
/// stato e' statico e di processo - un permesso solo, non uno per sessione - protetto da
/// <see cref="Gate"/> perche' piu' richieste HTTP possono arrivare insieme.
/// </summary>
/// <remarks>
/// La scadenza e' il punto su cui il permesso vive o muore: un browser che sparisce - scheda
/// chiusa, rete caduta, portatile chiuso - non restituisce niente, e senza scadenza l'impianto
/// resterebbe in sola lettura finche' qualcuno non riavvia l'host, un guasto vero su un magazzino
/// in esercizio. Il tempo arriva da <see cref="Clock"/>, non da <c>DateTime.Now</c> letto qui
/// dentro: cosi' i test provano la scadenza passando un orologio finto, senza aspettare davvero i
/// minuti della durata vera.
/// <para>
/// Vale anche verso l'Editor WinForms finche' i due convivono: questa classe non sa da quale
/// interfaccia arriva la richiesta, e non deve saperlo - il permesso e' delle rotte dell'host che
/// cambiano la forma dell'impianto, non di un'interfaccia in particolare.
/// </para>
/// </remarks>
public static class EditLock
{
    private static readonly object Gate = new();
    private static string? _holder;
    private static DateTime _since;
    private static DateTime _expires;
    private static TimeSpan _lease;

    /// <summary>
    /// L'orologio da cui il permesso legge "adesso". Sostituibile nei test (compito 4);
    /// in produzione resta quello vero.
    /// </summary>
    internal static Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>
    /// Concede il permesso se non c'e' un titolare, o se il titolare che c'era e' scaduto - in
    /// entrambi i casi <paramref name="holder"/> diventa il nuovo titolare, anche se e' diverso da
    /// quello scaduto. Se un titolare valido c'e' gia', non concede nulla: nemmeno a
    /// <paramref name="holder"/> stesso, che per rinnovare il proprio deve chiamare
    /// <see cref="Renew"/>.
    /// </summary>
    public static bool TryAcquire(string holder, TimeSpan lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        lock (Gate)
        {
            var now = Clock();
            if (_holder is not null && now < _expires) return false;

            _holder = holder;
            _since = now;
            _lease = lease;
            _expires = now + lease;
            return true;
        }
    }

    /// <summary>
    /// Sposta la scadenza avanti della stessa durata concessa da <see cref="TryAcquire"/>. Riesce
    /// solo se <paramref name="holder"/> e' il titolare attuale e il suo permesso non e' ancora
    /// scaduto: un permesso scaduto non si rinnova, si riacquisisce da capo con
    /// <see cref="TryAcquire"/> - rinnovarlo lo farebbe rivivere come se non fosse mai scaduto,
    /// il permesso concesso che finge di essere valido che questa classe non deve produrre.
    /// </summary>
    public static bool Renew(string holder)
    {
        lock (Gate)
        {
            var now = Clock();
            if (_holder != holder || now >= _expires) return false;

            _expires = now + _lease; // il possesso continua (_since resta quello originario): solo la scadenza si sposta
            return true;
        }
    }

    /// <summary>
    /// Verifica che <paramref name="holder"/> sia il titolare valido adesso e, se lo e', rinnova la
    /// scadenza nello stesso lock: e' quanto usa il cancello delle scritture
    /// (<c>EditPermissionFilter</c>, compito 4b) al posto di comporre <see cref="Current"/> e poi
    /// <see cref="Renew"/> come due chiamate separate. Due chiamate riaprirebbero, fra la lettura e
    /// il rinnovo, la stessa finestra che <see cref="AcquireOrRenew"/> ha chiuso per la rotta del
    /// permesso: un <see cref="Release"/> o una scadenza naturale concorrenti potrebbero cadere
    /// proprio li' in mezzo.
    /// </summary>
    /// <returns>
    /// <c>Valid</c> e' vero se <paramref name="holder"/> e' il titolare adesso e non era scaduto -
    /// in tal caso la scadenza e' gia' stata spostata avanti prima del ritorno. <c>State</c> descrive
    /// chi tiene davvero il permesso in questo momento (o e' nullo se nessuno lo tiene, o e' scaduto),
    /// per il messaggio di rifiuto quando <c>Valid</c> e' falso - lo stesso scopo che aveva
    /// <see cref="Current"/> prima di questo metodo.
    /// </returns>
    public static (bool Valid, EditLockState? State) VerifyAndRenew(string holder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        lock (Gate)
        {
            var now = Clock();
            var holderIsValid = _holder is not null && now < _expires;

            if (holderIsValid && _holder == holder)
            {
                _expires = now + _lease; // stesso conto di Renew: il possesso continua, solo la scadenza si sposta
                return (true, new EditLockState(_holder, _since, _expires));
            }

            return (false, holderIsValid ? new EditLockState(_holder!, _since, _expires) : null);
        }
    }

    /// <summary>Libera il permesso, ma solo se e' <paramref name="holder"/> a tenerlo: il rilascio di chi non e' titolare non fa niente.</summary>
    public static void Release(string holder)
    {
        lock (Gate)
        {
            if (_holder == holder) _holder = null;
        }
    }

    /// <summary>Lo stato attuale del permesso, o nullo se nessuno lo tiene o se il titolare che c'era e' scaduto.</summary>
    public static EditLockState? Current
    {
        get
        {
            lock (Gate)
            {
                var now = Clock();
                return _holder is not null && now < _expires ? new EditLockState(_holder, _since, _expires) : null;
            }
        }
    }

    /// <summary>
    /// Concede o rinnova per <paramref name="holder"/> e restituisce lo stato risultante, tutto
    /// sotto lo stesso <see cref="Gate"/>: e' l'operazione che la rotta HTTP deve chiamare invece di
    /// comporre <see cref="Renew"/>, poi <see cref="TryAcquire"/>, poi leggere <see cref="Current"/>
    /// come tre chiamate pubbliche separate. Con tre chiamate ognuna ha il proprio lock interno, ma
    /// fra l'una e l'altra il lock si rilascia: una <see cref="Release"/> concorrente puo' cadere
    /// proprio in quella finestra e azzerare il titolare dopo che l'acquisizione e' riuscita ma
    /// prima che lo stato sia letto, lasciando <c>Current</c> nullo nonostante l'esito positivo.
    /// Qui la decisione (rinnova, acquisisce, o niente) e la lettura dello stato che ne segue sono
    /// la stessa sezione critica: quella finestra non esiste piu'.
    /// </summary>
    /// <returns>
    /// <c>Granted</c> e' vero se il permesso e', dopo la chiamata, di <paramref name="holder"/>
    /// (concesso ora o rinnovato); <c>State</c> e' sempre non nullo - se non concesso a
    /// <paramref name="holder"/> descrive chi lo tiene davvero, cosi' come faceva il 409 con
    /// <c>Current</c> prima di questo metodo.
    /// </returns>
    public static (bool Granted, EditLockState State) AcquireOrRenew(string holder, TimeSpan lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        lock (Gate)
        {
            var now = Clock();
            if (_holder == holder && now < _expires)
            {
                _expires = now + _lease; // stesso conto di Renew: il possesso continua, solo la scadenza si sposta
            }
            else if (_holder is null || now >= _expires)
            {
                // stesso conto di TryAcquire: nessun titolare valido, holder lo prende da capo
                _holder = holder;
                _since = now;
                _lease = lease;
                _expires = now + lease;
            }
            // altrimenti: un titolare valido diverso da holder gia' c'e', e resta lui - niente da concedere.

            return (_holder == holder, new EditLockState(_holder!, _since, _expires));
        }
    }
}
