namespace Wcs.Api.Contracts;

/// <summary>Le rotte dell'API del motore: un solo posto, usato da host e client.</summary>
public static class ApiRoutes
{
    public const string Prefix = "/api/v1";
    public const string Hub = "/hub/plant";
    public const string ApiKeyHeader = "X-Api-Key";
    /// <summary>
    /// L'intestazione che porta l'identita' di chi scrive: lo stesso <c>Holder</c> del permesso di
    /// modifica (<see cref="EditLock"/>, compito 4/4b). Le rotte di scrittura di <c>/plant/*</c> che
    /// cambiano la forma dell'impianto la esigono e la confrontano con chi tiene il permesso in quel
    /// momento - non e' un'autenticazione (quella resta <see cref="ApiKeyHeader"/>), e' la prova che
    /// chi scrive e' davvero il titolare, non un'altra scheda del browser ne' l'Editor WinForms, che
    /// non la conosce e non la manda mai.
    /// </summary>
    public const string EditHolderHeader = "X-Edit-Holder";

    public const string Status = "/status";
    public const string Commands = "/commands";
    public const string Diagram = "/plant/diagram";
    public const string Items = "/plant/items";
    public const string ItemsConfig = "/plant/items/config";
    public const string ItemsGeometry = "/plant/items/geometry";
    public const string DeviceTypes = "/plant/device-types";
    public const string Junctions = "/plant/junctions";
    public const string DoorMappings = "/plant/door-mappings";
    /// <summary>La decorazione del disegno: scritte e riquadri liberi dell'operatore (compito 7). Vuota se l'impianto non ne ha mai avuta una.</summary>
    public const string PlantDecoration = "/plant/decoration";
    /// <summary>L'impianto intero — dispositivi, geometria, giunzioni, mappature porte — in un solo corpo (compito 12: esportazione e importazione).</summary>
    public const string PlantGraph = "/plant/graph";
    /// <summary>La geometria letta da un vecchio disegno <c>.edd</c>/<c>.xml</c> caricato per l'importazione: non scrive nulla, prepara solo il verbale che <see cref="PlantGraph"/> poi applica.</summary>
    public const string PlantGraphLegacyGeometry = "/plant/graph/legacy-geometry";
    public const string AutomaticCells = "/plant/automatic-cells";
    /// <summary>Il permesso di modifica dell'impianto: un solo editor per volta, con scadenza automatica (compito 4, 4b).</summary>
    public const string EditLock = "/plant/edit-lock";
    public const string Missions = "/missions";
    public const string ExtractionQueue = "/extraction-queue";
    public const string HandledLoadUnits = "/handled-load-units";
    public const string Log = "/log";
    public const string PlcMessages = "/history/plc-messages";
    public const string PlcConnections = "/plc/connections";
    public const string WmsCells = "/wms/cells";
    public const string WmsCellSearchContext = "/wms/cell-search-context";
    public const string WmsConfigurationCheck = "/wms/configuration-check";
}
