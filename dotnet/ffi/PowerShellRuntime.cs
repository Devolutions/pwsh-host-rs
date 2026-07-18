namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellRuntime
{
    private PowerShellRuntime(uint abiVersion, ulong featureFlags, PowerShellPayloadActivationOptions activation)
    {
        AbiVersion = abiVersion;
        FeatureFlags = featureFlags;
        PayloadDirectory = activation.PayloadDirectory;
        ManifestPath = activation.ManifestPath;
        TrustPolicy = activation.TrustPolicy;
    }

    public uint AbiVersion { get; }

    public ulong FeatureFlags { get; }

    public string PayloadDirectory { get; }

    public string ManifestPath { get; }

    public PowerShellPayloadTrustPolicy TrustPolicy { get; }

    public static PowerShellRuntime Activate(PowerShellPayloadActivationOptions activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        PowerShell.Initialize(activation);
        return new PowerShellRuntime(PowerShell.AbiVersion, PowerShell.FeatureFlags, activation);
    }

    [Obsolete("Use Activate(PowerShellPayloadActivationOptions) with a hash-pinned manifest. This overload is unsafe local development compatibility only.")]
    public static PowerShellRuntime Activate(string payloadDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        return Activate(PowerShellPayloadActivationOptions.UnsafeUntrustedLocalDevelopment(
            payloadDirectory,
            Path.Combine(payloadDirectory, "devolutions-pwsh-payload.json")));
    }

    public PowerShell Create()
    {
        return PowerShell.Create();
    }

    public PowerShellSession CreateSession(PowerShellSessionOptions options)
    {
        return PowerShellSession.Create(options);
    }

    public PowerShellSessionPool CreateSessionPool(PowerShellSessionPoolOptions options)
    {
        return PowerShellSessionPool.Create(options);
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
}
