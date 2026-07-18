namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellInvocationOperationStatus
{
    internal PowerShellInvocationOperationStatus(
        PowerShellOperationState state,
        PowerShellFfiStatus terminalStatus,
        string diagnostic)
    {
        State = state;
        TerminalStatus = terminalStatus;
        Diagnostic = diagnostic;
    }

    public PowerShellOperationState State { get; }

    public PowerShellFfiStatus TerminalStatus { get; }

    public string Diagnostic { get; }

    public bool IsTerminal => State is PowerShellOperationState.Completed or
        PowerShellOperationState.Cancelled or PowerShellOperationState.Failed;
}
