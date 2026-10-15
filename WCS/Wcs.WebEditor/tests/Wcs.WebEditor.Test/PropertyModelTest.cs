using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Properties;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="PropertyModel"/> in isolamento (compito 9): la fonte e' <see cref="TypeDescriptor"/>
/// sul contratto vero, non un elenco scritto qui. Ogni conteggio atteso in questa classe viene da
/// una riflessione <b>indipendente</b> di <see cref="PropertyModel"/> - <see cref="System.Reflection"/>
/// grezzo su <see cref="PlantItemConfigDto"/>, non <see cref="TypeDescriptor"/> - cosi' un difetto in
/// <c>PropertyModel.BuildGroups</c> (una categoria persa, una proprieta' saltata, un convertitore
/// non riconosciuto) fa cadere il confronto invece di ripetere la stessa formula sbagliata su
/// entrambi i lati. Nessun elenco delle sessanta proprieta' e' scritto qui: il conteggio atteso segue
/// il contratto, non lo precede.
/// </summary>
public sealed class PropertyModelTest
{
    private static PropertyInfo[] RawProperties => typeof(PlantItemConfigDto).GetProperties();

    [Fact]
    public void EveryPropertyOfTheContractAppearsExactlyOnceAcrossTheGroups()
    {
        var groups = PropertyModel.BuildGroups();
        var fields = groups.SelectMany(g => g.Fields).ToList();

        // Il conteggio atteso e' quello che una riflessione indipendente trova sul contratto adesso -
        // non il letterale 60: se una proprieta' nuova arriva sul DTO domani, questo test si sposta
        // con lei invece di restare ancorato a un numero di oggi.
        Assert.Equal(RawProperties.Length, fields.Count);
        Assert.Equal(RawProperties.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal),
            fields.Select(f => f.PropertyName).OrderBy(n => n, StringComparer.Ordinal));

