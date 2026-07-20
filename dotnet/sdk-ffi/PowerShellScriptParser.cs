using System.Text;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Parses PowerShell parameter metadata through the selected payload without
/// exposing SMA parser or AST types to the facade consumer.
/// </summary>
public static class PowerShellScriptParser
{
    private const int MaximumScriptBytes = 64 * 1024;
    private const int MaximumParameters = 16;
    private const int MaximumValidateSetValues = 16;
    private const int MaximumParseErrors = 16;
    private const int MaximumMetadataRecords = 32;
    private const int MaximumAliases = 8;
    private const int MaximumParameterSets = 8;
    private const int MaximumValidations = 8;
    private const int MaximumValidationArguments = 4;

    // The caller's script is passed only as a string argument to Parser.ParseInput.
    // It is never added as executable pipeline text.
    private const string ParserScript = """
        param([Parameter(Mandatory = $true)][string] $Script)

        function Get-DpsScriptMetadataLiteral {
            param($Expression)

            if ($null -eq $Expression) {
                return $null
            }

            if ($Expression -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                return $Expression.Value
            }

            if ($Expression -is [System.Management.Automation.Language.ConstantExpressionAst]) {
                return [string] $Expression.Value
            }

            return $Expression.Extent.Text
        }

        function Get-DpsScriptMetadataAttributeName {
            param([System.Management.Automation.Language.AttributeAst] $Attribute)

            $name = $Attribute.TypeName.Name
            if ($name.EndsWith('Attribute', [System.StringComparison]::OrdinalIgnoreCase)) {
                return $name.Substring(0, $name.Length - 'Attribute'.Length)
            }

            return $name
        }

        function Get-DpsScriptMetadataBoolean {
            param($Expression)

            $value = Get-DpsScriptMetadataLiteral $Expression
            return $null -eq $Expression -or $value -eq 'True' -or $value -eq '$true'
        }

        $metadataRecordCount = 0
        function Write-DpsScriptMetadataRecord {
            param($Properties)

            if ($script:metadataRecordCount -eq 32) {
                throw 'The script metadata exceeds the bounded record limit.'
            }

            [pscustomobject] $Properties
            $script:metadataRecordCount++
        }

        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput(
            $Script,
            [ref] $tokens,
            [ref] $errors)

        if ($errors.Count -gt 16) {
            throw 'The script contains too many parse errors for the bounded metadata API.'
        }

        if ($errors.Count -ne 0) {
            foreach ($parseError in $errors) {
                Write-DpsScriptMetadataRecord @{
                    RecordKind = 'parseError'
                    Message = $parseError.Message
                    ErrorId = $parseError.ErrorId
                    StartOffset = [int64] $parseError.Extent.StartOffset
                    EndOffset = [int64] $parseError.Extent.EndOffset
                }
            }

            return
        }

        if ($null -eq $ast.ParamBlock) {
            return
        }

        $parameters = @($ast.ParamBlock.Parameters)
        if ($parameters.Count -gt 16) {
            throw 'The script contains too many parameters for the bounded metadata API.'
        }

        $validateSetValueCount = 0
        foreach ($parameter in $parameters) {
            $typeName = $null
            $defaultValueExpression = if ($null -eq $parameter.DefaultValue) {
                $null
            }
            else {
                $parameter.DefaultValue.Extent.Text
            }
            $mandatory = $false
            $description = $null
            $helpMessage = $null
            $validateSetValues = @()
            $aliases = @()
            $parameterSets = @()
            $validations = @()

            foreach ($attribute in $parameter.Attributes) {
                if ($attribute -is [System.Management.Automation.Language.TypeConstraintAst]) {
                    if ($null -eq $typeName) {
                        $typeName = $attribute.TypeName.FullName
                    }

                    continue
                }

                if ($attribute -isnot [System.Management.Automation.Language.AttributeAst]) {
                    continue
                }

                switch (Get-DpsScriptMetadataAttributeName $attribute) {
                    'Parameter' {
                        $parameterSetName = '__AllParameterSets'
                        $position = $null
                        $valueFromPipeline = $false
                        $valueFromPipelineByPropertyName = $false
                        foreach ($namedArgument in $attribute.NamedArguments) {
                            if ($namedArgument.ArgumentName -eq 'Mandatory') {
                                $mandatory = Get-DpsScriptMetadataBoolean $namedArgument.Argument
                            }
                            elseif ($namedArgument.ArgumentName -eq 'HelpMessage') {
                                $helpMessage = Get-DpsScriptMetadataLiteral $namedArgument.Argument
                            }
                            elseif ($namedArgument.ArgumentName -eq 'ParameterSetName') {
                                $parameterSetName = Get-DpsScriptMetadataLiteral $namedArgument.Argument
                            }
                            elseif ($namedArgument.ArgumentName -eq 'Position') {
                                $positionText = Get-DpsScriptMetadataLiteral $namedArgument.Argument
                                [int64] $parsedPosition = 0
                                if ([int64]::TryParse(
                                    $positionText,
                                    [System.Globalization.NumberStyles]::Integer,
                                    [System.Globalization.CultureInfo]::InvariantCulture,
                                    [ref] $parsedPosition)) {
                                    $position = $parsedPosition
                                }
                            }
                            elseif ($namedArgument.ArgumentName -eq 'ValueFromPipeline') {
                                $valueFromPipeline = Get-DpsScriptMetadataBoolean $namedArgument.Argument
                            }
                            elseif ($namedArgument.ArgumentName -eq 'ValueFromPipelineByPropertyName') {
                                $valueFromPipelineByPropertyName = Get-DpsScriptMetadataBoolean $namedArgument.Argument
                            }
                        }

                        $parameterSets += [pscustomobject] @{
                            Name = $parameterSetName
                            Position = $position
                            ValueFromPipeline = $valueFromPipeline
                            ValueFromPipelineByPropertyName = $valueFromPipelineByPropertyName
                        }
                    }
                    'Description' {
                        if ($attribute.PositionalArguments.Count -ne 0) {
                            $description = Get-DpsScriptMetadataLiteral $attribute.PositionalArguments[0]
                        }
                    }
                    'ValidateSet' {
                        $validateSetValues += @($attribute.PositionalArguments |
                            ForEach-Object { Get-DpsScriptMetadataLiteral $_ })
                    }
                    'Alias' {
                        $aliases += @($attribute.PositionalArguments |
                            ForEach-Object { Get-DpsScriptMetadataLiteral $_ })
                    }
                    { $_ -in 'ValidatePattern', 'ValidateRange', 'ValidateLength', 'ValidateCount' } {
                        $validations += [pscustomobject] @{
                            Name = Get-DpsScriptMetadataAttributeName $attribute
                            Arguments = @($attribute.PositionalArguments |
                                ForEach-Object { Get-DpsScriptMetadataLiteral $_ })
                        }
                    }
                }
            }

            Write-DpsScriptMetadataRecord @{
                RecordKind = 'parameter'
                Name = $parameter.Name.VariablePath.UserPath
                TypeName = $typeName
                DefaultValueExpression = $defaultValueExpression
                IsMandatory = $mandatory
                Description = $description
                HelpMessage = $helpMessage
            }

            foreach ($validateSetValue in $validateSetValues) {
                if ($validateSetValueCount -eq 16) {
                    throw 'The script contains too many ValidateSet values for the bounded metadata API.'
                }

                Write-DpsScriptMetadataRecord @{
                    RecordKind = 'validateSet'
                    ParameterName = $parameter.Name.VariablePath.UserPath
                    Value = $validateSetValue
                }
                $validateSetValueCount++
            }

            if ($aliases.Count -gt 8) {
                throw 'The script contains too many aliases for the bounded metadata API.'
            }
            foreach ($alias in $aliases) {
                Write-DpsScriptMetadataRecord @{
                    RecordKind = 'alias'
                    ParameterName = $parameter.Name.VariablePath.UserPath
                    Value = $alias
                }
            }

            if ($parameterSets.Count -gt 8) {
                throw 'The script contains too many parameter sets for the bounded metadata API.'
            }
            foreach ($parameterSet in $parameterSets) {
                Write-DpsScriptMetadataRecord @{
                    RecordKind = 'parameterSet'
                    ParameterName = $parameter.Name.VariablePath.UserPath
                    Name = $parameterSet.Name
                    Position = $parameterSet.Position
                    ValueFromPipeline = $parameterSet.ValueFromPipeline
                    ValueFromPipelineByPropertyName = $parameterSet.ValueFromPipelineByPropertyName
                }
            }

            if ($validations.Count -gt 8) {
                throw 'The script contains too many validation attributes for the bounded metadata API.'
            }
            foreach ($validation in $validations) {
                if ($validation.Arguments.Count -gt 4) {
                    throw 'A script validation attribute contains too many arguments for the bounded metadata API.'
                }

                $validationRecord = [ordered] @{
                    RecordKind = 'validation'
                    ParameterName = $parameter.Name.VariablePath.UserPath
                    Name = $validation.Name
                    ArgumentCount = [int64] $validation.Arguments.Count
                }
                for ($index = 0; $index -lt $validation.Arguments.Count; $index++) {
                    $validationRecord["Argument$index"] = $validation.Arguments[$index]
                }
                Write-DpsScriptMetadataRecord $validationRecord
            }
        }
        """;

