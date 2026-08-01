namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Bounds for the independent result and diagnostic acknowledgement channels of one invocation.
/// </summary>
public sealed class PowerShellObservedInvocationOptions
{
    public PowerShellObservedInvocationOptions(
        int maximumBufferedResultRecords = 32,
        int maximumResultPageRecords = 32,
        int maximumBufferedDiagnosticRecords = 32,
        int maximumDiagnosticPageRecords = 32)
    {
        Validate(maximumBufferedResultRecords, maximumResultPageRecords, nameof(maximumBufferedResultRecords), nameof(maximumResultPageRecords));
        Validate(
            maximumBufferedDiagnosticRecords,
            maximumDiagnosticPageRecords,
            nameof(maximumBufferedDiagnosticRecords),
            nameof(maximumDiagnosticPageRecords));

        MaximumBufferedResultRecords = maximumBufferedResultRecords;
        MaximumResultPageRecords = maximumResultPageRecords;
        MaximumBufferedDiagnosticRecords = maximumBufferedDiagnosticRecords;
        MaximumDiagnosticPageRecords = maximumDiagnosticPageRecords;
    }

    public int MaximumBufferedResultRecords { get; }

    public int MaximumResultPageRecords { get; }

    public int MaximumBufferedDiagnosticRecords { get; }

    public int MaximumDiagnosticPageRecords { get; }

    private static void Validate(int maximumBufferedRecords, int maximumPageRecords, string bufferName, string pageName)
    {
        if (maximumBufferedRecords < 1 || maximumBufferedRecords > PowerShellValue.MaximumContainerEntries)
        {
            throw new ArgumentOutOfRangeException(bufferName);
        }

        if (maximumPageRecords < 1 || maximumPageRecords > maximumBufferedRecords)
        {
            throw new ArgumentOutOfRangeException(pageName);
        }
    }
}
