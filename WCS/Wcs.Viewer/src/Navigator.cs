using Wcs.Api.Contracts.Dtos;
using Wcs.Common;
using Wcs.Common.Events;
using Wcs.Viewer.Items;

namespace Wcs.Viewer;

public sealed class Navigator
{
    #region Public Properties (copia locale dello stato del motore)

    // Le cinque liste erano campi statici mutati dal thread dell'hub (ApplyItemStatus vi
    // aggiungeva, e ReloadAllAsync le riassegnava tutte) mentre i consumatori WinForms le
    // scorrevano: una enumerazione durante una mutazione lancia InvalidOperationException, e una
    // riassegnazione a meta' fa leggere uno stato lacerato. E' lo stesso difetto che l'Editor ha
    // gia' indurito in PlantModel; qui la forma e' copy-on-write dietro proprieta'.
    //
    // La regola: la lista a cui una proprieta' punta non si muta MAI sul posto. Chi legge prende
    // il riferimento corrente e lo scorre in pace; chi scrive costruisce una lista nuova e scambia
    // il riferimento sotto _sync. Cosi' i consumatori restano `Navigator.PathDevices.FirstOrDefault`
    // com'erano, senza sapere del lock.
    //
    // La regola la impone il tipo, non il commento: in lettura le proprieta' danno un
    // IReadOnlyList, che non ha ne' Add ne' Sort ne' Clear, e la sostituzione passa dai metodi
    // Replace* qui sotto, che sono internal. Finche' erano List<T> con il setter pubblico, un
    // Navigator.PathDevices.Add(...) fuori dal lock rimetteva il difetto dell'ondata precedente
    // senza che niente lo fermasse - e non era teorico: l'app dimostrativa faceva davvero
    // Clear()/Add() e Sort() sulla lista viva.
    private static readonly Lock Sync = new();

    private static List<PathMission?> _activeMissions = [];
    private static List<MissionStep?> _missionSteps = [];
    private static List<AutomationDevice?> _pathDevices = [];
    private static List<AutomationJunction?> _pathJunctions = [];
    private static List<AutomationDeviceBase?> _viewPathDevices = [];

    public static IReadOnlyList<PathMission?> ActiveMissions
    {
        get { lock (Sync) return _activeMissions; }
    }

    public static IReadOnlyList<MissionStep?> MissionSteps
    {
        get { lock (Sync) return _missionSteps; }
    }

    public static IReadOnlyList<AutomationDevice?> PathDevices
    {
        get { lock (Sync) return _pathDevices; }
    }

    public static IReadOnlyList<AutomationJunction?> PathJunctions
    {
        get { lock (Sync) return _pathJunctions; }
    }

    public static IReadOnlyList<AutomationDeviceBase?> ViewPathDevices
    {
        get { lock (Sync) return _viewPathDevices; }
    }

    #endregion Public Properties (copia locale dello stato del motore)

    #region Internal Methods (sostituzione delle cinque liste)

    // La lista che arriva qui diventa di questo oggetto: chi la costruisce non deve tenerne il
    // riferimento e mutarla dopo, che e' l'unico modo rimasto di rompere l'invariante.
    internal static void ReplaceActiveMissions(List<PathMission?> value) { lock (Sync) _activeMissions = value; }

    internal static void ReplaceMissionSteps(List<MissionStep?> value) { lock (Sync) _missionSteps = value; }

    internal static void ReplacePathDevices(List<AutomationDevice?> value) { lock (Sync) _pathDevices = value; }

    internal static void ReplacePathJunctions(List<AutomationJunction?> value) { lock (Sync) _pathJunctions = value; }

    internal static void ReplaceViewPathDevices(List<AutomationDeviceBase?> value) { lock (Sync) _viewPathDevices = value; }

