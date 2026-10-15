using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using Helper.Converters;
using Wcs.Engine.Collections;
using Wcs.Common.Events;
using Wcs.Engine.Converters;
using Wcs.Connection.Interface;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Items;

public sealed partial class AutomationDevice
{

    #region Private Methods

    private static bool CheckTransferLUs(int destinationLevelGroup)
    {
        return !GetGroupLevelItems(destinationLevelGroup).Any(x => x is { IsLinkedToStorage: true });
    }

    private static List<AutomationDevice?> GetGroupLevelItems(int groupLevelId)
    {
        if (groupLevelId == 0)
            return [];

        var result = PathDevices.Instance.Where(x =>
            x != null &&
            x.GroupLevelId == groupLevelId &&
            x.DeviceType != DeviceTypes.GroupOfAutomationDevices).Distinct().ToList();

        result.Sort((x, y) => x!.PriorityInRoutePath.CompareTo(y!.PriorityInRoutePath));

        return result;
    }

    private static List<AutomationDevice> SearchReachableBuffers(AutomationDevice sourceItem, int level,
        List<Guid> stompedItems, List<int> stompedGroupRoutePath)
    {
        var result = new List<AutomationDevice>();

        if (level > 50) return result;

        stompedItems.Add(sourceItem.ItemGuid);
        if (sourceItem.GroupRoutePathId > 0)
            stompedGroupRoutePath.Add(sourceItem.GroupRoutePathId);

        var destinations = sourceItem.DestinationItemsLinked;

        destinations = destinations.Where(x => x != null && x.ItemGuid != sourceItem.ItemGuid).ToList();

        foreach (var item in destinations.OfType<AutomationDevice>().Where(item =>
                     !stompedItems.Contains(item.ItemGuid) && !stompedGroupRoutePath.Contains(item.GroupRoutePathId)))
            switch (item.DeviceType)
            {
                case DeviceTypes.Buffer:
                case DeviceTypes.LoadingAndBuffer:
                    {
                        stompedItems.Add(item.ItemGuid);
                        if (sourceItem?.GroupRoutePathId > 0)
                            stompedGroupRoutePath.Add(item.GroupRoutePathId);
                        result.Add(item);
                        break;
                    }

                case DeviceTypes.Automation:
                case DeviceTypes.Loading:
                case DeviceTypes.Storage:
                case DeviceTypes.CircularDistributor:
                case DeviceTypes.GroupOfAutomationDevices:
                case DeviceTypes.None:
                default:
                    {
                        var destinationBuffers =
                            SearchReachableBuffers(item, level + 1, stompedItems, stompedGroupRoutePath);

                        if (destinationBuffers.Count > 0)
                            result = result.Union(destinationBuffers).ToList();
                        break;
                    }
            }

        return result;
    }

    private static List<AutomationDevice> SearchReachableStoreOperators(AutomationDevice sourceItem, int level,
        List<Guid> stompedItems, List<int> stompedGroupRoutePath)
    {
        var result = new List<AutomationDevice>();

        if (level > 50) return result;

        stompedItems.Add(sourceItem.ItemGuid);
        if (sourceItem.GroupRoutePathId > 0)
            stompedGroupRoutePath.Add(sourceItem.GroupRoutePathId);

        var destinations = sourceItem.DestinationItemsLinked;

        destinations = destinations.Where(x => x != null && x.ItemGuid != sourceItem.ItemGuid).ToList();

        foreach (var item in destinations.OfType<AutomationDevice>().Where(item =>
                     !stompedItems.Contains(item.ItemGuid) && !stompedGroupRoutePath.Contains(item.GroupRoutePathId)))
            switch (item.DeviceType)
            {
                case DeviceTypes.Storage:
                    {
                        stompedItems.Add(item.ItemGuid);
                        if (sourceItem != null && sourceItem.GroupRoutePathId > 0)
                            stompedGroupRoutePath.Add(item.GroupRoutePathId);
                        result.Add(item);
                        break;
                    }

                default:
                    {
                        if (item.IsLinkedToStorage && item.Status != DeviceStatusTypes.Ready)
                            return result;

                        var destinationStores =
                            SearchReachableStoreOperators(item, level + 1, stompedItems, stompedGroupRoutePath);

                        if (destinationStores.Count > 0)
                            result = result.Union(destinationStores).ToList();
                        break;
                    }
            }

        return result;
    }