    /// <summary>
    /// Parses a script's declared parameters. The supplied script is parser input
    /// only and is never executed.
    /// </summary>
    public static PowerShellScriptParseResult Parse(PowerShellRuntime runtime, string script)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(script);
        if (Encoding.UTF8.GetByteCount(script) > MaximumScriptBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(script), "Script metadata input exceeds 64 KiB.");
        }

        using PowerShell parser = runtime.Create();
        PowerShellInvocationResult invocation = parser
            .AddScript(ParserScript)
            .AddArgument(script)
            .InvokeWithDiagnostics();

        if (invocation.Output.IsTruncated)
        {
            throw new InvalidOperationException("The PowerShell payload truncated the bounded script metadata result.");
        }

        if (invocation.Output.Records.Count > MaximumMetadataRecords)
        {
            throw new InvalidOperationException("The PowerShell payload returned too many script metadata records.");
        }

        if (invocation.Errors.Records.Count != 0)
        {
            throw new InvalidOperationException(
                $"The PowerShell payload failed while collecting script metadata: {invocation.Errors.Records[0].Message}");
        }

        var parameters = new Dictionary<string, ParameterBuilder>(StringComparer.OrdinalIgnoreCase);
        var parseErrors = new List<PowerShellScriptParseError>();

        foreach (PowerShellObjectSnapshot record in invocation.Output.Records)
        {
            IReadOnlyDictionary<string, PowerShellValue> properties = GetPropertyBag(record);
            string recordKind = GetRequiredString(properties, "RecordKind");
            switch (recordKind)
            {
                case "parameter":
                {
                    string name = GetRequiredString(properties, "Name");
                    if (parameters.Count == MaximumParameters || !parameters.TryAdd(name, ParameterBuilder.FromProperties(properties)))
                    {
                        throw new InvalidOperationException("The PowerShell payload returned invalid script parameter metadata.");
                    }

                    break;
                }
                case "validateSet":
                {
                    string parameterName = GetRequiredString(properties, "ParameterName");
                    if (!parameters.TryGetValue(parameterName, out ParameterBuilder? parameter) ||
                        parameter.ValidateSetValues.Count == MaximumValidateSetValues)
                    {
                        throw new InvalidOperationException("The PowerShell payload returned invalid ValidateSet metadata.");
                    }

                    parameter.ValidateSetValues.Add(GetRequiredString(properties, "Value"));
                    break;
                }
                case "alias":
                {
                    string parameterName = GetRequiredString(properties, "ParameterName");
                    if (!parameters.TryGetValue(parameterName, out ParameterBuilder? parameter) ||
                        parameter.Aliases.Count == MaximumAliases)
                    {
                        throw new InvalidOperationException("The PowerShell payload returned invalid alias metadata.");
                    }

                    parameter.Aliases.Add(GetRequiredString(properties, "Value"));
                    break;
                }
                case "parameterSet":
                {
                    string parameterName = GetRequiredString(properties, "ParameterName");
                    if (!parameters.TryGetValue(parameterName, out ParameterBuilder? parameter) ||
                        parameter.ParameterSets.Count == MaximumParameterSets ||
                        !parameter.ParameterSetNames.Add(GetRequiredString(properties, "Name")))
                    {
                        throw new InvalidOperationException("The PowerShell payload returned invalid parameter-set metadata.");
                    }

                    parameter.ParameterSets.Add(new PowerShellScriptParameterSetMetadata(
                        GetRequiredString(properties, "Name"),
                        GetOptionalInt64(properties, "Position"),
                        GetRequiredBoolean(properties, "ValueFromPipeline"),
                        GetRequiredBoolean(properties, "ValueFromPipelineByPropertyName")));
                    break;
                }
                case "validation":
                {
                    string parameterName = GetRequiredString(properties, "ParameterName");
                    if (!parameters.TryGetValue(parameterName, out ParameterBuilder? parameter) ||
                        parameter.Validations.Count == MaximumValidations)
                    {
                        throw new InvalidOperationException("The PowerShell payload returned invalid validation metadata.");
                    }

                    long argumentCount = GetRequiredInt64(properties, "ArgumentCount");
                    if (argumentCount is < 0 or > MaximumValidationArguments)
                    {
                        throw new InvalidOperationException("The PowerShell payload returned an invalid validation argument count.");
                    }

                    var arguments = new string[checked((int)argumentCount)];
                    for (int index = 0; index < arguments.Length; index++)
                    {
                        arguments[index] = GetRequiredString(properties, $"Argument{index}");
                    }
                    parameter.Validations.Add(new PowerShellScriptValidationMetadata(
                        GetRequiredString(properties, "Name"),
                        arguments));
                    break;
                }
                case "parseError":
                {
                    if (parseErrors.Count == MaximumParseErrors)
                    {
                        throw new InvalidOperationException("The PowerShell payload returned too many parse errors.");
                    }

                    parseErrors.Add(new PowerShellScriptParseError(
                        GetRequiredString(properties, "Message"),
                        GetOptionalString(properties, "ErrorId"),
                        GetRequiredInt64(properties, "StartOffset"),
                        GetRequiredInt64(properties, "EndOffset")));
                    break;
                }
                default:
                    throw new InvalidOperationException("The PowerShell payload returned an unknown script metadata record.");
            }
        }

        if (parseErrors.Count != 0 && parameters.Count != 0)
        {
            throw new InvalidOperationException("The PowerShell payload mixed script parameter metadata and parse errors.");
        }

        return new PowerShellScriptParseResult(
            parameters.Values.Select(static parameter => parameter.Build()).ToArray(),
            parseErrors.ToArray());
    }

    private static IReadOnlyDictionary<string, PowerShellValue> GetPropertyBag(PowerShellObjectSnapshot record)
    {
        if (record.IsTruncated ||
            record.IsPropertyBagTruncated ||
            record.PropertyBag is null ||
            record.PropertyBag.Kind != PowerShellValueKind.PropertyBag)
        {
            throw new InvalidOperationException("The PowerShell payload returned incomplete script metadata.");
        }

        return record.PropertyBag.GetPropertyBag();
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, PowerShellValue> properties, string name)
    {
        string? value = GetOptionalString(properties, name);
        return value ?? throw new InvalidOperationException($"The PowerShell payload omitted script metadata property '{name}'.");
    }

    private static string? GetOptionalString(IReadOnlyDictionary<string, PowerShellValue> properties, string name)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value))
        {
            return null;
        }

        if (value.IsNull)
        {
            return null;
        }

        if (!value.TryGetString(out string? text))
        {
            throw new InvalidOperationException($"The PowerShell payload returned a non-string script metadata property '{name}'.");
        }

        return text;
    }

    private static bool GetRequiredBoolean(IReadOnlyDictionary<string, PowerShellValue> properties, string name)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value) || !value.TryGetBoolean(out bool result))
        {
            throw new InvalidOperationException($"The PowerShell payload returned an invalid Boolean script metadata property '{name}'.");
        }

        return result;
    }

    private static long GetRequiredInt64(IReadOnlyDictionary<string, PowerShellValue> properties, string name)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value) || !value.TryGetSignedInteger(out long result))
        {
            throw new InvalidOperationException($"The PowerShell payload returned an invalid integer script metadata property '{name}'.");
        }

        return result;
    }

    private static long? GetOptionalInt64(IReadOnlyDictionary<string, PowerShellValue> properties, string name)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value) || value.IsNull)
        {
            return null;
        }
        if (!value.TryGetSignedInteger(out long result))
        {
            throw new InvalidOperationException($"The PowerShell payload returned an invalid integer script metadata property '{name}'.");
        }

        return result;
    }

    private sealed class ParameterBuilder
    {
        private ParameterBuilder(
            string name,
            string? typeName,
            string? defaultValueExpression,
            bool isMandatory,
            string? description,
            string? helpMessage)
        {
            Name = name;
            TypeName = typeName;
            DefaultValueExpression = defaultValueExpression;
            IsMandatory = isMandatory;
            Description = description;
            HelpMessage = helpMessage;
        }

        public string Name { get; }

        public string? TypeName { get; }

        public string? DefaultValueExpression { get; }

        public bool IsMandatory { get; }

        public string? Description { get; }

        public string? HelpMessage { get; }

        public List<string> ValidateSetValues { get; } = new();

        public List<string> Aliases { get; } = new();

        public HashSet<string> ParameterSetNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<PowerShellScriptParameterSetMetadata> ParameterSets { get; } = new();

        public List<PowerShellScriptValidationMetadata> Validations { get; } = new();

        public static ParameterBuilder FromProperties(IReadOnlyDictionary<string, PowerShellValue> properties)
        {
            return new ParameterBuilder(
                GetRequiredString(properties, "Name"),
                GetOptionalString(properties, "TypeName"),
                GetOptionalString(properties, "DefaultValueExpression"),
                GetRequiredBoolean(properties, "IsMandatory"),
                GetOptionalString(properties, "Description"),
                GetOptionalString(properties, "HelpMessage"));
        }

        public PowerShellScriptParameterMetadata Build()
        {
            return new PowerShellScriptParameterMetadata(
                Name,
                TypeName,
                DefaultValueExpression,
                IsMandatory,
                Description,
                HelpMessage,
                ValidateSetValues.ToArray(),
                Aliases.ToArray(),
                ParameterSets.ToArray(),
                Validations.ToArray());
        }
    }
}

