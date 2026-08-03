#nullable enable

using System;
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
[BridgeContract("496D8528-7AE6-4C31-835E-597E2166E35B", 1, 0, "BF3A7727-1B58-435D-A4E9-C4E83A93D47E")]
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
}
