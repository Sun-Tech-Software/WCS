using System.Reflection;
using Xunit.Sdk;

[assembly: Wcs.Engine.Test.BackgroundWorkBarrier]

namespace Wcs.Engine.Test;

/// <summary>
/// Prima di ogni test aspetta che il lavoro di sfondo lanciato dai test precedenti sia finito.
/// </summary>
/// <remarks>
/// <para>
/// Il motore lancia senza attenderli alcuni lavori che scrivono sullo store e sullo scrittore di
/// stato <b>correnti quando arrivano</b> (<see cref="BackgroundWork"/>). I test condividono quello
/// stato statico, e il parallelismo fra classi e' spento (Impostazioni.cs) proprio per questo: ma
/// un lavoro lanciato da un test e ancora in volo alla sua fine arrivava nel test successivo. Sotto
/// carico una trentina di classi ne lasciavano, e le prove che leggono lo store intero o contano le
/// scritture fallivano a intermittenza.
/// </para>
/// <para>
/// L'attesa sta qui, in un punto solo, e non nel <c>Dispose</c> di ogni classe: copre anche il
/// lavoro che parte dai <c>Dispose</c> stessi (una missione tolta lancia il suo
/// <c>PositionWork</c>) e dai costruttori, che girano prima di questo attributo. Un lavoro che non
/// finisce entro il tempo massimo fa fallire il test che sta per partire, con il conto in chiaro:
/// meglio un rosso che dice dove guardare che un rosso a caso piu' avanti.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class BackgroundWorkBarrierAttribute : BeforeAfterTestAttribute
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public override void Before(MethodInfo methodUnderTest)
    {
        if (!BackgroundWork.WaitUntilIdle(Timeout))
            throw new InvalidOperationException(
                $"{BackgroundWork.Count} lavori di sfondo dei test precedenti ancora in corso dopo {Timeout.TotalSeconds} s");
    }
}
