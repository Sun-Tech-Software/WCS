using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Duplica un dispositivo (compito 7): stessa configurazione, identificativo nuovo, spostato di
/// mezza cella perche' il duplicato non nasca esattamente sotto l'originale, dove sarebbe
/// invisibile. Non duplica le giunzioni - il brief parla solo di "stessa configurazione", non di
/// topologia, e il duplicato nasce quindi scollegato: chi lo vuole collegato traccia le giunzioni
/// come farebbe con un dispositivo creato da zero.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Mezza cella", dichiarata: meta' del riquadro del dispositivo stesso.</b> Il compito 5 lascia
/// <c>SnapStep</c> a zero (nessuna griglia impostata - l'Editor WinForms non ne ha mai avuta una
/// misurata) e questo compito non lo cambia: senza una spaziatura di griglia da cui prendere "una
/// cella", lo scostamento usa meta' larghezza e meta' altezza del dispositivo di origine
/// (<see cref="PlantCanvas.DuplicateSelectedAsync"/> lo calcola) - la sola misura che l'impianto
/// gia' offre. Un dispositivo senza dimensione (larghezza o altezza nulla o zero, o non collocato)
/// non avrebbe da cui prendere meta' cella: <c>PlantCanvas</c> usa in quel caso uno scostamento
/// minimo dichiarato, non zero, cosi' il duplicato non nasce comunque invisibile sotto l'originale.
/// </para>
/// <para>
/// <b>L'ASI si azzera, come fa gia' l'Editor WinForms.</b> <see cref="CopyConfigWithNewId"/> copia
/// ogni proprieta' tranne <see cref="PlantItemConfigDto.ItemId"/> e poi porta
/// <see cref="PlantItemConfigDto.Asi"/> a <c>"XXXX"</c> - lo stesso valore e la stessa ragione di
/// <c>Wcs.Editor.Model.ConfigCopier.Copy</c> (<c>WCS/Wcs.Editor/src/Model/ConfigCopier.cs</c>):
/// l'ASI individua l'indirizzo fisico del dispositivo, e un duplicato che lo tenesse identico
/// all'originale sarebbe due righe che dichiarano lo stesso indirizzo. Il codice qui e' una copia
/// indipendente, non un riuso di quella classe: <c>Wcs.WebEditor</c> non referenzia <c>Wcs.Editor</c>
/// (progetto WinForms, verificato in <c>Wcs.WebEditor.csproj</c>: solo <c>Wcs.Api.Client</c> e
/// <c>Wcs.Api.Contracts</c>) - ma <see cref="Wcs.WebEditor.Test.BoundaryTest"/>, a dispetto di quanto
/// diceva questo commento, non e' la guardia che lo tiene cosi': quella
/// classe verifica solo il confine verso <c>Wcs.Engine</c> (il motore, "la porta verso il
/// gestionale"), non verso <c>Wcs.Editor</c>. Se in futuro qualcuno aggiungesse il riferimento per
/// riusare <c>ConfigCopier</c>, nessun test di questa suite lo segnalerebbe.
/// </para>
/// <para>
/// <b>Stesso limite di <see cref="CreateItemCommand"/> sulle due scritture, e stessa rotta.</b> La
/// copia nasce con <c>POST /plant/items</c> (creare e' un gesto suo da questo lotto: la PUT della
/// configurazione risponde ora <c>404</c> sul dispositivo assente); configurazione e geometria
/// (quando c'e') restano due chiamate HTTP indipendenti - si veda il remarks di
/// <see cref="CreateItemCommand"/>, valido qui allo stesso modo.
/// </para>
/// </remarks>
public sealed record DuplicateItemCommand : EditCommand
{
    public PlantItemConfigDto NewConfig { get; }
    public PlantItemGeometryDto? NewGeometry { get; }
    private readonly string _sourceName;

    /// <summary>
    /// <paramref name="newGeometry"/> e' nullo quando l'originale non era collocato (<c>X</c>/<c>Y</c>
    /// nulli): il duplicato allora non riceve una geometria - non e' un dato inventato - e resta fra
    /// i "Dispositivi non collocati" come l'originale, finche' non lo si posiziona a mano.
    /// </summary>
    public DuplicateItemCommand(PlantItemConfigDto newConfig, PlantItemGeometryDto? newGeometry, string sourceName)
    {
        if (newGeometry is not null && newGeometry.ItemId != newConfig.ItemId)
            throw new ArgumentException(
                $"La geometria porta l'ItemId {newGeometry.ItemId}, la configurazione {newConfig.ItemId}: devono essere lo stesso dispositivo nuovo.");

        NewConfig = newConfig;
        NewGeometry = newGeometry;
        _sourceName = sourceName;
    }

    public override string Description => _sourceName is { Length: > 0 } ? $"Duplica {_sourceName}" : "Duplica dispositivo";

    public override async Task ApplyAsync(WcsClient client, PlantGraph graph)
    {
        await client.CreateItemAsync(NewConfig);
        // Queue item 21: the duplicate exists with this configuration from here on.
        graph.ApplyConfiguration(NewConfig);
        var created = await client.GetItemAsync(NewConfig.ItemId)
            ?? throw new InvalidOperationException(
                $"The duplicate just created ({NewConfig.ItemId}) is not on the host any more: the duplication is inconsistent.");

        if (NewGeometry is not null)
        {
            var confirmation = await client.SetItemGeometryAsync(
                GeometryWriter.ToUpdate(NewGeometry, created.GeometryVersion));
            created = GeometryWriter.ApplyConfirmation(created, NewGeometry, confirmation);
        }

        graph.AddItem(created);
    }

    /// <summary>Disfare un duplicato e' cancellarlo: nessuna giunzione da rimettere, il duplicato non ne ha mai avute (vedi il remarks della classe).</summary>
    public override async Task UndoAsync(WcsClient client, PlantGraph graph)
    {
        await client.DeleteItemAsync(NewConfig.ItemId);
        graph.RemoveItem(NewConfig.ItemId);
    }

    /// <summary>
    /// Copia ogni proprieta' scrivibile di <paramref name="source"/> tranne <c>ItemId</c>, che
    /// diventa <paramref name="newItemId"/>, e riporta <c>Asi</c> a <c>"XXXX"</c> (vedi il remarks
    /// della classe). Per riflessione, come <c>Wcs.Editor.Model.ConfigCopier.Copy</c>: le proprieta'
    /// di <see cref="PlantItemConfigDto"/> sono una sessantina, e un elenco scritto a mano qui
    /// smetterebbe di essere vero al primo campo nuovo aggiunto la' senza essere aggiunto anche qui.
    /// </summary>
    public static PlantItemConfigDto CopyConfigWithNewId(PlantItemConfigDto source, Guid newItemId)
    {
        var copy = new PlantItemConfigDto();
        foreach (var property in typeof(PlantItemConfigDto).GetProperties().Where(p => p.CanWrite && p.Name != nameof(PlantItemConfigDto.ItemId)))
            property.SetValue(copy, property.GetValue(source));

        copy.ItemId = newItemId;
        copy.Asi = "XXXX";
        return copy;
    }
}
