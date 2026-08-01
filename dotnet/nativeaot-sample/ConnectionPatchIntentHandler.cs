#nullable enable

using System;
using System.Collections.Generic;
using Devolutions.PowerShell.Ffi;

namespace NativeAotFfiSample;

internal sealed class ConnectionPatchIntentHandler : IPowerShellStagedIntentHandler
{
    private readonly object gate = new();
    private readonly Dictionary<string, ConnectionPatchIntent> staged = new(StringComparer.Ordinal);
    private string? abortedStageIdentifier;
    private ConnectionPatchIntent? committedIntent;

    internal ConnectionPatchIntent? CommittedIntent
    {
        get
        {
            lock (gate)
            {
                return committedIntent;
            }
        }
    }

    internal string? AbortedStageIdentifier
    {
        get
        {
            lock (gate)
            {
                return abortedStageIdentifier;
            }
        }
    }

    public PowerShellStagedIntentHandlerResult Invoke(PowerShellStagedIntentInvocation invocation)
    {
        invocation.CancellationToken.ThrowIfCancellationRequested();
        if (invocation.Intent.OperationName != "rdm.connection-patch")
        {
            throw new ArgumentException("The connection patch intent operation is invalid.", nameof(invocation));
        }

        IReadOnlyDictionary<string, PowerShellValue> properties = invocation.Intent.Intent.GetPropertyBag();
        string connectionId = GetRequiredString(properties, "ConnectionId");
        string displayName = GetRequiredString(properties, "DisplayName");
        if (properties.Count != 2)
        {
            throw new ArgumentException("The connection patch intent contains an unsupported property.", nameof(invocation));
        }

        var intent = new ConnectionPatchIntent(invocation.Intent.StageIdentifier, connectionId, displayName);
        lock (gate)
        {
            return invocation.Operation switch
            {
                PowerShellStagedIntentOperation.Stage => Stage(intent),
                PowerShellStagedIntentOperation.Validate => Validate(intent),
                PowerShellStagedIntentOperation.Commit => Commit(intent),
                PowerShellStagedIntentOperation.Abort => Abort(intent),
                _ => throw new ArgumentOutOfRangeException(nameof(invocation)),
            };
        }
    }

    private PowerShellStagedIntentHandlerResult Stage(ConnectionPatchIntent intent)
    {
        if (!staged.TryAdd(intent.StageIdentifier, intent))
        {
            return PowerShellStagedIntentHandlerResult.Reject("The patch is already staged.");
        }

        return PowerShellStagedIntentHandlerResult.Accept("The patch is staged for review.");
    }

    private PowerShellStagedIntentHandlerResult Validate(ConnectionPatchIntent intent)
    {
        return staged.TryGetValue(intent.StageIdentifier, out ConnectionPatchIntent? stagedIntent) &&
            stagedIntent == intent
            ? PowerShellStagedIntentHandlerResult.Accept("The patch is approved.")
            : PowerShellStagedIntentHandlerResult.Reject("The patch is no longer staged.");
    }

    private PowerShellStagedIntentHandlerResult Commit(ConnectionPatchIntent intent)
    {
        if (!staged.Remove(intent.StageIdentifier))
        {
            return PowerShellStagedIntentHandlerResult.Reject("The patch is no longer staged.");
        }

        committedIntent = intent;
        return PowerShellStagedIntentHandlerResult.Accept("The host accepted the patch.");
    }

    private PowerShellStagedIntentHandlerResult Abort(ConnectionPatchIntent intent)
    {
        if (staged.Remove(intent.StageIdentifier))
        {
            abortedStageIdentifier = intent.StageIdentifier;
        }

        return PowerShellStagedIntentHandlerResult.Accept("The patch was discarded or already released.");
    }

    private static string GetRequiredString(
        IReadOnlyDictionary<string, PowerShellValue> properties,
        string name)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value) ||
            !value.TryGetString(out string? text) ||
            string.IsNullOrWhiteSpace(text) ||
            text.Length > 128)
        {
            throw new ArgumentException($"The connection patch intent has an invalid {name}.");
        }

        return text;
    }
}

internal sealed record ConnectionPatchIntent(string StageIdentifier, string ConnectionId, string DisplayName);
