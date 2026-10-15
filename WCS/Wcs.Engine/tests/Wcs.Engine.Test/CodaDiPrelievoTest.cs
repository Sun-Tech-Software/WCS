using Wcs.Engine.Items;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La coda <c>QueuedLUtoPickUp</c> e la presa in carico.
/// </summary>
/// <remarks>
/// <para>
/// La coda e' scritta dal dispositivo, dal gestore del gruppo e dai comandi dell'operatore, ed e'
/// <b>percorsa</b> da <c>Navigator.GetMissionByLuId</c> su tutti i dispositivi e dal pubblicatore
/// verso il Viewer. Era una <c>List</c> nuda.
/// </para>
/// <para>
/// Le missioni si costruiscono con <c>Guid.Empty</c>: cosi' il setter di <c>MissionId</c> trova il
/// campo gia' uguale, esce subito e non tocca il database. Con <c>new PathMission()</c> il
/// costruttore assegna un identificativo nuovo e la scrittura partirebbe.
/// </para>
/// </remarks>
public class CodaDiPrelievoTest
{
    private static PathMission MissioneInerte() => new(Guid.Empty);

    [Fact]
    public void UnaSolaPresaInCarico_QuandoLaChiedonoTuttiInsieme()
    {
        const int pretendenti = 32;

        var dispositivo = new AutomationDevice(Guid.NewGuid());
        var udc = MissioneInerte();

        dispositivo.EnqueueLuToPickUp(udc);

        var esiti = Contesa.Insieme(pretendenti, _ => dispositivo.TryTakeLuToPickUp(_ => true));

        Assert.Equal(1, esiti.Count(x => x != null));
        Assert.Same(udc, dispositivo.LUtoPick);
        Assert.Equal(0, dispositivo.QueuedLuToPickUpCount);
    }

    [Fact]
    public void NonSiPrendeNullaSeIlDispositivoNeHaGiaUnaInCarico()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());
        var prima = MissioneInerte();
        var seconda = MissioneInerte();

        dispositivo.EnqueueLuToPickUp(prima);
        Assert.NotNull(dispositivo.TryTakeLuToPickUp(_ => true));

        dispositivo.EnqueueLuToPickUp(seconda);
        Assert.Null(dispositivo.TryTakeLuToPickUp(_ => true));

        Assert.Same(prima, dispositivo.LUtoPick);
        Assert.Equal(1, dispositivo.QueuedLuToPickUpCount);
    }

    [Fact]
    public void SeIlControlloDiPrelevabilitaDiceDiNoLaUdcRestaInCoda()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());
        var udc = MissioneInerte();

        dispositivo.EnqueueLuToPickUp(udc);

        Assert.Null(dispositivo.TryTakeLuToPickUp(_ => false));
        Assert.Null(dispositivo.LUtoPick);
        Assert.Equal(1, dispositivo.QueuedLuToPickUpCount);
    }

    [Fact]
    public void PercorrereLaCodaMentreQualcunoAccodaNonSolleva()
    {
        const int giri = 3000;

        var dispositivo = new AutomationDevice(Guid.NewGuid());
        var fissa = MissioneInerte();

        dispositivo.EnqueueLuToPickUp(fissa);

        var errori = Contesa.InsiemeRaccogliendoErrori(2, indice =>
        {
            if (indice == 0)
                for (var giro = 0; giro < giri; giro++)
                {
                    var temporanea = MissioneInerte();
                    dispositivo.EnqueueLuToPickUp(temporanea);
                    dispositivo.RemoveLuToPickUp(temporanea);
                }
            else
                for (var giro = 0; giro < giri; giro++)
                {
                    // E' cio' che fa Navigator.GetMissionByLuId, che attraversa le code di tutti i
                    // dispositivi dal thread di MasterProcess.
                    var trovata = dispositivo.QueuedLUtoPickUp.FirstOrDefault(x => x != null && x.LuId == 0);
                    Assert.NotNull(trovata);
                }
        });

        Assert.Empty(errori);
    }

    [Fact]
    public void AccodareDaPiuThreadNonPerdeNessunaUdc()
    {
        const int quante = 64;

        var dispositivo = new AutomationDevice(Guid.NewGuid());

        Contesa.Insieme(quante, _ =>
        {
            dispositivo.EnqueueLuToPickUp(MissioneInerte());
            return true;
        });

        Assert.Equal(quante, dispositivo.QueuedLuToPickUpCount);
    }

    [Fact]
    public void ChiLeggeNonPuoModificareLaCodaVera()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());
        dispositivo.EnqueueLuToPickUp(MissioneInerte());

        var copia = dispositivo.QueuedLUtoPickUp;
        copia.Clear();

        Assert.Equal(1, dispositivo.QueuedLuToPickUpCount);
    }

    [Fact]
    public void LaRimozioneAPredicatoToglieSoloQuelleIndicate()
    {
        var dispositivo = new AutomationDevice(Guid.NewGuid());
        var daTogliere = MissioneInerte();
        var daTenere = MissioneInerte();

        daTogliere.MissionType = Common.Enums.MissionTypes.DefragmentCell;

        dispositivo.EnqueueLuToPickUp(daTogliere);
        dispositivo.EnqueueLuToPickUp(daTenere);

        var tolte = dispositivo.RemoveLuToPickUpWhere(x =>
            x is { MissionType: Common.Enums.MissionTypes.DefragmentCell });

        Assert.Equal(1, tolte);
        Assert.Equal(1, dispositivo.QueuedLuToPickUpCount);
        Assert.Same(daTenere, dispositivo.QueuedLUtoPickUp[0]);
    }
}
