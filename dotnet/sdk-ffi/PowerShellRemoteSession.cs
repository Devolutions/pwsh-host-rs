namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Immutable copied metadata for a payload-owned PSSession.
/// </summary>
public sealed class PowerShellRemoteSessionMetadata
{
    internal PowerShellRemoteSessionMetadata(long id, string state, string computerName, string availability)
    {
        Id = id;
        State = state;
        ComputerName = computerName;
        Availability = availability;
    }

    public long Id { get; }

    public string State { get; }

    public string ComputerName { get; }

    public string Availability { get; }
}

/// <summary>
/// Bounded creation options for a payload-owned PSSession.
/// </summary>
public sealed class PowerShellRemoteSessionOptions
{
    public PowerShellRemoteSessionOptions(
        string computerName,
        PowerShellCredential? credential = null,
        bool useSsl = false,
        int port = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computerName);
        if (computerName.IndexOf('\0') >= 0 || computerName.Length > 255)
        {
            throw new ArgumentException("Remote computer names must be non-NUL text of at most 255 characters.", nameof(computerName));
        }
        if (port is < 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        ComputerName = computerName;
        Credential = credential;
        UseSsl = useSsl;
        Port = port;
    }

    public string ComputerName { get; }

    public PowerShellCredential? Credential { get; }

    public bool UseSsl { get; }

    public int Port { get; }
}

/// <summary>
/// An opaque PSSession retained entirely inside a supplied persistent local
/// session. It exposes only copied invocation results and copied metadata.
/// </summary>
public sealed class PowerShellRemoteSession : IDisposable
{
    private const string CreateScript = """
        param($computerName, $credential, $useSsl, $port)
        $parameters = @{ ComputerName = $computerName; ErrorAction = 'Stop' }
        if ($null -ne $credential) { $parameters.Credential = $credential }
        if ($useSsl) { $parameters.UseSSL = $true }
        if ($port -ne 0) { $parameters.Port = $port }
        Set-Variable -Scope Global -Name '__MULTIPWSH_REMOTE_VARIABLE__' -Value (New-PSSession @parameters)
        """;
    private const string RemoveScript = """
        $remoteSession = Get-Variable -Scope Global -Name '__MULTIPWSH_REMOTE_VARIABLE__' -ValueOnly -ErrorAction Stop
        Remove-PSSession -Session $remoteSession -ErrorAction SilentlyContinue
        Remove-Variable -Scope Global -Name '__MULTIPWSH_REMOTE_VARIABLE__' -ErrorAction SilentlyContinue
        """;
    private const string InvokeRemoteScript = """
        param($script, $arguments)
        $remoteSession = Get-Variable -Scope Global -Name '__MULTIPWSH_REMOTE_VARIABLE__' -ValueOnly -ErrorAction Stop
        Invoke-Command -Session $remoteSession -ScriptBlock ([ScriptBlock]::Create($script)) -ArgumentList $arguments
        """;
    private const string InvokeCommandScript = """
        param($commandName, $arguments)
        $remoteSession = Get-Variable -Scope Global -Name '__MULTIPWSH_REMOTE_VARIABLE__' -ValueOnly -ErrorAction Stop
        Invoke-Command -Session $remoteSession -ScriptBlock {
            param($remoteCommandName, $remoteArguments)
            & $remoteCommandName @remoteArguments
        } -ArgumentList $commandName, $arguments
        """;
    private const string MetadataScript = """
        $remoteSession = Get-Variable -Scope Global -Name '__MULTIPWSH_REMOTE_VARIABLE__' -ValueOnly -ErrorAction Stop
        [PSCustomObject]@{
            Id = [long]$remoteSession.Id
            State = [string]$remoteSession.State
            ComputerName = [string]$remoteSession.ComputerName
            Availability = [string]$remoteSession.Availability
        }
        """;

    private readonly PowerShellSession owner;
    private readonly string variableName;
    private int disposed;

    private PowerShellRemoteSession(PowerShellSession owner, string variableName)
    {
        this.owner = owner;
        this.variableName = variableName;
    }

