using Devolutions.PowerShell.Ffi;

PowerShellValue valid = SampleDtoPowerShellDtoProjection.Write(new SampleDto
{
    Name = "alpha",
    Enabled = true,
    Identifiers = [1, 2, 3],
});

if (!SampleDtoPowerShellDtoProjection.TryRead(valid, out SampleDto? roundTrip, out PowerShellDtoProjectionError? error) ||
    error is not null ||
    roundTrip is not { Name: "alpha", Enabled: true } ||
    !roundTrip.Identifiers.SequenceEqual([1L, 2L, 3L]))
{
    throw new InvalidOperationException("Generated PowerShell DTO projection did not round-trip a bounded contract.");
}

PowerShellValue unknown = PowerShellValue.PropertyBag(
[
    new("$version", PowerShellValue.UnsignedInteger(2)),
    new("Name", PowerShellValue.String("alpha")),
    new("Enabled", PowerShellValue.Boolean(true)),
    new("Identifiers", PowerShellValue.Array([PowerShellValue.SignedInteger(1)])),
    new("Unexpected", PowerShellValue.String("rejected")),
]);
if (SampleDtoPowerShellDtoProjection.TryRead(unknown, out _, out error) ||
    error?.Failure != PowerShellDtoProjectionFailure.UnknownMember)
{
    throw new InvalidOperationException("Generated PowerShell DTO projection accepted an unknown member.");
}

PowerShellValue wrongVersion = PowerShellValue.PropertyBag(
[
    new("$version", PowerShellValue.UnsignedInteger(1)),
    new("Name", PowerShellValue.String("alpha")),
    new("Enabled", PowerShellValue.Boolean(true)),
    new("Identifiers", PowerShellValue.Array([PowerShellValue.SignedInteger(1)])),
]);
if (SampleDtoPowerShellDtoProjection.TryRead(wrongVersion, out _, out error) ||
    error?.Failure != PowerShellDtoProjectionFailure.InvalidVersion)
{
    throw new InvalidOperationException("Generated PowerShell DTO projection accepted an incompatible version.");
}

using (var pager = new PowerShellValuePager(new PowerShellValuePagerOptions(maximumBufferedRecords: 2, maximumPageRecords: 1)))
{
    pager.Write(PowerShellValue.String("one"));
    pager.Write(PowerShellValue.String("two"));
    PowerShellValuePage first = pager.Read(0);
    if (first.Records.Count != 1 || first.NextSequence != 1)
    {
        throw new InvalidOperationException("Bounded pager did not return the first ordered page.");
    }

    pager.Acknowledge(first.NextSequence);
    PowerShellValuePage second = pager.Read(first.NextSequence);
    pager.Acknowledge(second.NextSequence);
    pager.Complete();
    if (!pager.GetCompletion().IsComplete)
    {
        throw new InvalidOperationException("Bounded pager reported a fully acknowledged terminal result as incomplete.");
    }
}

[PowerShellDtoContract(2)]
public sealed class SampleDto
{
    [PowerShellDtoMember(MaximumStringLength = 16)]
    public string Name { get; set; } = string.Empty;

    [PowerShellDtoMember]
    public bool Enabled { get; set; }

    [PowerShellDtoMember(MaximumCollectionCount = 8)]
    public long[] Identifiers { get; set; } = [];
}