    /// <summary>
    /// Sostituisce tutte e cinque le liste sotto un solo lock, per la ricarica completa.
    /// </summary>
    /// <remarks>
    /// Scambiandole una alla volta un lettore puo' vedere i dispositivi nuovi con le giunzioni
    /// vecchie, cioe' un impianto che non e' mai esistito: non e' la lacerazione della singola
    /// lista, ma e' lo stesso genere di stato intermedio. Le ricariche di una lista sola
    /// (<c>Connector.GetPathJunctions</c> e sorelle) restano quelle che sono: li' la lista
    /// aggiornata e' una, e le altre quattro non hanno motivo di cambiare.
    /// </remarks>
    internal static void ReplaceAll(
        List<PathMission?> activeMissions,
        List<AutomationDevice?> pathDevices,
        List<MissionStep?> missionSteps,
        List<AutomationJunction?> pathJunctions,
        List<AutomationDeviceBase?> viewPathDevices)
    {
        lock (Sync)
        {
            _activeMissions = activeMissions;
            _pathDevices = pathDevices;
            _missionSteps = missionSteps;
            _pathJunctions = pathJunctions;
            _viewPathDevices = viewPathDevices;
        }
    }

    #endregion Internal Methods (sostituzione delle cinque liste)

    #region Private Fields

    private static Enums.DefragmentationTypes _defragmentation;
    private static bool? _isConnected;
    private static bool? _isRunning;
    private static Enums.NavigatorStatusTypes _navStatus;

    #endregion Private Fields

    #region Public Delegates

    public delegate void DefragmentationChangedEventHandler(object? sender, DefragmentationChangedEventArgs value);

    public delegate void IsConnectedChangedEventHandler(object? sender, EventArgs value);

    public delegate void IsRunningChangedEventHandler(object? sender, EventArgs value);

    public delegate void StatusChangedEventHandler(object? sender, NavigatorStatusChangedEventArgs value);

    #endregion Public Delegates

    #region Public Events

    public static event DefragmentationChangedEventHandler? DefragmentationChanged;

    public static event IsConnectedChangedEventHandler? IsConnectedChanged;

    public static event IsRunningChangedEventHandler? IsRunningChanged;

    public static event StatusChangedEventHandler? StatusChanged;

    #endregion Public Events

    #region Public Properties

    public static Enums.DefragmentationTypes Defragmentation
    {
        get => _defragmentation;
        set
        {
            if (_defragmentation == value) return;
            _defragmentation = value;
            DefragmentationChanged?.Invoke(null, new DefragmentationChangedEventArgs(value));
        }
    }

    public static bool? IsConnected
    {
        get => _isConnected ?? false;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            IsConnectedChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static bool? IsRunning
    {
        get => _isRunning ?? false;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            IsRunningChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static Enums.NavigatorStatusTypes NavStatus
    {
        get => _navStatus;
        set
        {
            if (_navStatus == value) return;
            var oldStatus = _navStatus;
            _navStatus = value;
            StatusChanged?.Invoke(null, new NavigatorStatusChangedEventArgs(oldStatus, value));
        }
    }

    #endregion Public Properties

    #region Internal Methods

    /// <summary>
    /// Aggiorna la voce di <see cref="ViewPathDevices"/> con lo stesso ItemGuid, o la aggiunge se
    /// non c'e' ancora. Chiamata dal thread dell'hub.
    /// </summary>
    /// <remarks>
    /// Copy-on-write: si costruisce una lista nuova sotto il lock e si scambia il riferimento,
    /// invece di mutare sul posto quella che un consumatore potrebbe star scorrendo. Anche
    /// l'aggiornamento della voce esistente passa da una voce nuova (<c>AutomationDeviceBase.From</c>
    /// con lo stato appena arrivato), cosi' non si tocca l'oggetto che qualcuno tiene in mano.
    /// </remarks>
    internal static void ApplyItemStatus(ItemStatus status)
    {
        lock (Sync)
        {
            var updated = new List<AutomationDeviceBase?>(_viewPathDevices.Count + 1);
            var replaced = false;
            foreach (var device in _viewPathDevices)
                if (device != null && device.ItemGuid == status.ItemId)
                {
                    var fresh = AutomationDeviceBase.From(status);
                    updated.Add(fresh);
                    replaced = true;
                }
                else
                {
                    updated.Add(device);
                }

            if (!replaced) updated.Add(AutomationDeviceBase.From(status));

            _viewPathDevices = updated;
        }
    }

    /// <summary>Imposta NavStatus e Defragmentation dallo stato del motore; i setter esistenti sollevano gia' gli eventi.</summary>
    internal static void ApplyStatus(EngineStatus status)
    {
        NavStatus = status.NavStatus;
        Defragmentation = status.Defragmentation;
    }

    #endregion Internal Methods
}