using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Il primo <see cref="EditCommand"/>: uno spostamento di uno o piu' dispositivi (compito 5), reso
/// annullabile. <see cref="Before"/> e' la geometria catturata **prima** di scrivere lo spostamento -
/// non ricostruita dopo, e non l'origine del trascinamento ne' zero: e' quella che l'annulla deve
/// rimettere. <see cref="After"/> e' la geometria **calcolata** da <c>PlantCanvas.OnMouseUp</c> (il
/// punto di arrivo del trascinamento) ma non ancora scritta al momento in cui il comando viene
/// costruito: <see cref="ApplyAsync"/> **e'** quella scrittura, non un doppione. <c>OnMouseUp</c> non
/// scrive piu' niente da se' (compito 6): costruisce il comando e lo passa a
/// <c>CommandStack.DoAsync</c>, che chiama <see cref="ApplyAsync"/> - e' quella chiamata a fare la
/// scrittura iniziale del trascinamento del compito 5. Se in futuro il comando venisse anche rifatto
/// (oggi non ancora esposto: solo l'annulla e' dichiarato da questo compito), sarebbe la stessa
/// <see cref="ApplyAsync"/> a farlo di nuovo - non un percorso diverso, ne' codice morto.
/// </summary>
/// <remarks>
/// Una chiamata sola all'host anche per il gruppo, sia rifacendo sia annullando: la stessa regola
/// del compito 5 (mai una PUT per dispositivo quando se ne muovono piu' d'uno). La scelta fra la
/// rotta singola e quella di gruppo guarda il conteggio della lista che <see cref="WriteAsync"/> sta
/// scrivendo in quel momento (<see cref="After"/> in <see cref="ApplyAsync"/>, <see cref="Before"/>
/// in <see cref="UndoAsync"/>), non la selezione corrente sulla tela - un annulla non deve dipendere
/// da che cosa e' selezionato nel momento in cui si preme "Annulla". I due conteggi coincidono sempre
/// perche' il costruttore impone <see cref="Before"/>.Count == <see cref="After"/>.Count: senza
/// quel controllo un chiamante futuro con liste di lunghezza diversa manderebbe l'annulla sulla rotta
/// sbagliata.
/// </remarks>
public sealed record MoveItemsCommand : EditCommand
{
    public IReadOnlyList<PlantItemGeometryDto> Before { get; }
    public IReadOnlyList<PlantItemGeometryDto> After { get; }

    /// <summary>
    /// <paramref name="before"/> e <paramref name="after"/> devono avere lo stesso numero di elementi
    /// - sono la stessa selezione, prima e dopo lo spostamento: senza questo controllo il
    /// costruttore pubblico permetterebbe due liste di lunghezza diversa, e la scelta fra rotta
    /// singola e di gruppo (che guarda una sola delle due liste alla volta) smetterebbe di essere la
    /// stessa nei due versi.
    /// </summary>
    public MoveItemsCommand(IReadOnlyList<PlantItemGeometryDto> before, IReadOnlyList<PlantItemGeometryDto> after)
    {
        if (before.Count != after.Count)
            throw new ArgumentException(
                $"Before ha {before.Count} elementi, After ne ha {after.Count}: devono essere la stessa selezione.");

        Before = before;
        After = after;
    }

    public override string Description => After.Count == 1
        ? "Sposta un dispositivo"
        : $"Sposta {After.Count} dispositivi";

    public override Task ApplyAsync(WcsClient client, PlantGraph graph) => GeometryWriter.WriteAsync(client, graph, After);

    public override Task UndoAsync(WcsClient client, PlantGraph graph) => GeometryWriter.WriteAsync(client, graph, Before);

    /// <summary>
    /// La stessa scrittura di <c>PlantCanvas.OnMouseUp</c> (compito 5): singola sotto
    /// <see cref="WcsClient.SetItemGeometryAsync"/> per un dispositivo solo, di gruppo sotto
    /// <see cref="WcsClient.SetItemGeometriesAsync"/> altrimenti - una chiamata sola, mai una per
    /// dispositivo. Il modello locale si aggiorna solo dopo che la scrittura e' andata a buon fine:
    /// se <c>client.SetItemGeometry...Async</c> solleva, <see cref="PlantGraph.ApplyGeometry"/> non
    /// viene mai raggiunto, ed e' la stessa eccezione a risalire a <see cref="CommandStack"/> perche'
    /// il comando non entri (o non esca dalla) pila.
    /// </summary>
}
