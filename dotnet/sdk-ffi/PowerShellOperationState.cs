namespace Devolutions.PowerShell.Ffi;

public enum PowerShellOperationState
{
    Pending = 1,
    Running = 2,
    Completed = 3,
    Cancelled = 4,
    Failed = 5,
}