public sealed class PowerShellScriptParseResult
{
    internal PowerShellScriptParseResult(
        PowerShellScriptParameterMetadata[] parameters,
        PowerShellScriptParseError[] errors)
    {
        Parameters = Array.AsReadOnly(parameters);
        Errors = Array.AsReadOnly(errors);
    }

    public IReadOnlyList<PowerShellScriptParameterMetadata> Parameters { get; }

    public IReadOnlyList<PowerShellScriptParseError> Errors { get; }

    public bool HasErrors => Errors.Count != 0;
}

public sealed class PowerShellScriptParameterMetadata
{
    internal PowerShellScriptParameterMetadata(
        string name,
        string? typeName,
        string? defaultValueExpression,
        bool isMandatory,
        string? description,
        string? helpMessage,
        string[] validateSetValues,
        string[] aliases,
        PowerShellScriptParameterSetMetadata[] parameterSets,
        PowerShellScriptValidationMetadata[] validations)
    {
        Name = name;
        TypeName = typeName;
        DefaultValueExpression = defaultValueExpression;
        IsMandatory = isMandatory;
        Description = description;
        HelpMessage = helpMessage;
        ValidateSetValues = Array.AsReadOnly(validateSetValues);
        Aliases = Array.AsReadOnly(aliases);
        ParameterSets = Array.AsReadOnly(parameterSets);
        Validations = Array.AsReadOnly(validations);
    }

