namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Bounded configuration for a <see cref="PowerShellBrokerChannel"/>.
/// Every value is validated by the native host before a channel opens.
/// </summary>
public sealed class PowerShellBrokerChannelOptions
{
    /// <summary>The largest number of simultaneously outstanding frames the ABI accepts.</summary>
    public const int MaximumSupportedInflightFrames = 32;

    /// <summary>The largest frame body the ABI accepts.</summary>
    public const int MaximumSupportedBodyBytes = 64 * 1024;

    /// <summary>The longest deadline the ABI accepts.</summary>
    public static readonly TimeSpan MaximumSupportedDeadline = TimeSpan.FromSeconds(30);

    public PowerShellBrokerChannelOptions(
        int maximumInflightFrames = MaximumSupportedInflightFrames,
        int maximumBodyBytes = MaximumSupportedBodyBytes,
        TimeSpan? defaultDeadline = null)
    {
        if (maximumInflightFrames < 1 || maximumInflightFrames > MaximumSupportedInflightFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInflightFrames),
                maximumInflightFrames,
                $"The broker channel supports 1 to {MaximumSupportedInflightFrames} inflight frames.");
        }

        if (maximumBodyBytes < 1 || maximumBodyBytes > MaximumSupportedBodyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBodyBytes),
                maximumBodyBytes,
                $"The broker channel supports 1 to {MaximumSupportedBodyBytes} body bytes.");
        }

        TimeSpan deadline = defaultDeadline ?? MaximumSupportedDeadline;
        if (deadline <= TimeSpan.Zero || deadline > MaximumSupportedDeadline)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultDeadline),
                deadline,
                $"The broker channel deadline must be greater than zero and at most {MaximumSupportedDeadline}.");
        }

        MaximumInflightFrames = maximumInflightFrames;
        MaximumBodyBytes = maximumBodyBytes;
        DefaultDeadline = deadline;
    }

    public int MaximumInflightFrames { get; }

    public int MaximumBodyBytes { get; }

    public TimeSpan DefaultDeadline { get; }
}
