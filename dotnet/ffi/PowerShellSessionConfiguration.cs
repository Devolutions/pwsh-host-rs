using System.Collections.ObjectModel;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Copied, bounded configuration for a newly owned local session.
/// Module paths, imports, working directory, and environment keys must also be approved
/// by the hash-pinned payload manifest's sessionPolicy.
/// </summary>
public sealed class PowerShellSessionConfiguration
{
    internal const int MaximumEntries = 32;
    private const int MaximumPathLength = 4096;

    public PowerShellSessionConfiguration(
        IEnumerable<KeyValuePair<string, PowerShellValue>>? initialVariables = null,
        IEnumerable<string>? moduleImports = null,
        IEnumerable<string>? allowedModulePaths = null,
        string? workingDirectory = null,
        IEnumerable<KeyValuePair<string, string>>? environment = null,
        PowerShellSessionExecutionPolicy executionPolicy = PowerShellSessionExecutionPolicy.Default)
    {
        if (!Enum.IsDefined(executionPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(executionPolicy));
        }

        InitialVariables = CopyValues(initialVariables, nameof(initialVariables), "initial variable");
        ModuleImports = CopyNames(moduleImports, nameof(moduleImports), "module import", IsModuleImportName);
        AllowedModulePaths = CopyPaths(allowedModulePaths);
        WorkingDirectory = ValidatePath(workingDirectory, nameof(workingDirectory));
        Environment = CopyEnvironment(environment);
        ExecutionPolicy = executionPolicy;
        ValidateEncodedPayloads();
    }

    public IReadOnlyDictionary<string, PowerShellValue> InitialVariables { get; }

    public IReadOnlyList<string> ModuleImports { get; }

    public IReadOnlyList<string> AllowedModulePaths { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }

    public PowerShellSessionExecutionPolicy ExecutionPolicy { get; }

    internal static PowerShellSessionConfiguration FromLegacyModulePath(string? allowedModulePath)
    {
        return string.IsNullOrEmpty(allowedModulePath)
            ? new PowerShellSessionConfiguration()
            : new PowerShellSessionConfiguration(allowedModulePaths: [allowedModulePath]);
    }

    internal PowerShellValue InitialVariablesValue =>
        PowerShellValue.PropertyBag(InitialVariables);

    internal PowerShellValue ModuleImportsValue =>
        PowerShellValue.Array(ModuleImports.Select(PowerShellValue.String));

    internal PowerShellValue AllowedModulePathsValue =>
        PowerShellValue.Array(AllowedModulePaths.Select(PowerShellValue.String));

    internal PowerShellValue EnvironmentValue =>
        PowerShellValue.PropertyBag(Environment.Select(static pair =>
            new KeyValuePair<string, PowerShellValue>(pair.Key, PowerShellValue.String(pair.Value))));

    private static IReadOnlyDictionary<string, PowerShellValue> CopyValues(
        IEnumerable<KeyValuePair<string, PowerShellValue>>? values,
        string parameterName,
        string description)
    {
        var copy = new Dictionary<string, PowerShellValue>(StringComparer.OrdinalIgnoreCase);
        if (values is not null)
        {
            foreach (KeyValuePair<string, PowerShellValue> pair in values)
            {
                ValidateName(pair.Key, parameterName, description);
                ArgumentNullException.ThrowIfNull(pair.Value, parameterName);
                if (copy.Count == MaximumEntries || !copy.TryAdd(pair.Key, pair.Value))
                {
                    throw new ArgumentException($"{description} names must be unique and contain at most {MaximumEntries} entries.", parameterName);
                }
            }
        }

        return new ReadOnlyDictionary<string, PowerShellValue>(copy);
    }

    private static IReadOnlyList<string> CopyNames(
        IEnumerable<string>? values,
        string parameterName,
        string description,
        Func<string, bool> isValid)
    {
        var copy = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values is not null)
        {
            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !isValid(value) || !unique.Add(value))
                {
                    throw new ArgumentException($"{description} values must be unique, bounded, and use the documented identifier form.", parameterName);
                }
                if (copy.Count == MaximumEntries)
                {
                    throw new ArgumentOutOfRangeException(parameterName, $"{description} values contain at most {MaximumEntries} entries.");
                }

                copy.Add(value);
            }
        }

        return new ReadOnlyCollection<string>(copy);
    }

    private static IReadOnlyList<string> CopyPaths(IEnumerable<string>? values)
    {
        var copy = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values is not null)
        {
            foreach (string value in values)
            {
                string path = ValidatePath(value, nameof(values));
                if (!unique.Add(path))
                {
                    throw new ArgumentException("Allowed module paths must be unique.", nameof(values));
                }
                if (copy.Count == MaximumEntries)
                {
                    throw new ArgumentOutOfRangeException(nameof(values), $"Allowed module paths contain at most {MaximumEntries} entries.");
                }

                copy.Add(path);
            }
        }

        return new ReadOnlyCollection<string>(copy);
    }

    private static IReadOnlyDictionary<string, string> CopyEnvironment(
        IEnumerable<KeyValuePair<string, string>>? values)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is not null)
        {
            foreach (KeyValuePair<string, string> pair in values)
            {
                ValidateName(pair.Key, nameof(values), "environment variable");
                ArgumentNullException.ThrowIfNull(pair.Value, nameof(values));
                if (pair.Value.IndexOf('\0') >= 0 || pair.Value.Length > 4096)
                {
                    throw new ArgumentException("Environment variable values must be non-NUL text of at most 4096 characters.", nameof(values));
                }
                if (copy.Count == MaximumEntries || !copy.TryAdd(pair.Key, pair.Value))
                {
                    throw new ArgumentException($"Environment variable names must be unique and contain at most {MaximumEntries} entries.", nameof(values));
                }
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static string ValidatePath(string? value, string parameterName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        if (value.IndexOf('\0') >= 0 ||
            value.Length > MaximumPathLength ||
            !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("Paths must be absolute, non-NUL paths.", parameterName);
        }

        return Path.GetFullPath(value);
    }

    private static void ValidateName(string? value, string parameterName, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            throw new ArgumentException($"{description} names must be non-empty identifiers of at most 64 characters.", parameterName);
        }

        if (!IsAsciiLetter(value[0]) && value[0] != '_' ||
            !value.All(character => IsAsciiLetter(character) || IsAsciiDigit(character) || character == '_'))
        {
            throw new ArgumentException($"{description} names must use ASCII-like identifier characters.", parameterName);
        }
    }

    private static bool IsModuleImportName(string value)
    {
        return value.All(character => IsAsciiLetter(character) || IsAsciiDigit(character) || character is '.' or '_' or '-');
    }

    private void ValidateEncodedPayloads()
    {
        _ = InitialVariablesValue;
        _ = ModuleImportsValue;
        _ = AllowedModulePathsValue;
        _ = EnvironmentValue;
    }

    private static bool IsAsciiLetter(char value)
    {
        return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }

    private static bool IsAsciiDigit(char value)
    {
        return value is >= '0' and <= '9';
    }
}
