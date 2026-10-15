using System.Diagnostics;
using Log.Interface;
using Log.Windows;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.Viewer.Items;

namespace Wcs.Viewer;

/// <summary>La copia locale dello stato del motore, riempita dall'API e tenuta aggiornata dall'hub. Prima passava da ZeroMQ.</summary>
public sealed class Connector
{
    internal static WcsClient? Client;
    internal static PlantEventsConnection? Events;

    public static Uri? Address { get; private set; }

    public static async Task ConnectAsync(Uri address, string apiKey)
    {
        Address = address;
        var options = new WcsClientOptions { BaseAddress = address, ApiKey = apiKey };
        Client = new WcsClient(options);
        Events = new PlantEventsConnection(options);
        Events.ItemStatusChanged += Navigator.ApplyItemStatus;
        Events.NavigatorStatusChanged += Navigator.ApplyStatus;
        Events.Watchdog += _ => Navigator.IsConnected = true;
        Events.Reconnected += ReloadAfterReconnect;
        Events.Closed += () => Navigator.IsConnected = false;
        await Events.StartAsync();
        await ReloadAllAsync();
    }

    /// <summary>
    /// Ricarica lo stato completo dopo una riconnessione dell'hub.
    /// </summary>
    /// <remarks>
    /// Era <c>_ = ReloadAllAsync()</c>: un fallimento della ricarica finiva in un <c>Task</c> che
    /// nessuno osservava, e il Viewer restava marcato connesso con un modello vecchio. E' la stessa
    /// forma che l'Editor ha gia' dato a <c>EditorSession.ReloadAfterReconnect</c>: l'errore si
    /// scrive e <see cref="Navigator.IsConnected"/> resta falso, che e' quello che l'interfaccia
    /// mostra.
    /// </remarks>
    private static async void ReloadAfterReconnect()
    {
        try
        {
            await ReloadAllAsync();
        }
        catch (Exception ex)
        {
            Navigator.IsConnected = false;
            WriteLog($"Reload after reconnect failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Il registro del Viewer: la stessa sorgente dell'Editor, perche' e' lo stesso prodotto e i
    /// messaggi si leggono insieme.
    /// </summary>
    /// <remarks>
    /// Qui c'era <c>Trace.TraceWarning</c>, e in tutto il Viewer - come in tutto l'Editor - non e'
    /// registrato nessun <c>TraceListener</c>: in un binario rilasciato quel messaggio andava nel
    /// nulla. Il Viewer e' una libreria e non ha una finestra da cui parlare, quindi la via e' la
    /// stessa che <c>EditorSession.ReloadAfterReconnect</c> usa per lo stesso identico guasto: un
    /// <c>Log.Windows.Writer</c> sul registro eventi.
    /// <para>
    /// Si costruisce pigramente e dentro un <c>try</c>: <c>EventLog.CreateEventSource</c> vuole i
    /// diritti di amministratore la prima volta che la sorgente non esiste, e una libreria non deve
    /// cadere perche' non e' riuscita a scrivere un avviso. Quando non c'e' resta il
    /// <c>Debug.Print</c>, cioe' quello che si aveva prima.
    /// </para>
    /// <para>
    /// La meta' visibile all'operatore non e' questa riga: e' <see cref="Navigator.IsConnected"/>
    /// che resta falso, ed e' quello che l'interfaccia mostra. Questa dice <b>perche'</b>.
    /// </para>
    /// </remarks>
    private static readonly Lazy<IWriter?> LogWriter = new(() =>
    {
        try
        {
            // La sorgente non segue la rinomina dell'area, come in FrmEDITOR: cambiarla
            // spezzerebbe in due la storia dei messaggi gia' scritti.
            return new Writer("KazamControl", "KazamControlLog");
        }
        catch (Exception ex)
        {
            Debug.Print($"Registro eventi non disponibile: {ex.Message}");
            return null;
        }
    });

    private static void WriteLog(string description)
    {
        try
        {
            LogWriter.Value?.WriteLog(description, LogEntryType.Warning);
        }
        catch (Exception ex)
        {
            // Il log non deve mai sostituire il guasto vero con il proprio.
            Debug.Print($"Log non riuscito: {ex.Message}");
        }
    }

    public static async Task DisconnectAsync()
    {
        if (Events != null) await Events.DisposeAsync();
        Client?.Dispose();
        Events = null;
        Client = null;
    }

    /// <summary>
    /// Ricarica completa: si chiedono tutte e cinque le liste e si pubblicano insieme.
    /// </summary>
    /// <remarks>
    /// Pubblicarle una alla volta faceva vedere, per la durata delle cinque chiamate, i
    /// dispositivi nuovi con le giunzioni vecchie. Le cinque richieste restano in fila come
    /// prima; cambia solo il momento in cui il risultato diventa visibile.
    /// </remarks>
    public static async Task ReloadAllAsync()
    {
        var missions = await LoadActiveMissionsAsync();
        var devices = await LoadAutomationDevicesAsync();
        var steps = await LoadMissionStepsAsync();
        var junctions = await LoadPathJunctionsAsync();
        var statuses = await LoadViewItemsStatusAsync();
        Navigator.ReplaceAll(missions, devices, steps, junctions, statuses);
        Navigator.ApplyStatus(await Client!.GetStatusAsync());
    }

    public static async Task<bool> GetActiveMissions()
    {
        if (Client == null) return false;
        Navigator.ReplaceActiveMissions(await LoadActiveMissionsAsync());
        return true;
    }

    public static async Task<bool> GetAutomationDevices()
    {
        if (Client == null) return false;
        Navigator.ReplacePathDevices(await LoadAutomationDevicesAsync());
        return true;
    }

    /// <summary>
    /// Tutti gli step delle missioni attive, in una richiesta sola: chiederli missione per
    /// missione voleva dire una richiesta HTTP per missione, e con un impianto pieno sono decine.
    /// </summary>
    public static async Task<bool> GetMissionSteps()
    {
        if (Client == null) return false;
        Navigator.ReplaceMissionSteps(await LoadMissionStepsAsync());
        return true;
    }

    public static async Task<bool> GetPathJunctions()
    {
        if (Client == null) return false;
        Navigator.ReplacePathJunctions(await LoadPathJunctionsAsync());
        return true;
    }

    public static async Task<bool> GetViewItemsStatus()
    {
        if (Client == null) return false;
        Navigator.ReplaceViewPathDevices(await LoadViewItemsStatusAsync());
        return true;
    }

    // Le cinque letture, separate dalla pubblicazione: la ricarica completa le vuole tutte e
    // cinque prima di scambiare, le cinque pubbliche ne pubblicano una sola.
    private static async Task<List<PathMission?>> LoadActiveMissionsAsync() =>
        (await Client!.GetMissionsAsync()).Select(PathMission.From).ToList<PathMission?>();

    private static async Task<List<AutomationDevice?>> LoadAutomationDevicesAsync() =>
        (await Client!.GetItemsAsync()).Select(AutomationDevice.From).ToList<AutomationDevice?>();

    private static async Task<List<MissionStep?>> LoadMissionStepsAsync() =>
        (await Client!.GetAllMissionStepsAsync()).Select(MissionStep.From).ToList<MissionStep?>();

    private static async Task<List<AutomationJunction?>> LoadPathJunctionsAsync() =>
        (await Client!.GetJunctionsAsync()).Select(AutomationJunction.From).ToList<AutomationJunction?>();

    private static async Task<List<AutomationDeviceBase?>> LoadViewItemsStatusAsync() =>
        (await Client!.GetItemStatusesAsync()).Select(AutomationDeviceBase.From).ToList<AutomationDeviceBase?>();

    public static async Task<bool> GetViewItemStatus(AutomationDeviceBase item)
    {
        if (Client == null) return false;
        var dto = await Client.GetItemAsync(item.ItemGuid);
        if (dto == null) return false;
        item.Apply(dto.Live);
        return true;
    }
}