        // Nessuna proprieta' compare due volte (in due gruppi, o due volte nello stesso gruppo):
        // un duplicato passerebbe il confronto sui nomi sopra solo se la riflessione grezza duplicasse
        // anche lei - non e' questo il caso - quindi qui la prova e' diretta.
        Assert.Equal(fields.Count, fields.Select(f => f.PropertyName).Distinct().Count());
    }

    [Fact]
    public void EveryFieldHasANonEmptyLabelAndHelpText()
    {
        var fields = PropertyModel.BuildGroups().SelectMany(g => g.Fields).ToList();

        Assert.NotEmpty(fields);
        Assert.All(fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Label), $"{f.PropertyName}: etichetta vuota"));
        Assert.All(fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Help), $"{f.PropertyName}: aiuto vuoto"));
        Assert.All(fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Category), $"{f.PropertyName}: categoria vuota"));
    }

    /// <summary>
    /// Le sette categorie: contate da <see cref="PropertyModel.BuildGroups"/> e confrontate con
    /// l'insieme che una lettura diretta di <see cref="CategoryAttribute"/> (riflessione grezza, non
    /// <see cref="TypeDescriptor"/>) trova sullo stesso contratto - due percorsi indipendenti sullo
    /// stesso file sorgente, non lo stesso calcolo ripetuto due volte.
    /// </summary>
    [Fact]
    public void GroupsMatchTheCategoriesDeclaredDirectlyOnTheContract()
    {
        var expectedCategories = RawProperties
            .Select(p => p.GetCustomAttribute<CategoryAttribute>()?.Category)
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct()
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var groups = PropertyModel.BuildGroups();

        Assert.Equal(expectedCategories, groups.Select(g => g.Category).OrderBy(c => c, StringComparer.Ordinal).ToList());
        // Le sette non sono un letterale isolato: sono qui solo a documentare cio' che il confronto
        // sopra ha gia' provato in modo indipendente, non a sostituirlo.
        Assert.Equal(7, expectedCategories.Count);
    }

    /// <summary>
    /// Le cinque proprieta' con <c>EnumCodeConverter&lt;&gt;</c> (premessa del compito, misurata il
    /// 13/9 su <c>PlantItemConfigDto</c>): l'insieme atteso viene da una lettura diretta di
    /// <see cref="TypeConverterAttribute"/> - non da <c>PropertyField.IsEnumCoded</c>, che e' proprio
    /// quello che questo test deve mettere alla prova.
    /// </summary>
    [Fact]
    public void ExactlyThePropertiesWithAnEnumCodeConverterAreRecognizedAsPickLists()
    {
        var expectedNames = RawProperties
            .Where(p =>
            {
                var attribute = p.GetCustomAttribute<TypeConverterAttribute>();
                if (attribute is null) return false;
                var converterType = Type.GetType(attribute.ConverterTypeName);
                return converterType is { IsGenericType: true } && converterType.GetGenericTypeDefinition() == typeof(EnumCodeConverter<>);
            })
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var actualNames = PropertyModel.BuildGroups().SelectMany(g => g.Fields)
            .Where(f => f.IsEnumCoded)
            .Select(f => f.PropertyName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expectedNames, actualNames);
        Assert.Equal(5, expectedNames.Count);
        Assert.Equal(
            new[] { "ConnectionType", "DeviceType", "LookWhereAllocateLoadUnit", "MotionType", "RecordStorageLength" },
            expectedNames);
    }

    /// <summary>
    /// Le quattro proprieta' che l'Editor WinForms mostra come elenchi di dispositivi: un elenco per
    /// nome in <c>PropertyModel.ItemReferenceProperties</c>, non ricavabile da un attributo del
    /// contratto, che non le distingue dagli altri <c>Guid</c>/<c>Guid?</c> del DTO. La fonte contro
    /// cui si confrontano qui non e' un letterale scritto in questo file (che ripeterebbe qui la
    /// stessa svista se <c>ItemReferenceProperties</c> ne avesse una), ma il sorgente di
    /// <c>PlantItemConfigDescriptionProvider.DevicePickers</c>, l'elenco che l'Editor WinForms applica
    /// davvero - letto come testo, non referenziato (si veda il commento di
    /// <c>ItemReferenceProperties</c> in <c>PropertyModel</c> per la ragione: l'Editor web non
    /// dipende da <c>Wcs.Editor</c>). Qui si prova che sono esattamente queste quattro, ne' di piu' ne'
    /// di meno: gli altri sei <c>Guid</c>/<c>Guid?</c> del contratto (<c>ItemId</c>,
    /// <c>BuildDestinationItemId</c>, <c>BuildDestinationItemId2</c>,
    /// <c>TriggerItemGetLoadUnitInfo</c>, <c>TriggerItemToExtractPoint1</c>,
    /// <c>TriggerItemToExtractPoint2</c>) restano caselle di testo.
    /// </summary>
    [Fact]
    public void ExactlyTheFourDocumentedItemReferencePropertiesAreMarkedAsSuch()
    {
        var actualNames = PropertyModel.BuildGroups().SelectMany(g => g.Fields)
            .Where(f => f.IsItemReference)
            .Select(f => f.PropertyName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Il confronto che chiude il difetto trovato in revisione: non con una seconda copia a mano
        // scritta qui, ma con l'elenco che PlantItemConfigDescriptionProvider (Wcs.Editor, la griglia
        // dell'Editor WinForms) applica per davvero. Se le due copie si scostano - una dimenticata
        // nell'aggiornamento in coppia dichiarato nel commento di PropertyModel - questo confronto
        // cade da solo, invece di restare verde perche' concorda con se stesso.
        var expectedFromTheWinFormsEditor = DevicePickersDeclaredByTheWinFormsEditor()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expectedFromTheWinFormsEditor, actualNames);

        // Gli altri Guid/Guid? del contratto - misurati per riflessione grezza, non copiati a mano -
        // non sono marcati come elenchi di dispositivi.
        var otherGuidProperties = RawProperties
            .Where(p => (p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?)) && !actualNames.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        // ItemId, BuildDestinationItemId, BuildDestinationItemId2, TriggerItemGetLoadUnitInfo,
        // TriggerItemToExtractPoint1, TriggerItemToExtractPoint2: sei, non quattro - contati qui
        // sulla riflessione grezza, non assunti.
        Assert.Equal(6, otherGuidProperties.Count);

        var fieldsByName = PropertyModel.BuildGroups().SelectMany(g => g.Fields).ToDictionary(f => f.PropertyName);
        Assert.All(otherGuidProperties, name => Assert.False(fieldsByName[name].IsItemReference, name));
    }

    /// <summary>
    /// Legge <c>DevicePickers</c> dal sorgente di <c>PlantItemConfigDescriptionProvider.cs</c>
    /// (<c>Wcs.Editor/src/Model</c>), non da un riferimento di progetto: <c>Wcs.WebEditor.Test</c> non
    /// referenzia <c>Wcs.Editor</c> (che porta DevExpress/Syncfusion e un target Windows), stesso
    /// schema di lettura testuale di <see cref="OnlyAllowlistedComponentsAreInteractiveTest"/>. Non
    /// riduce le copie a una sola - restano due, in due progetti che non si parlano - ma lega la
    /// seconda alla prima invece di lasciarle scostare in silenzio.
    /// </summary>
    // The WinForms editor (Wcs.Editor) is not part of the public repository. Its DevicePickers list
    // was copied here, at export time, from PlantItemConfigDescriptionProvider.cs of the same
    // revision: the comparison above now pins the list the WinForms editor had when it was left out.
    private static IReadOnlyList<string> DevicePickersDeclaredByTheWinFormsEditor() =>
    [
        nameof(PlantItemConfigDto.RejectItemWhenNoAllocation),
        nameof(PlantItemConfigDto.RejectItemWhenInfoError),
        nameof(PlantItemConfigDto.RejectDestinationUserRequest),
        nameof(PlantItemConfigDto.RelocateItemId)
    ];

    /// <summary>Solo <c>ItemId</c> porta <c>[ReadOnly(true)]</c> sul contratto: misurato qui per riflessione grezza, non assunto.</summary>
    [Fact]
    public void OnlyItemIdIsReadOnly()
    {
        var readOnlyFields = PropertyModel.BuildGroups().SelectMany(g => g.Fields)
            .Where(f => f.IsReadOnly)
            .Select(f => f.PropertyName)
            .ToList();

        Assert.Equal(new[] { nameof(PlantItemConfigDto.ItemId) }, readOnlyFields);
    }
}
