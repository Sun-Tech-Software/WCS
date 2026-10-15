using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>Missions and steps as the host hands them, for the tests of compito 3 (rows 60, 61).</summary>
internal static class TestMissions
{
    public static MissionDto Mission(Guid missionId, Guid? position, string? loadUnitCode = null, decimal loadUnitId = 0m,
        Guid? source = null, Guid? destination = null, MissionStatusTypes status = default, string? statusDescription = null) =>
        new(missionId, loadUnitId, loadUnitCode, position, source, destination, new DateTime(2026, 9, 26, 10, 0, 0),
            statusDescription, null, null, status, null, null, MissionTypes.MovingToItem, null, OperationTypes.Inbound,
            0m, 0m, 0m, null, 0, null, null, null, null, null, null, null, 0m, null, 0m, false, 0m, false, false);

    public static MissionStepDto Step(Guid stepId, Guid missionId, Guid? from, Guid? to, Guid? running = null,
        ResultCodeTypes status = default) =>
        new(stepId, missionId, from, to, running, new DateTime(2026, 9, 26, 10, 0, 0), default, default, status,
            Guid.Empty, 0);
}
