using Microsoft.Extensions.Configuration.Json;

namespace Wcs.Host;

/// <summary>
/// Il file dei segreti dell'impianto: <c>appsettings.Secrets.json</c>, non tracciato, che
/// <c>appsettings.json</c> indica come posto dove mettere <c>Wcs:ApiKey</c> e <c>Wms:ApiKey</c>.
/// </summary>
public static class SecretsFileConfiguration
{
    /// <summary>Il nome del file, uguale nei due host del confine.</summary>
    public const string FileName = "appsettings.Secrets.json";

    /// <summary>
    /// Aggiunge il file dei segreti <b>dopo</b> gli <c>appsettings*.json</c> e <b>prima</b> delle
    /// variabili d'ambiente.
    /// </summary>
    /// <remarks>
    /// L'ordine conta quanto la presenza, e <c>AddJsonFile</c> da solo non lo da': accoda, quindi
    /// il file finirebbe <i>dopo</i> le variabili d'ambiente e le sovrascriverebbe. Qui la sorgente
    /// si accoda e poi si sposta subito dopo l'ultimo <c>appsettings*.json</c>: cosi' sovrascrive i
    /// valori di esempio del file tracciato, e le variabili d'ambiente restano l'ultima parola —
    /// che e' l'altra strada che le istruzioni di installazione indicano.
    /// </remarks>
    public static IConfigurationBuilder AddSecretsFile(this IConfigurationBuilder configuration)
    {
        configuration.AddJsonFile(FileName, optional: true, reloadOnChange: true);

        var sources = configuration.Sources;
        var added = sources[^1];
        sources.RemoveAt(sources.Count - 1);

        var lastSettings = -1;
        for (var i = 0; i < sources.Count; i++)
            if (sources[i] is JsonConfigurationSource { Path: { } path } &&
                path.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase))
                lastSettings = i;

        sources.Insert(lastSettings + 1, added);

        return configuration;
    }
}
