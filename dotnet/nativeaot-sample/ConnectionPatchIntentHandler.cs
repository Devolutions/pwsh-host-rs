#nullable enable

using System;
using System.Collections.Generic;
using Devolutions.PowerShell.Ffi;

namespace NativeAotFfiSample;

internal sealed class ConnectionPatchIntentHandler : IPowerShellCapabilityHandler
{
    private readonly object gate = new();
    private ConnectionPatchIntent? intent;

    internal ConnectionPatchIntent? Intent
    {
        get
        {
            lock (gate)
            {
                return intent;
            }
        }
    }

    public PowerShellValue Invoke(
        PowerShellCapabilityInvocation invocation,
        IReadOnlyList<PowerShellValue> arguments)
    {
        invocation.CancellationToken.ThrowIfCancellationRequested();
        if (arguments.Count != 1 || arguments[0].Kind != PowerShellValueKind.PropertyBag)
        {
            throw new ArgumentException("The connection patch intent must be one property bag.", nameof(arguments));
        }

        IReadOnlyDictionary<string, PowerShellValue> properties = arguments[0].GetPropertyBag();
        string connectionId = GetRequiredString(properties, "ConnectionId");
        string displayName = GetRequiredString(properties, "DisplayName");
        if (properties.Count != 2)
        {
            throw new ArgumentException("The connection patch intent contains an unsupported property.", nameof(arguments));
        }

        lock (gate)
        {
            intent = new ConnectionPatchIntent(connectionId, displayName);
        }

        return PowerShellValue.PropertyBag(
        [
            new("Accepted", PowerShellValue.Boolean(true)),
        ]);
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

internal sealed record ConnectionPatchIntent(string ConnectionId, string DisplayName);
