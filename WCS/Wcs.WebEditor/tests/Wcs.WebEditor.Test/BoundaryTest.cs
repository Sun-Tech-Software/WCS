using System.Reflection;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Il cancello dell'Editor web: parla all'host come qualunque client, e non conosce il motore.
/// </summary>
/// <remarks>
/// E' il gemello di <c>Wcs.Engine.Test.EngineDependenciesTest</c>, che tiene chiusa la porta verso
/// il gestionale, e vale per la stessa ragione: un confine sorvegliato a mano si perde alla prima
/// comodita'. Si guardano due cose, e servono tutt'e due - il manifest non elenca un riferimento
/// presente ma inutilizzato, quindi la sola prima sarebbe verde a porta aperta.
/// </remarks>
public sealed class BoundaryTest
{
    private static readonly Assembly WebEditor = typeof(WebEditorExtensions).Assembly;

    [Fact]
    public void TheWebEditorDoesNotReferenceTheEngine()
    {
        var referenced = WebEditor.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain("Wcs.Engine", referenced);
    }

    [Fact]
    public void TheEngineIsNotEvenInTheOutputFolder()
    {
        Assert.Empty(Directory.GetFiles(AppContext.BaseDirectory, "Wcs.Engine.dll"));
    }
}
