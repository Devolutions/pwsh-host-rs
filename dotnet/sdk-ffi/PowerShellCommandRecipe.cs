using System.Collections.ObjectModel;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// A bounded, copied one-shot command invocation definition.
/// </summary>
public sealed class PowerShellCommandRecipe
{
    private readonly IReadOnlyDictionary<string, PowerShellValue> parameters;

    public PowerShellCommandRecipe(
        string command,
        IEnumerable<KeyValuePair<string, PowerShellValue>>? parameters = null,
        PowerShellResultSchema? resultSchema = null,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 128 || command.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Command names must be non-empty, non-NUL text of at most 128 characters.", nameof(command));
        }

        var copy = new Dictionary<string, PowerShellValue>(StringComparer.OrdinalIgnoreCase);
        if (parameters is not null)
        {
            foreach (KeyValuePair<string, PowerShellValue> parameter in parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter.Key) || parameter.Key.Length > 64 || parameter.Key.IndexOf('\0') >= 0)
                {
                    throw new ArgumentException("Parameter names must be non-empty, non-NUL text of at most 64 characters.", nameof(parameters));
                }
                ArgumentNullException.ThrowIfNull(parameter.Value, nameof(parameters));
                if (copy.Count == PowerShellValue.MaximumContainerEntries || !copy.TryAdd(parameter.Key, parameter.Value))
                {
                    throw new ArgumentException("Recipe parameters must be unique and bounded.", nameof(parameters));
                }
            }
        }

        if (timeout is { } timeoutValue &&
            (timeoutValue <= TimeSpan.Zero || timeoutValue > TimeSpan.FromMinutes(5)))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Command = command;
        this.parameters = new ReadOnlyDictionary<string, PowerShellValue>(copy);
        ResultSchema = resultSchema;
        Timeout = timeout;
    }

    public string Command { get; }

    public IReadOnlyDictionary<string, PowerShellValue> Parameters => parameters;

    public PowerShellResultSchema? ResultSchema { get; }

    public TimeSpan? Timeout { get; }

    internal void Apply(PowerShell powerShell)
    {
        powerShell.AddCommand(Command);
        foreach (KeyValuePair<string, PowerShellValue> parameter in parameters)
        {
            powerShell.AddParameter(parameter.Key, parameter.Value);
        }
    }
}

/// <summary>
/// A bounded script recipe. The source remains subject to the caller's own
/// authorization and is never represented as a CLR object graph.
/// </summary>
public sealed class PowerShellScriptRecipe
{
    public PowerShellScriptRecipe(
        string script,
        PowerShellResultSchema? resultSchema = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.IndexOf('\0') >= 0 || System.Text.Encoding.UTF8.GetByteCount(script) > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(script), "Scripts must be non-NUL UTF-8 text of at most 64 KiB.");
        }
        if (timeout is { } timeoutValue &&
            (timeoutValue <= TimeSpan.Zero || timeoutValue > TimeSpan.FromMinutes(5)))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Script = script;
        ResultSchema = resultSchema;
        Timeout = timeout;
    }

    public string Script { get; }

    public PowerShellResultSchema? ResultSchema { get; }

    public TimeSpan? Timeout { get; }

    internal void Apply(PowerShell powerShell)
    {
        powerShell.AddScript(Script);
    }
}
