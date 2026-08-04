namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// A pull-only transcript over one observed invocation.
/// </summary>
/// <remarks>
/// <para>
/// Result and presentation pages are retained until their matching commit method
/// succeeds. This permits a consumer to persist or render copied records before
/// advancing either acknowledgement cursor.
/// </para>
/// <para>
/// The transcript owns the invocation's result and diagnostic cursors through
/// <see cref="ReadResults"/> and <see cref="ReadPresentation"/>. Do not call
/// <see cref="PowerShellObservedInvocation.ReadResults(ulong, int?)"/>,
/// <see cref="PowerShellObservedInvocation.ReadDiagnostics(ulong, int?)"/> or
/// <see cref="PowerShellObservedInvocation.ReadPresentation(ulong, int?)"/>
/// directly while this transcript is in use.
/// </para>
/// </remarks>
public sealed class PowerShellObservedTranscript
{
    private readonly object gate = new();
    private readonly PowerShellObservedInvocation invocation;
    private ulong resultAcknowledgedThrough;
    private ulong presentationAcknowledgedThrough;
    private PowerShellValuePage? pendingResultPage;
    private PowerShellObservedPresentationPage? pendingPresentationPage;

    internal PowerShellObservedTranscript(PowerShellObservedInvocation invocation)
    {
        this.invocation = invocation;
    }

    /// <summary>
    /// Gets the highest result sequence explicitly committed through this transcript.
    /// </summary>
    public ulong ResultAcknowledgedThrough
    {
        get
        {
            lock (gate)
            {
                return resultAcknowledgedThrough;
            }
        }
    }

    /// <summary>
    /// Gets the highest presentation sequence explicitly committed through this transcript.
    /// </summary>
    public ulong PresentationAcknowledgedThrough
    {
        get
        {
            lock (gate)
            {
                return presentationAcknowledgedThrough;
            }
        }
    }

    /// <summary>
    /// Pulls the next bounded result page without advancing its acknowledgement cursor.
    /// </summary>
    /// <remarks>
    /// Repeated calls return the same immutable page until
    /// <see cref="CommitResults(PowerShellValuePage)"/> succeeds.
    /// </remarks>
    public PowerShellValuePage ReadResults()
    {
        lock (gate)
        {
            pendingResultPage ??= invocation.ReadResults(resultAcknowledgedThrough);
            return pendingResultPage;
        }
    }

    /// <summary>
    /// Pulls the next bounded presentation page without advancing its acknowledgement cursor.
    /// </summary>
    /// <remarks>
    /// Progress records include fixed copied progress fields; all other diagnostic
    /// streams remain bounded display text. Repeated calls return the same immutable
    /// page until <see cref="CommitPresentation(PowerShellObservedPresentationPage)"/>
    /// succeeds.
    /// </remarks>
    public PowerShellObservedPresentationPage ReadPresentation()
    {
        lock (gate)
        {
            pendingPresentationPage ??= invocation.ReadPresentation(presentationAcknowledgedThrough);
            return pendingPresentationPage;
        }
    }

    /// <summary>
    /// Commits a result page previously returned by <see cref="ReadResults"/>.
    /// </summary>
    public void CommitResults(PowerShellValuePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        lock (gate)
        {
            if (!ReferenceEquals(page, pendingResultPage))
            {
                throw new InvalidOperationException(
                    "The result page was not returned by this transcript or was already committed.");
            }

            resultAcknowledgedThrough = page.NextSequence;
            pendingResultPage = null;
        }
    }

    /// <summary>
    /// Commits a presentation page previously returned by <see cref="ReadPresentation"/>.
    /// </summary>
    public void CommitPresentation(PowerShellObservedPresentationPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        lock (gate)
        {
            if (!ReferenceEquals(page, pendingPresentationPage))
            {
                throw new InvalidOperationException(
                    "The presentation page was not returned by this transcript or was already committed.");
            }

            presentationAcknowledgedThrough = page.NextSequence;
            pendingPresentationPage = null;
        }
    }
}
