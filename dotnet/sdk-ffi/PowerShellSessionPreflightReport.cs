using System.Collections.ObjectModel;

namespace Devolutions.PowerShell.Ffi;

public enum PowerShellSessionPreflightStatus : uint
{
    Valid = 0,
    InvalidConfiguration = 1,
    InvalidModuleRoots = 2,
    UnresolvableModuleImports = 3,
    InvalidModuleManifest = 4,
    ExternalModuleDeclarations = 5,
    InvalidWorkingDirectory = 6,
}

public enum PowerShellSessionModuleRootStatus : uint
{
    Valid = 0,
    Missing = 1,
    Invalid = 2,
}

public enum PowerShellSessionModuleImportStatus : uint
{
    Resolved = 0,
    Unresolvable = 1,
    ManifestInvalid = 2,
    ManifestUnreadable = 3,
    ManifestDeclarationsUnavailable = 4,
    ManifestDeclaresExternalPath = 5,
}

public sealed class PowerShellSessionModuleRootDiagnostic
{
    internal PowerShellSessionModuleRootDiagnostic(
        string path,
        string canonicalPath,
        PowerShellSessionModuleRootStatus status,
        string diagnostic)
    {
        Path = path;
        CanonicalPath = canonicalPath;
        Status = status;
        Diagnostic = diagnostic;
    }

    public string Path { get; }

    public string CanonicalPath { get; }

    public PowerShellSessionModuleRootStatus Status { get; }

    public string Diagnostic { get; }
}

public sealed class PowerShellSessionModuleImportDiagnostic
{
    internal PowerShellSessionModuleImportDiagnostic(
        string moduleImport,
        string resolvedPath,
        string manifestPath,
        PowerShellSessionModuleImportStatus status,
        string declaredVersion,
        IReadOnlyList<string> declaredCommands,
        bool declaredCommandsTruncated,
        string diagnostic)
    {
        ModuleImport = moduleImport;
        ResolvedPath = resolvedPath;
        ManifestPath = manifestPath;
        Status = status;
        DeclaredVersion = declaredVersion;
        DeclaredCommands = declaredCommands;
        DeclaredCommandsTruncated = declaredCommandsTruncated;
        Diagnostic = diagnostic;
    }

    public string ModuleImport { get; }

    public string ResolvedPath { get; }

    public string ManifestPath { get; }

    public PowerShellSessionModuleImportStatus Status { get; }

    public string DeclaredVersion { get; }

    public IReadOnlyList<string> DeclaredCommands { get; }

    public bool DeclaredCommandsTruncated { get; }

    public string Diagnostic { get; }
}

public sealed class PowerShellSessionPreflightReport
{
    private const int MaximumEntries = 32;
    private const int MaximumTextLength = 128;
    private const int MaximumPathLength = 256;
    private const int MaximumDeclaredCommands = 4;
    private const int MaximumDeclaredCommandLength = 64;

    internal PowerShellSessionPreflightReport(
        PowerShellSessionPreflightStatus status,
        string diagnostic,
        IReadOnlyList<PowerShellSessionModuleRootDiagnostic> moduleRoots,
        IReadOnlyList<PowerShellSessionModuleImportDiagnostic> moduleImports)
    {
        Status = status;
        Diagnostic = diagnostic;
        ModuleRoots = moduleRoots;
        ModuleImports = moduleImports;
    }

    public PowerShellSessionPreflightStatus Status { get; }

    public string Diagnostic { get; }

    public IReadOnlyList<PowerShellSessionModuleRootDiagnostic> ModuleRoots { get; }

    public IReadOnlyList<PowerShellSessionModuleImportDiagnostic> ModuleImports { get; }

