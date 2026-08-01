namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Identifies one operation in a staged intent lifecycle.
/// </summary>
public enum PowerShellStagedIntentOperation
{
    Stage,
    Validate,
    Commit,
    Abort,
}

/// <summary>
/// Describes the explicit outcome returned for a staged intent operation.
/// </summary>
public enum PowerShellStagedIntentStatus
{
    Staged,
    Validated,
    Committed,
    Aborted,
    Rejected,
    UnknownStage,
    Expired,
    Terminal,
    Cancelled,
    Busy,
}

/// <summary>
/// A copied, bounded intent retained by a <see cref="PowerShellStagedIntentCoordinator"/>.
/// </summary>
public sealed class PowerShellStagedIntent
{
    internal PowerShellStagedIntent(
        string operationName,
        string stageIdentifier,
        PowerShellValue intent,
        DateTimeOffset expiresAt)
    {
        OperationName = operationName;
        StageIdentifier = stageIdentifier;
        Intent = intent;
        ExpiresAt = expiresAt;
    }

    /// <summary>
    /// Gets the canonical application operation name.
    /// </summary>
    public string OperationName { get; }

    /// <summary>
    /// Gets the opaque application-supplied identifier for this stage.
    /// </summary>
    public string StageIdentifier { get; }

    /// <summary>
    /// Gets the copied property-bag payload.
    /// </summary>
    public PowerShellValue Intent { get; }

    /// <summary>
    /// Gets the UTC deadline after which the coordinator rejects the stage.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; }
}

/// <summary>
/// Provides a staged intent lifecycle operation to an application handler.
/// </summary>
public sealed class PowerShellStagedIntentInvocation
{
    internal PowerShellStagedIntentInvocation(
        PowerShellStagedIntentOperation operation,
        PowerShellStagedIntent intent,
        CancellationToken cancellationToken)
    {
        Operation = operation;
        Intent = intent;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the requested lifecycle operation.
    /// </summary>
    public PowerShellStagedIntentOperation Operation { get; }

    /// <summary>
    /// Gets the copied staged intent.
    /// </summary>
    public PowerShellStagedIntent Intent { get; }

    /// <summary>
    /// Gets cancellation for the bounded capability callback.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>
/// Handles application-owned authorization, review, persistence, and effects for a staged intent.
/// </summary>
public interface IPowerShellStagedIntentHandler
{
    /// <summary>
    /// Handles one lifecycle operation. The coordinator also delivers best-effort
    /// <see cref="PowerShellStagedIntentOperation.Abort"/> cleanup after
    /// coordinator-initiated expiry, cancellation, or disposal; handlers must
    /// make abort idempotent and must not infer a rollback guarantee from it.
    /// </summary>
    PowerShellStagedIntentHandlerResult Invoke(PowerShellStagedIntentInvocation invocation);
}

/// <summary>
/// The bounded application decision for a staged intent lifecycle operation.
/// </summary>
public sealed class PowerShellStagedIntentHandlerResult
{
    private const int MaximumMessageBytes = 256;

    private PowerShellStagedIntentHandlerResult(bool accepted, string? message)
    {
        if (message is { } value &&
            System.Text.Encoding.UTF8.GetByteCount(value) > MaximumMessageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(message), "The staged intent message exceeds 256 UTF-8 bytes.");
        }

        IsAccepted = accepted;
        Message = message;
    }

    /// <summary>
    /// Gets whether the application accepted the lifecycle operation.
    /// </summary>
    public bool IsAccepted { get; }

    /// <summary>
    /// Gets an optional bounded application message returned to the payload.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Accepts the lifecycle operation.
    /// </summary>
    public static PowerShellStagedIntentHandlerResult Accept(string? message = null)
    {
        return new PowerShellStagedIntentHandlerResult(accepted: true, message);
    }

    /// <summary>
    /// Rejects the lifecycle operation without changing its non-terminal state.
    /// </summary>
    public static PowerShellStagedIntentHandlerResult Reject(string? message = null)
    {
        return new PowerShellStagedIntentHandlerResult(accepted: false, message);
    }
}

/// <summary>
/// A copied response for one staged intent lifecycle operation.
/// </summary>
public sealed class PowerShellStagedIntentResult
{
    internal PowerShellStagedIntentResult(
        PowerShellStagedIntentOperation operation,
        PowerShellStagedIntentStatus status,
        string stageIdentifier,
        DateTimeOffset? expiresAt,
        string? message)
    {
        Operation = operation;
        Status = status;
        StageIdentifier = stageIdentifier;
        ExpiresAt = expiresAt;
        Message = message;
    }

    /// <summary>
    /// Gets the completed lifecycle operation.
    /// </summary>
    public PowerShellStagedIntentOperation Operation { get; }

    /// <summary>
    /// Gets the explicit operation outcome.
    /// </summary>
    public PowerShellStagedIntentStatus Status { get; }

    /// <summary>
    /// Gets the opaque stage identifier, when the request supplied one.
    /// </summary>
    public string StageIdentifier { get; }

    /// <summary>
    /// Gets the retained stage deadline, when one is known.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Gets an optional bounded diagnostic message.
    /// </summary>
    public string? Message { get; }

    internal PowerShellValue ToPowerShellValue()
    {
        return PowerShellValue.PropertyBag(
        [
            new("operation", PowerShellValue.String(ToWireName(Operation))),
            new("status", PowerShellValue.String(ToWireName(Status))),
            new("stageId", PowerShellValue.String(StageIdentifier)),
            new("expiresAt", ExpiresAt is { } value ? PowerShellValue.DateTimeOffset(value) : PowerShellValue.Null),
            new("message", Message is { } message ? PowerShellValue.String(message) : PowerShellValue.Null),
        ]);
    }

    private static string ToWireName(PowerShellStagedIntentOperation value)
    {
        return value switch
        {
            PowerShellStagedIntentOperation.Stage => "stage",
            PowerShellStagedIntentOperation.Validate => "validate",
            PowerShellStagedIntentOperation.Commit => "commit",
            PowerShellStagedIntentOperation.Abort => "abort",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private static string ToWireName(PowerShellStagedIntentStatus value)
    {
        return value switch
        {
            PowerShellStagedIntentStatus.Staged => "staged",
            PowerShellStagedIntentStatus.Validated => "validated",
            PowerShellStagedIntentStatus.Committed => "committed",
            PowerShellStagedIntentStatus.Aborted => "aborted",
            PowerShellStagedIntentStatus.Rejected => "rejected",
            PowerShellStagedIntentStatus.UnknownStage => "unknown-stage",
            PowerShellStagedIntentStatus.Expired => "expired",
            PowerShellStagedIntentStatus.Terminal => "terminal",
            PowerShellStagedIntentStatus.Cancelled => "cancelled",
            PowerShellStagedIntentStatus.Busy => "busy",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }
}
