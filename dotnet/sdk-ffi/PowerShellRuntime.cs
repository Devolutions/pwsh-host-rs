namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellRuntime
{
    private PowerShellRuntime(
        uint abiVersion,
        ulong featureFlags,
        string payloadDirectory)
    {
        AbiVersion = abiVersion;
        FeatureFlags = featureFlags;
        PayloadDirectory = payloadDirectory;
    }

    public uint AbiVersion { get; }

    public ulong FeatureFlags { get; }

    public string PayloadDirectory { get; }

    public static PowerShellRuntime Activate()
    {
        PowerShell.Initialize();
        return CreateActivatedRuntime();
    }

    public static PowerShellRuntime Activate(string payloadDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        PowerShell.Initialize(payloadDirectory);
        return CreateActivatedRuntime();
    }

    private static PowerShellRuntime CreateActivatedRuntime()
    {
        return new PowerShellRuntime(
            PowerShell.AbiVersion,
            PowerShell.FeatureFlags,
            PowerShell.GetActivePayloadDirectory());
    }

    public PowerShell Create()
    {
        return PowerShell.Create();
    }

    public PowerShellInvocationResult Invoke(
        PowerShellCommandRecipe recipe,
        PowerShellCommandPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        policy?.Validate(recipe);
        using PowerShell powerShell = Create();
        recipe.Apply(powerShell);
        return InvokeRecipe(powerShell, recipe.ResultSchema, recipe.Timeout);
    }

    public PowerShellInvocationResult Invoke(
        PowerShellScriptRecipe recipe,
        PowerShellCommandPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        policy?.Validate(recipe);
        using PowerShell powerShell = Create();
        recipe.Apply(powerShell);
        return InvokeRecipe(powerShell, recipe.ResultSchema, recipe.Timeout);
    }

    public async Task<PowerShellInvocationResult> InvokeAsync(
        PowerShellCommandRecipe recipe,
        PowerShellCommandPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        policy?.Validate(recipe);
        using PowerShell powerShell = Create();
        recipe.Apply(powerShell);
        return await InvokeAsync(powerShell, recipe.ResultSchema, recipe.Timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PowerShellInvocationResult> InvokeAsync(
        PowerShellScriptRecipe recipe,
        PowerShellCommandPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        policy?.Validate(recipe);
        using PowerShell powerShell = Create();
        recipe.Apply(powerShell);
        return await InvokeAsync(powerShell, recipe.ResultSchema, recipe.Timeout, cancellationToken).ConfigureAwait(false);
    }

    public PowerShellSession CreateSession(PowerShellSessionOptions options)
    {
        return PowerShellSession.Create(options);
    }

    public PowerShellSessionPool CreateSessionPool(PowerShellSessionPoolOptions options)
    {
        return PowerShellSessionPool.Create(options);
    }

    /// <summary>
    /// Parses copied parameter metadata without executing the supplied script or
    /// exposing SMA parser/AST types.
    /// </summary>
    public PowerShellScriptParseResult ParseScriptParameters(string script)
    {
        return PowerShellScriptParser.Parse(this, script);
    }

    public PowerShellCapabilitySet RegisterCapabilities(IEnumerable<PowerShellCapabilityBinding> bindings)
    {
        if ((FeatureFlags & (1UL << 16)) == 0)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.UnsupportedCapability,
                "The selected PowerShell payload does not support bounded capability RPC.");
        }

        return PowerShellCapabilitySet.Register(bindings);
    }

    internal static PowerShellInvocationResult InvokeRecipe(
        PowerShell powerShell,
        PowerShellResultSchema? resultSchema,
        TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(powerShell);
        if (timeout is null)
        {
            PowerShellInvocationResult result = powerShell.InvokeWithDiagnostics();
            resultSchema?.Validate(result);
            return result;
        }

        return InvokeAsync(powerShell, resultSchema, timeout, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task<PowerShellInvocationResult> InvokeAsync(
        PowerShell powerShell,
        PowerShellResultSchema? resultSchema,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = timeout is { } timeoutValue
            ? new CancellationTokenSource(timeoutValue)
            : null;
        using var linkedCancellation = timeoutCancellation is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        PowerShellInvocationResult result = await powerShell.InvokeAsync(
            linkedCancellation?.Token ?? cancellationToken).ConfigureAwait(false);
        resultSchema?.Validate(result);
        return result;
    }
}
