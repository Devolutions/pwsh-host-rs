#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Devolutions.PowerShell.Ffi.LiveObjects;

/// <summary>
/// Internal acceptance contract used to verify application-provided live-object
/// contract packs without adding contract-specific native plumbing.
/// </summary>
public static class PowerShellLiveObjectTestContracts
{
    public static readonly PowerShellLiveObjectContract Count = new(
        typeof(IPowerShellLiveObjectTestCount).GUID,
        majorVersion: 1,
        minorVersion: 0,
        PowerShellLiveObjectDirection.ConsumerToSession);
}

[GeneratedComInterface]
[Guid("C9A4FEA0-4EA6-48BE-8B4F-B30BB328CCBD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellLiveObjectTestCount
{
    [PreserveSig]
    int GetCount(out long count);

    [PreserveSig]
    int Increment(out long count);
}
