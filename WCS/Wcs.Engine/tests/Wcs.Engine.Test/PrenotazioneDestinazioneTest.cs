using Wcs.Engine.Items;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La presa di posto sulla destinazione, voce <c>D73</c> del registro.
/// </summary>
/// <remarks>
/// <para>
/// Prima della riparazione il mittente <b>scriveva</b> <c>WaitingStepId</c> della destinazione
/// dopo aver chiesto <c>CanAcceptLu</c>: non esisteva nessun gate, quindi il primo test qui sotto
/// avrebbe avuto <b>tutti</b> vincitori invece di uno.
/// </para>
/// <para>
/// Quella riparazione rese esclusiva la <b>decisione</b>, non protetto il <b>gettone</b>: la
/// proprieta' restava pubblica e scrivibile, e ogni lettura la leggeva fuori dal lock. Il gettone
/// e il lock stanno adesso nello stesso oggetto, <c>IncomingStepReservation</c>; che non ci sia
/// piu' un'altra via per toccarlo lo tiene fermo
/// <see cref="IncomingStepReservationTest"/>.
/// </para>
/// </remarks>
public class PrenotazioneDestinazioneTest
{
    [Fact]
    public void UnSoloMittenteOttieneIlPosto_QuandoChiedonoTuttiInsieme()
    {
        const int mittenti = 64;

        var destinazione = new AutomationDevice(Guid.NewGuid());
        var passi = new Guid[mittenti];

        for (var i = 0; i < mittenti; i++)
            passi[i] = Guid.NewGuid();

        var esiti = Contesa.Insieme(mittenti, i => destinazione.TryReserveForIncomingStep(passi[i]));

        var vincitori = esiti.Count(x => x);

        Assert.Equal(1, vincitori);

        // E il gettone e' quello del vincitore, non di un altro.
        var indiceVincitore = Array.IndexOf(esiti, true);
        Assert.Equal(passi[indiceVincitore], destinazione.WaitingStepId);
    }

    [Fact]
    public void LoStessoStepPuoRiprendereIlProprioPosto()
    {
        var destinazione = new AutomationDevice(Guid.NewGuid());
        var passo = Guid.NewGuid();

        Assert.True(destinazione.TryReserveForIncomingStep(passo));

        // E' il caso del ritentativo dopo un timeout: il posto e' ancora suo.
        Assert.True(destinazione.TryReserveForIncomingStep(passo));
        Assert.Equal(passo, destinazione.WaitingStepId);
    }

    [Fact]
    public void UnAltroStepNonPuoRilasciareIlPostoAltrui()
    {
        var destinazione = new AutomationDevice(Guid.NewGuid());
        var mio = Guid.NewGuid();
        var altrui = Guid.NewGuid();

        Assert.True(destinazione.TryReserveForIncomingStep(mio));

        Assert.False(destinazione.ReleaseIncomingStep(altrui));
        Assert.Equal(mio, destinazione.WaitingStepId);

        Assert.True(destinazione.ReleaseIncomingStep(mio));
        Assert.Equal(Guid.Empty, destinazione.WaitingStepId);
    }

    [Fact]
    public void DopoIlRilascioIlPostoTornaLibero()
    {
        var destinazione = new AutomationDevice(Guid.NewGuid());
        var primo = Guid.NewGuid();
        var secondo = Guid.NewGuid();

        Assert.True(destinazione.TryReserveForIncomingStep(primo));
        Assert.False(destinazione.TryReserveForIncomingStep(secondo));

        destinazione.ReleaseIncomingStep(primo);

        Assert.True(destinazione.TryReserveForIncomingStep(secondo));
        Assert.Equal(secondo, destinazione.WaitingStepId);
    }

    [Fact]
    public void PrenotareERilasciareInsiemeNonLasciaMaiDueProprietari()
    {
        const int giri = 200;
        var destinazione = new AutomationDevice(Guid.NewGuid());

        // Due mittenti che si alternano sullo stesso posto per molti giri. L'invariante e' che il
        // gettone valga sempre Empty oppure uno dei due passi: mai un valore misto o di un terzo.
        var passoA = Guid.NewGuid();
        var passoB = Guid.NewGuid();

        var errori = Contesa.InsiemeRaccogliendoErrori(2, indice =>
        {
            var mio = indice == 0 ? passoA : passoB;

            for (var giro = 0; giro < giri; giro++)
            {
                if (destinazione.TryReserveForIncomingStep(mio))
                {
                    Assert.Equal(mio, destinazione.WaitingStepId);
                    Assert.True(destinazione.ReleaseIncomingStep(mio));
                }

                // Questa lettura cadeva fuori dal lock, ed e' l'unica delle tre asserzioni che
                // poteva vedere un valore misto: una Guid e' sedici byte copiati in due mosse da
                // otto, e la lettura poteva prendere meta' di un valore e meta' dell'altro. Ora
                // WaitingStepId inoltra a IncomingStepReservation.Current, che legge sotto lo
                // stesso lock delle scritture: il valore e' sempre intero **per costruzione**, e
                // non perche' questi duecento giri non lo abbiano colto.
                var attuale = destinazione.WaitingStepId;
                Assert.True(attuale == Guid.Empty || attuale == passoA || attuale == passoB,
                    $"gettone inatteso: {attuale}");
            }
        });

        Assert.Empty(errori);
    }

    [Fact]
    public void IlPostoNonSiPrendeConUnoStepVuoto()
    {
        var destinazione = new AutomationDevice(Guid.NewGuid());

        Assert.False(destinazione.TryReserveForIncomingStep(Guid.Empty));
        Assert.Equal(Guid.Empty, destinazione.WaitingStepId);
        Assert.False(destinazione.ReleaseIncomingStep(Guid.Empty));
    }
}
