using Wcs.Engine.Items;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La lista <c>DevicesReadyToTransferLu</c>: l'unico stato di un dispositivo che <b>altri</b>
/// dispositivi scrivono.
/// </summary>
/// <remarks>
/// Era una <c>List</c> nuda. Il mittente si iscriveva dal proprio thread mentre il destinatario la
/// percorreva dal suo: <c>InvalidOperationException</c> dentro <c>ExecuteStep</c>, oppure
/// un'iscrizione persa. I due test sulla contesa qui sotto sono quelli che la vecchia forma non
/// supera.
/// </remarks>
public class IscrizioniAlTrasferimentoTest
{
    [Fact]
    public void NessunaIscrizioneSiPerde_QuandoSiIscrivonoTuttiInsieme()
    {
        const int mittenti = 64;

        var destinazione = new AutomationDevice(Guid.NewGuid());
        var chiSiIscrive = new AutomationDevice[mittenti];

        for (var i = 0; i < mittenti; i++)
            chiSiIscrive[i] = new AutomationDevice(Guid.NewGuid());

        var esiti = Contesa.Insieme(mittenti, i => destinazione.AddDeviceReadyToTransferLu(chiSiIscrive[i]));

        Assert.All(esiti, Assert.True);
        Assert.Equal(mittenti, destinazione.DevicesReadyToTransferLuCount);
        Assert.Equal(mittenti, destinazione.DevicesReadyToTransferLu.Distinct().Count());
    }

    [Fact]
    public void PercorrereLeIscrizioniMentreQualcunoSiIscriveNonSolleva()
    {
        const int giri = 3000;

        var destinazione = new AutomationDevice(Guid.NewGuid());
        var mittente = new AutomationDevice(Guid.NewGuid());
        var altro = new AutomationDevice(Guid.NewGuid());

        destinazione.AddDeviceReadyToTransferLu(altro);

        var errori = Contesa.InsiemeRaccogliendoErrori(2, indice =>
        {
            if (indice == 0)
                for (var giro = 0; giro < giri; giro++)
                {
                    destinazione.AddDeviceReadyToTransferLu(mittente);
                    destinazione.RemoveDeviceReadyToTransferLu(mittente);
                }
            else
                for (var giro = 0; giro < giri; giro++)
                {
                    // E' cio' che fanno GetLUsToReceive e ProcessReadyDevicesWithoutLu: percorrono
                    // la lista mentre i mittenti vi entrano ed escono.
                    foreach (var item in destinazione.DevicesReadyToTransferLu)
                        Assert.NotNull(item);
                }
        });

        Assert.Empty(errori);
    }

    [Fact]
    public void LaStessaIscrizioneNonSiRipete()
    {
        var destinazione = new AutomationDevice(Guid.NewGuid());
        var mittente = new AutomationDevice(Guid.NewGuid());

        Assert.True(destinazione.AddDeviceReadyToTransferLu(mittente));
        Assert.False(destinazione.AddDeviceReadyToTransferLu(mittente));
        Assert.Equal(1, destinazione.DevicesReadyToTransferLuCount);
    }

    [Fact]
    public void ChiLeggeNonPuoModificareLaListaVera()
    {
        var destinazione = new AutomationDevice(Guid.NewGuid());
        var mittente = new AutomationDevice(Guid.NewGuid());

        destinazione.AddDeviceReadyToTransferLu(mittente);

        // Il getter restituisce una copia: e' cio' che mette al sicuro le letture, ed e' anche la
        // trappola della riparazione -- un Add sulla copia compila e non fa niente.
        var copia = destinazione.DevicesReadyToTransferLu;
        copia.Clear();

        Assert.Equal(1, destinazione.DevicesReadyToTransferLuCount);
    }

    [Fact]
    public void LaPotaturaTogliesoloQuelliCheIlPredicatoIndica()
    {
        var destinazione = new AutomationDevice(Guid.NewGuid());
        var daTogliere = new AutomationDevice(Guid.NewGuid());
        var daTenere = new AutomationDevice(Guid.NewGuid());

        destinazione.AddDeviceReadyToTransferLu(daTogliere);
        destinazione.AddDeviceReadyToTransferLu(daTenere);

        destinazione.PruneDevicesReadyToTransferLu(x => x.ItemGuid == daTogliere.ItemGuid);

        Assert.Equal(1, destinazione.DevicesReadyToTransferLuCount);
        Assert.Contains(daTenere, destinazione.DevicesReadyToTransferLu);
    }

    [Fact]
    public void LoSvuotamentoTogliaTutte()
    {
        var destinazione = new AutomationDevice(Guid.NewGuid());

        for (var i = 0; i < 5; i++)
            destinazione.AddDeviceReadyToTransferLu(new AutomationDevice(Guid.NewGuid()));

        destinazione.ClearDevicesReadyToTransferLu();

        Assert.Equal(0, destinazione.DevicesReadyToTransferLuCount);
    }
}
