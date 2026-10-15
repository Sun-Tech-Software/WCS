namespace Wcs.Engine.Persistence;

/// <summary>
/// Una scrittura sul database del WCS: un'operazione su un contesto, che lo scrittore esegue
/// insieme alle altre del lotto in una sola transazione.
/// </summary>
public delegate Task WcsWrite(WcsDbContext context);

/// <summary>
/// Esegue un lotto di scritture sul database del WCS: un contesto nuovo, tutte le operazioni,
/// un solo <c>SaveChanges</c>, quindi una sola transazione.
/// </summary>
/// <remarks>
/// Le operazioni della stessa entita' nello stesso lotto si incontrano nel contesto — e' cosi'
/// che <see cref="MissionStore.UpsertAsync"/> trova la scrittura precedente della stessa missione
/// — e l'ordine del lotto e' l'ordine della coda. Se una operazione o il salvataggio sollevano,
/// il lotto non e' scritto e lo scrittore lo ritenta una scrittura per volta.
/// </remarks>
internal sealed class WcsBatchExecutor(WcsDatabase database)
{
    public async Task ExecuteAsync(IReadOnlyList<WcsWrite> batch, CancellationToken cancellationToken)
    {
        await using var context = database.CreateContext();

        foreach (var write in batch)
            await write(context).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
