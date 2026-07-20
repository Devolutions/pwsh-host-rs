namespace Devolutions.PowerShell.Ffi;

public interface IPowerShellCapabilityHandler
{
    PowerShellValue Invoke(
        PowerShellCapabilityInvocation invocation,
        IReadOnlyList<PowerShellValue> arguments);
}
