using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Le scritture su una stessa missione non si sovrappongono, e la riga scritta per ultima porta
/// tutti i valori scritti prima di lei.
/// </summary>
/// <remarks>
/// Ogni setter di <c>PathMission</c> riscrive l'<b>intera</b> riga di <c>MISSION_LU</c> con i
/// valori del momento. Due thread che scrivono due campi insieme componevano due righe intere, e
/// quella eseguita per ultima poteva essere stata composta per prima, senza il campo dell'altro:
/// in memoria c'erano entrambi i valori, sul database uno solo. La sonda dell'ORM riceve la
/// missione nell'istante in cui la riga sarebbe stata composta, cioe' quando i parametri vengono
/// legati. Voce D81 del registro.
/// </remarks>
public class ScrittureDellaMissioneTest : IDisposable
{
    private readonly NavigatorStatusTypes _statoPrecedente = Navigator.NavStatus;

    public ScrittureDellaMissioneTest()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
    }

    public void Dispose()
    {
        ORM.PathMission.SondaScrittura = null;
        Navigator.NavStatus = _statoPrecedente;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void OgniSetterPersistenteComponeLaRigaSottoIlLockEIncludeLeScritturePrecedenti()
    {
        var missione = new PathMission();
        var righe = new List<(MissionStatusTypes stato, string? descrizione)>();
        // Solo le scritture di questa missione: la sonda e' statica, e riceve anche quelle che il
        // lavoro di sfondo di altre prove fa su missioni ancora vive, da altri thread (coda, voce 42).
        ORM.PathMission.SondaScrittura = m =>
        {
            if (ReferenceEquals(m, missione)) righe.Add((m.Status, m.StatusDescription));
        };

        missione.Status = MissionStatusTypes.Run;
        missione.StatusDescription = "in marcia";

        Assert.Equal([(MissionStatusTypes.Run, ""), (MissionStatusTypes.Run, "in marcia")], righe);
    }

    [Fact]
    public void UnaScritturaFuoriDalLockDellaMissioneVieneRifiutata()
    {
        var missione = new PathMission();

        var errore = Assert.Throws<InvalidOperationException>(() => ORM.PathMission.Update(missione));

        Assert.Contains("lock della missione", errore.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnValoreUgualeNonScriveLaRiga()
    {
        var missione = new PathMission();
        var scritture = 0;
        ORM.PathMission.SondaScrittura = m =>
        {
            if (ReferenceEquals(m, missione)) Interlocked.Increment(ref scritture);
        };

        missione.StatusDescription = "prima";
        missione.StatusDescription = "prima";
        missione.StatusDescription = "seconda";

        Assert.Equal(2, scritture);
    }
}
