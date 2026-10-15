using System.Reflection;
using Wcs.Engine.Items;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Il gettone della prenotazione e la porta da cui ci si arriva.
/// </summary>
/// <remarks>
/// <para>
/// Prima dell'incapsulamento <c>AutomationDevice.WaitingStepId</c> era una
/// <c>{ get; set; }</c> pubblica su una <see cref="Guid"/>: sedici byte copiati in due mosse da
/// otto. Le due letture qui sotto valgono per quello che vietano — non un comportamento, ma una
/// <b>forma</b>: se qualcuno rimette il setter o il campo grezzo, la prenotazione torna
/// scrivibile e leggibile fuori dal lock, e la lacerazione torna con lei.
/// </para>
/// <para>
/// Il comportamento vero e proprio sta in <see cref="PrenotazioneDestinazioneTest"/> e nei
/// metodi qui sotto.
/// </para>
/// </remarks>
public class IncomingStepReservationTest
{
    [Fact]
    public void TheTokenPropertyHasNoSetterAtAll()
    {
        var property = typeof(AutomationDevice).GetProperty(nameof(AutomationDevice.WaitingStepId));

        Assert.NotNull(property);

        // Non "non ha un setter pubblico": non ne ha nessuno. Un setter privato basterebbe a
        // riaprire la porta dall'interno dell'assembly, che e' esattamente da dove passavano le
        // cinque scritture nude.
        Assert.Null(property!.SetMethod);
    }

    [Fact]
    public void TheDeviceKeepsNoBareGuidFieldForTheToken()
    {
        // Prende anche il campo di appoggio generato dal compilatore per una proprieta'
        // automatica: e' quello il campo che si lacerava.
        var bareFields = typeof(AutomationDevice)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(Guid) &&
                        f.Name.Contains("Waiting", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name)
            .ToArray();

        Assert.Empty(bareFields);
    }

    [Fact]
    public void ForceReleaseTakesTheSeatFromWhoeverHoldsIt()
    {
        var destination = new AutomationDevice(Guid.NewGuid());
        var holder = Guid.NewGuid();

        Assert.True(destination.TryReserveForIncomingStep(holder));

        // La revoca non porta con se' nessuno step: e' il caso della ricarica dello stato, dove
        // chi teneva il posto non esiste piu' e non c'e' niente da confrontare.
        Assert.True(destination.ForceReleaseIncomingStep());
        Assert.Equal(Guid.Empty, destination.WaitingStepId);

        // E il posto e' davvero libero, non solo azzerato: un altro step lo prende.
        Assert.True(destination.TryReserveForIncomingStep(Guid.NewGuid()));
    }

    [Fact]
    public void ForceReleaseSaysWhetherThereWasAnythingToRelease()
    {
        var destination = new AutomationDevice(Guid.NewGuid());

        Assert.False(destination.ForceReleaseIncomingStep());

        Assert.True(destination.TryReserveForIncomingStep(Guid.NewGuid()));
        Assert.True(destination.ForceReleaseIncomingStep());
        Assert.False(destination.ForceReleaseIncomingStep());
    }

    [Fact]
    public void OnlyTheHolderIsRecognisedAsWaitingForItsStep()
    {
        var destination = new AutomationDevice(Guid.NewGuid());
        var mine = Guid.NewGuid();

        // Il posto libero non e' «di» nessuno, nemmeno dello step vuoto: e' la domanda che il
        // setter di RunningStepId fa su tutti i dispositivi dell'impianto, e un si' di troppo
        // libererebbe la prenotazione di un altro.
        Assert.False(destination.IsWaitingForStep(mine));
        Assert.False(destination.IsWaitingForStep(Guid.Empty));

        Assert.True(destination.TryReserveForIncomingStep(mine));

        Assert.True(destination.IsWaitingForStep(mine));
        Assert.False(destination.IsWaitingForStep(Guid.NewGuid()));
        Assert.False(destination.IsWaitingForStep(Guid.Empty));
    }

    [Fact]
    public void TheTwinPropertySetterStillGoesThroughTheReservation()
    {
        // WaitingStep e' la proprieta' gemella, anteriore alla prenotazione e senza chiamanti in
        // tutto il monorepo. Resta superficie pubblica, quindi qui si prova che scrive il gettone
        // passando dal tipo nuovo — non che sia una via consigliata: scavalca la prenotazione
        // altrui, ed e' la porta descritta nella voce D121 del registro.
        var destination = new AutomationDevice(Guid.NewGuid());
        var holder = Guid.NewGuid();
        var intruder = new MissionStep { StepId = Guid.NewGuid() };

        Assert.True(destination.TryReserveForIncomingStep(holder));

        destination.WaitingStep = intruder;
        Assert.Equal(intruder.StepId, destination.WaitingStepId);

        destination.WaitingStep = null;
        Assert.Equal(Guid.Empty, destination.WaitingStepId);
    }
}
