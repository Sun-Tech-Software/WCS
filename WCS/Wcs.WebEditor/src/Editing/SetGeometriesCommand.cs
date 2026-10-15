using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Un cambio di geometria reso annullabile, condiviso da ogni gesto del compito B4 che scrive
/// <c>PlantItemGeometryDto</c> senza essere uno spostamento puro: ridimensionare con una maniglia,
/// ruotare con la maniglia, "stessa larghezza/altezza/dimensione", spostare con le frecce. Stessa
/// forma di <see cref="MoveItemsCommand"/> (prima e dopo, una scrittura sola anche di gruppo) ma con
/// una <see cref="Description"/> propria di ogni gesto invece del testo fisso "Sposta" - un
/// ridimensionamento annullato con il titolo "Annulla: Sposta un dispositivo" sarebbe un dato falso
/// nel pulsante.
/// </summary>
/// <remarks>
/// <see cref="MoveItemsCommand"/> non e' stato generalizzato per riusare questa forma: e' gia'
/// provato da <c>DragTest</c>/<c>UndoTest</c>/<c>CommandStackTest</c>, e toccarlo per un compito che
/// non cambia il trascinamento avrebbe rischiato quelle prove per un guadagno di sole poche righe.
/// I due tipi restano paralleli, non uno sull'altro.
/// </remarks>
public sealed record SetGeometriesCommand : EditCommand
{
    public IReadOnlyList<PlantItemGeometryDto> Before { get; }
    public IReadOnlyList<PlantItemGeometryDto> After { get; }
    public override string Description { get; }

    /// <summary><paramref name="before"/> e <paramref name="after"/> devono avere lo stesso numero di elementi - la stessa selezione, prima e dopo il gesto.</summary>
    public SetGeometriesCommand(string description,
        IReadOnlyList<PlantItemGeometryDto> before, IReadOnlyList<PlantItemGeometryDto> after)
    {
        if (before.Count != after.Count)
            throw new ArgumentException(
                $"Before ha {before.Count} elementi, After ne ha {after.Count}: devono essere la stessa selezione.");

        Description = description;
        Before = before;
        After = after;
    }

    public override Task ApplyAsync(WcsClient client, PlantGraph graph) => GeometryWriter.WriteAsync(client, graph, After);

    public override Task UndoAsync(WcsClient client, PlantGraph graph) => GeometryWriter.WriteAsync(client, graph, Before);

    /// <summary>
    /// Una scrittura sola, singola o di gruppo secondo il conteggio della lista che si sta
    /// scrivendo in quel momento - mai una PUT per dispositivo (la stessa regola del compito 5,
    /// ripetuta qui perche' ogni gesto di questo compito puo' toccare piu' di un dispositivo in un
    /// colpo solo, come "stessa dimensione" o lo spostamento con le frecce di un gruppo selezionato).
    /// </summary>
}
