namespace Devolutions.PowerShell.Ffi;

public enum PowerShellFfiStatus
{
    Success = 0,
    BufferTooSmall = 1,
    InvalidArgument = -1,
    NotInitialized = -2,
    IncompatiblePayload = -3,
    InvalidHandle = -4,
    HostFailure = -5,
    ManagedFailure = -6,
    Panic = -7,
    InputNotCompleted = -8,
    Backpressure = -9,
    UnsupportedValue = -10,
    OperationCancelled = -11,
    OperationNotTerminal = -12,
    UnsupportedCapability = -17,
    BrokerBusy = -18,
    BrokerNoConsumer = -19,
    BrokerClosed = -20,
    BrokerInvalidTerminalState = -21,
    BrokerDispatchViolation = -22,
    BrokerTimeout = -23,
}
