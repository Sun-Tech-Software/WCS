using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// La regola: un elemento impegnato <b>rifiuta</b> l'esecuzione, non la rimanda.
/// </summary>
/// <remarks>
/// «Impegnato» sono due cose: un comando in volo verso il campo (<c>RunningStepId</c> valorizzato)
/// oppure un'altra esecuzione gia' dentro il semaforo. Il rifiuto e' visibile perche'
/// <c>ExecuteStep</c> restituisce <c>false</c>, ed e' cosi' che il comando dell'operatore si chiude
/// in errore invece che in silenzio.
/// </remarks>
public class RifiutoEsecuzioneTest : IDisposable
{
    private readonly NavigatorStatusTypes _statoPrecedente = Navigator.NavStatus;

    public RifiutoEsecuzioneTest()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Run;
    }

    public void Dispose()
    {
        Navigator.NavStatus = _statoPrecedente;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RifiutaSeImpegnatoConIlCampo()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());
        var passo = Guid.NewGuid();

        // Lo step deve esistere davvero: il getter di RunningStepId restituisce Guid.Empty quando
        // lo step non e' piu' fra quelli vivi, quindi senza questa riga il dispositivo risulterebbe
        // libero e il test proverebbe il contrario di cio' che dice il nome.
        //
        // E il comando dev'essere partito davvero da qui: RunningNode e' scritto insieme a
        // StartStep, nell'istruzione che precede l'invio al campo. Senza, la rete del gettone
        // appeso lo scioglierebbe - «impegnato con il campo» vuol dire che al campo si e' parlato,
        // non solo che un identificativo e' rimasto addosso.
        MissionSteps.Instance.Add(new MissionStep { StepId = passo, RunningNode = dispositivo.ItemGuid });
        dispositivo.RunningStepId = passo;

        Assert.Equal(passo, dispositivo.RunningStepId);
        Assert.False(await dispositivo.ExecuteStep());
    }

    [Fact]
    public async Task AmmetteSeLibero()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());

        Assert.Equal(Guid.Empty, dispositivo.RunningStepId);
        Assert.True(await dispositivo.ExecuteStep());
    }

    [Fact]
    public async Task RifiutaLaSecondaChiamataMentreLaPrimaEInCorso()
    {
        using var dispositivo = new AutomationDevice(Guid.NewGuid());

        // Il semaforo preso come lo prende un'esecuzione in corso. Prima lo si teneva con
        // ExecuteStep(int.MaxValue), che restava dentro il semaforo per tutta la pausa: dalla voce 41
        // la pausa si fa prima del semaforo, e non lo tiene piu'.
        Assert.True(dispositivo.TryHoldExecutionForTesting());

        try
        {
            Assert.False(await dispositivo.ExecuteStep());
        }
        finally
        {
            dispositivo.ReleaseExecutionHoldForTesting();
        }

        // Restituito il semaforo, la stessa chiamata passa: il rifiuto era il semaforo, non altro.
        Assert.True(await dispositivo.ExecuteStep());
    }

    /// <summary>
    /// La pausa chiesta da chi chiama (<c>ExecuteStep(500)</c> dopo ogni step completato) si fa
    /// prima del semaforo: mentre dura, il dispositivo non risulta impegnato, il commit del cambio di
    /// destinazione puo' prendere il semaforo, e un'altra esecuzione entra invece di essere
    /// rifiutata. Prima la pausa si faceva col semaforo preso, e per mezzo secondo rifiutava tutti.
    /// </summary>
    [Fact]
    public async Task LaPausaNonTieneIlSemaforo()
    {
        using var dispositivo = new AutomationDevice(Guid.NewGuid());
        var pausa = new PausaTrattenuta(dispositivo);

        var inPausa = dispositivo.ExecuteStep(500);
        await pausa.Cominciata.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            Assert.Equal(500, pausa.Durata);
            Assert.False(dispositivo.ExecuteStepIsRunning);
            Assert.False(dispositivo.IsBusyWithCommand);

            Assert.True(dispositivo.TryHoldExecutionForTesting());
            dispositivo.ReleaseExecutionHoldForTesting();

            Assert.True(await dispositivo.ExecuteStep());
        }
        finally
        {
            pausa.Finisci();
        }

        // Finita la pausa, la chiamata fa il suo giro: il dispositivo e' libero, e passa.
        Assert.True(await inPausa.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// La marcia si guarda dopo la pausa: un passaggio a Stop mentre la pausa dura ferma
    /// l'esecuzione. Prima si guardava solo all'ingresso, e una chiamata entrata in Run eseguiva
    /// mezzo secondo dopo anche col navigatore gia' fermo.
    /// </summary>
    [Fact]
    public async Task LaMarciaSiGuardaDopoLaPausa()
    {
        using var dispositivo = new AutomationDevice(Guid.NewGuid());
        var pausa = new PausaTrattenuta(dispositivo);

        var inPausa = dispositivo.ExecuteStep(500);
        await pausa.Cominciata.WaitAsync(TimeSpan.FromSeconds(30));

        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        pausa.Finisci();

        Assert.False(await inPausa.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// La chiusura del dispositivo durante la pausa chiude la chiamata con <c>false</c>, senza
    /// eccezioni, e il semaforo resta libero (D41: una cancellazione nella pausa lo lasciava preso
    /// per sempre). La pausa vera, senza cuciture: la si interrompe con la chiusura.
    /// </summary>
    [Fact]
    public async Task LaChiusuraDuranteLaPausaChiudeLaChiamata()
    {
        using var dispositivo = new AutomationDevice(Guid.NewGuid());

        var inPausa = dispositivo.ExecuteStep(int.MaxValue);
        Assert.False(inPausa.IsCompleted);

        dispositivo.Dispose();

        Assert.False(await inPausa.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(dispositivo.TryHoldExecutionForTesting());
        dispositivo.ReleaseExecutionHoldForTesting();
    }

    /// <summary>
    /// La pausa di un dispositivo trattenuta dalla prova: si sa quando comincia e la si fa finire
    /// quando serve, senza contare sul tempo. Rispetta la chiusura del dispositivo come la pausa vera.
    /// </summary>
    private sealed class PausaTrattenuta
    {
        private readonly TaskCompletionSource _cominciata = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fine = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PausaTrattenuta(AutomationDevice dispositivo)
        {
            dispositivo.ExecutionPauseForTesting = (durata, token) =>
            {
                Durata = durata;
                _cominciata.TrySetResult();
                return _fine.Task.WaitAsync(token);
            };
        }

        public Task Cominciata => _cominciata.Task;

        public int Durata { get; private set; }

        public void Finisci() => _fine.TrySetResult();
    }

    [Fact]
    public async Task RifiutaSeIlNavigatoreNonEInMarcia()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        try
        {
            var dispositivo = new AutomationDevice(Guid.NewGuid());
            Assert.False(await dispositivo.ExecuteStep());
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Run;
        }
    }

    [Fact]
    public async Task TornaAdAmmettereQuandoLoStepSiChiude()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());
        var passo = Guid.NewGuid();

        // Come sopra: lo step esiste e il comando e' partito da questo dispositivo, altrimenti il
        // gettone verrebbe sciolto dalla rete invece di bloccare.
        MissionSteps.Instance.Add(new MissionStep { StepId = passo, RunningNode = dispositivo.ItemGuid });
        dispositivo.RunningStepId = passo;

        Assert.False(await dispositivo.ExecuteStep());

        // E' cio' che devono fare i percorsi d'errore: uno step concluso male non e' uno step in
        // volo, e finche' resta addosso il dispositivo non riparte.
        dispositivo.RunningStepId = Guid.Empty;

        Assert.True(await dispositivo.ExecuteStep());
    }
    [Theory]
    [InlineData(DeviceStatusTypes.Unloading)]
    [InlineData(DeviceStatusTypes.Loading)]
    [InlineData(DeviceStatusTypes.Moving)]
    [InlineData(DeviceStatusTypes.Busy)]
    public async Task RifiutaMentreStaLavorando(DeviceStatusTypes stato)
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());

        dispositivo.Status = stato;

        Assert.True(dispositivo.IsBusyWithCommand);
        Assert.False(await dispositivo.ExecuteStep());
    }

    [Fact]
    public void LoScaricoRendeIlDispositivoImpegnatoAncheSenzaStepInVolo()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());

        dispositivo.Status = DeviceStatusTypes.Unloading;

        // E' il caso che la sola guardia su RunningStepId non prendeva: quando il campo comunica lo
        // scarico lo step e' gia' stato chiuso, quindi il dispositivo risulterebbe libero mentre
        // sta ancora lavorando.
        Assert.Equal(Guid.Empty, dispositivo.RunningStepId);
        Assert.True(dispositivo.IsBusyWithCommand);
    }

    [Theory]
    [InlineData(DeviceStatusTypes.Ready)]
    [InlineData(DeviceStatusTypes.Warning)]
    [InlineData(DeviceStatusTypes.Error)]
    [InlineData(DeviceStatusTypes.Recovering)]
    public void NonEImpegnatoQuandoNonStaEseguendo(DeviceStatusTypes stato)
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());

        dispositivo.Status = stato;

        // Warning ed Error restano fuori perche' da li' si esce riprovando, e Recovering perche'
        // e' un dispositivo che si rimette in ordine, non uno che esegue: il prelievo dalla coda lo
        // ammette esplicitamente.
        Assert.False(dispositivo.IsBusyWithCommand);
    }
    [Fact]
    public void SeIlMittenteFallisceLaDestinazioneTornaDisponibile()
    {
        var destinazione = new AutomationDevice(Guid.NewGuid());
        var passo = Guid.NewGuid();

        // Com'e' durante un trasferimento: la destinazione e' occupata e il posto e' preso.
        destinazione.Status = DeviceStatusTypes.Busy;
        Assert.True(destinazione.TryReserveForIncomingStep(passo));

        destinazione.ReleaseAfterFailedStep(passo);

        // Deve poter accettare da un altro elemento: stato libero e posto restituito.
        Assert.Equal(DeviceStatusTypes.Ready, destinazione.Status);
        Assert.False(destinazione.IsBusyWithCommand);
        Assert.Equal(Guid.Empty, destinazione.WaitingStepId);

        var altroPasso = Guid.NewGuid();
        Assert.True(destinazione.TryReserveForIncomingStep(altroPasso));
    }

    [Fact]
    public void LiberareNonSovrascriveUnErroreSopraggiunto()
    {
        var destinazione = new AutomationDevice(Guid.NewGuid());
        var passo = Guid.NewGuid();

        destinazione.TryReserveForIncomingStep(passo);
        destinazione.Status = DeviceStatusTypes.Error;

        destinazione.ReleaseAfterFailedStep(passo);

        // Il posto si restituisce comunque, ma lo stato di errore vale piu' della Busy che si
        // stava togliendo: azzerarlo qui nasconderebbe un guasto del campo.
        Assert.Equal(DeviceStatusTypes.Error, destinazione.Status);
        Assert.Equal(Guid.Empty, destinazione.WaitingStepId);
    }
}
