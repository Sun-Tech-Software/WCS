using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Api;

public static class MissionQueries
{
    public static IReadOnlyList<PathMission> Active() => Active(null, null, null);

    /// <summary>Le missioni attive che passano i filtri (tutti facoltativi, in AND): UDC, dispositivo su cui sta l'UDC, tipo.</summary>
    public static IReadOnlyList<PathMission> Active(decimal? loadUnitId, Guid? itemId, MissionTypes? type) =>
        ActiveMissions.Instance.List.Where(m => m != null
            && (loadUnitId == null || m.LuId == loadUnitId)
            && (itemId == null || m.PositionItemId == itemId)
            && (type == null || m.MissionType == type)).ToList()!;
    public static PathMission? Find(Guid missionId) => ActiveMissions.Instance[missionId];
    public static IReadOnlyList<MissionStep> Steps(Guid missionId) => MissionSteps.Instance.Where(s => s.MissionId == missionId).ToList();

    /// <summary>
    /// Gli step di tutte le missioni attive, in una volta sola: chi ricostruisce la vista completa
    /// li vuole tutti, e chiederli missione per missione sarebbe una richiesta per missione.
    /// </summary>
    public static IReadOnlyList<MissionStep> AllSteps() => MissionSteps.Instance.List.Where(s => s != null).ToList()!;

    /// <summary>
    /// C'e' una missione attiva per l'UDC? Con <paramref name="status"/> conta solo quelle negli
    /// stati indicati: il WMS chiede «in Run o Stop», cioe' STATUS IN (0, 1), e una missione in
    /// Error (99) ancora in memoria non deve bloccare la modifica dell'UDC. Elenco nullo o vuoto:
    /// nessun filtro, com'era.
    /// </summary>
    public static bool ExistsForLoadUnit(decimal loadUnitId, IReadOnlyCollection<MissionStatusTypes>? status = null) =>
        ActiveMissions.Instance.Any(m => m != null && m.LuId == loadUnitId
            && (status == null || status.Count == 0 || status.Contains(m.Status)));

    /// <summary>
    /// Il motore ha gia' questa UDC? Non e' <see cref="ExistsForLoadUnit"/> con un altro filtro: la
    /// domanda e' piu' larga in tutti e due i sensi. Conta le missioni attive <b>a qualunque stato</b>
    /// (anche Error e Done, che restano in memoria e tengono la UDC), e guarda anche i due posti
    /// fuori dall'elenco delle attive - la UDC che un dispositivo sta prelevando
    /// (<c>LUtoPick</c>) e le code di prelievo dei dispositivi legati al deposito. E' esattamente
    /// l'insieme di <see cref="Navigator.IsLuInExtraction"/>, la guardia che poi ferma davvero il
    /// comando di estrazione: chi valida un'assegnazione di nota deve chiedere questo, altrimenti
    /// l'assegnazione passa la validazione, torna al motore e il comando esce in silenzio -
    /// l'assegnazione non viene ne' servita ne' liberata.
    /// <para>
    /// <paramref name="excludeAssignmentId"/> non conta la missione che porta gia' quella stessa
    /// <see cref="PathMission.LuNoteAssignmentId"/>: e' la ragione per cui il gestionale puo' fare
    /// questa domanda mentre rivaluta un'assegnazione che ha gia' generato una missione.
    /// </para>
    /// </summary>
    public static bool HoldsLoadUnit(decimal loadUnitId, decimal? excludeAssignmentId = null) =>
        // Il confronto e' quello che faceva Navigator.BuildLuExtraction prima del compito 14, sulla
        // missione restituita da GetMissionByLuId: «se non porta questa assegnazione, scartala».
        Navigator.GetMissionByLuId(loadUnitId) is { } mission
        && (excludeAssignmentId == null || mission.LuNoteAssignmentId != excludeAssignmentId);

    /// <summary>
    /// La riga della missione, presa ora: e' quello che l'host mappa nel DTO. Passaggio verso
    /// <c>MissionStore</c>, che resta interno al motore.
    /// </summary>
    public static Mission Snapshot(PathMission mission) => MissionStore.Snapshot(mission);
}
