using Microsoft.Extensions.Configuration;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <c>ResolveHostAddress</c> (compito 2, corretto in revisione) trasforma l'indirizzo di
/// <b>ascolto</b> di Kestrel in uno a cui l'Editor web possa davvero <b>collegarsi</b>. La versione
/// originale passava da <c>Uri.TryCreate</c>, che rifiuta un host jolly (<c>*</c>, <c>+</c>) e non
/// riconosce <c>[::]</c> come lo stesso host di <c>::</c> - due difetti invisibili perche' nessun
/// test chiamava il metodo, e in <c>appsettings.json</c> c'e' solo il caso che per coincidenza
/// funzionava (<c>0.0.0.0</c>). Questi test lo esercitano su ogni forma incontrata in revisione.
/// </summary>
public sealed class ResolveHostAddressTest
{
    private static IConfiguration ConfigurationWith(string? httpUrl = null, string? httpsUrl = null)
    {
        var values = new Dictionary<string, string?>();
        if (httpUrl is not null) values["Kestrel:Endpoints:Http:Url"] = httpUrl;
        if (httpsUrl is not null) values["Kestrel:Endpoints:Https:Url"] = httpsUrl;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void AllInterfacesBecomesLocalhost()
    {
        var resolved = WebEditorExtensions.ResolveHostAddress(ConfigurationWith("http://0.0.0.0:5580"));
        Assert.Equal(new Uri("http://localhost:5580"), resolved);
    }

    [Fact]
    public void StarWildcardBecomesLocalhost()
    {
        // Uri.TryCreate rifiuta "*" come host: prima della correzione questo caso cadeva nel
        // ripiego silenzioso su localhost:5580, per coincidenza corretto solo perche' la porta di
        // prova e' proprio 5580 (si veda AWildcardWithADifferentPortKeepsThatPort).
        var resolved = WebEditorExtensions.ResolveHostAddress(ConfigurationWith("http://*:5580"));
        Assert.Equal(new Uri("http://localhost:5580"), resolved);
    }

    [Fact]
    public void PlusWildcardBecomesLocalhost()
    {
        var resolved = WebEditorExtensions.ResolveHostAddress(ConfigurationWith("http://+:5580"));
        Assert.Equal(new Uri("http://localhost:5580"), resolved);
    }

    [Fact]
    public void Ipv6AnyAddressBecomesLocalhost()
    {
        // Uri.Host per questo letterale vale "[::]" con le parentesi, non "::": il confronto
        // originale non scattava mai e l'indirizzo tornava intatto (il difetto critico).
        var resolved = WebEditorExtensions.ResolveHostAddress(ConfigurationWith("http://[::]:5580"));
        Assert.Equal(new Uri("http://localhost:5580"), resolved);
    }

    [Fact]
    public void AWildcardWithADifferentPortKeepsThatPort()
    {
        var resolved = WebEditorExtensions.ResolveHostAddress(ConfigurationWith("http://0.0.0.0:6600"));
        Assert.Equal(new Uri("http://localhost:6600"), resolved);
    }

    [Fact]
    public void MultipleEndpointsSeparatedBySemicolonUseTheFirst()
    {
        var resolved = WebEditorExtensions.ResolveHostAddress(
            ConfigurationWith("http://+:5580;https://+:5443"));
        Assert.Equal(new Uri("http://localhost:5580"), resolved);
    }

    [Fact]
    public void OnlyAnHttpsUrlIsReadToo()
    {
        var resolved = WebEditorExtensions.ResolveHostAddress(ConfigurationWith(httpsUrl: "https://0.0.0.0:5443"));
        Assert.Equal(new Uri("https://localhost:5443"), resolved);
    }

    [Fact]
    public void HttpUrlWinsOverHttpsWhenBothArePresent()
    {
        var resolved = WebEditorExtensions.ResolveHostAddress(
            ConfigurationWith(httpUrl: "http://0.0.0.0:5580", httpsUrl: "https://0.0.0.0:5443"));
        Assert.Equal(new Uri("http://localhost:5580"), resolved);
    }

    [Fact]
    public void ARealHostNameIsKeptAsIs()
    {
        var resolved = WebEditorExtensions.ResolveHostAddress(ConfigurationWith("http://wcs-host:5580"));
        Assert.Equal(new Uri("http://wcs-host:5580"), resolved);
    }

    [Fact]
    public void MissingConfigurationThrowsInsteadOfFallingBackSilently()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => WebEditorExtensions.ResolveHostAddress(ConfigurationWith()));
        Assert.Contains("Kestrel:Endpoints:Http:Url", ex.Message);
    }

    [Fact]
    public void AnUnparsableValueThrowsInsteadOfFallingBackSilently()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => WebEditorExtensions.ResolveHostAddress(ConfigurationWith("non-un-url")));
        Assert.Contains("non-un-url", ex.Message);
    }
}
