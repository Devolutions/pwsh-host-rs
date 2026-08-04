#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.MultiPwsh.BridgeTest;

/// <summary>
/// Acceptance-only transport and contract used to exercise the generated v2
/// pack handshake through the NativeAOT sample.
/// </summary>
[GeneratedComInterface]
[Guid("BF3A7727-1B58-435D-A4E9-C4E83A93D47E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellBridgeTestCountTransport
{
    [PreserveSig]
    int Invoke(
        ulong leaseId,
        uint generation,
        ulong objectId,
        uint memberId,
        nint input,
        int inputLength,
        nint output,
        int outputCapacity,
        out int outputLength);

    [PreserveSig]
    int CloseLease(ulong leaseId, uint generation);
}

/// <summary>Acceptance-only closed bridge contract with no transport-specific members.</summary>
[BridgeContract("496D8528-7AE6-4C31-835E-597E2166E35B", 1, 1, "BF3A7727-1B58-435D-A4E9-C4E83A93D47E")]
[BridgeObject(1, ReleaseId = 100)]
public interface IPowerShellBridgeTestCount
{
    [BridgeMember(1, Permission = BridgePermission.Read)]
    long Count { get; }

    [BridgeMember(2, Permission = BridgePermission.Execute)]
    long Increment();

    [BridgeMember(3, Permission = BridgePermission.Execute)]
    long Add(int value);

    [BridgeEvent(4)]
    void Report(long value);

    [BridgeReliableEvent(6, Permission = BridgePermission.Execute, MaximumRetainedEvents = 2)]
    void ReportReliable(long value);

    [BridgeMember(5, Permission = BridgePermission.Execute, ResultObjectId = 2)]
    IPowerShellBridgeTestJob StartJob();
}

/// <summary>
/// A finite generated job shape. Jobs are ordinary lease-scoped bridge objects:
/// start returns a typed handle; every status, cancellation, and result-page
/// operation remains a generated bounded member.
/// </summary>
[BridgeObject(2, ReleaseId = 101)]
[BridgeFiniteOperation(
    StatusMemberId = 10,
    StatusTerminalFieldId = 2,
    CancelMemberId = 11,
    PageMemberId = 12,
    MaximumLifetimeMilliseconds = 60_000)]
public interface IPowerShellBridgeTestJob
{
    [BridgeMember(10, Permission = BridgePermission.Read)]
    PowerShellBridgeTestJobStatus Status { get; }

    [BridgeMember(11, Permission = BridgePermission.Execute, Mutation = BridgeMutation.Direct)]
    void Cancel();

    [BridgeMember(12, Permission = BridgePermission.Read)]
    PowerShellBridgeTestJobPage ReadResults(Guid cursor, long snapshotRevision, long permissionRevision);
}

[BridgeEnum(90)]
public enum PowerShellBridgeTestJobState
{
    Running = 0,
    Completed = 1,
    Cancelled = 2,
}

[BridgeData(91)]
public interface PowerShellBridgeTestJobStatus
{
    [BridgeField(1)]
    PowerShellBridgeTestJobState State { get; }

    [BridgeField(2)]
    bool IsTerminal { get; }

    [BridgeField(3)]
    long ResultCount { get; }
}

[BridgeData(92)]
[BridgeSnapshotPage(
    ColumnsFieldId = 1,
    RowsFieldId = 2,
    NextCursorFieldId = 3,
    SnapshotRevisionFieldId = 4,
    PermissionRevisionFieldId = 5,
    CursorLeaseExpiresAtFieldId = 6,
    IsTerminalFieldId = 7,
    IsGapFieldId = 8,
    IsOverflowFieldId = 9,
    IsTruncatedFieldId = 10,
    TotalCountFieldId = 11)]
public interface PowerShellBridgeTestJobPage
{
    [BridgeField(1, MaximumCollectionCount = 4)]
    IReadOnlyList<PowerShellBridgeTestGridColumn> Columns { get; }

    [BridgeField(2, MaximumCollectionCount = 8)]
    IReadOnlyList<PowerShellBridgeTestGridRow> Rows { get; }

    [BridgeField(3)]
    Guid NextCursor { get; }

    [BridgeField(4)]
    long SnapshotRevision { get; }

    [BridgeField(5)]
    long PermissionRevision { get; }

    [BridgeField(6)]
    long CursorLeaseExpiresAtMilliseconds { get; }

    [BridgeField(7)]
    bool IsTerminal { get; }

    [BridgeField(8)]
    bool IsGap { get; }

    [BridgeField(9)]
    bool IsOverflow { get; }

    [BridgeField(10)]
    bool IsTruncated { get; }

    [BridgeField(11)]
    long TotalCount { get; }
}

[BridgeEnum(93)]
public enum PowerShellBridgeTestGridColumnType
{
    Int64 = 0,
    Utf8String = 1,
}

/// <summary>
/// A fixed schema column. It is copied metadata, not an adapted property bag or
/// a dynamically discovered column.
/// </summary>
[BridgeData(94)]
public interface PowerShellBridgeTestGridColumn
{
    [BridgeField(1, MaximumUtf8Bytes = 32)]
    string Name { get; }

    [BridgeField(2)]
    PowerShellBridgeTestGridColumnType Type { get; }
}

/// <summary>
/// A typed row whose fields have the same static order and type as the column
/// metadata above. No cell or object-union transport is involved.
/// </summary>
[BridgeData(95)]
public interface PowerShellBridgeTestGridRow
{
    [BridgeField(1)]
    long Sequence { get; }

    [BridgeField(2)]
    long Value { get; }

    [BridgeField(3, MaximumUtf8Bytes = 32)]
    string Label { get; }
}
