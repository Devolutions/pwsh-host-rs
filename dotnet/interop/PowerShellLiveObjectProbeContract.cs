using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Devolutions.PowerShell.Ffi.LiveObjects;

[GeneratedComInterface]
[Guid("9A2A6F07-319B-422A-A7A4-6C3A32C7B379")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IPowerShellLiveObjectProbe
{
    [PreserveSig]
    int GetCount(out long count);

    [PreserveSig]
    int Increment(out long count);
}
