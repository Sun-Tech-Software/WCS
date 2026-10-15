using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// La rete automatica: un gettone appeso si scioglie da solo, uno tenuto di proposito no.
/// </summary>
/// <remarks>
/// <para>
/// La normalizzazione che c'era guardava <b>solo se lo step esiste ancora</b>. Il 2026-09-09 lo
/// step esisteva — resto' in <c>MissionSteps</c> per tutta l'ora e mezza — e il gettone non si
/// sciolse mai: <c>SH01.0001.0001</c> rifiuto' 2662 esecuzioni e l'unico rimedio fu riavviare il
/// motore.
/// </para>
/// <para>
/// <b>La condizione che distingue i due casi.</b> Un identificativo di passo puo' restare addosso
/// di proposito — il ritentativo di gruppo lo usa per sapere che cosa ripetere — e scioglierlo
/// sarebbe peggio del guasto. I due casi si distinguono da due cose che il codice scrive da se':
/// </para>
/// <list type="bullet">
/// <item>lo <b>stato</b>. Chi tiene il gettone di proposito e' in <c>Warning</c> o in <c>Error</c>
/// (e' scritto su <c>IsBusyWithCommand</c> e su <c>ReleaseAfterFailedStep</c>); chi lo sta usando
/// davvero e' in <c>Loading</c>, <c>Unloading</c>, <c>Moving</c> o <c>Busy</c>, perche' ogni punto
/// che prende il gettone scrive lo stato di lavorazione nella stessa sequenza. Resta <c>Ready</c>
/// solo chi non sta facendo niente;</item>
/// <item>il <b>mittente del comando</b>. <c>MissionStep.RunningNode</c> viene scritto insieme a
/// <c>StartStep</c>, sempre nell'istruzione che precede l'invio al campo, in tutti e cinque i punti
/// che spediscono un movimento. Se non porta l'identificativo di questo dispositivo, da questo
/// dispositivo non e' mai partito nessun comando per quello step.</item>
/// </list>
/// <para>
/// Perche' scattare da <c>ExecuteStep</c> e non dal getter di <c>RunningStepId</c>: una lettura che
/// scioglie il gettone in base allo stato lo scioglierebbe anche nell'istante in cui un prelievo lo
/// sta prendendo, e due comandi partirebbero per lo stesso movimento. Qui la rete e' un passo
/// deliberato, fuori dalle letture, e chiede anche che nessuna esecuzione sia in corso.
/// </para>
/// </remarks>
public sealed class HungRunningStepTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public HungRunningStepTest()
    {
        MissionSteps.Instance.RemoveAll(_ => true);
        Navigator.NavStatus = NavigatorStatusTypes.Run;
    }

    public void Dispose()
    {
        MissionSteps.Instance.RemoveAll(_ => true);
        Navigator.NavStatus = _previousStatus;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Uno step con una missione tutta sua. Con <c>MissionId</c> vuoto lo step apparteneva a ogni
    /// missione dall'identificativo vuoto: il Run del costruttore sveglia i dispositivi ancora vivi di
    /// altre classi, uno di loro rimetteva in gioco una missione cosi', e la sua costruzione della
    /// rotta (RemoveMissionSteps, cioe' RemoveAll per MissionId) si portava via anche lo step della
    /// prova - e con lui il gettone, che il getter di RunningStepId azzera quando lo step non c'e'
    /// piu'. Era il rosso intermittente di ACommandInFlightIsNotUndone (voce D144 del registro).
    /// </summary>
    private static MissionStep AStepOfItsOwnMission() => new() { StepId = Guid.NewGuid(), MissionId = Guid.NewGuid() };

    /// <summary>
    /// Un dispositivo con addosso il gettone di uno step che esiste ancora: la terna del guasto
    /// osservato, <c>Ready</c> piu' <c>RunningStepId</c> piu' nessun movimento in atto.
    /// </summary>
    private static (AutomationDevice Device, MissionStep Step) ADeviceHoldingAToken()
    {
        var device = new AutomationDevice(Guid.NewGuid());
        var step = AStepOfItsOwnMission();

        MissionSteps.Instance.Add(step);
        device.RunningStepId = step.StepId;

        Assert.Equal(step.StepId, device.RunningStepId);

        return (device, step);
    }

    [Fact]
    public async Task AHungTokenIsReleasedAndTheDeviceExecutesAgain()
    {
        var (device, _) = ADeviceHoldingAToken();

        // Nessun comando e' mai partito da qui per quello step: RunningNode non e' stato scritto.
        Assert.True(device.IsBusyWithCommand);

        Assert.True(await device.ExecuteStep());
        Assert.Equal(Guid.Empty, device.RunningStepId);
        Assert.False(device.IsBusyWithCommand);
    }

    /// <summary>
    /// Il gettone appeso si porta via anche la prenotazione che era stata presa con lui: per un
    /// prelievo la destinazione e' il dispositivo stesso, ed e' quella che il 2026-09-09 lo rendeva
    /// irraggiungibile anche per chiunque altro.
    /// </summary>
    [Fact]
    public async Task ReleasingTheTokenGivesBackTheReservationTakenWithIt()
    {
        var (device, step) = ADeviceHoldingAToken();

        PathDevices.Instance.Add(device);

        try
        {
            Assert.True(device.TryReserveForIncomingStep(step.StepId));
            Assert.Equal(step.StepId, device.WaitingStepId);

            Assert.True(await device.ExecuteStep());

            Assert.Equal(Guid.Empty, device.RunningStepId);
            Assert.Equal(Guid.Empty, device.WaitingStepId);
        }
        finally
        {
            PathDevices.Instance.Clear();
        }
    }

    /// <summary>
    /// Il comando e' partito davvero e la risposta del campo non e' ancora arrivata: il gettone
    /// non si tocca, o si comanderebbe due volte lo stesso pezzo di impianto.
    /// </summary>
    [Theory]
    [InlineData(DeviceStatusTypes.Loading)]
    [InlineData(DeviceStatusTypes.Unloading)]
    [InlineData(DeviceStatusTypes.Moving)]
    [InlineData(DeviceStatusTypes.Busy)]
    public async Task ACommandInFlightIsNotUndone(DeviceStatusTypes status)
    {
        var (device, step) = ADeviceHoldingAToken();

        step.RunningNode = device.ItemGuid;
        device.Status = status;

        Assert.False(await device.ExecuteStep());
        Assert.Equal(step.StepId, device.RunningStepId);
    }

    /// <summary>
    /// Il caso delicato: l'identificativo tenuto <b>di proposito</b>. Un dispositivo in
    /// <c>Warning</c> o in <c>Error</c> conserva il suo <c>RunningStepId</c> perche' il ritentativo
    /// di gruppo sappia che cosa ripetere, e scioglierlo lo spegnerebbe.
    /// </summary>
    [Theory]
    [InlineData(DeviceStatusTypes.Warning)]
    [InlineData(DeviceStatusTypes.Error)]
    public async Task ADeviceThatIsRetryingIsNotUndone(DeviceStatusTypes status)
    {
        var (device, step) = ADeviceHoldingAToken();

        device.Status = status;

        // Uno stato d'errore non e' mai «impegnato»: l'esecuzione e' ammessa comunque. Cio' che
        // deve restare e' il gettone.
        Assert.True(await device.ExecuteStep());
        Assert.Equal(step.StepId, device.RunningStepId);
    }

    /// <summary>
    /// Lo stesso vale per il dispositivo che si sta rimettendo in ordine: non e' <c>Ready</c>, e la
    /// rete non lo tocca. L'esecuzione resta rifiutata — <c>Recovering</c> non e' fra gli stati che
    /// <c>IsBusyWithCommand</c> perdona — ed e' proprio il punto: il gettone resta al suo posto.
    /// </summary>
    [Fact]
    public async Task ARecoveringDeviceIsNotUndone()
    {
        var (device, step) = ADeviceHoldingAToken();

        device.Status = DeviceStatusTypes.Recovering;

        Assert.False(await device.ExecuteStep());
        Assert.Equal(step.StepId, device.RunningStepId);
    }

    /// <summary>
    /// Un comando partito da qui, con il dispositivo tornato <c>Ready</c> mentre la risposta del
    /// campo non e' ancora arrivata: e' la seconda condizione a tenerlo, non lo stato.
    /// </summary>
    [Fact]
    public async Task ATokenOfACommandThisDeviceSentIsNotUndone()
    {
        var (device, step) = ADeviceHoldingAToken();

        step.RunningNode = device.ItemGuid;

        Assert.Equal(DeviceStatusTypes.Ready, device.Status);
        Assert.False(await device.ExecuteStep());
        Assert.Equal(step.StepId, device.RunningStepId);
    }

    /// <summary>
    /// Il secondo passo della rete: una <b>prenotazione</b> tenuta per uno step che non esiste piu'.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TryReleaseHungRunningStep</c> esce alla prima riga se <c>RunningStepId</c> e' vuoto, e non
    /// vedeva questo stato — che e' proprio quello in cui la prenotazione non ha nessuna via di
    /// restituzione: il setter di <c>RunningStepId</c> libera il prenotato solo quando un gettone
    /// finisce, e qui nessuno ce l'ha.
    /// </para>
    /// <para>
    /// Lo stato si produce da solo: far sparire lo step dalla collezione azzera il gettone — il
    /// getter risponde <c>Guid.Empty</c> quando lo step non c'e' piu' — e lascia il posto preso.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AHungReservationWithoutATokenIsReleased()
    {
        var destination = new AutomationDevice(Guid.NewGuid());
        var step = AStepOfItsOwnMission();

        MissionSteps.Instance.Add(step);
        Assert.True(destination.TryReserveForIncomingStep(step.StepId));

        // Lo step sparisce: da qui in poi nessuno tiene il gettone, e la prenotazione e' appesa.
        MissionSteps.Instance.Remove(step);
        Assert.Equal(Guid.Empty, destination.RunningStepId);
        Assert.Equal(step.StepId, destination.WaitingStepId);

        Assert.True(await destination.ExecuteStep());

        Assert.Equal(Guid.Empty, destination.WaitingStepId);
    }

    /// <summary>
    /// La meta' che deve non scattare: una prenotazione il cui step esiste ancora e' un posto tenuto
    /// per un movimento che deve arrivare, e scioglierla farebbe consegnare due volte. Senza questo
    /// collaudo, una rete che liberasse sempre passerebbe quello qui sopra.
    /// </summary>
    [Fact]
    public async Task AReservationOfAStepThatStillExistsIsNotUndone()
    {
        var destination = new AutomationDevice(Guid.NewGuid());
        var step = AStepOfItsOwnMission();

        MissionSteps.Instance.Add(step);
        Assert.True(destination.TryReserveForIncomingStep(step.StepId));

        Assert.True(await destination.ExecuteStep());

        Assert.Equal(step.StepId, destination.WaitingStepId);
    }
}
