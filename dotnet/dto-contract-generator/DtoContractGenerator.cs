#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Devolutions.MultiPwsh.DtoContract.Generator;

[Generator]
public sealed class DtoContractGenerator : IIncrementalGenerator
{
    private const string ContractAttribute = "Devolutions.PowerShell.Ffi.PowerShellDtoContractAttribute";
    private const string MemberAttribute = "Devolutions.PowerShell.Ffi.PowerShellDtoMemberAttribute";

    private static readonly DiagnosticDescriptor InvalidContract = new(
        "MPWDTO001",
        "Invalid PowerShell DTO contract",
        "PowerShell DTO contract '{0}' {1}",
        "MultiPwsh.DTO",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnsupportedMember = new(
        "MPWDTO002",
        "Unsupported PowerShell DTO member",
        "PowerShell DTO member '{0}' {1}",
        "MultiPwsh.DTO",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<INamedTypeSymbol> contracts = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax { AttributeLists.Count: > 0 },
                static (syntax, _) => syntax.SemanticModel.GetDeclaredSymbol(syntax.Node) as INamedTypeSymbol)
            .Where(static type => type is not null && HasAttribute(type, ContractAttribute))
            .Select(static (type, _) => type!);

        context.RegisterSourceOutput(contracts.Collect(), static (production, types) =>
        {
            foreach (INamedTypeSymbol? candidate in types.Distinct(SymbolEqualityComparer.Default))
            {
                if (candidate is not INamedTypeSymbol type)
                {
                    continue;
                }

                ContractInfo? contract = Analyze(type, production);
                if (contract is not null)
                {
                    production.AddSource(
                        GetHintName(type),
                        SourceText.From(Emit(contract), Encoding.UTF8));
                }
            }
        });
    }

    private static ContractInfo? Analyze(INamedTypeSymbol type, SourceProductionContext production)
    {
        AttributeData? attribute = GetAttribute(type, ContractAttribute);
        if (type.DeclaredAccessibility != Accessibility.Public ||
            type.ContainingType is not null ||
            type.TypeParameters.Length != 0 ||
            type.TypeKind != TypeKind.Class ||
            type.IsAbstract ||
            attribute is null ||
            attribute.ConstructorArguments.Length != 1 ||
            attribute.ConstructorArguments[0].Value is not int version ||
            version < 1)
        {
            production.ReportDiagnostic(Diagnostic.Create(InvalidContract, Location(type), type.Name,
                "must be a public, non-abstract, non-generic top-level class with a positive version"));
            return null;
        }

        bool rejectUnknown = GetNamedBoolean(attribute, "RejectUnknownMembers", true);
        if (type.TypeKind == TypeKind.Class &&
            !type.InstanceConstructors.Any(constructor => constructor.DeclaredAccessibility == Accessibility.Public &&
                                                         constructor.Parameters.Length == 0))
        {
            production.ReportDiagnostic(Diagnostic.Create(InvalidContract, Location(type), type.Name,
                "must have a public parameterless constructor"));
            return null;
        }

        var members = new List<MemberInfo>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>())
        {
            AttributeData? memberAttribute = GetAttribute(property, MemberAttribute);
            if (memberAttribute is null)
            {
                continue;
            }

            if (property.IsStatic || property.IsIndexer || property.IsRequired || property.SetMethod is null ||
                property.SetMethod.IsInitOnly ||
                property.SetMethod.DeclaredAccessibility != Accessibility.Public ||
                property.GetMethod is null || property.GetMethod.DeclaredAccessibility != Accessibility.Public)
            {
                production.ReportDiagnostic(Diagnostic.Create(UnsupportedMember, Location(property), property.Name,
                    "must have public instance getter and non-init setter and cannot be required or an indexer"));
                continue;
            }

            string wireName = memberAttribute.ConstructorArguments.Length == 1 &&
                              memberAttribute.ConstructorArguments[0].Value is string explicitName &&
                              !string.IsNullOrWhiteSpace(explicitName)
                ? explicitName
                : property.Name;
            int maxString = GetNamedInt(memberAttribute, "MaximumStringLength", 4096);
            int maxCollection = GetNamedInt(memberAttribute, "MaximumCollectionCount", 64);
            if (string.Equals(wireName, "$version", StringComparison.OrdinalIgnoreCase) ||
                wireName.Length > 128 || wireName.IndexOf('\0') >= 0 ||
                !names.Add(wireName) || maxString < 0 || maxString > 64 * 1024 ||
                maxCollection < 0 || maxCollection > 64)
            {
                production.ReportDiagnostic(Diagnostic.Create(UnsupportedMember, Location(property), property.Name,
                    "has an invalid or duplicate wire name or bound"));
                continue;
            }

            ProjectionKind kind = GetProjectionKind(property.Type);
            if (kind == ProjectionKind.Unsupported)
            {
                production.ReportDiagnostic(Diagnostic.Create(UnsupportedMember, Location(property), property.Name,
                    "must use a supported scalar or one-dimensional array of supported scalars"));
                continue;
            }

            members.Add(new MemberInfo(
                property.Name,
                wireName,
                property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                kind,
                GetNamedBoolean(memberAttribute, "Required", true),
                maxString,
                maxCollection));
        }