    private List<PathMission?> LoadableLUs(int sourceLevelGroup)
    {
        var result = LUsLoadedByLuType(sourceLevelGroup, 0, false,
            0);

        return result;
    }

    private List<PathMission?> LUsLoadedByLuType(int idLevel, int numberOfFamilies, bool moveOnlySameStorageDoor,
        int numAcceptableLUs)
    {
        var lastStorageDoorCode = string.Empty;

        var result = new List<PathMission?>();

        var luToAnalyze = PathDevices.Instance.Where(x =>
            x != null && x.GroupLevelId == idLevel && x.LoadedLu != null).Select(x => x?.LoadedLu).ToList();

        if (numberOfFamilies == 0 && numAcceptableLUs == 0)
            return luToAnalyze;

        var families = luToAnalyze.Where(x => x != null).Select(x => x?.IdLuType).Distinct().ToList();

        for (int index = 0, loopTo = families.Count - 1; index <= loopTo; index++)
        {
            if (numberOfFamilies > 0 && index > numberOfFamilies - 1)
                return result;

            var pointerToLu = index;

            var luToAdd = luToAnalyze.Where(x => x != null && x.IdLuType == pointerToLu);

            foreach (var lu in luToAdd.Where(x => x != null))
            {
                if (moveOnlySameStorageDoor && !string.IsNullOrEmpty(lastStorageDoorCode) &&
                    lastStorageDoorCode == lu?.StorageDoorCode)
                {
                    result.Add(lu);
                }
                else
                {
                    if (lu?.StorageDoorCode != null) lastStorageDoorCode = lu.StorageDoorCode;
                    result.Add(lu);
                }

                if (numAcceptableLUs > 0 && result.Count >= numAcceptableLUs)
                    break;
            }

            if (numAcceptableLUs > 0 && result.Count >= numAcceptableLUs)
                break;
        }

        result.Sort((x, y) => x!.Date.CompareTo(y!.Date));

        return result;
    }

    private List<PathMission?> LUsReadyToTransferByLevel(int levelId)
    {
        var result = new List<PathMission?>();

        if (GroupItemId == 0 || !CheckTransferLUs(levelId))
            return result;

        var lUsToTransfer = TransferLUs();

        if (lUsToTransfer.Count == 0)
            return result;

        var listAssignedItem = new List<Guid>();

        foreach (var mission in lUsToTransfer.Where(x => x is { StepToDo: not null }))
        {
            var item = mission?.StepToDo?.DestinationItem;

            if (item != null)
                switch (item.DeviceType)
                {
                    case DeviceTypes.Storage:
                        {
                            if (mission != null) result.Add(mission);
                            break;
                        }
                    case DeviceTypes.Automation:
                    case DeviceTypes.Loading:
                    case DeviceTypes.Buffer:
                    case DeviceTypes.CircularDistributor:
                    case DeviceTypes.GroupOfAutomationDevices:
                    case DeviceTypes.LoadingAndBuffer:
                    case DeviceTypes.None:
                    default:
                        {
                            if (item.GroupLevelId == levelId && item.LuManager != null && item.LuManager.CanAcceptLu(mission))
                                if (!listAssignedItem.Contains(item.ItemGuid))
                                {
                                    listAssignedItem.Add(item.ItemGuid);
                                    if (mission != null) result.Add(mission);
                                }

                            break;
                        }
                }
        }

        return result;
    }

    private void SetProperty<T>(ref T field, T value, bool updateDb,
                                    [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (updateDb && !_loadingFromDatabase) ORM.AutomationDevice.Update(this);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    
    private List<PathMission?> TransferLUs()
    {
        var result = LUsLoadedByLuType(GroupLevelId, 0, false, 0);

        return result;
    }

    #endregion Private Methods

}