    internal static PowerShellSessionPreflightReport FromNative(PowerShellValue value)
    {
        IReadOnlyDictionary<string, PowerShellValue> report = GetPropertyBag(value, 4, "preflight report");
        var roots = new List<PowerShellSessionModuleRootDiagnostic>();
        foreach (PowerShellValue root in GetArray(report, "ModuleRoots", MaximumEntries))
        {
            IReadOnlyDictionary<string, PowerShellValue> values = GetPropertyBag(root, 4, "module root diagnostic");
            roots.Add(new PowerShellSessionModuleRootDiagnostic(
                GetString(values, "Path", MaximumPathLength),
                GetString(values, "CanonicalPath", MaximumPathLength),
                GetEnum<PowerShellSessionModuleRootStatus>(values, "Status"),
                GetString(values, "Diagnostic", MaximumTextLength)));
        }

        var imports = new List<PowerShellSessionModuleImportDiagnostic>();
        foreach (PowerShellValue moduleImport in GetArray(report, "ModuleImports", MaximumEntries))
        {
            IReadOnlyDictionary<string, PowerShellValue> values = GetPropertyBag(moduleImport, 8, "module import diagnostic");
            IReadOnlyList<string> commands = new ReadOnlyCollection<string>(
                GetArray(values, "DeclaredCommands", MaximumDeclaredCommands)
                    .Select(command => GetString(command, MaximumDeclaredCommandLength, "declared command"))
                    .ToArray());
            imports.Add(new PowerShellSessionModuleImportDiagnostic(
                GetString(values, "ModuleImport", MaximumTextLength),
                GetString(values, "ResolvedPath", MaximumPathLength),
                GetString(values, "ManifestPath", MaximumPathLength),
                GetEnum<PowerShellSessionModuleImportStatus>(values, "Status"),
                GetString(values, "DeclaredVersion", MaximumTextLength),
                commands,
                GetBoolean(values, "DeclaredCommandsTruncated"),
                GetString(values, "Diagnostic", MaximumTextLength)));
        }

        return new PowerShellSessionPreflightReport(
            GetEnum<PowerShellSessionPreflightStatus>(report, "Status"),
            GetString(report, "Diagnostic", MaximumTextLength),
            new ReadOnlyCollection<PowerShellSessionModuleRootDiagnostic>(roots),
            new ReadOnlyCollection<PowerShellSessionModuleImportDiagnostic>(imports));
    }

    private static IReadOnlyDictionary<string, PowerShellValue> GetPropertyBag(
        PowerShellValue value,
        int expectedCount,
        string description)
    {
        if (value.Kind != PowerShellValueKind.PropertyBag)
        {
            throw InvalidNativeReport($"{description} is not a property bag.");
        }

        IReadOnlyDictionary<string, PowerShellValue> properties = value.GetPropertyBag();
        if (properties.Count != expectedCount)
        {
            throw InvalidNativeReport($"{description} has an unsupported schema.");
        }

        return properties;
    }

    private static IReadOnlyList<PowerShellValue> GetArray(
        IReadOnlyDictionary<string, PowerShellValue> properties,
        string name,
        int maximumCount)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value))
        {
            throw InvalidNativeReport($"Preflight report is missing {name}.");
        }

        return GetArray(value, maximumCount, name);
    }

    private static IReadOnlyList<PowerShellValue> GetArray(PowerShellValue value, int maximumCount, string description)
    {
        if (value.Kind != PowerShellValueKind.Array)
        {
            throw InvalidNativeReport($"{description} is not an array.");
        }

        IReadOnlyList<PowerShellValue> values = value.GetArray();
        if (values.Count > maximumCount)
        {
            throw InvalidNativeReport($"{description} exceeds its bound.");
        }

        return values;
    }

    private static string GetString(
        IReadOnlyDictionary<string, PowerShellValue> properties,
        string name,
        int maximumLength)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value))
        {
            throw InvalidNativeReport($"Preflight report is missing {name}.");
        }

        return GetString(value, maximumLength, name);
    }

    private static string GetString(PowerShellValue value, int maximumLength, string description)
    {
        if (!value.TryGetString(out string? text) || text is null || text.Length > maximumLength)
        {
            throw InvalidNativeReport($"{description} is invalid or exceeds its bound.");
        }

        return text;
    }

    private static bool GetBoolean(IReadOnlyDictionary<string, PowerShellValue> properties, string name)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value) || !value.TryGetBoolean(out bool result))
        {
            throw InvalidNativeReport($"Preflight report {name} is invalid.");
        }

        return result;
    }

    private static T GetEnum<T>(IReadOnlyDictionary<string, PowerShellValue> properties, string name)
        where T : struct, Enum
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value) ||
            !value.TryGetUnsignedInteger(out ulong raw) ||
            raw > uint.MaxValue ||
            !Enum.IsDefined(typeof(T), (uint)raw))
        {
            throw InvalidNativeReport($"Preflight report {name} is invalid.");
        }

        return (T)Enum.ToObject(typeof(T), (uint)raw);
    }

    private static PowerShellFfiException InvalidNativeReport(string message)
    {
        return new PowerShellFfiException(PowerShellFfiStatus.ManagedFailure, message);
    }
}
