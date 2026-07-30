using Devolutions.PowerShell.Ffi;
using Devolutions.MultiPwsh.DtoContract.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

PowerShellValue valid = SampleDtoPowerShellDtoProjection.Write(new SampleDto
{
    Name = "alpha",
    Enabled = true,
    Identifiers = [1, 2, 3],
});

if (!SampleDtoPowerShellDtoProjection.TryRead(valid, out SampleDto? roundTrip, out PowerShellDtoProjectionError? error) ||
    error is not null ||
    roundTrip is not { Name: "alpha", Enabled: true, Identifiers: not null } ||
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

PowerShellValue keywordProperty = KeywordAndStringsDtoPowerShellDtoProjection.Write(new KeywordAndStringsDto
{
    @event = ["one", "two"],
});
if (!KeywordAndStringsDtoPowerShellDtoProjection.TryRead(keywordProperty, out KeywordAndStringsDto? keywordRoundTrip, out error) ||
    error is not null ||
    keywordRoundTrip is null ||
    !keywordRoundTrip.@event.SequenceEqual(["one", "two"]))
{
    throw new InvalidOperationException("Generated PowerShell DTO projection did not support a keyword property identifier.");
}

ExpectValueTooLarge(
    () => KeywordAndStringsDtoPowerShellDtoProjection.Write(new KeywordAndStringsDto { @event = ["exceeds"] }),
    "Generated PowerShell DTO projection did not enforce nested string bounds when writing an array.");

PowerShellValue firstShared = First.Contracts.SharedDtoPowerShellDtoProjection.Write(new First.Contracts.SharedDto { Name = "first" });
PowerShellValue secondShared = Second.Contracts.SharedDtoPowerShellDtoProjection.Write(new Second.Contracts.SharedDto { Name = "second" });
if (!firstShared.TryGetProperty("Name", out PowerShellValue? firstName) ||
    !firstName!.TryGetString(out string? firstText) ||
    firstText != "first" ||
    !secondShared.TryGetProperty("Name", out PowerShellValue? secondName) ||
    !secondName!.TryGetString(out string? secondText) ||
    secondText != "second")
{
    throw new InvalidOperationException("Generated PowerShell DTO projections with duplicate simple type names conflicted.");
}

if (SixtyThreeMemberDtoPowerShellDtoProjection.Write(new SixtyThreeMemberDto()).GetPropertyBag().Count != 64)
{
    throw new InvalidOperationException("Generated PowerShell DTO projection did not reserve one property bag entry for $version.");
}

VerifyGeneratorDiagnostic(
    """
    using Devolutions.PowerShell.Ffi;
    [PowerShellDtoContract(1)]
    public abstract class AbstractDto
    {
        [PowerShellDtoMember] public bool Enabled { get; set; }
    }
    """,
    "MPWDTO001",
    "an abstract DTO");
VerifyGeneratorDiagnostic(
    """
    using Devolutions.PowerShell.Ffi;
    [PowerShellDtoContract(1)]
    public class RequiredDto
    {
        [PowerShellDtoMember] public required string Name { get; set; }
    }
    """,
    "MPWDTO002",
    "a required DTO property");
VerifyGeneratorDiagnostic(
    """
    using Devolutions.PowerShell.Ffi;
    [PowerShellDtoContract(1)]
    public class IndexedDto
    {
        [PowerShellDtoMember] public string this[int index] { get => string.Empty; set { } }
    }
    """,
    "MPWDTO002",
    "a DTO indexer");
VerifyGeneratorDiagnostic(
    """
    using Devolutions.PowerShell.Ffi;
    [PowerShellDtoContract(1)]
    public class InitOnlyDto
    {
        [PowerShellDtoMember] public string Name { get; init; } = string.Empty;
    }
    """,
    "MPWDTO002",
    "an init-only DTO property");
VerifyGeneratorDiagnostic(
    """
    using Devolutions.PowerShell.Ffi;
    [PowerShellDtoContract(1)]
    public class ReservedNameDto
    {
        [PowerShellDtoMember("$VERSION")] public bool Value { get; set; }
    }
    """,
    "MPWDTO002",
    "a case-insensitive $version member name");
VerifyGeneratorDiagnostic(
    """
    using Devolutions.PowerShell.Ffi;
    [PowerShellDtoContract(1)]
    public struct ValueDto
    {
        [PowerShellDtoMember] public bool Value { get; set; }
    }
    """,
    "MPWDTO001",
    "a struct DTO");
VerifyGeneratorDiagnostic(
    "using Devolutions.PowerShell.Ffi; [PowerShellDtoContract(1)] public class TooManyMembersDto { " +
    string.Concat(Enumerable.Range(1, 64).Select(static index =>
        $"[PowerShellDtoMember] public bool Value{index} {{ get; set; }} ")) +
    "}",
    "MPWDTO001",
    "a DTO with more than 63 members");

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

static void ExpectValueTooLarge(Action action, string description)
{
    try
    {
        action();
    }
    catch (PowerShellDtoProjectionException exception) when (exception.Error.Failure == PowerShellDtoProjectionFailure.ValueTooLarge)
    {
        return;
    }

    throw new InvalidOperationException(description);
}

static void VerifyGeneratorDiagnostic(string source, string expectedDiagnostic, string description)
{
    CSharpCompilation compilation = CSharpCompilation.Create(
        "DtoContractGeneratorRegression",
        [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(typeof(PowerShellDtoContractAttribute).Assembly.Location),
        ],
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    GeneratorDriver driver = CSharpGeneratorDriver.Create(new DtoContractGenerator());
    driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out var generatorDiagnostics);
    Diagnostic[] diagnostics = generatorDiagnostics.Concat(output.GetDiagnostics()).ToArray();
    if (!diagnostics.Any(diagnostic => diagnostic.Id == expectedDiagnostic))
    {
        throw new InvalidOperationException($"The DTO generator did not reject {description} with {expectedDiagnostic}: {string.Join("; ", diagnostics.Select(static diagnostic => $"{diagnostic.Id}: {diagnostic.GetMessage()}"))}");
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

[PowerShellDtoContract(1)]
public sealed class KeywordAndStringsDto
{
    [PowerShellDtoMember(MaximumStringLength = 4, MaximumCollectionCount = 4)]
    public string[] @event { get; set; } = [];
}

[PowerShellDtoContract(1)]
public sealed class SixtyThreeMemberDto
{
    [PowerShellDtoMember] public bool Value01 { get; set; }
    [PowerShellDtoMember] public bool Value02 { get; set; }
    [PowerShellDtoMember] public bool Value03 { get; set; }
    [PowerShellDtoMember] public bool Value04 { get; set; }
    [PowerShellDtoMember] public bool Value05 { get; set; }
    [PowerShellDtoMember] public bool Value06 { get; set; }
    [PowerShellDtoMember] public bool Value07 { get; set; }
    [PowerShellDtoMember] public bool Value08 { get; set; }
    [PowerShellDtoMember] public bool Value09 { get; set; }
    [PowerShellDtoMember] public bool Value10 { get; set; }
    [PowerShellDtoMember] public bool Value11 { get; set; }
    [PowerShellDtoMember] public bool Value12 { get; set; }
    [PowerShellDtoMember] public bool Value13 { get; set; }
    [PowerShellDtoMember] public bool Value14 { get; set; }
    [PowerShellDtoMember] public bool Value15 { get; set; }
    [PowerShellDtoMember] public bool Value16 { get; set; }
    [PowerShellDtoMember] public bool Value17 { get; set; }
    [PowerShellDtoMember] public bool Value18 { get; set; }
    [PowerShellDtoMember] public bool Value19 { get; set; }
    [PowerShellDtoMember] public bool Value20 { get; set; }
    [PowerShellDtoMember] public bool Value21 { get; set; }
    [PowerShellDtoMember] public bool Value22 { get; set; }
    [PowerShellDtoMember] public bool Value23 { get; set; }
    [PowerShellDtoMember] public bool Value24 { get; set; }
    [PowerShellDtoMember] public bool Value25 { get; set; }
    [PowerShellDtoMember] public bool Value26 { get; set; }
    [PowerShellDtoMember] public bool Value27 { get; set; }
    [PowerShellDtoMember] public bool Value28 { get; set; }
    [PowerShellDtoMember] public bool Value29 { get; set; }
    [PowerShellDtoMember] public bool Value30 { get; set; }
    [PowerShellDtoMember] public bool Value31 { get; set; }
    [PowerShellDtoMember] public bool Value32 { get; set; }
    [PowerShellDtoMember] public bool Value33 { get; set; }
    [PowerShellDtoMember] public bool Value34 { get; set; }
    [PowerShellDtoMember] public bool Value35 { get; set; }
    [PowerShellDtoMember] public bool Value36 { get; set; }
    [PowerShellDtoMember] public bool Value37 { get; set; }
    [PowerShellDtoMember] public bool Value38 { get; set; }
    [PowerShellDtoMember] public bool Value39 { get; set; }
    [PowerShellDtoMember] public bool Value40 { get; set; }
    [PowerShellDtoMember] public bool Value41 { get; set; }
    [PowerShellDtoMember] public bool Value42 { get; set; }
    [PowerShellDtoMember] public bool Value43 { get; set; }
    [PowerShellDtoMember] public bool Value44 { get; set; }
    [PowerShellDtoMember] public bool Value45 { get; set; }
    [PowerShellDtoMember] public bool Value46 { get; set; }
    [PowerShellDtoMember] public bool Value47 { get; set; }
    [PowerShellDtoMember] public bool Value48 { get; set; }
    [PowerShellDtoMember] public bool Value49 { get; set; }
    [PowerShellDtoMember] public bool Value50 { get; set; }
    [PowerShellDtoMember] public bool Value51 { get; set; }
    [PowerShellDtoMember] public bool Value52 { get; set; }
    [PowerShellDtoMember] public bool Value53 { get; set; }
    [PowerShellDtoMember] public bool Value54 { get; set; }
    [PowerShellDtoMember] public bool Value55 { get; set; }
    [PowerShellDtoMember] public bool Value56 { get; set; }
    [PowerShellDtoMember] public bool Value57 { get; set; }
    [PowerShellDtoMember] public bool Value58 { get; set; }
    [PowerShellDtoMember] public bool Value59 { get; set; }
    [PowerShellDtoMember] public bool Value60 { get; set; }
    [PowerShellDtoMember] public bool Value61 { get; set; }
    [PowerShellDtoMember] public bool Value62 { get; set; }
    [PowerShellDtoMember] public bool Value63 { get; set; }
}

namespace First.Contracts
{
    [PowerShellDtoContract(1)]
    public sealed class SharedDto
    {
        [PowerShellDtoMember]
        public string Name { get; set; } = string.Empty;
    }
}

namespace Second.Contracts
{
    [PowerShellDtoContract(1)]
    public sealed class SharedDto
    {
        [PowerShellDtoMember]
        public string Name { get; set; } = string.Empty;
    }
}
