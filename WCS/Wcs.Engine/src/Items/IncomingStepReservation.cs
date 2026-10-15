namespace Wcs.Engine.Items;

/// <summary>
/// Il posto che un dispositivo tiene per lo step che gli sta arrivando: il <b>gettone</b> della
/// prenotazione, insieme al lock che lo protegge.
/// </summary>
/// <remarks>
/// <para>
/// Il gettone era un campo <see cref="Guid"/> dietro una proprieta' <c>{ get; set; }</c> pubblica
/// di <see cref="AutomationDevice"/>. Due metodi lo maneggiavano sotto lock, ma la proprieta'
/// restava scrivibile e leggibile da fuori, e una <see cref="Guid"/> e' <b>sedici byte copiati in
/// due mosse da otto</b>: una lettura concorrente con una scrittura puo' prendere meta' di un
/// valore e meta' dell'altro. E' stato riprodotto sul motore vero — 469 letture lacerate su 40
/// milioni, con la frattura sempre al confine degli otto byte.
/// </para>
/// <para>
/// Con <b>due</b> scrittori il danno non e' di passaggio: il valore misto <b>resta scritto</b>. Da
/// quel momento <see cref="TryReserve"/> rifiuta chiunque (il gettone non e' vuoto e non e' di
/// nessuno) e <see cref="Release"/> non libera nessuno (non corrisponde a nessuno step): il
/// dispositivo resta prenotato da un fantasma e il tratto si ferma.
/// </para>
/// <para>
/// Mettere sotto lock le sole scritture non basta: lo strato sotto e' lo stesso campo da sedici
/// byte, e ogni <b>lettura</b> fuori dal lock resta lacerabile. Il livello da riparare e' la
/// rappresentazione — questo tipo. Il campo non esce di qui: chi vuole il valore passa da
/// <see cref="Current"/>, che lo legge sotto lock e quindi sempre intero.
/// </para>
/// </remarks>
internal sealed class IncomingStepReservation
{
    private readonly object _gate = new();

    private Guid _stepId;

    /// <summary>
    /// Il gettone corrente, letto sotto lock: <see cref="Guid.Empty"/> se il posto e' libero.
    /// </summary>
    internal Guid Current
    {
        get
        {
            lock (_gate)
                return _stepId;
        }
    }

    /// <summary>
    /// Prova a prendere il posto per lo step indicato.
    /// </summary>
    /// <param name="stepId">Lo step che chiede il posto.</param>
    /// <returns>
    /// True se il posto e' stato preso — o se lo aveva gia' preso lo stesso step, che e' il caso
    /// del ritentativo. False se il posto e' di uno step diverso.
    /// </returns>
    internal bool TryReserve(Guid stepId)
    {
        if (stepId == Guid.Empty) return false;

        lock (_gate)
        {
            if (_stepId != Guid.Empty && _stepId != stepId) return false;

            _stepId = stepId;
            return true;
        }
    }

    /// <summary>
    /// Restituisce il posto, se e' ancora quello dello step indicato.
    /// </summary>
    /// <param name="stepId">Lo step che rinuncia al posto.</param>
    /// <returns>True se il posto era suo ed e' stato liberato.</returns>
    internal bool Release(Guid stepId)
    {
        if (stepId == Guid.Empty) return false;

        lock (_gate)
        {
            if (_stepId != stepId) return false;

            _stepId = Guid.Empty;
            return true;
        }
    }

    /// <summary>
    /// Libera il posto <b>chiunque</b> lo tenga.
    /// </summary>
    /// <returns>True se c'era una prenotazione da liberare.</returns>
    /// <remarks>
    /// Non e' una rinuncia: e' una revoca. Chi la usa sta dicendo «questo posto non vale piu'»
    /// mentre il mittente puo' ancora credere di averlo. Si vedano i due chiamanti — la ricarica
    /// dello stato in <c>DbInfo.RecoverFailure</c> e il gestore degli errori — dove il significato
    /// e' scritto per esteso.
    /// </remarks>
    internal bool ForceRelease()
    {
        lock (_gate)
        {
            if (_stepId == Guid.Empty) return false;

            _stepId = Guid.Empty;
            return true;
        }
    }

    /// <summary>
    /// Scrive il gettone senza chiedere niente a nessuno.
    /// </summary>
    /// <remarks>
    /// Esiste per <b>un solo</b> chiamante: il setter di <c>AutomationDevice.WaitingStep</c>, che
    /// e' anteriore alla prenotazione e scriveva il gettone cosi'. Non ha chiamanti a sua volta.
    /// Il codice nuovo chiede il posto con <see cref="TryReserve"/>: questa via lo <b>ruba</b>, ed
    /// e' esattamente cio' che la prenotazione esiste per impedire. Sta qui perche' passi almeno
    /// dal lock, non perche' vada usata.
    /// </remarks>
    /// <param name="stepId">Il nuovo gettone; <see cref="Guid.Empty"/> libera il posto.</param>
    internal void Overwrite(Guid stepId)
    {
        lock (_gate)
            _stepId = stepId;
    }

    /// <summary>
    /// Dice se il posto e' tenuto proprio da quello step, confrontando sotto lock.
    /// </summary>
    /// <param name="stepId">Lo step di cui si cerca il posto.</param>
    /// <returns>False per <see cref="Guid.Empty"/>: il posto libero non e' «di» nessuno.</returns>
    internal bool IsHeldBy(Guid stepId)
    {
        if (stepId == Guid.Empty) return false;

        lock (_gate)
            return _stepId == stepId;
    }
}