    public string Name { get; }

    public string? TypeName { get; }

    public string? DefaultValueExpression { get; }

    public bool IsMandatory { get; }

    public string? Description { get; }

    public string? HelpMessage { get; }

    public IReadOnlyList<string> ValidateSetValues { get; }

    public IReadOnlyList<string> Aliases { get; }

    public IReadOnlyList<PowerShellScriptParameterSetMetadata> ParameterSets { get; }

    public IReadOnlyList<PowerShellScriptValidationMetadata> Validations { get; }
}

public sealed class PowerShellScriptParameterSetMetadata
{
    internal PowerShellScriptParameterSetMetadata(
        string name,
        long? position,
        bool valueFromPipeline,
        bool valueFromPipelineByPropertyName)
    {
        Name = name;
        Position = position;
        ValueFromPipeline = valueFromPipeline;
        ValueFromPipelineByPropertyName = valueFromPipelineByPropertyName;
    }

    public string Name { get; }

    public long? Position { get; }

    public bool ValueFromPipeline { get; }

    public bool ValueFromPipelineByPropertyName { get; }
}

public sealed class PowerShellScriptValidationMetadata
{
    internal PowerShellScriptValidationMetadata(string name, string[] arguments)
    {
        Name = name;
        Arguments = Array.AsReadOnly(arguments);
    }

    public string Name { get; }

    public IReadOnlyList<string> Arguments { get; }
}

public sealed class PowerShellScriptParseError
{
    internal PowerShellScriptParseError(string message, string? errorId, long startOffset, long endOffset)
    {
        Message = message;
        ErrorId = errorId;
        StartOffset = startOffset;
        EndOffset = endOffset;
    }

    public string Message { get; }

    public string? ErrorId { get; }

    public long StartOffset { get; }

    public long EndOffset { get; }
}
