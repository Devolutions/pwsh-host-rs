using System.Text;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Coordinates bounded staged intents using the existing capability dispatcher.
/// </summary>
/// <remarks>
/// This coordinator owns only the lifetime of copied staged intent data. A committed
/// result means the host accepted the intent; it is not a cross-resource transaction,
/// does not provide rollback, and does not prove that an application side effect
/// completed. Every retained stage reaches at most one terminal commit or abort.
/// Coordinator-initiated expiry, cancellation, and disposal deliver a best-effort
/// abort notification after releasing the coordinator lock; handlers must make
/// that cleanup idempotent. Applications own authorization, persistence, review
/// UI, effects, and any compensating action.
/// </remarks>
public sealed class PowerShellStagedIntentCoordinator : IDisposable
{
    private const int MaximumStages = 64;
    private const int MaximumTerminalStages = 64;
    private const int MaximumStageIdentifierBytes = 128;
    private const int StageInputOverheadBytes = 128;
    private const int StageIdentifierInputBytes = MaximumStageIdentifierBytes + 32;
    private const int ResponseBytes = 1024;

    private readonly object gate = new();
    private readonly IReadOnlyDictionary<string, PowerShellStagedIntentDefinition> definitions;
    private readonly Dictionary<string, StageEntry> stages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalEntry> terminalStages = new(StringComparer.Ordinal);
    private readonly Queue<string> terminalStageOrder = new();
    private readonly PowerShellCapabilitySet capabilities;
    private int disposed;

    private PowerShellStagedIntentCoordinator(PowerShellStagedIntentDefinition[] definitions)
    {
        this.definitions = definitions.ToDictionary(definition => definition.OperationName, StringComparer.Ordinal);
        capabilities = PowerShellCapabilitySet.Register(CreateBindings(definitions));
    }

    /// <summary>
    /// Gets the existing capability set to attach with <see cref="PowerShell.WithCapabilities"/>.
    /// </summary>
    public PowerShellCapabilitySet Capabilities => capabilities;

    /// <summary>
    /// Gets the registered staged intent definitions.
    /// </summary>
    public IReadOnlyCollection<PowerShellStagedIntentDefinition> Definitions => definitions.Values.ToArray();

    /// <summary>
    /// Registers one to four staged intent definitions over the existing capability dispatcher.
    /// </summary>
    public static PowerShellStagedIntentCoordinator Register(
        IEnumerable<PowerShellStagedIntentDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        PowerShellStagedIntentDefinition[] definitionArray = definitions.ToArray();
        if (definitionArray.Length == 0 ||
            definitionArray.Length > PowerShellStagedIntentDefinition.MaximumDefinitions ||
            definitionArray.Any(definition => definition is null) ||
            definitionArray.Select(definition => definition.OperationName).Distinct(StringComparer.Ordinal).Count() != definitionArray.Length)
        {
            throw new ArgumentException(
                "Staged intent registrations require one to four uniquely named definitions.",
                nameof(definitions));
        }

        return new PowerShellStagedIntentCoordinator(definitionArray);
    }

    /// <summary>
    /// Removes retained stages and unregisters their capability set.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var cleanupNotifications = new List<CleanupNotification>();
        lock (gate)
        {
            foreach ((string stageIdentifier, StageEntry entry) in stages.ToArray())
            {
                CleanupNotification? cleanup = RequestCoordinatorTerminationLocked(
                    stageIdentifier,
                    entry,
                    PowerShellStagedIntentStatus.Cancelled);
                if (cleanup is not null)
                {
                    cleanupNotifications.Add(cleanup);
                }
            }

            terminalStages.Clear();
            terminalStageOrder.Clear();
        }

