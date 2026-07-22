namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Declares a trusted net8 payload adapter assembly for registered live-object
/// contracts. The adapter is loaded only during runtime activation.
/// </summary>
public sealed class PowerShellLiveObjectContractPack
{
    public PowerShellLiveObjectContractPack(
        string payloadAdapterAssemblyPath,
        string payloadAdapterTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadAdapterAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadAdapterTypeName);
        if (!Path.IsPathFullyQualified(payloadAdapterAssemblyPath) ||
            payloadAdapterTypeName.Length > 512)
        {
            throw new ArgumentException("The live object contract pack metadata is invalid.");
        }

        PayloadAdapterAssemblyPath = Path.GetFullPath(payloadAdapterAssemblyPath);
        PayloadAdapterTypeName = payloadAdapterTypeName;
    }

    public string PayloadAdapterAssemblyPath { get; }

    public string PayloadAdapterTypeName { get; }
}
