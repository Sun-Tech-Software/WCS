using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// <c>appsettings.Secrets.json</c> e' il posto che <c>appsettings.json</c> indica per
/// <c>Wcs:ApiKey</c> e <c>Wms:ApiKey</c>, e nessuno lo apriva: <c>AddJsonFile</c> non compariva in
/// tutto il repository.
/// </summary>
/// <remarks>
/// Chi installa segue l'istruzione, mette la chiave nel file, riavvia — e l'host si rifiuta di
/// partire nominando un file che non veniva letto. L'ordine conta quanto la presenza: dopo gli
/// <c>appsettings*.json</c>, cosi' il file dei segreti sovrascrive il valore di esempio, e prima
/// delle variabili d'ambiente, che restano l'ultima parola.
/// </remarks>
[Collection("host")]
public sealed class SecretsFileIsReadTest(HostFixture host)
{
    [Fact]
    public void TheSecretsFileIsReadAfterTheSettingsAndBeforeTheEnvironment()
    {
        host.CreateAuthenticatedClient();

        var providers = ((IConfigurationRoot)host.Services.GetRequiredService<IConfiguration>()).Providers.ToList();

        var secrets = providers.FindIndex(p =>
            p is JsonConfigurationProvider { Source.Path: SecretsFileConfiguration.FileName });
        var settings = providers.FindLastIndex(p =>
            p is JsonConfigurationProvider { Source.Path: not null } json &&
            json.Source.Path!.StartsWith("appsettings.", StringComparison.Ordinal) &&
            json.Source.Path != SecretsFileConfiguration.FileName);
        // L'ULTIMA sorgente di variabili d'ambiente: le prime due le aggiunge il costruttore
        // dell'host per la propria configurazione (i prefissi DOTNET_ e ASPNETCORE_), e stanno
        // prima di qualunque appsettings.
        var environment = providers.FindLastIndex(p => p is EnvironmentVariablesConfigurationProvider);

        Assert.True(secrets >= 0, "appsettings.Secrets.json non e' fra le sorgenti di configurazione");
        Assert.True(settings >= 0, "gli appsettings.json non sono fra le sorgenti di configurazione");
        Assert.True(environment >= 0, "le variabili d'ambiente non sono fra le sorgenti di configurazione");

        Assert.True(secrets > settings, "il file dei segreti deve venire dopo gli appsettings");
        Assert.True(secrets < environment, "le variabili d'ambiente devono restare l'ultima parola");
    }
}