    public static PowerShellRemoteSession Create(PowerShellSession owner, PowerShellRemoteSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        string variableName = $"__multiPwshRemote_{Guid.NewGuid():N}";
        using PowerShell powerShell = owner.CreatePowerShell();
        powerShell.AddScript(CreateScript.Replace("__MULTIPWSH_REMOTE_VARIABLE__", variableName, StringComparison.Ordinal))
            .AddParameter("computerName", options.ComputerName)
            .AddParameter("useSsl", PowerShellValue.Boolean(options.UseSsl))
            .AddParameter("port", (long)options.Port);
        if (options.Credential is null)
        {
            powerShell.AddParameter("credential", PowerShellValue.Null);
            _ = powerShell.InvokeWithDiagnostics();
        }
        else
        {
            powerShell.AddParameter("credential", options.Credential);
            using PowerShellSecretResult result = powerShell.InvokeWithSecretBindings();
        }

        return new PowerShellRemoteSession(owner, variableName);
    }

    public PowerShellInvocationResult InvokeScript(string script, IEnumerable<PowerShellValue>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ThrowIfDisposed();
        using PowerShell powerShell = owner.CreatePowerShell();
        return powerShell
            .AddScript(Format(InvokeRemoteScript))
            .AddParameter("script", script)
            .AddParameter("arguments", PowerShellValue.Array(arguments ?? Array.Empty<PowerShellValue>()))
            .InvokeWithDiagnostics();
    }

    public PowerShellInvocationResult InvokeCommand(string commandName, IEnumerable<PowerShellValue>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ThrowIfDisposed();
        using PowerShell powerShell = owner.CreatePowerShell();
        return powerShell
            .AddScript(Format(InvokeCommandScript))
            .AddParameter("commandName", commandName)
            .AddParameter("arguments", PowerShellValue.Array(arguments ?? Array.Empty<PowerShellValue>()))
            .InvokeWithDiagnostics();
    }

    public PowerShellRemoteSessionMetadata GetMetadata()
    {
        ThrowIfDisposed();
        using PowerShell powerShell = owner.CreatePowerShell();
        PowerShellInvocationResult result = powerShell.AddScript(Format(MetadataScript)).InvokeWithDiagnostics();
        if (result.Output.IsTruncated ||
            result.Output.Records.Count != 1 ||
            result.Output.Records[0].PropertyBag is not PowerShellValue properties ||
            result.Output.Records[0].IsPropertyBagTruncated)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "The payload returned invalid PSSession metadata.");
        }

        IReadOnlyDictionary<string, PowerShellValue> values = properties.GetPropertyBag();
        return new PowerShellRemoteSessionMetadata(
            GetId(values, "Id"),
            GetString(values, "State"),
            GetString(values, "ComputerName"),
            GetString(values, "Availability"));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        using PowerShell powerShell = owner.CreatePowerShell();
        _ = powerShell.AddScript(Format(RemoveScript)).InvokeWithDiagnostics();
    }

    private string Format(string template)
    {
        return template.Replace("__MULTIPWSH_REMOTE_VARIABLE__", variableName, StringComparison.Ordinal);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private static long GetId(IReadOnlyDictionary<string, PowerShellValue> values, string name)
    {
        if (!values.TryGetValue(name, out PowerShellValue? value))
        {
            throw new PowerShellFfiException(PowerShellFfiStatus.ManagedFailure, "The payload returned invalid PSSession metadata.");
        }
        if (value.TryGetSignedInteger(out long signed))
        {
            return signed;
        }
        if (value.TryGetUnsignedInteger(out ulong unsigned) && unsigned <= long.MaxValue)
        {
            return (long)unsigned;
        }

        throw new PowerShellFfiException(PowerShellFfiStatus.ManagedFailure, "The payload returned invalid PSSession metadata.");
    }

    private static string GetString(IReadOnlyDictionary<string, PowerShellValue> values, string name)
    {
        if (!values.TryGetValue(name, out PowerShellValue? value) ||
            !value.TryGetString(out string? text) ||
            string.IsNullOrWhiteSpace(text))
        {
            throw new PowerShellFfiException(PowerShellFfiStatus.ManagedFailure, "The payload returned invalid PSSession metadata.");
        }

        return text;
    }
}