        DeliverCleanupNotifications(cleanupNotifications);
        capabilities.Dispose();
        GC.SuppressFinalize(this);
    }

    private IEnumerable<PowerShellCapabilityBinding> CreateBindings(
        IEnumerable<PowerShellStagedIntentDefinition> definitions)
    {
        foreach (PowerShellStagedIntentDefinition definition in definitions)
        {
            yield return CreateBinding(definition, PowerShellStagedIntentOperation.Stage);
            yield return CreateBinding(definition, PowerShellStagedIntentOperation.Validate);
            yield return CreateBinding(definition, PowerShellStagedIntentOperation.Commit);
            yield return CreateBinding(definition, PowerShellStagedIntentOperation.Abort);
        }
    }

    private PowerShellCapabilityBinding CreateBinding(
        PowerShellStagedIntentDefinition definition,
        PowerShellStagedIntentOperation operation)
    {
        bool isStage = operation == PowerShellStagedIntentOperation.Stage;
        int maximumInputBytes = isStage
            ? checked(definition.Schema.MaximumPayloadBytes + MaximumStageIdentifierBytes + StageInputOverheadBytes)
            : StageIdentifierInputBytes;
        string name = operation switch
        {
            PowerShellStagedIntentOperation.Stage => $"{definition.OperationName}.stage",
            PowerShellStagedIntentOperation.Validate => $"{definition.OperationName}.validate",
            PowerShellStagedIntentOperation.Commit => $"{definition.OperationName}.commit",
            PowerShellStagedIntentOperation.Abort => $"{definition.OperationName}.abort",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var capability = new PowerShellCapabilityDefinition(
            name,
            [new PowerShellCapabilityArgumentSchema(
                [isStage ? PowerShellValueKind.PropertyBag : PowerShellValueKind.String])],
            [PowerShellValueKind.PropertyBag],
            PowerShellCapabilityPermission.Write,
            maximumInputBytes,
            ResponseBytes,
            PowerShellStagedIntentDefinition.CapabilityCallbackDeadline);
        return new PowerShellCapabilityBinding(capability, new CoordinatorHandler(this, definition, operation));
    }

    private PowerShellValue Invoke(
        PowerShellStagedIntentDefinition definition,
        PowerShellStagedIntentOperation operation,
        PowerShellCapabilityInvocation capabilityInvocation,
        IReadOnlyList<PowerShellValue> arguments)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return CreateResult(operation, PowerShellStagedIntentStatus.Cancelled, string.Empty, null, "The coordinator is disposed.");
        }

        return operation == PowerShellStagedIntentOperation.Stage
            ? Stage(definition, capabilityInvocation.CancellationToken, arguments)
            : Continue(definition, operation, capabilityInvocation.CancellationToken, arguments);
    }

    private PowerShellValue Stage(
        PowerShellStagedIntentDefinition definition,
        CancellationToken cancellationToken,
        IReadOnlyList<PowerShellValue> arguments)
    {
        if (!TryReadStageRequest(arguments, out string? stageIdentifier, out PowerShellValue? payload, out string? requestMessage))
        {
            return CreateResult(
                PowerShellStagedIntentOperation.Stage,
                PowerShellStagedIntentStatus.Rejected,
                stageIdentifier ?? string.Empty,
                null,
                requestMessage);
        }
        if (!definition.Schema.TryValidate(payload!, out string? validationMessage))
        {
            return CreateResult(
                PowerShellStagedIntentOperation.Stage,
                PowerShellStagedIntentStatus.Rejected,
                stageIdentifier!,
                null,
                validationMessage);
        }

        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(definition.Deadline);
        var intent = new PowerShellStagedIntent(definition.OperationName, stageIdentifier!, payload!, expiresAt);
        var entry = new StageEntry(definition, intent);
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return CreateResult(
                    PowerShellStagedIntentOperation.Stage,
                    PowerShellStagedIntentStatus.Cancelled,
                    stageIdentifier!,
                    null,
                    "The coordinator is disposed.");
            }
            if (stages.ContainsKey(stageIdentifier!) || terminalStages.ContainsKey(stageIdentifier!))
            {
                return CreateResult(
                    PowerShellStagedIntentOperation.Stage,
                    PowerShellStagedIntentStatus.Rejected,
                    stageIdentifier!,
                    null,
                    "The stage identifier is already in use.");
            }
            if (stages.Count == MaximumStages)
            {
                return CreateResult(
                    PowerShellStagedIntentOperation.Stage,
                    PowerShellStagedIntentStatus.Rejected,
                    stageIdentifier!,
                    null,
                    "The coordinator has reached its stage limit.");
            }

            stages.Add(stageIdentifier!, entry);
        }

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => ((CancellationState)state!).Coordinator.CancelStage(((CancellationState)state!).StageIdentifier),
            new CancellationState(this, stageIdentifier!));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PowerShellStagedIntentHandlerResult result = InvokeHandler(
                definition,
                PowerShellStagedIntentOperation.Stage,
                intent,
                cancellationToken);
            CleanupNotification? cleanup = null;
            PowerShellValue response;
            lock (gate)
            {
                if (!stages.TryGetValue(stageIdentifier!, out StageEntry? current) || !ReferenceEquals(current, entry))
                {
                    response = CreateTerminalResult(
                        PowerShellStagedIntentOperation.Stage,
                        stageIdentifier!,
                        expiresAt);
                }
                else
                {
                    TimeSpan remaining = expiresAt - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        entry.PendingTermination ??= PowerShellStagedIntentStatus.Expired;
                    }

                    if (result.IsAccepted)
                    {
                        entry.State = StageState.Staged;
                        PowerShellStagedIntentStatus? termination = entry.PendingTermination;
                        cleanup = ApplyPendingTerminationLocked(stageIdentifier!, entry);
                        if (termination is { } status)
                        {
                            response = CreateResult(
                                PowerShellStagedIntentOperation.Stage,
                                status,
                                stageIdentifier!,
                                expiresAt,
                                status == PowerShellStagedIntentStatus.Expired
                                    ? "The stage deadline elapsed before the intent was retained."
                                    : "The stage operation was cancelled.");
                        }
                        else
                        {
                            entry.Timer = new Timer(
                                static state => ((ExpiryState)state!).Coordinator.ExpireStage(((ExpiryState)state!).StageIdentifier),
                                new ExpiryState(this, stageIdentifier!),
                                dueTime: remaining,
                                period: Timeout.InfiniteTimeSpan);
                            response = CreateResult(
                                PowerShellStagedIntentOperation.Stage,
                                PowerShellStagedIntentStatus.Staged,
                                stageIdentifier!,
                                expiresAt,
                                result.Message);
                        }
                    }
                    else if (entry.PendingTermination is { } status)
                    {
                        entry.State = StageState.Staged;
                        cleanup = ApplyPendingTerminationLocked(stageIdentifier!, entry);
                        response = CreateResult(
                            PowerShellStagedIntentOperation.Stage,
                            status,
                            stageIdentifier!,
                            expiresAt,
                            "The stage operation ended before the intent was retained.");
                    }
                    else
                    {
                        stages.Remove(stageIdentifier!);
                        response = CreateResult(
                            PowerShellStagedIntentOperation.Stage,
                            PowerShellStagedIntentStatus.Rejected,
                            stageIdentifier!,
                            expiresAt,
                            result.Message);
                    }
                }
            }

            DeliverCleanupNotification(cleanup);
            return response;
        }
        catch (OperationCanceledException)
        {
            CleanupNotification? cleanup = null;
            lock (gate)
            {
                if (stages.TryGetValue(stageIdentifier!, out StageEntry? current) && ReferenceEquals(current, entry))
                {
                    entry.State = StageState.Staged;
                    entry.PendingTermination ??= PowerShellStagedIntentStatus.Cancelled;
                    cleanup = ApplyPendingTerminationLocked(stageIdentifier!, entry);
                }
            }

            DeliverCleanupNotification(cleanup);
            return CreateResult(
                PowerShellStagedIntentOperation.Stage,
                PowerShellStagedIntentStatus.Cancelled,
                stageIdentifier!,
                expiresAt,
                "The stage operation was cancelled.");
        }
        catch
        {
            CleanupNotification? cleanup = null;
            lock (gate)
            {
                if (stages.TryGetValue(stageIdentifier!, out StageEntry? current) && ReferenceEquals(current, entry))
                {
                    entry.State = StageState.Staged;
                    entry.PendingTermination ??= PowerShellStagedIntentStatus.Cancelled;
                    cleanup = ApplyPendingTerminationLocked(stageIdentifier!, entry);
                }
            }

            DeliverCleanupNotification(cleanup);
            throw;
        }
    }

    private PowerShellValue Continue(
        PowerShellStagedIntentDefinition definition,
        PowerShellStagedIntentOperation operation,
        CancellationToken cancellationToken,
        IReadOnlyList<PowerShellValue> arguments)
    {
        if (!TryReadStageIdentifier(arguments, out string? stageIdentifier))
        {
            return CreateResult(operation, PowerShellStagedIntentStatus.Rejected, string.Empty, null, "The stage identifier is invalid.");
        }

        StageEntry? entry;
        StageState previousState;
        CleanupNotification? immediateCleanup = null;
        PowerShellValue? immediateResponse = null;
        lock (gate)
        {
            if (!stages.TryGetValue(stageIdentifier!, out entry))
            {
                return CreateMissingResult(operation, stageIdentifier!);
            }
            if (DateTimeOffset.UtcNow >= entry.Intent.ExpiresAt)
            {
                immediateCleanup = RequestCoordinatorTerminationLocked(
                    stageIdentifier!,
                    entry,
                    PowerShellStagedIntentStatus.Expired);
                immediateResponse = CreateResult(
                    operation,
                    PowerShellStagedIntentStatus.Expired,
                    stageIdentifier!,
                    entry.Intent.ExpiresAt,
                    "The stage deadline has elapsed.");
            }
            else if (!CanBegin(operation, entry.State))
            {
                return CreateResult(
                    operation,
                    entry.State is StageState.Staged or StageState.Validated
                        ? PowerShellStagedIntentStatus.Rejected
                        : PowerShellStagedIntentStatus.Busy,
                    stageIdentifier!,
                    entry.Intent.ExpiresAt,
                    entry.State is StageState.Staged or StageState.Validated
                        ? "The stage is not ready for this operation."
                        : "Another lifecycle operation is in progress.");
            }

            previousState = entry.State;
            entry.State = operation switch
            {
                PowerShellStagedIntentOperation.Validate => StageState.Validating,
                PowerShellStagedIntentOperation.Commit => StageState.Committing,
                PowerShellStagedIntentOperation.Abort => StageState.Aborting,
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
        }

        if (immediateResponse is not null)
        {
            DeliverCleanupNotification(immediateCleanup);
            return immediateResponse;
        }

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => ((CancellationState)state!).Coordinator.CancelStage(((CancellationState)state!).StageIdentifier),
            new CancellationState(this, stageIdentifier!));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PowerShellStagedIntentHandlerResult result = InvokeHandler(definition, operation, entry.Intent, cancellationToken);
            CleanupNotification? cleanup = null;
            PowerShellValue response;
            lock (gate)
            {
                if (!stages.TryGetValue(stageIdentifier!, out StageEntry? current) || !ReferenceEquals(current, entry))
                {
                    response = CreateTerminalResult(operation, stageIdentifier!, entry.Intent.ExpiresAt);
                }
                else if (!result.IsAccepted)
                {
                    entry.State = previousState;
                    PowerShellStagedIntentStatus? termination = entry.PendingTermination;
                    cleanup = ApplyPendingTerminationLocked(stageIdentifier!, entry);
                    response = termination is { } status
                        ? CreateResult(
                            operation,
                            status,
                            stageIdentifier!,
                            entry.Intent.ExpiresAt,
                            "The lifecycle operation ended before the stage could remain active.")
                        : CreateResult(
                            operation,
                            PowerShellStagedIntentStatus.Rejected,
                            stageIdentifier!,
                            entry.Intent.ExpiresAt,
                            result.Message);
                }
                else
                {
                    switch (operation)
                    {
                        case PowerShellStagedIntentOperation.Validate:
                            entry.State = StageState.Validated;
                            PowerShellStagedIntentStatus? termination = entry.PendingTermination;
                            cleanup = ApplyPendingTerminationLocked(stageIdentifier!, entry);
                            response = termination is { } status
                                ? CreateResult(
                                    operation,
                                    status,
                                    stageIdentifier!,
                                    entry.Intent.ExpiresAt,
                                    "The stage was terminated after validation completed.")
                                : CreateResult(
                                    operation,
                                    PowerShellStagedIntentStatus.Validated,
                                    stageIdentifier!,
                                    entry.Intent.ExpiresAt,
                                    result.Message);
                            break;
                        case PowerShellStagedIntentOperation.Commit:
                            _ = TerminateLocked(
                                stageIdentifier!,
                                entry,
                                PowerShellStagedIntentStatus.Committed,
                                inactiveStatus: null,
                                notifyHandler: false);
                            response = CreateResult(
                                operation,
                                PowerShellStagedIntentStatus.Committed,
                                stageIdentifier!,
                                entry.Intent.ExpiresAt,
                                result.Message);
                            break;
                        case PowerShellStagedIntentOperation.Abort:
                            _ = TerminateLocked(
                                stageIdentifier!,
                                entry,
                                PowerShellStagedIntentStatus.Aborted,
                                inactiveStatus: null,
                                notifyHandler: false);
                            response = CreateResult(
                                operation,
                                PowerShellStagedIntentStatus.Aborted,
                                stageIdentifier!,
                                entry.Intent.ExpiresAt,
                                result.Message);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(operation));
                    }
                }
            }

            DeliverCleanupNotification(cleanup);
            return response;
        }
        catch (OperationCanceledException)
        {
            CleanupNotification? cleanup = null;
            lock (gate)
            {
                if (stages.TryGetValue(stageIdentifier!, out StageEntry? current) && ReferenceEquals(current, entry))
                {
                    entry.State = previousState;
                    entry.PendingTermination ??= PowerShellStagedIntentStatus.Cancelled;
                    cleanup = ApplyPendingTerminationLocked(stageIdentifier!, entry);
                }
            }

            DeliverCleanupNotification(cleanup);
            return CreateResult(
                operation,
                PowerShellStagedIntentStatus.Cancelled,
                stageIdentifier!,
                entry.Intent.ExpiresAt,
                "The lifecycle operation was cancelled.");
        }
        catch
        {
            CleanupNotification? cleanup = null;
            lock (gate)
            {
                if (stages.TryGetValue(stageIdentifier!, out StageEntry? current) && ReferenceEquals(current, entry))
                {
                    entry.State = previousState;
                    entry.PendingTermination ??= PowerShellStagedIntentStatus.Cancelled;
                    cleanup = ApplyPendingTerminationLocked(stageIdentifier!, entry);
                }
            }

            DeliverCleanupNotification(cleanup);
            throw;
        }
    }

    private static bool CanBegin(PowerShellStagedIntentOperation operation, StageState state)
    {
        return operation switch
        {
            PowerShellStagedIntentOperation.Validate => state is StageState.Staged or StageState.Validated,
            PowerShellStagedIntentOperation.Commit => state == StageState.Validated,
            PowerShellStagedIntentOperation.Abort => state is StageState.Staged or StageState.Validated,
            _ => false,
        };
    }

    private static PowerShellStagedIntentHandlerResult InvokeHandler(
        PowerShellStagedIntentDefinition definition,
        PowerShellStagedIntentOperation operation,
        PowerShellStagedIntent intent,
        CancellationToken cancellationToken)
    {
        PowerShellStagedIntentHandlerResult result = definition.Handler.Invoke(
            new PowerShellStagedIntentInvocation(operation, intent, cancellationToken));
        return result ?? throw new InvalidOperationException("The staged intent handler returned null.");
    }

    private static bool TryReadStageRequest(
        IReadOnlyList<PowerShellValue> arguments,
        out string? stageIdentifier,
        out PowerShellValue? payload,
        out string? message)
    {
        stageIdentifier = null;
        payload = null;
        if (arguments.Count != 1 || arguments[0].Kind != PowerShellValueKind.PropertyBag)
        {
            message = "The stage request must be a property bag.";
            return false;
        }

        IReadOnlyDictionary<string, PowerShellValue> properties = arguments[0].GetPropertyBag();
        if (properties.Count != 2 ||
            !properties.TryGetValue("stageId", out PowerShellValue? stageIdentifierValue) ||
            !stageIdentifierValue.TryGetString(out stageIdentifier) ||
            !IsStageIdentifier(stageIdentifier) ||
            !properties.TryGetValue("intent", out payload) ||
            payload.Kind != PowerShellValueKind.PropertyBag)
        {
            message = "The stage request requires a bounded stageId and intent property bag.";
            return false;
        }

        message = null;
        return true;
    }

    private static bool TryReadStageIdentifier(IReadOnlyList<PowerShellValue> arguments, out string? stageIdentifier)
    {
        stageIdentifier = null;
        return arguments.Count == 1 &&
            arguments[0].TryGetString(out stageIdentifier) &&
            IsStageIdentifier(stageIdentifier);
    }

    private static bool IsStageIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            Encoding.UTF8.GetByteCount(value) <= MaximumStageIdentifierBytes;
    }

    private PowerShellValue CreateMissingResult(PowerShellStagedIntentOperation operation, string stageIdentifier)
    {
        if (terminalStages.TryGetValue(stageIdentifier, out TerminalEntry? terminal))
        {
            return CreateResult(
                operation,
                terminal.InactiveStatus is { } status
                    ? status
                    : PowerShellStagedIntentStatus.Terminal,
                stageIdentifier,
                terminal.ExpiresAt,
                terminal.InactiveStatus is null
                    ? "The stage already reached a terminal transition."
                    : "The stage is no longer active.");
        }

        return CreateResult(
            operation,
            PowerShellStagedIntentStatus.UnknownStage,
            stageIdentifier,
            null,
            "The stage identifier is not known.");
    }

    private PowerShellValue CreateTerminalResult(
        PowerShellStagedIntentOperation operation,
        string stageIdentifier,
        DateTimeOffset expiresAt)
    {
        lock (gate)
        {
            return terminalStages.TryGetValue(stageIdentifier, out TerminalEntry? terminal)
                ? CreateMissingResult(operation, stageIdentifier)
                : CreateResult(
                    operation,
                    PowerShellStagedIntentStatus.Cancelled,
                    stageIdentifier,
                    expiresAt,
                    "The stage was cancelled.");
        }
    }

    private static PowerShellValue CreateResult(
        PowerShellStagedIntentOperation operation,
        PowerShellStagedIntentStatus status,
        string stageIdentifier,
        DateTimeOffset? expiresAt,
        string? message)
    {
        return new PowerShellStagedIntentResult(operation, status, stageIdentifier, expiresAt, message).ToPowerShellValue();
    }

    private void ExpireStage(string stageIdentifier)
    {
        CleanupNotification? cleanup = null;
        lock (gate)
        {
            if (stages.TryGetValue(stageIdentifier, out StageEntry? entry))
            {
                cleanup = RequestCoordinatorTerminationLocked(
                    stageIdentifier,
                    entry,
                    PowerShellStagedIntentStatus.Expired);
            }
        }

        DeliverCleanupNotification(cleanup);
    }

    private void CancelStage(string stageIdentifier)
    {
        CleanupNotification? cleanup = null;
        lock (gate)
        {
            if (stages.TryGetValue(stageIdentifier, out StageEntry? entry))
            {
                cleanup = RequestCoordinatorTerminationLocked(
                    stageIdentifier,
                    entry,
                    PowerShellStagedIntentStatus.Cancelled);
            }
        }

        DeliverCleanupNotification(cleanup);
    }

    private CleanupNotification? RequestCoordinatorTerminationLocked(
        string stageIdentifier,
        StageEntry entry,
        PowerShellStagedIntentStatus inactiveStatus)
    {
        if (!stages.TryGetValue(stageIdentifier, out StageEntry? current) || !ReferenceEquals(current, entry))
        {
            return null;
        }

        if (IsOperationInFlight(entry.State))
        {
            entry.PendingTermination ??= inactiveStatus;
            return null;
        }

        return TerminateLocked(
            stageIdentifier,
            entry,
            PowerShellStagedIntentStatus.Aborted,
            inactiveStatus,
            notifyHandler: true);
    }

    private CleanupNotification? ApplyPendingTerminationLocked(string stageIdentifier, StageEntry entry)
    {
        if (!stages.TryGetValue(stageIdentifier, out StageEntry? current) || !ReferenceEquals(current, entry))
        {
            return null;
        }
        if (entry.PendingTermination is not { } inactiveStatus)
        {
            return null;
        }

        entry.PendingTermination = null;
        return TerminateLocked(
            stageIdentifier,
            entry,
            PowerShellStagedIntentStatus.Aborted,
            inactiveStatus,
            notifyHandler: true);
    }

    private static bool IsOperationInFlight(StageState state)
    {
        return state is StageState.Staging or StageState.Validating or StageState.Committing or StageState.Aborting;
    }

    private CleanupNotification? TerminateLocked(
        string stageIdentifier,
        StageEntry entry,
        PowerShellStagedIntentStatus terminalStatus,
        PowerShellStagedIntentStatus? inactiveStatus,
        bool notifyHandler)
    {
        stages.Remove(stageIdentifier);
        entry.Timer?.Dispose();
        terminalStages[stageIdentifier] = new TerminalEntry(terminalStatus, inactiveStatus, entry.Intent.ExpiresAt);
        terminalStageOrder.Enqueue(stageIdentifier);
        while (terminalStageOrder.Count > MaximumTerminalStages)
        {
            terminalStages.Remove(terminalStageOrder.Dequeue());
        }

        return notifyHandler ? new CleanupNotification(entry.Definition, entry.Intent) : null;
    }

    private static void DeliverCleanupNotifications(IEnumerable<CleanupNotification> notifications)
    {
        foreach (CleanupNotification notification in notifications)
        {
            DeliverCleanupNotification(notification);
        }
    }

    private static void DeliverCleanupNotification(CleanupNotification? notification)
    {
        if (notification is null)
        {
            return;
        }

        try
        {
            _ = InvokeHandler(
                notification.Definition,
                PowerShellStagedIntentOperation.Abort,
                notification.Intent,
                CancellationToken.None);
        }
        catch
        {
        }
    }

    private sealed class CoordinatorHandler : IPowerShellCapabilityHandler
    {
        private readonly PowerShellStagedIntentCoordinator coordinator;
        private readonly PowerShellStagedIntentDefinition definition;
        private readonly PowerShellStagedIntentOperation operation;

        internal CoordinatorHandler(
            PowerShellStagedIntentCoordinator coordinator,
            PowerShellStagedIntentDefinition definition,
            PowerShellStagedIntentOperation operation)
        {
            this.coordinator = coordinator;
            this.definition = definition;
            this.operation = operation;
        }

        public PowerShellValue Invoke(
            PowerShellCapabilityInvocation invocation,
            IReadOnlyList<PowerShellValue> arguments)
        {
            return coordinator.Invoke(definition, operation, invocation, arguments);
        }
    }

    private sealed class StageEntry
    {
        internal StageEntry(
            PowerShellStagedIntentDefinition definition,
            PowerShellStagedIntent intent)
        {
            Definition = definition;
            Intent = intent;
        }

        internal PowerShellStagedIntentDefinition Definition { get; }

        internal PowerShellStagedIntent Intent { get; }

        internal StageState State { get; set; }

        internal PowerShellStagedIntentStatus? PendingTermination { get; set; }

        internal Timer? Timer { get; set; }
    }

    private sealed record TerminalEntry(
        PowerShellStagedIntentStatus TerminalStatus,
        PowerShellStagedIntentStatus? InactiveStatus,
        DateTimeOffset ExpiresAt);

    private sealed record CancellationState(PowerShellStagedIntentCoordinator Coordinator, string StageIdentifier);

    private sealed record ExpiryState(PowerShellStagedIntentCoordinator Coordinator, string StageIdentifier);

    private sealed record CleanupNotification(
        PowerShellStagedIntentDefinition Definition,
        PowerShellStagedIntent Intent);

    private enum StageState
    {
        Staging,
        Staged,
        Validating,
        Validated,
        Committing,
        Aborting,
    }
}
