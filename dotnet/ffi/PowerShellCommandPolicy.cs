namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// An opt-in application guardrail for recipes. It is not a PowerShell sandbox
/// and cannot make arbitrary approved script source safe.
/// </summary>
public sealed class PowerShellCommandPolicy
{
    private readonly HashSet<string>? allowedCommandSet;
    private readonly IReadOnlyList<string>? allowedCommands;

    public PowerShellCommandPolicy(
        IEnumerable<string>? allowedCommands = null,
        bool allowScripts = false,
        int maximumScriptBytes = 64 * 1024,
        int maximumParameters = PowerShellValue.MaximumContainerEntries)
    {
        if (maximumScriptBytes is <= 0 or > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumScriptBytes));
        }
        if (maximumParameters is < 0 or > PowerShellValue.MaximumContainerEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumParameters));
        }

        if (allowedCommands is not null)
        {
            string[] copy = allowedCommands.ToArray();
            if (copy.Length == 0 ||
                copy.Any(command => string.IsNullOrWhiteSpace(command) || command.Length > 128 || command.IndexOf('\0') >= 0) ||
                copy.Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
            {
                throw new ArgumentException("Allowed command names must be unique, non-empty, and bounded.", nameof(allowedCommands));
            }

            allowedCommandSet = new HashSet<string>(copy, StringComparer.OrdinalIgnoreCase);
            this.allowedCommands = Array.AsReadOnly(copy);
        }

        AllowsScripts = allowScripts;
        MaximumScriptBytes = maximumScriptBytes;
        MaximumParameters = maximumParameters;
    }

    public IReadOnlyList<string>? AllowedCommands => allowedCommands;

    public bool AllowsScripts { get; }

    public int MaximumScriptBytes { get; }

    public int MaximumParameters { get; }

    internal void Validate(PowerShellCommandRecipe recipe)
    {
        if (recipe.Parameters.Count > MaximumParameters)
        {
            throw new InvalidOperationException("The command recipe exceeds the policy parameter bound.");
        }
        if (allowedCommandSet is not null && !allowedCommandSet.Contains(recipe.Command))
        {
            throw new InvalidOperationException("The command recipe is not allowed by the configured command policy.");
        }
    }

    internal void Validate(PowerShellScriptRecipe recipe)
    {
        if (!AllowsScripts ||
            System.Text.Encoding.UTF8.GetByteCount(recipe.Script) > MaximumScriptBytes)
        {
            throw new InvalidOperationException("The script recipe is not allowed by the configured command policy.");
        }
    }
}
