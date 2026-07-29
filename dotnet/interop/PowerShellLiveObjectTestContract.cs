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

    public static readonly PowerShellLiveObjectContract SessionCreatorBroker = new(
        typeof(IPowerShellLiveObjectBrokerContract).GUID,
        majorVersion: 1,
        minorVersion: 0,
        PowerShellLiveObjectDirection.ConsumerToSession);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("C9A4FEA0-4EA6-48BE-8B4F-B30BB328CCBD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellLiveObjectTestCount
{
    [PreserveSig]
    int GetCount(out long count);

    [PreserveSig]
    int Increment(out long count);

    [PreserveSig]
    int GetRevision(out long revision);

    [PreserveSig]
    int SetRevision(long revision);

    [PreserveSig]
    int GetPrimary(out IPowerShellLiveObjectTestChild child);

    [PreserveSig]
    int GetChildren(out IPowerShellLiveObjectTestChildCollection children);

    [PreserveSig]
    int Add(string name, out IPowerShellLiveObjectTestChild child);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("EDB0F021-1CA0-4A03-829D-D6325A34E642")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellLiveObjectTestChild
{
    [PreserveSig]
    int GetValue(out long value);

    [PreserveSig]
    int SetValue(long value);

    [PreserveSig]
    int GetIdentity(out long identity);

    [PreserveSig]
    int GetName(out string name);

    [PreserveSig]
    int SetName(string name);

    [PreserveSig]
    int GetHost(out string host);

    [PreserveSig]
    int SetHost(string host);

    [PreserveSig]
    int GetDescription(out string description);

    [PreserveSig]
    int SetDescription(string description);

    [PreserveSig]
    int GetGroup(out string group);

    [PreserveSig]
    int SetGroup(string group);
}

[GeneratedComInterface]
[Guid("B120DAA8-8A42-4417-8315-BB3AFA0FD5DE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellLiveObjectTestChildCollection
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetAt(int index, out IPowerShellLiveObjectTestChild child);
}
