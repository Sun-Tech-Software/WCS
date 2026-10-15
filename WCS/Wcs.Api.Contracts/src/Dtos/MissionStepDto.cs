using static Wcs.Common.Enums;

namespace Wcs.Api.Contracts.Dtos;

public sealed record MissionStepDto(Guid StepId, Guid MissionId, Guid? FromNodeId, Guid? ToNodeId, Guid? RunningNode,
    DateTime Date, DateTime StartStep, DateTime EndStep, ResultCodeTypes StepStatus, Guid GroupedMovementId,
    int FromGroupRoutePath);
