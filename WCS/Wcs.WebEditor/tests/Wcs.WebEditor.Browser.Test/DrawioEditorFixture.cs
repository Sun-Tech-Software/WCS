using Microsoft.Playwright;
using Wcs.Api.Client;
using Xunit;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Quello che serve a una prova nel browser e costa troppo per rifarlo a ogni prova: un host vero
/// in ascolto su una porta vera, e un browser vero.
/// </summary>
/// <remarks>
/// L'impianto NON sta qui: ogni prova se lo semina da sola (<see cref="PlantSeed"/>), perche' due
/// prove che si passassero l'impianto l'una all'altra non direbbero piu' che cosa ha spostato chi.
/// </remarks>
public sealed class DrawioEditorFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    public WcsHostProcess Host { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = await WcsHostProcess.StartAsync();

        try
        {
            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex)
        {
            await Host.DisposeAsync();
            // Non un salto silenzioso: la prova FALLISCE, e il messaggio dice il comando che
            // rimedia. Una prova che si salta da sola quando manca l'ambiente e' peggio di una
            // prova che manca - nessuno si accorge che non sta piu' provando niente.
            throw new InvalidOperationException(
                "Il browser di Playwright non si e' potuto avviare: quasi sempre vuol dire che i browser non sono " +
                "stati scaricati su questa macchina. Si scaricano una volta sola, con lo script che il pacchetto " +
                $"copia accanto alle prove:{Environment.NewLine}" +
                $"    pwsh \"{Path.Combine(AppContext.BaseDirectory, "playwright.ps1")}\" install chromium{Environment.NewLine}" +
                $"Il guasto originale era: {ex.Message}", ex);
        }
        catch
        {
            await Host.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Smonta tutto, e smonta l'host <b>anche se la chiusura del browser va male</b>. Senza il
    /// try/finally un browser gia' morto faceva sollevare <c>CloseAsync</c> e l'host non veniva mai
    /// fermato: restava un Wcs.Host.exe vivo, con il motore acceso, la sua porta e la sua cartella
    /// temporanea, per il resto della sessione di chi lavora. E' il passo che fa piu' danno se
    /// salta, quindi e' quello che va per ultimo e senza condizioni.
    /// </summary>
    public async Task DisposeAsync()
    {
        try
        {
            try
            {
                if (Browser is not null) await Browser.CloseAsync();
            }
            finally
            {
                // Anche questo lascia un processo dietro di se' se salta: Dispose e' cio' che ferma
                // il driver di Playwright.
                _playwright?.Dispose();
            }
        }
        finally
        {
            if (Host is not null) await Host.DisposeAsync();
        }
    }

    /// <summary>Un client dell'API verso questo host, con la sua chiave: e' la via da cui la prova semina e rilegge l'impianto.</summary>
    public WcsClient CreateClient() =>
        new(new WcsClientOptions { BaseAddress = Host.BaseAddress, ApiKey = Host.ApiKey });
}
