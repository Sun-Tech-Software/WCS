using System.ComponentModel;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Properties;

/// <summary>
/// Un campo del pannello di configurazione (compito 9): <see cref="Descriptor"/> resta la fonte vera
/// - valore corrente, tipo, convertitore - <see cref="Category"/>/<see cref="Label"/>/<see cref="Help"/>
/// sono solo le tre letture che <see cref="PropertyDescriptor"/> ricava gia' dagli stessi attributi
/// del contratto (<c>[Category]</c>, <c>[DisplayName]</c>, <c>[Description]</c>), non una copia.
/// </summary>
public sealed record PropertyField(
    PropertyDescriptor Descriptor,
    string Category,
    string Label,
    string Help,
    bool IsReadOnly,
    bool IsItemReference)
{
    public string PropertyName => Descriptor.Name;

    /// <summary>
    /// Vero per le cinque proprieta' che portano <c>[TypeConverter(typeof(EnumCodeConverter&lt;&gt;))]</c>
    /// sul contratto (<c>DeviceType</c>, <c>ConnectionType</c>, <c>MotionType</c>,
    /// <c>LookWhereAllocateLoadUnit</c>, <c>RecordStorageLength</c>): riconosciuto dal tipo del
    /// convertitore che <see cref="Descriptor"/> porta gia' - non da un elenco di nomi scritto qui,
    /// che smetterebbe di essere vero se il contratto cambiasse convertitore senza toccare questo file.
    /// </summary>
    public bool IsEnumCoded
    {
        get
        {
            var converterType = Descriptor.Converter.GetType();
            return converterType.IsGenericType && converterType.GetGenericTypeDefinition() == typeof(EnumCodeConverter<>);
        }
    }

    public object? GetValue(PlantItemConfigDto config) => Descriptor.GetValue(config);

    public void SetValue(PlantItemConfigDto config, object? value) => Descriptor.SetValue(config, value);
}

public sealed record PropertyGroup(string Category, IReadOnlyList<PropertyField> Fields);

/// <summary>
/// Legge <see cref="PlantItemConfigDto"/> per riflessione e produce i gruppi ordinati che
/// <c>PropertyPanel</c> mostra (compito 9): la fonte e' <see cref="TypeDescriptor"/>, lo stesso
/// meccanismo <c>System.ComponentModel</c> con cui la griglia delle proprieta' dell'Editor WinForms
/// legge il dispositivo dal vivo, applicato qui al DTO. Un campo nuovo sul contratto - o una
/// categoria nuova, o un convertitore nuovo - compare da solo, con i propri testi, senza toccare
/// questo file: nessun elenco delle sessanta proprieta' e' scritto qui, solo la lettura di quello che
/// il contratto dichiara davvero.
/// </summary>
public static class PropertyModel
{
    /// <summary>
    /// Le quattro proprieta' che nell'Editor WinForms sono elenchi di dispositivi: hanno bisogno
    /// dell'impianto per esistere, quindi il contratto stesso non le distingue dagli altri sei
    /// <c>Guid</c>/<c>Guid?</c> del DTO (<c>ItemId</c>, <c>BuildDestinationItemId</c>,
    /// <c>BuildDestinationItemId2</c>, <c>TriggerItemGetLoadUnitInfo</c>,
    /// <c>TriggerItemToExtractPoint1</c>, <c>TriggerItemToExtractPoint2</c> restano caselle di testo,
    /// non elenchi). Non c'e' un attributo da cui ricavare l'elenco per riflessione qui, quindi va
    /// dichiarato per nome - ma la fonte vera non e' il commento in prosa di
    /// <see cref="PlantItemConfigDto"/>: e' codice, ed e' altrove. L'Editor WinForms applica lo
    /// stesso elenco per davvero in
    /// <c>Wcs.Editor.Model.PlantItemConfigDescriptionProvider.DevicePickers</c> (un
    /// <c>TypeDescriptionProvider</c> registrato solo li', che l'Editor web non puo' referenziare
    /// senza dipendere da <c>Wcs.Editor</c>). Questo elenco e quello sono due copie a mano dello
    /// stesso insieme di nomi, da aggiornare in coppia: <c>PropertyModelTest</c> lega la
    /// seconda alla prima leggendo il sorgente di <c>PlantItemConfigDescriptionProvider</c>, cosi' che
    /// un aggiornamento dimenticato da un lato faccia cadere il test invece di restare silenzioso.
    /// </summary>
    private static readonly HashSet<string> ItemReferenceProperties =
    [
        nameof(PlantItemConfigDto.RejectItemWhenNoAllocation),
        nameof(PlantItemConfigDto.RejectItemWhenInfoError),
        nameof(PlantItemConfigDto.RejectDestinationUserRequest),
        nameof(PlantItemConfigDto.RelocateItemId),
    ];

    /// <summary>
    /// The SAME device, as an independent snapshot to change without touching
    /// <paramref name="source"/>: every writable property copied by reflection, the identifier
    /// included - unlike <c>DuplicateItemCommand.CopyConfigWithNewId</c>, which leaves it out on
    /// purpose (a duplicate wants a new identifier). By reflection for that method's own reason: some
    /// sixty properties, and a list written by hand would stop being true at the first field added to
    /// the contract. One copy for the properties panel and the device list alike.
    /// </summary>
    public static PlantItemConfigDto Copy(PlantItemConfigDto source)
    {
        var copy = new PlantItemConfigDto();
        foreach (var property in typeof(PlantItemConfigDto).GetProperties().Where(p => p.CanWrite))
            property.SetValue(copy, property.GetValue(source));
        return copy;
    }

    /// <summary>
    /// I gruppi ordinati per categoria e, dentro ciascuno, per etichetta: ne' <c>[Category]</c> ne'
    /// <c>[DisplayName]</c> portano un numero d'ordine sul contratto, quindi l'ordine alfabetico e'
    /// una scelta di questo compito (come <c>DefaultCreatedItemSize</c> in <c>PlantCanvas</c>) - non
    /// una misura - ma una scelta stabile: lo stesso ordine a ogni apertura, indipendente dalla
    /// posizione delle proprieta' nel file sorgente del contratto.
    /// </summary>
    public static IReadOnlyList<PropertyGroup> BuildGroups()
    {
        var fields = TypeDescriptor.GetProperties(typeof(PlantItemConfigDto))
            .Cast<PropertyDescriptor>()
            .Select(BuildField)
            .ToList();

        return fields
            .GroupBy(f => f.Category, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PropertyGroup(g.Key, g.OrderBy(f => f.Label, StringComparer.Ordinal).ToList()))
            .ToList();
    }

    private static PropertyField BuildField(PropertyDescriptor descriptor) => new(
        descriptor,
        descriptor.Category,
        descriptor.DisplayName,
        descriptor.Description,
        descriptor.IsReadOnly,
        ItemReferenceProperties.Contains(descriptor.Name));
}
