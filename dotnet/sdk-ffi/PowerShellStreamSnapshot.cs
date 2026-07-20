using System;
using System.Collections.Generic;

namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellStreamSnapshot<T>
{
    internal PowerShellStreamSnapshot(
        PowerShellStreamKind kind,
        T[] records,
        bool isTruncated,
        ulong totalRecordCount,
        ulong droppedRecordCount)
    {
        Kind = kind;
        Records = Array.AsReadOnly(records);
        IsTruncated = isTruncated;
        TotalRecordCount = totalRecordCount;
        DroppedRecordCount = droppedRecordCount;
    }

    public PowerShellStreamKind Kind { get; }

    public IReadOnlyList<T> Records { get; }

    public bool IsTruncated { get; }

    public ulong TotalRecordCount { get; }

    public ulong DroppedRecordCount { get; }
}
