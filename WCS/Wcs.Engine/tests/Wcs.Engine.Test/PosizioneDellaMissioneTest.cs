using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Dove si trova una missione, e quale dispositivo risulta quindi carico. Voce <c>D74</c>.
/// </summary>
/// <remarks>
/// <para>
/// Questi test non provano che il codice di prima fosse sbagliato: provano che rifacendo
/// <c>LoadedLu</c> — da ricerca su tutte le missioni attive a lettura indicizzata — il
/// comportamento non e' cambiato. E' quello il rischio della voce <c>D74</c>: <c>LoadedLu</c> e' la
/// proprieta' piu' letta del percorso di movimento, e un indice che si disallinea fa risultare
/// scarico un dispositivo che non lo e'.
/// </para>
/// <para>
/// Il navigatore sta in <c>Recover</c> per tutta la classe: e' lo stato in cui <c>ORM.PathMission</c>
/// esce prima di scrivere, quindi le missioni si possono creare e spostare senza database.
/// </para>
/// </remarks>
public class PosizioneDellaMissioneTest : IDisposable
{
    private readonly NavigatorStatusTypes _statoPrecedente = Navigator.NavStatus;

    public PosizioneDellaMissioneTest()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        ActiveMissions.Instance.RemoveAll(_ => true);
    }

    public void Dispose()
    {
        ActiveMissions.Instance.RemoveAll(_ => true);
        Navigator.NavStatus = _statoPrecedente;
        GC.SuppressFinalize(this);
    }

    private static PathMission MissioneSu(AutomationDevice dispositivo)
    {
        var missione = new PathMission { PositionItemId = dispositivo.ItemGuid };
        ActiveMissions.Instance.Add(missione, buildRoute: false);
        return missione;
    }

    [Fact]
    public void IlDispositivoPortaLaMissioneCheDichiaraLaSuaPosizione()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());
        var missione = MissioneSu(dispositivo);

        Assert.Same(missione, dispositivo.LoadedLu);
    }

    [Fact]
    public void UnDispositivoSenzaMissioniEScarico()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());

        Assert.Null(dispositivo.LoadedLu);
    }

    [Fact]
    public void SpostandoLaMissioneSiSpostaAncheIlCarico()
    {
        var partenza = new AutomationDevice(Guid.NewGuid());
        var arrivo = new AutomationDevice(Guid.NewGuid());

        var missione = MissioneSu(partenza);

        Assert.Same(missione, partenza.LoadedLu);
        Assert.Null(arrivo.LoadedLu);

        missione.PositionItemId = arrivo.ItemGuid;

        Assert.Null(partenza.LoadedLu);
        Assert.Same(missione, arrivo.LoadedLu);
    }

    [Fact]
    public void TogliendoLaMissioneIlDispositivoTornaScarico()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());
        var missione = MissioneSu(dispositivo);

        Assert.NotNull(dispositivo.LoadedLu);

        ActiveMissions.Instance.RemoveAll(x => x.MissionId == missione.MissionId);

        Assert.Null(dispositivo.LoadedLu);
    }

    [Fact]
    public void UnaMissioneNonAttivaNonRisultaABordo()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());

        // Dichiara la posizione ma non entra fra le missioni attive: e' il caso di una UDC in coda
        // di prelievo, che non e' a bordo di niente.
        _ = new PathMission { PositionItemId = dispositivo.ItemGuid };

        Assert.Null(dispositivo.LoadedLu);
    }

    [Fact]
    public void LettureRipetuteDannoLaStessaRisposta()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());
        var missione = MissioneSu(dispositivo);

        // E' la forma che ricorre nel motore: «if (device.LoadedLu != null) device.LoadedLu.X».
        // Erano due ricerche distinte, e potevano rispondere due cose diverse.
        for (var giro = 0; giro < 200; giro++)
        {
            var prima = dispositivo.LoadedLu;
            var seconda = dispositivo.LoadedLu;

            Assert.Same(prima, seconda);
            Assert.Same(missione, prima);
        }
    }

    [Fact]
    public void QuandoDueMissioniDichiaranoLoStessoPostoLaRispostaEStabile()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());

        var prima = MissioneSu(dispositivo);
        var seconda = MissioneSu(dispositivo);

        // Vince chi ha dichiarato per ultimo, ed e' una scelta *determinata*: prima dipendeva
        // dall'ordine di enumerazione della ConcurrentDictionary, che non e' definito.
        Assert.Same(seconda, dispositivo.LoadedLu);

        for (var giro = 0; giro < 50; giro++)
            Assert.Same(seconda, dispositivo.LoadedLu);

        Assert.NotSame(prima, dispositivo.LoadedLu);
    }

    [Fact]
    public void SpostareLaMissioneMentreQualcunoLeggeNonSolleva()
    {
        const int giri = 2000;

        var uno = new AutomationDevice(Guid.NewGuid());
        var due = new AutomationDevice(Guid.NewGuid());
        var missione = MissioneSu(uno);

        // NOTA su cosa si puo' e non si puo' pretendere qui. «La missione sta su uno dei due e mai
        // su entrambi» non e' verificabile leggendo `uno.LoadedLu` e poi `due.LoadedLu`: sono due
        // letture distinte, e uno spostamento puo' cadere in mezzo. Non lo garantiva nemmeno la
        // vecchia ricerca, che erano due scansioni. Cio' che si pretende, e che vale, e' che ogni
        // singola lettura risponda una cosa sensata, e che alla fine lo stato sia coerente.
        var errori = Contesa.InsiemeRaccogliendoErrori(2, indice =>
        {
            if (indice == 0)
            {
                for (var giro = 0; giro < giri; giro++)
                    missione.PositionItemId = giro % 2 == 0 ? due.ItemGuid : uno.ItemGuid;

                // L'ultimo spostamento e' esplicito e va sul dispositivo di ARRIVO, non su quello di
                // partenza: se dipendesse dalla parita' del ciclo, un indice rimasto fermo
                // all'inizio darebbe comunque la risposta giusta alla fine, e il controllo qui sotto
                // non proverebbe niente.
                missione.PositionItemId = due.ItemGuid;
            }
            else
                for (var giro = 0; giro < giri; giro++)
                {
                    var suUno = uno.LoadedLu;
                    var suDue = due.LoadedLu;

                    Assert.True(suUno == null || ReferenceEquals(suUno, missione));
                    Assert.True(suDue == null || ReferenceEquals(suDue, missione));
                }
        });

        Assert.Empty(errori);

        // Finito il movimento, il dispositivo che la porta e' quello che il campo della missione
        // dichiara, e l'altro e' scarico. E' qui che si vedrebbe un indice rimasto indietro.
        var atteso = missione.PositionItemId == uno.ItemGuid ? uno : due;
        var altro = ReferenceEquals(atteso, uno) ? due : uno;

        Assert.Same(missione, atteso.LoadedLu);
        Assert.Null(altro.LoadedLu);
    }
}
