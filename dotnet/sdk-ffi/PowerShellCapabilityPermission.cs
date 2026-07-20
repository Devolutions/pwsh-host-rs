namespace Devolutions.PowerShell.Ffi;

[Flags]
public enum PowerShellCapabilityPermission : uint
{
    None = 0,
    Read = 1,
    Write = 1 << 1,
    Report = 1 << 2,
    HostInteraction = 1 << 3,
}
