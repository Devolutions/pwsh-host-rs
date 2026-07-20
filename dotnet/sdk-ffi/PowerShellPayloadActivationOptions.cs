namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellPayloadActivationOptions
{
    public PowerShellPayloadActivationOptions(
        string payloadDirectory,
        string manifestPath,
        string manifestSha256)
        : this(
            payloadDirectory,
            manifestPath,
            manifestSha256,
            PowerShellPayloadTrustPolicy.HashPinnedManifest)
    {
    }

    private PowerShellPayloadActivationOptions(
        string payloadDirectory,
        string manifestPath,
        string manifestSha256,
        PowerShellPayloadTrustPolicy trustPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(manifestSha256);

        PayloadDirectory = payloadDirectory;
        ManifestPath = manifestPath;
        ManifestSha256 = manifestSha256;
        TrustPolicy = trustPolicy;
    }

    public string PayloadDirectory { get; }

    public string ManifestPath { get; }

    public string ManifestSha256 { get; }

    public PowerShellPayloadTrustPolicy TrustPolicy { get; }

    [Obsolete("Use PowerShell.Initialize or PowerShellRuntime.Activate for direct payload activation without a manifest.")]
    public static PowerShellPayloadActivationOptions UnsafeUntrustedLocalDevelopment(
        string payloadDirectory,
        string manifestPath)
    {
        return new PowerShellPayloadActivationOptions(
            payloadDirectory,
            manifestPath,
            string.Empty,
            PowerShellPayloadTrustPolicy.Direct);
    }
}
