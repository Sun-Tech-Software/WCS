using static Wcs.Common.Enums;

namespace Wcs.Api.Contracts.Dtos;

/// <summary>Lo stato dal vivo di un dispositivo: quello che prima viaggiava come <c>ZMessages.AutomationDeviceBase</c>.</summary>
public sealed record ItemStatus(Guid ItemId, string Asi, string Partition, string Subitem, string Name,
    DeviceStatusTypes Status, LuLoadStatusTypes LuLoadStatus, string LoadedLuCode, decimal LoadedLuId,
    Guid RunningStepId, Guid WaitingStepId);
