using System;
using System.Collections.Generic;

namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellObjectSnapshot
{
    internal PowerShellObjectSnapshot(
        string displayText,
        string[] typeNames,
        ulong sequence,
        bool isTruncated,
        PowerShellValue? scalarValue,
        PowerShellValue? propertyBag,
        uint propertyEntryCount,
        uint droppedPropertyEntryCount,
        uint typeNameCount,
        uint droppedTypeNameCount,
        bool isPropertyBagTruncated)
    {
        DisplayText = displayText;
        TypeNames = Array.AsReadOnly(typeNames);
        Sequence = sequence;
        IsTruncated = isTruncated;
        ScalarValue = scalarValue;
        PropertyBag = propertyBag;
        PropertyEntryCount = propertyEntryCount;
        DroppedPropertyEntryCount = droppedPropertyEntryCount;
        TypeNameCount = typeNameCount;
        DroppedTypeNameCount = droppedTypeNameCount;
        IsPropertyBagTruncated = isPropertyBagTruncated;
    }

    public string DisplayText { get; }

    public IReadOnlyList<string> TypeNames { get; }

    public ulong Sequence { get; }

    public bool IsTruncated { get; }

    public PowerShellValue? ScalarValue { get; }

    public PowerShellValue? PropertyBag { get; }

    public uint PropertyEntryCount { get; }

    public uint DroppedPropertyEntryCount { get; }

    public uint TypeNameCount { get; }

    public uint DroppedTypeNameCount { get; }

    public bool IsPropertyBagTruncated { get; }
}
