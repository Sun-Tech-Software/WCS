using System.ComponentModel;
using Wcs.Engine.Collections;

namespace Wcs.Engine.Converters;

public class AutomationDeviceType : GuidConverter
{
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context)
    {
        return true; // True tells the PropertyGrid to display a Combobox
    }

    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context)
    {
        return false; // True makes the Combobox select only. False allows free text entry.
    }

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
    {
        return new StandardValuesCollection(PathDevices.Instance.List.Select(c => c!.ItemGuid)
            .ToList()); // Exports our global collection of options
    }
}