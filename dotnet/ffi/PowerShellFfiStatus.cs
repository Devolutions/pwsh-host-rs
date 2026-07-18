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
    PayloadManifestInvalid = -13,
    PayloadUntrusted = -14,
    PayloadHashMismatch = -15,
    PayloadIncompatible = -16,
    UnsupportedCapability = -17,
    SessionPolicyViolation = -18,
}