        if (members.Count == 0)
        {
            production.ReportDiagnostic(Diagnostic.Create(InvalidContract, Location(type), type.Name,
                "must declare at least one [PowerShellDtoMember] property"));
            return null;
        }

        if (members.Count > 63)
        {
            production.ReportDiagnostic(Diagnostic.Create(InvalidContract, Location(type), type.Name,
                "must declare no more than 63 [PowerShellDtoMember] properties because $version uses one of the 64 property bag entries"));
            return null;
        }

        return new ContractInfo(
            type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString(),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.Name,
            version,
            rejectUnknown,
            members);
    }

    private static string Emit(ContractInfo contract)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        if (!string.IsNullOrEmpty(contract.Namespace))
        {
            source.Append("namespace ").Append(contract.Namespace).AppendLine(";");
            source.AppendLine();
        }

        source.Append("public static class ").Append(contract.Name).AppendLine("PowerShellDtoProjection");
        source.AppendLine("{");
        source.Append("    private static readonly global::System.Collections.Generic.IReadOnlySet<string> DeclaredMembers = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.OrdinalIgnoreCase) { ");
        foreach (MemberInfo member in contract.Members)
        {
            source.Append(Literal(member.WireName)).Append(", ");
        }
        source.AppendLine("};");
        source.AppendLine();
        source.Append("    public static bool TryRead(global::Devolutions.PowerShell.Ffi.PowerShellValue value, out ")
            .Append(contract.TypeName)
            .Append("? result, out global::Devolutions.PowerShell.Ffi.PowerShellDtoProjectionError? error)");
        source.AppendLine();
        source.AppendLine("    {");
        source.Append("        if (!global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.TryGetPropertyBag(value, ")
            .Append(contract.Version.ToString(CultureInfo.InvariantCulture)).Append(", ")
            .Append(contract.RejectUnknown ? "true" : "false")
            .AppendLine(", DeclaredMembers, string.Empty, out var properties, out error))");
        source.AppendLine("        { result = default; return false; }");
        source.Append("        var dto = new ").Append(contract.TypeName).AppendLine("();");
        foreach (MemberInfo member in contract.Members)
        {
            EmitReadMember(source, member);
        }
        source.AppendLine("        result = dto;");
        source.AppendLine("        error = null;");
        source.AppendLine("        return true;");
        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    public static ").Append(contract.TypeName).AppendLine(" Read(global::Devolutions.PowerShell.Ffi.PowerShellValue value)");
        source.AppendLine("    {");
        source.AppendLine("        if (!TryRead(value, out var result, out var error)) throw global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.CreateException(error!);");
        source.AppendLine("        return result!;");
        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    public static global::Devolutions.PowerShell.Ffi.PowerShellValue Write(").Append(contract.TypeName).AppendLine(" value)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(value);");
        foreach (MemberInfo member in contract.Members)
        {
            if (ReferenceEquals(member.Kind, ProjectionKind.String))
            {
                source.Append("        if (value.@").Append(member.PropertyName).Append(".Length > ")
                    .Append(member.MaximumStringLength.ToString(CultureInfo.InvariantCulture))
                    .AppendLine(") throw global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.CreateException(global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.ValueTooLarge(" + Literal(member.WireName) + ", \"The DTO string member exceeds its declared bound.\"));");
            }
            else if (member.Kind.IsArray)
            {
                source.Append("        if (value.@").Append(member.PropertyName).Append(".Length > ")
                    .Append(member.MaximumCollectionCount.ToString(CultureInfo.InvariantCulture))
                    .AppendLine(") throw global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.CreateException(global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.ValueTooLarge(" + Literal(member.WireName) + ", \"The DTO array exceeds its declared bound.\"));");
                if (ReferenceEquals(member.Kind.Element, ProjectionKind.String))
                {
                    source.Append("        if (global::System.Linq.Enumerable.Any(value.@").Append(member.PropertyName)
                        .Append(", static item => item.Length > ")
                        .Append(member.MaximumStringLength.ToString(CultureInfo.InvariantCulture))
                        .AppendLine(")) throw global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.CreateException(global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.ValueTooLarge(" + Literal(member.WireName) + ", \"A DTO string array member contains an item that exceeds its declared bound.\"));");
                }
            }
        }
        source.AppendLine("        return global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.CreatePropertyBag(" + contract.Version.ToString(CultureInfo.InvariantCulture) + ", new global::System.Collections.Generic.KeyValuePair<string, global::Devolutions.PowerShell.Ffi.PowerShellValue>[]");
        source.AppendLine("        {");
        foreach (MemberInfo member in contract.Members)
        {
            source.Append("            new(").Append(Literal(member.WireName)).Append(", ")
                .Append(WriteExpression("value.@" + member.PropertyName, member)).AppendLine("),");
        }
        source.AppendLine("        });");
        source.AppendLine("    }");
        source.AppendLine("}");
        return source.ToString();
    }

    private static void EmitReadMember(StringBuilder source, MemberInfo member)
    {
        string path = "global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.JoinPath(string.Empty, " + Literal(member.WireName) + ")";
        source.Append("        if (!global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.TryGetMember(properties!, ")
            .Append(Literal(member.WireName)).Append(", ").Append(member.Required ? "true" : "false")
            .Append(", string.Empty, out var ").Append(member.PropertyName).Append("Value, out error))")
            .AppendLine(" { result = default; return false; }");
        source.Append("        if (").Append(member.PropertyName).Append("Value is not null)").AppendLine();
        source.AppendLine("        {");
        if (member.Kind.IsArray)
        {
            string element = member.Kind.ElementName!;
            source.Append("            if (!global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.TryReadArray(")
                .Append(member.PropertyName).Append("Value, ").Append(member.MaximumCollectionCount.ToString(CultureInfo.InvariantCulture))
                .Append(", ").Append(path).Append(", out var values, out error)) { result = default; return false; }").AppendLine();
            source.Append("            var converted = new ").Append(element).Append("[values!.Count];").AppendLine();
            source.AppendLine("            for (var index = 0; index < values.Count; index++)");
            source.AppendLine("            {");
            EmitScalarRead(source, member.Kind.Element!, "values[index]", "converted[index]", path, member.MaximumStringLength, "                ");
            source.AppendLine("            }");
            source.Append("            dto.@").Append(member.PropertyName).AppendLine(" = converted;");
        }
        else
        {
            EmitScalarRead(source, member.Kind, member.PropertyName + "Value", "dto.@" + member.PropertyName, path, member.MaximumStringLength, "            ");
        }
        source.AppendLine("        }");
    }

    private static void EmitScalarRead(StringBuilder source, ProjectionKind kind, string input, string output, string path, int maximumStringLength, string indent)
    {
        if (kind == ProjectionKind.String)
        {
            source.Append(indent).Append("if (!global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.TryReadString(")
                .Append(input).Append(", ").Append(maximumStringLength.ToString(CultureInfo.InvariantCulture))
                .Append(", ").Append(path).Append(", out var scalar, out error)) { result = default; return false; }").AppendLine();
            source.Append(indent).Append(output).AppendLine(" = scalar!;");
            return;
        }

        string method = ReferenceEquals(kind, ProjectionKind.Boolean) ? "TryGetBoolean"
            : ReferenceEquals(kind, ProjectionKind.SignedInteger) ? "TryGetSignedInteger"
            : ReferenceEquals(kind, ProjectionKind.UnsignedInteger) ? "TryGetUnsignedInteger"
            : ReferenceEquals(kind, ProjectionKind.Double) ? "TryGetDouble"
            : ReferenceEquals(kind, ProjectionKind.Decimal) ? "TryGetDecimal"
            : ReferenceEquals(kind, ProjectionKind.DateTime) ? "TryGetDateTime"
            : ReferenceEquals(kind, ProjectionKind.DateTimeOffset) ? "TryGetDateTimeOffset"
            : ReferenceEquals(kind, ProjectionKind.Guid) ? "TryGetGuid"
            : ReferenceEquals(kind, ProjectionKind.Uri) ? "TryGetUri"
            : throw new InvalidOperationException();
        source.Append(indent).Append("if (!").Append(input).Append(".").Append(method)
            .Append("(out var scalar)) { error = global::Devolutions.PowerShell.Ffi.PowerShellDtoProjection.InvalidValue(")
            .Append(path).Append(", \"The DTO member has an invalid tagged value kind.\"); result = default; return false; }").AppendLine();
        source.Append(indent).Append(output).AppendLine(" = scalar!;");
    }

    private static string WriteExpression(string value, MemberInfo member)
    {
        if (member.Kind.IsArray)
        {
            return "global::Devolutions.PowerShell.Ffi.PowerShellValue.Array(global::System.Linq.Enumerable.Select(" + value + ", static item => " +
                   WriteScalarExpression("item", member.Kind.Element!, member.MaximumStringLength) + "))";
        }

        return WriteScalarExpression(value, member.Kind, member.MaximumStringLength);
    }

    private static string WriteScalarExpression(string value, ProjectionKind kind, int maximumStringLength)
    {
        string method = ReferenceEquals(kind, ProjectionKind.String) ? "String"
            : ReferenceEquals(kind, ProjectionKind.Boolean) ? "Boolean"
            : ReferenceEquals(kind, ProjectionKind.SignedInteger) ? "SignedInteger"
            : ReferenceEquals(kind, ProjectionKind.UnsignedInteger) ? "UnsignedInteger"
            : ReferenceEquals(kind, ProjectionKind.Double) ? "Double"
            : ReferenceEquals(kind, ProjectionKind.Decimal) ? "Decimal"
            : ReferenceEquals(kind, ProjectionKind.DateTime) ? "DateTime"
            : ReferenceEquals(kind, ProjectionKind.DateTimeOffset) ? "DateTimeOffset"
            : ReferenceEquals(kind, ProjectionKind.Guid) ? "Guid"
            : ReferenceEquals(kind, ProjectionKind.Uri) ? "Uri"
            : throw new InvalidOperationException();
        return "global::Devolutions.PowerShell.Ffi.PowerShellValue." + method + "(" + value + ")";
    }

    private static ProjectionKind GetProjectionKind(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            ProjectionKind element = GetProjectionKind(array.ElementType);
            return element.IsArray || element == ProjectionKind.Unsupported
                ? ProjectionKind.Unsupported
                : ProjectionKind.Array(element);
        }

        return type.SpecialType switch
        {
            SpecialType.System_String => ProjectionKind.String,
            SpecialType.System_Boolean => ProjectionKind.Boolean,
            SpecialType.System_Int64 => ProjectionKind.SignedInteger,
            SpecialType.System_UInt64 => ProjectionKind.UnsignedInteger,
            SpecialType.System_Double => ProjectionKind.Double,
            SpecialType.System_Decimal => ProjectionKind.Decimal,
            _ => type.ToDisplayString() switch
            {
                "System.DateTime" => ProjectionKind.DateTime,
                "System.DateTimeOffset" => ProjectionKind.DateTimeOffset,
                "System.Guid" => ProjectionKind.Guid,
                "System.Uri" => ProjectionKind.Uri,
                _ => ProjectionKind.Unsupported,
            },
        };
    }

    private static bool HasAttribute(ISymbol symbol, string metadataName) => GetAttribute(symbol, metadataName) is not null;

    private static AttributeData? GetAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static int GetNamedInt(AttributeData attribute, string name, int defaultValue) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is int value ? value : defaultValue;

    private static bool GetNamedBoolean(AttributeData attribute, string name, bool defaultValue) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is bool value ? value : defaultValue;

    private static Location Location(ISymbol symbol) =>
        symbol.Locations.FirstOrDefault() ?? Microsoft.CodeAnalysis.Location.None;

    private static string GetHintName(INamedTypeSymbol type)
    {
        var hintName = new StringBuilder();
        foreach (char character in type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
        {
            if ((character >= 'a' && character <= 'z') ||
                (character >= 'A' && character <= 'Z') ||
                (character >= '0' && character <= '9'))
            {
                hintName.Append(character);
            }
            else
            {
                hintName.Append('_')
                    .Append(((int)character).ToString("X4", CultureInfo.InvariantCulture))
                    .Append('_');
            }
        }

        return hintName.Append(".PowerShellDtoProjection.g.cs").ToString();
    }

    private static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

    private sealed class ContractInfo
    {
        public ContractInfo(string @namespace, string typeName, string name, int version, bool rejectUnknown, List<MemberInfo> members)
        {
            Namespace = @namespace;
            TypeName = typeName;
            Name = name;
            Version = version;
            RejectUnknown = rejectUnknown;
            Members = members;
        }

        public string Namespace { get; }
        public string TypeName { get; }
        public string Name { get; }
        public int Version { get; }
        public bool RejectUnknown { get; }
        public List<MemberInfo> Members { get; }
    }

    private sealed class MemberInfo
    {
        public MemberInfo(string propertyName, string wireName, string typeName, ProjectionKind kind, bool required, int maximumStringLength, int maximumCollectionCount)
        {
            PropertyName = propertyName;
            WireName = wireName;
            TypeName = typeName;
            Kind = kind;
            Required = required;
            MaximumStringLength = maximumStringLength;
            MaximumCollectionCount = maximumCollectionCount;
        }

        public string PropertyName { get; }
        public string WireName { get; }
        public string TypeName { get; }
        public ProjectionKind Kind { get; }
        public bool Required { get; }
        public int MaximumStringLength { get; }
        public int MaximumCollectionCount { get; }
    }

    private sealed class ProjectionKind
    {
        private ProjectionKind(string name, ProjectionKind? element = null)
        {
            Name = name;
            Element = element;
        }

        public static ProjectionKind Unsupported { get; } = new("Unsupported");
        public static ProjectionKind String { get; } = new("String");
        public static ProjectionKind Boolean { get; } = new("Boolean");
        public static ProjectionKind SignedInteger { get; } = new("SignedInteger");
        public static ProjectionKind UnsignedInteger { get; } = new("UnsignedInteger");
        public static ProjectionKind Double { get; } = new("Double");
        public static ProjectionKind Decimal { get; } = new("Decimal");
        public static ProjectionKind DateTime { get; } = new("DateTime");
        public static ProjectionKind DateTimeOffset { get; } = new("DateTimeOffset");
        public static ProjectionKind Guid { get; } = new("Guid");
        public static ProjectionKind Uri { get; } = new("Uri");

        public string Name { get; }
        public ProjectionKind? Element { get; }
        public bool IsArray => Element is not null;
        public string? ElementName => Element?.Name switch
        {
            "String" => "string",
            "Boolean" => "bool",
            "SignedInteger" => "long",
            "UnsignedInteger" => "ulong",
            "Double" => "double",
            "Decimal" => "decimal",
            "DateTime" => "global::System.DateTime",
            "DateTimeOffset" => "global::System.DateTimeOffset",
            "Guid" => "global::System.Guid",
            "Uri" => "global::System.Uri",
            _ => null,
        };

        public static ProjectionKind Array(ProjectionKind element) => new("Array", element);
    }
}
