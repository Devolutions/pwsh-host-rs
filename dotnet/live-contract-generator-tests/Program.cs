using Devolutions.MultiPwsh.LiveContract.Generator;
using Devolutions.PowerShell.Ffi.LiveObjects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

const string ValidContract = """
    using System.Collections.Generic;
    using Devolutions.PowerShell.Ffi.LiveObjects;

    namespace Fixture;

    [LiveContract("A1E95B5C-9E8E-4A3D-B6F2-93F81C51B19E", 1, 0, "5BFBF6D7-7BFA-4C15-8D35-02B665F39A18")]
    [LiveObject(1)]
    public interface ISessionCreatorLiveContract
    {
        [LiveMember(1, ResultObjectId = 3, MaximumUtf8Bytes = 128)]
        ISessionCreatorLiveChild Add(string name);

        [LiveMember(2, ResultObjectId = 2, MaximumCollectionCount = 32)]
        IReadOnlyList<ISessionCreatorLiveChild> Children { get; }
    }

    [LiveObject(2)]
    public interface ISessionCreatorLiveChildren
    {
        [LiveMember(3, MaximumCollectionCount = 32)]
        int Count { get; }

        [LiveMember(4, ResultObjectId = 3)]
        ISessionCreatorLiveChild GetAt(int index);
    }

    [LiveObject(3)]
    public interface ISessionCreatorLiveChild
    {
        [LiveMember(10, SetterId = 11, MaximumUtf8Bytes = 128)]
        string Name { get; set; }
    }
    """;

VerifyGeneratedSurface("Host", "SessionCreatorLiveContractHostAdapter");
VerifyGeneratedSurface("Payload", "SessionCreatorLiveContractProxy");
VerifyGeneratedSurface("Payload", "SessionCreatorLiveContractProxy", ValidContract.Replace("namespace Fixture;", string.Empty, StringComparison.Ordinal));
VerifyDiagnostic(ValidContract.Replace(", MaximumUtf8Bytes = 128", string.Empty, StringComparison.Ordinal), "MPWLC006");
VerifyDiagnostic(ValidContract.Replace("public interface ISessionCreatorLiveContract", "public interface ISessionCreatorLiveContract : System.IDisposable", StringComparison.Ordinal), "MPWLC009");
VerifyNoDiagnostics("""
    namespace Fixture { [Unrelated.LiveContract] public interface UnrelatedContract { } }
    namespace Unrelated { public sealed class LiveContractAttribute : System.Attribute { } }
    """);

// The v2 compiler is a second generator in the same analyzer assembly. Running
// both here also proves an ordinary v1 compilation gets no v2 diagnostics.
BridgeContractTests.Run(RunBridgeGenerators, AssertNoErrors);
BridgeWireTests.Run();

Console.WriteLine("live-contract and bridge-contract generator fixtures passed");

static (GeneratorDriverRunResult Result, Compilation Output) RunBridgeGenerators(string source, string mode)
{
    CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
    SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
    CSharpCompilation compilation = CSharpCompilation.Create(
        "BridgeContractGeneratorFixture",
        [syntaxTree],
        GetFrameworkReferences(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    GeneratorDriver driver = CSharpGeneratorDriver.Create(
        [new LiveContractGenerator().AsSourceGenerator(), new BridgeContractGenerator().AsSourceGenerator()],
        parseOptions: parseOptions,
        optionsProvider: new LiveContractModeOptions(mode));
    driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);
    return (driver.GetRunResult(), output);
}

static void VerifyGeneratedSurface(string mode, string requiredText, string? source = null)
{
    GeneratorDriverRunResult result = RunGenerator(source ?? ValidContract, mode, out Compilation output);
    AssertNoErrors(GetGeneratorDiagnostics(result));
    AssertNoErrors(output.GetDiagnostics());

    string generated = string.Join(
        Environment.NewLine,
        result.GeneratedTrees.Select(static tree => tree.GetText().ToString()));
    if (!generated.Contains(requiredText, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"The {mode} generator output does not contain {requiredText}.");
    }

    if (mode == "Host" &&
        (generated.Contains("System.Reflection", StringComparison.Ordinal) ||
         generated.Contains("dynamic", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("Generated host output must not use reflection or dynamic dispatch.");
    }
}

static void VerifyDiagnostic(string source, string expectedDiagnostic)
{
    GeneratorDriverRunResult result = RunGenerator(source, "Host", out _);
    if (!GetGeneratorDiagnostics(result).Any(diagnostic => diagnostic.Id == expectedDiagnostic))
    {
        throw new InvalidOperationException($"Expected {expectedDiagnostic} was not reported.");
    }
}

static void VerifyNoDiagnostics(string source)
{
    GeneratorDriverRunResult result = RunGenerator(source, "Host", out Compilation output);
    AssertNoErrors(GetGeneratorDiagnostics(result));
    AssertNoErrors(output.GetDiagnostics());
}

static GeneratorDriverRunResult RunGenerator(string source, string mode, out Compilation output)
{
    CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
    SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
    CSharpCompilation compilation = CSharpCompilation.Create(
        "LiveContractGeneratorFixture",
        [syntaxTree],
        GetFrameworkReferences(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    GeneratorDriver driver = CSharpGeneratorDriver.Create(
        [new LiveContractGenerator().AsSourceGenerator()],
        parseOptions: parseOptions,
        optionsProvider: new LiveContractModeOptions(mode));
    driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out _);
    return driver.GetRunResult();
}

static IEnumerable<MetadataReference> GetFrameworkReferences()
{
    string trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
    return trustedAssemblies
        .Split(Path.PathSeparator)
        .Append(typeof(LiveContractAttribute).Assembly.Location)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(static path => MetadataReference.CreateFromFile(path));
}

static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics)
{
    Diagnostic[] errors = diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length != 0)
    {
        throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
    }
}

static IEnumerable<Diagnostic> GetGeneratorDiagnostics(GeneratorDriverRunResult result) =>
    result.Results.SelectMany(static generator => generator.Diagnostics);

sealed class LiveContractModeOptions(string mode) : AnalyzerConfigOptionsProvider
{
    private readonly AnalyzerConfigOptions global = new LiveContractOptions(mode);

    public override AnalyzerConfigOptions GlobalOptions => global;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => EmptyOptions.Instance;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => EmptyOptions.Instance;

    private sealed class LiveContractOptions(string mode) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = key == "build_property.LiveContractMode" ? mode : string.Empty;
            return key == "build_property.LiveContractMode";
        }
    }

    private sealed class EmptyOptions : AnalyzerConfigOptions
    {
        internal static readonly EmptyOptions Instance = new();
        public override bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }
    }
}
