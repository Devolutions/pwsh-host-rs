namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// A bounded declarative module import for a persistent local session.
/// </summary>
public sealed class PowerShellModuleImport
{
    public PowerShellModuleImport(
        string nameOrPath,
        Version? requiredVersion = null,
        PowerShellModuleImportOptions options = PowerShellModuleImportOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        if ((options & ~PowerShellModuleImportOptions.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (nameOrPath.IndexOf('\0') >= 0 || nameOrPath.Length > 4096)
        {
            throw new ArgumentException("Module imports must be non-NUL text of at most 4096 characters.", nameof(nameOrPath));
        }

        if (Path.IsPathFullyQualified(nameOrPath))
        {
            NameOrPath = Path.GetFullPath(nameOrPath);
        }
        else if (!nameOrPath.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            throw new ArgumentException("Module names must use identifier characters or be absolute paths.", nameof(nameOrPath));
        }
        else
        {
            NameOrPath = nameOrPath;
        }

        RequiredVersion = requiredVersion;
        Options = options;
    }

    public string NameOrPath { get; }

    public Version? RequiredVersion { get; }

    public PowerShellModuleImportOptions Options { get; }

    internal bool IsSimpleImport =>
        !Path.IsPathFullyQualified(NameOrPath) &&
        RequiredVersion is null &&
        Options == PowerShellModuleImportOptions.None;
}

[Flags]
public enum PowerShellModuleImportOptions
{
    None = 0,
    Force = 1,
    DisableNameChecking = 2,
    SkipEditionCheck = 4,
    All = Force | DisableNameChecking | SkipEditionCheck,
}
