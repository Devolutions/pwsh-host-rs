namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Indicates why a completed copied result could not be projected by an explicit
/// source-generated DTO mapper.
/// </summary>
public enum PowerShellCompleteResultProjectionFailure
{
    ZeroResults,
    MultipleResults,
    IncompleteOrTruncated,
    MapperFailure,
}

/// <summary>
/// Describes a failed complete-result DTO projection.
/// </summary>
public sealed class PowerShellCompleteResultProjectionException : InvalidOperationException
{
    internal PowerShellCompleteResultProjectionException(
        PowerShellCompleteResultProjectionFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public PowerShellCompleteResultProjectionFailure Failure { get; }
}

/// <summary>
/// Projects one complete copied result through an application-selected generated DTO mapper.
/// </summary>
/// <remarks>
/// Pass the generated <c>Read</c> method explicitly, for example
/// <c>MyDtoPowerShellDtoProjection.Read</c>. This helper does not invoke PowerShell,
/// discover contracts, or transfer arbitrary CLR or PowerShell objects.
/// </remarks>
public static class PowerShellCompleteResultProjection
{
    /// <summary>
    /// Projects exactly one complete output record from a completed invocation.
    /// </summary>
    public static TDto Read<TDto>(
        PowerShellInvocationResult result,
        Func<PowerShellValue, TDto> generatedMapper)
        where TDto : class
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(generatedMapper);

        if (result.State != PowerShellInvocationState.Completed ||
            result.IsTerminatingFailure ||
            result.IsSequenceTruncated ||
            result.Output.IsTruncated ||
            result.Output.DroppedRecordCount != 0 ||
            result.Output.TotalRecordCount != (ulong)result.Output.Records.Count ||
            result.Output.Records.Any(static record =>
                record.IsTruncated ||
                record.IsPropertyBagTruncated ||
                record.DroppedPropertyEntryCount != 0))
        {
            throw Incomplete();
        }

        return ProjectExactlyOne(result.Output.Records.Select(static record => record.PropertyBag), generatedMapper);
    }

    /// <summary>
    /// Projects exactly one complete copied value from an ordered typed or observed result-page sequence.
    /// </summary>
    /// <remarks>
    /// The sequence must contain every page from acknowledgement cursor zero through a
    /// successful complete terminal page. A page sequence that omits, truncates, drops,
    /// or replays records is rejected rather than projected.
    /// </remarks>
    public static TDto Read<TDto>(
        IReadOnlyList<PowerShellValuePage> pages,
        Func<PowerShellValue, TDto> generatedMapper)
        where TDto : class
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(generatedMapper);

        if (pages.Count == 0)
        {
            throw Incomplete();
        }

        var values = new List<PowerShellValue>();
        ulong expectedAcknowledgement = 0;
        ulong expectedSequence = 1;
        foreach (PowerShellValuePage page in pages)
        {
            if (page is null ||
                page.AcknowledgedSequence != expectedAcknowledgement ||
                page.NextSequence < page.AcknowledgedSequence ||
                page.NextSequence > page.TotalRecordCount ||
                page.DroppedRecordCount != 0 ||
                page.IsTruncated ||
                page.TerminalStatus != PowerShellFfiStatus.Success)
            {
                throw Incomplete();
            }

            foreach (PowerShellValuePageRecord record in page.Records)
            {
                if (record is null || record.Sequence != expectedSequence)
                {
                    throw Incomplete();
                }

                values.Add(record.Value);
                expectedSequence = checked(expectedSequence + 1);
            }

            ulong actualNext = page.Records.Count == 0
                ? page.AcknowledgedSequence
                : page.Records[^1].Sequence;
            if (page.NextSequence != actualNext)
            {
                throw Incomplete();
            }

            expectedAcknowledgement = page.NextSequence;
        }

        PowerShellValuePage finalPage = pages[^1];
        if (!finalPage.IsComplete ||
            !finalPage.IsTerminal ||
            finalPage.TotalRecordCount != finalPage.AcknowledgedSequence ||
            finalPage.TotalRecordCount != (ulong)values.Count)
        {
            throw Incomplete();
        }

        return ProjectExactlyOne(values, generatedMapper);
    }

    private static TDto ProjectExactlyOne<TDto>(
        IEnumerable<PowerShellValue?> values,
        Func<PowerShellValue, TDto> generatedMapper)
        where TDto : class
    {
        using IEnumerator<PowerShellValue?> enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            throw new PowerShellCompleteResultProjectionException(
                PowerShellCompleteResultProjectionFailure.ZeroResults,
                "The completed PowerShell result contains no projectable records.");
        }

        PowerShellValue? value = enumerator.Current;
        if (enumerator.MoveNext())
        {
            throw new PowerShellCompleteResultProjectionException(
                PowerShellCompleteResultProjectionFailure.MultipleResults,
                "The completed PowerShell result contains more than one record.");
        }

        if (value is null)
        {
            throw new PowerShellCompleteResultProjectionException(
                PowerShellCompleteResultProjectionFailure.MapperFailure,
                "The completed PowerShell record has no copied property bag for the generated DTO mapper.");
        }

        try
        {
            return generatedMapper(value) ?? throw new InvalidOperationException(
                "The generated DTO mapper returned null.");
        }
        catch (Exception exception) when (exception is not PowerShellCompleteResultProjectionException)
        {
            throw new PowerShellCompleteResultProjectionException(
                PowerShellCompleteResultProjectionFailure.MapperFailure,
                "The generated DTO mapper rejected the completed copied result.",
                exception);
        }
    }

    private static PowerShellCompleteResultProjectionException Incomplete()
    {
        return new PowerShellCompleteResultProjectionException(
            PowerShellCompleteResultProjectionFailure.IncompleteOrTruncated,
            "The PowerShell result sequence is incomplete or truncated and cannot be projected.");
    }
}
