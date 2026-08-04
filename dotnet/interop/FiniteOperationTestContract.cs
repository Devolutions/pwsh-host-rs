#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.MultiPwsh.FiniteOperationTest;

/// <summary>
/// Acceptance-only fixed transport and schema used to prove that finite
/// operation identifiers and copied pages traverse the generated payload path.
/// </summary>
[GeneratedComInterface]
[Guid("82AD9A61-416F-47B3-A0B5-8CB2DA29D5CC")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellBridgeTestFiniteOperationTransport
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

/// <summary>
/// A static, finite contract: it has three fixed operation modes and one fixed
/// copied report-page shape. It is deliberately not a generic job dispatcher.
/// </summary>
[BridgeContract("3F1DD1D0-AB49-4FE1-A40C-FE5BB48D8AEC", 1, 0, "82AD9A61-416F-47B3-A0B5-8CB2DA29D5CC")]
[BridgeObject(1, ReleaseId = 701)]
public interface IPowerShellBridgeTestFiniteOperation
{
    [BridgeMember(1, Permission = BridgePermission.Execute)]
    IFiniteOperationTicket Start(int mode);

    [BridgeMember(2, Permission = BridgePermission.Read)]
    IFiniteOperationPageRead ReadPage(Guid operationId, int cursor);

    [BridgeMember(3, Permission = BridgePermission.Execute)]
    IFiniteOperationTicket Cancel(Guid operationId);

    [BridgeMember(4, Permission = BridgePermission.Execute)]
    int Release(Guid operationId);
}

[BridgeData(80)]
public interface IFiniteOperationTicket
{
    [BridgeField(1)]
    Guid OperationId { get; }

    [BridgeField(2)]
    int Status { get; }

    [BridgeField(3)]
    long SnapshotRevision { get; }

    [BridgeField(4)]
    long PermissionRevision { get; }
}

[BridgeData(81)]
public interface IFiniteOperationPageRead
{
    [BridgeField(1)]
    int Status { get; }

    [BridgeField(2)]
    bool HasPage { get; }

    [BridgeField(3)]
    int NextCursor { get; }

    [BridgeField(4)]
    int Ordinal { get; }

    [BridgeField(5, MaximumCollectionCount = 2, MaximumUtf8Bytes = 32)]
    IReadOnlyList<string> Rows { get; }
}
