using Wcs.Api.Client;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Una modifica reversibile fatta sulla tela: sa rifarsi (<see cref="ApplyAsync"/>) e disfarsi
/// (<see cref="UndoAsync"/>) sull'host vero, e aggiornare il modello locale di conseguenza. Vive
/// dentro un <see cref="CommandStack"/>, che decide quando chiamare l'uno o l'altro - questo tipo
/// non sa nulla della pila che lo contiene.
/// </summary>
/// <remarks>
/// Deviazione dal piano (autorizzata): il brief della tappa 4b dichiara la firma
/// <c>Task ApplyAsync(WcsClient)</c>. Ogni comando di questo compito deve pero' anche aggiornare il
/// modello locale (<see cref="PlantGraph"/>) dopo una scrittura riuscita - esattamente come faceva
/// gia' <c>PlantCanvas.OnMouseUp</c> con <see cref="PlantGraph.ApplyGeometry"/> prima di questo
/// compito - o la tela mostrerebbe una posizione diversa da quella appena scritta sull'host: un dato
/// falso a video. La firma qui sotto porta quindi anche <see cref="PlantGraph"/>, non solo
/// <see cref="WcsClient"/>.
/// </remarks>
public abstract record EditCommand
{
    /// <summary>Una riga per l'utente: che cosa questo comando ha fatto. Mostrata come <c>title</c> del pulsante "Annulla" (<c>PlantCanvas.UndoButtonTitle</c>) attraverso <see cref="CommandStack.UndoDescription"/>.</summary>
    public abstract string Description { get; }

    /// <summary>Rifa' il comando: scrive sull'host e, solo se la scrittura riesce, aggiorna <paramref name="graph"/>.</summary>
    public abstract Task ApplyAsync(WcsClient client, PlantGraph graph);

    /// <summary>Disfa il comando: scrive lo stato precedente sull'host e, solo se la scrittura riesce, aggiorna <paramref name="graph"/>.</summary>
    public abstract Task UndoAsync(WcsClient client, PlantGraph graph);
}
