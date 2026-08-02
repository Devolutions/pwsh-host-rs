#nullable enable

using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Devolutions.MultiPwsh.LiveContract.Generator;

/// <summary>Maps a resolved value position to the CLR type the generated code uses.</summary>
internal static class BridgeTypeNames
{
    internal const string Wire = "global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeWire";
    internal const string Tag = "global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeTag";
    internal const string FrameKind = "global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeFrameKind";
    internal const string ReplyKind = "global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeReplyKind";
    internal const string Status = "global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeStatus";
    internal const string RequestHeader = "global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeRequestHeader";
    internal const string ReplyHeader = "global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeReplyHeader";
    internal const string Writer = "global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeValueWriter";
    internal const string Reader = "global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeValueReader";
    internal const string Transport = "global::Devolutions.PowerShell.Ffi.LiveObjects.IPowerShellBridgeTransport";
    internal const string BridgeException = "global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeException";

    /// <summary>Returns the CLR type for a value position, without its nullable suffix.</summary>
    internal static string Core(BridgeTypeRef type, BridgeContractModel contract, bool payload)
    {
        switch (type.Tag)
        {
            case BridgeTag.Null:
                return "void";
            case BridgeTag.Bool:
                return "bool";
            case BridgeTag.Int32:
                return "int";
            case BridgeTag.Int64:
                return "long";
            case BridgeTag.Double:
                return "double";
            case BridgeTag.Utf8String:
                return "string";
            case BridgeTag.Bytes:
                return "byte[]";
            case BridgeTag.Guid:
                return "global::System.Guid";
            case BridgeTag.Enum32:
                return type.Symbol!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            case BridgeTag.Handle:
                return payload
                    ? BridgeNames.Wrapper(contract.Objects.Find(model => model.Id == type.TypeId)!)
                    : BridgeNames.Handler(contract.Objects.Find(model => model.Id == type.TypeId)!);
            case BridgeTag.Data:
                return BridgeNames.Value(contract.DataById[type.TypeId]);
            case BridgeTag.List:
                return "global::System.Collections.Generic.IReadOnlyList<" + Full(type.Element!, contract, payload) + ">";
            default:
                return "object";
        }
    }

    /// <summary>Returns the CLR type for a value position, including its nullable suffix.</summary>
    internal static string Full(BridgeTypeRef type, BridgeContractModel contract, bool payload)
    {
        string core = Core(type, contract, payload);
        if (!type.IsNullable || core == "void")
        {
            return core;
        }

        // A handle is a plain identifier on the consumer side, so it uses the
        // value-type nullable form there and the reference form in the payload.
        return core + "?";
    }

    /// <summary>Returns whether the position is a nullable value type rather than a nullable reference.</summary>
    internal static bool IsNullableValue(BridgeTypeRef type, bool payload)
    {
        if (!type.IsNullable)
        {
            return false;
        }

        return type.Tag switch
        {
            BridgeTag.Bool or BridgeTag.Int32 or BridgeTag.Int64 or BridgeTag.Double
                or BridgeTag.Guid or BridgeTag.Enum32 => true,
            _ => false,
        };
    }

    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Number(byte value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Emits the typed binary codec statements for one value position. Every branch
/// is a static, closed switch over the declared tag; nothing here inspects a
/// runtime type, a name, or a member.
/// </summary>
internal static class BridgeCodecEmitter
{
    internal static void EmitWrite(
        StringBuilder source,
        BridgeContractModel contract,
        BridgeTypeRef type,
        string expression,
        string indent,
        string onFail,
        bool payload,
        ref int temp)
    {
        if (type.IsNullable)
        {
            string suffix = BridgeTypeNames.IsNullableValue(type, payload) ? ".Value" : string.Empty;
            source.Append(indent).Append("if (").Append(expression).AppendLine(" is null)");
            source.Append(indent).AppendLine("{");
            source.Append(indent).Append("    if (!__bridgeWriter.TryWriteNull()) { ").Append(onFail).AppendLine(" }");
            source.Append(indent).AppendLine("}");
            source.Append(indent).AppendLine("else");
            source.Append(indent).AppendLine("{");
            EmitWriteCore(source, contract, type, expression + suffix, indent + "    ", onFail, payload, ref temp);
            source.Append(indent).AppendLine("}");
            return;
        }

        EmitWriteCore(source, contract, type, expression, indent, onFail, payload, ref temp);
    }

    private static void EmitWriteCore(
        StringBuilder source,
        BridgeContractModel contract,
        BridgeTypeRef type,
        string expression,
        string indent,
        string onFail,
        bool payload,
        ref int temp)
    {
        switch (type.Tag)
        {
            case BridgeTag.Null:
                Guard(source, indent, "__bridgeWriter.TryWriteNull()", onFail);
                return;
            case BridgeTag.Bool:
                Guard(source, indent, $"__bridgeWriter.TryWriteBool({expression})", onFail);
                return;
            case BridgeTag.Int32:
                Guard(source, indent, $"__bridgeWriter.TryWriteInt32({expression})", onFail);
                return;
            case BridgeTag.Int64:
                Guard(source, indent, $"__bridgeWriter.TryWriteInt64({expression})", onFail);
                return;
            case BridgeTag.Double:
                Guard(source, indent, $"__bridgeWriter.TryWriteDouble({expression})", onFail);
                return;
            case BridgeTag.Guid:
                Guard(source, indent, $"__bridgeWriter.TryWriteGuid({expression})", onFail);
                return;
            case BridgeTag.Enum32:
                Guard(source, indent, $"__bridgeWriter.TryWriteEnum32((int)({expression}))", onFail);
                return;
            case BridgeTag.Utf8String:
                Guard(source, indent, $"__bridgeWriter.TryWriteString({expression}, {BridgeTypeNames.Number(type.MaximumBytes)})", onFail);
                return;
            case BridgeTag.Bytes:
                // A null byte[] would otherwise become an empty ReadOnlySpan and
                // encode as a well-formed empty value, silently substituting data
                // where the sibling string path fails closed.
                Guard(source, indent, $"{expression} is not null", onFail);
                Guard(source, indent, $"__bridgeWriter.TryWriteBytes({expression}, {BridgeTypeNames.Number(type.MaximumBytes)})", onFail);
                return;
            case BridgeTag.Handle:
                Guard(
                    source,
                    indent,
                    payload
                        ? $"__bridgeWriter.TryWriteHandle({BridgeTypeNames.Number(type.TypeId)}UL, ({expression}).ObjectId)"
                        : $"__bridgeWriter.TryWriteHandle({BridgeTypeNames.Number(type.TypeId)}UL, Register{BridgeTypeNames.Number(type.TypeId)}(in admission, {expression}))",
                    onFail);
                return;
            case BridgeTag.Data:
                Guard(
                    source,
                    indent,
                    $"{BridgeNames.Codec(contract)}.TryWrite{BridgeNames.Value(contract.DataById[type.TypeId])}(ref __bridgeWriter, {expression})",
                    onFail);
                return;
            case BridgeTag.List:
                EmitWriteList(source, contract, type, expression, indent, onFail, payload, ref temp);
                return;
            default:
                Guard(source, indent, "false", onFail);
                return;
        }
    }

    private static void EmitWriteList(
        StringBuilder source,
        BridgeContractModel contract,
        BridgeTypeRef type,
        string expression,
        string indent,
        string onFail,
        bool payload,
        ref int temp)
    {
        int id = temp++;
        string list = "__bridgeList" + BridgeTypeNames.Number(id);
        string count = "__bridgeCount" + BridgeTypeNames.Number(id);
        string scope = "__bridgeScope" + BridgeTypeNames.Number(id);
        string loop = "__bridgeIndex" + BridgeTypeNames.Number(id);
        string item = "__bridgeItem" + BridgeTypeNames.Number(id);
        source.Append(indent).Append("var ").Append(list).Append(" = ").Append(expression).AppendLine(";");
        source.Append(indent).Append("int ").Append(count).Append(" = ").Append(list).AppendLine(".Count;");
        Guard(
            source,
            indent,
            $"__bridgeWriter.TryBeginList({count}, {BridgeTypeNames.Tag}.{TagName(type.Element!.Tag)}, {BridgeTypeNames.Number(type.MaximumCount)}, out int {scope})",
            onFail);
        source.Append(indent).Append("for (int ").Append(loop).Append(" = 0; ").Append(loop).Append(" < ").Append(count)
            .Append("; ").Append(loop).AppendLine("++)");
        source.Append(indent).AppendLine("{");
        source.Append(indent).Append("    var ").Append(item).Append(" = ").Append(list).Append('[').Append(loop).AppendLine("];");
        EmitWrite(source, contract, type.Element!, item, indent + "    ", onFail, payload, ref temp);
        source.Append(indent).AppendLine("}");
        Guard(source, indent, $"__bridgeWriter.TryEndList({scope})", onFail);
    }

    internal static void EmitRead(
        StringBuilder source,
        BridgeContractModel contract,
        BridgeTypeRef type,
        string target,
        string indent,
        string onFail,
        bool payload,
        ref int temp)
    {
        if (type.IsNullable)
        {
            int id = temp++;
            string tag = "__bridgeTag" + BridgeTypeNames.Number(id);
            Guard(source, indent, $"__bridgeReader.TryPeekTag(out byte {tag})", onFail);
            source.Append(indent).Append("if (").Append(tag).Append(" == ").Append(BridgeTypeNames.Tag).AppendLine(".Null)");
            source.Append(indent).AppendLine("{");
            Guard(source, indent + "    ", "__bridgeReader.TryReadNull()", onFail);
            source.Append(indent).Append("    ").Append(target).AppendLine(" = null;");
            source.Append(indent).AppendLine("}");
            source.Append(indent).AppendLine("else");
            source.Append(indent).AppendLine("{");
            EmitReadCore(source, contract, type, target, indent + "    ", onFail, payload, ref temp);
            source.Append(indent).AppendLine("}");
            return;
        }

        EmitReadCore(source, contract, type, target, indent, onFail, payload, ref temp);
    }

    private static void EmitReadCore(
        StringBuilder source,
        BridgeContractModel contract,
        BridgeTypeRef type,
        string target,
        string indent,
        string onFail,
        bool payload,
        ref int temp)
    {
        int id = temp++;
        string local = "__bridgeValue" + BridgeTypeNames.Number(id);
        switch (type.Tag)
        {
            case BridgeTag.Null:
                Guard(source, indent, "__bridgeReader.TryReadNull()", onFail);
                return;
            case BridgeTag.Bool:
                Guard(source, indent, $"__bridgeReader.TryReadBool(out bool {local})", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Int32:
                Guard(source, indent, $"__bridgeReader.TryReadInt32(out int {local})", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Int64:
                Guard(source, indent, $"__bridgeReader.TryReadInt64(out long {local})", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Double:
                Guard(source, indent, $"__bridgeReader.TryReadDouble(out double {local})", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Guid:
                Guard(source, indent, $"__bridgeReader.TryReadGuid(out global::System.Guid {local})", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Enum32:
            {
                string enumType = type.Symbol!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                Guard(source, indent, $"__bridgeReader.TryReadEnum32(out int {local})", onFail);
                Guard(source, indent, $"{BridgeNames.Codec(contract)}.IsDefined{Sanitize(enumType)}({local})", onFail);
                source.Append(indent).Append(target).Append(" = (").Append(enumType).Append(')').Append(local).AppendLine(";");
                return;
            }

            case BridgeTag.Utf8String:
                Guard(source, indent, $"__bridgeReader.TryReadString({BridgeTypeNames.Number(type.MaximumBytes)}, out string? {local})", onFail);
                Guard(source, indent, $"{local} is not null", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Bytes:
                Guard(source, indent, $"__bridgeReader.TryReadBytes({BridgeTypeNames.Number(type.MaximumBytes)}, out global::System.ReadOnlySpan<byte> {local})", onFail);
                source.Append(indent).Append(target).Append(" = ").Append(local).AppendLine(".ToArray();");
                return;
            case BridgeTag.Handle:
                Guard(source, indent, $"__bridgeReader.TryReadHandle({BridgeTypeNames.Number(type.TypeId)}UL, out ulong {local})", onFail);
                Guard(source, indent, $"{local} != 0UL", onFail);
                if (payload)
                {
                    source.Append(indent).Append(target).Append(" = client.Resolve")
                        .Append(BridgeNames.Wrapper(contract.Objects.Find(model => model.Id == type.TypeId)!))
                        .Append('(').Append(local).AppendLine(");");
                }
                else
                {
                    string resolved = local + "Handler";
                    Guard(
                        source,
                        indent,
                        $"TryResolve{BridgeTypeNames.Number(type.TypeId)}(in admission, {local}, out var {resolved})",
                        onFail);
                    Assign(source, indent, target, resolved);
                }

                return;
            case BridgeTag.Data:
            {
                string valueType = BridgeNames.Value(contract.DataById[type.TypeId]);
                Guard(
                    source,
                    indent,
                    $"{BridgeNames.Codec(contract)}.TryRead{valueType}(ref __bridgeReader, out {valueType}? {local})",
                    onFail);
                Guard(source, indent, $"{local} is not null", onFail);
                Assign(source, indent, target, local);
                return;
            }

            case BridgeTag.List:
                EmitReadList(source, contract, type, target, indent, onFail, payload, id, ref temp);
                return;
            default:
                Guard(source, indent, "false", onFail);
                return;
        }
    }

    private static void EmitReadList(
        StringBuilder source,
        BridgeContractModel contract,
        BridgeTypeRef type,
        string target,
        string indent,
        string onFail,
        bool payload,
        int id,
        ref int temp)
    {
        string count = "__bridgeCount" + BridgeTypeNames.Number(id);
        string element = "__bridgeElement" + BridgeTypeNames.Number(id);
        string buffer = "__bridgeBuffer" + BridgeTypeNames.Number(id);
        string loop = "__bridgeIndex" + BridgeTypeNames.Number(id);
        string item = "__bridgeItem" + BridgeTypeNames.Number(id);
        string elementType = BridgeTypeNames.Full(type.Element!, contract, payload);
        Guard(
            source,
            indent,
            $"__bridgeReader.TryReadListHeader({BridgeTypeNames.Number(type.MaximumCount)}, out int {count}, out byte {element})",
            onFail);
        Guard(source, indent, $"{element} == {BridgeTypeNames.Tag}.{TagName(type.Element!.Tag)}", onFail);
        source.Append(indent).Append("var ").Append(buffer).Append(" = new ").Append(elementType).Append('[').Append(count).AppendLine("];");
        source.Append(indent).Append("for (int ").Append(loop).Append(" = 0; ").Append(loop).Append(" < ").Append(count)
            .Append("; ").Append(loop).AppendLine("++)");
        source.Append(indent).AppendLine("{");
        source.Append(indent).Append("    ").Append(elementType).Append(' ').Append(item).AppendLine(" = default!;");
        EmitRead(source, contract, type.Element!, item, indent + "    ", onFail, payload, ref temp);
        source.Append(indent).Append("    ").Append(buffer).Append('[').Append(loop).Append("] = ").Append(item).AppendLine(";");
        source.Append(indent).AppendLine("}");
        Guard(source, indent, "__bridgeReader.TryEndContainer()", onFail);
        source.Append(indent).Append(target).Append(" = ").Append(buffer).AppendLine(";");
    }

    internal static void Guard(StringBuilder source, string indent, string condition, string onFail)
    {
        source.Append(indent).Append("if (!(").Append(condition).Append(")) { ").Append(onFail).AppendLine(" }");
    }

    private static void Assign(StringBuilder source, string indent, string target, string local)
    {
        source.Append(indent).Append(target).Append(" = ").Append(local).AppendLine(";");
    }

    internal static string TagName(byte tag) => tag switch
    {
        BridgeTag.Null => "Null",
        BridgeTag.Bool => "Bool",
        BridgeTag.Int32 => "Int32",
        BridgeTag.Int64 => "Int64",
        BridgeTag.Double => "Double",
        BridgeTag.Utf8String => "Utf8String",
        BridgeTag.Bytes => "Bytes",
        BridgeTag.Guid => "Guid",
        BridgeTag.Enum32 => "Enum32",
        BridgeTag.Handle => "Handle",
        BridgeTag.List => "List",
        BridgeTag.Data => "Data",
        _ => "Error",
    };

    internal static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }
}

/// <summary>
/// Emits the mode-independent copied data contracts, their typed codecs, and the
/// closed enumeration allow-lists.
/// </summary>
internal static class BridgeContractDataEmitter
{
    internal static void Emit(StringBuilder source, BridgeContractModel contract)
    {
        foreach (BridgeDataModel model in contract.Data)
        {
            EmitValue(source, contract, model);
        }

        source.Append("internal static class ").Append(BridgeNames.Codec(contract)).AppendLine();
        source.AppendLine("{");
        foreach (BridgeEnumModel model in contract.Enums)
        {
            EmitEnumGuard(source, model);
        }

        foreach (BridgeDataModel model in contract.Data)
        {
            EmitDataCodec(source, contract, model);
        }

        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitValue(StringBuilder source, BridgeContractModel contract, BridgeDataModel model)
    {
        string name = BridgeNames.Value(model);
        // A parameter is qualified with `this.` on assignment and made unique
        // against every declared field name, so a field that is already
        // lowercase cannot produce a silent self-assignment and two fields that
        // differ only by case cannot collide.
        Dictionary<uint, string> parameters = ParameterNames(model);
        source.Append("/// <summary>A copied, bounded value of bridge data contract ")
            .Append(BridgeTypeNames.Number(model.Id)).AppendLine(".</summary>");
        source.Append("public sealed class ").Append(name).AppendLine();
        source.AppendLine("{");
        source.Append("    public ").Append(name).Append('(');
        for (int index = 0; index < model.Fields.Count; index++)
        {
            BridgeFieldModel field = model.Fields[index];
            if (index != 0)
            {
                source.Append(", ");
            }

            source.Append(BridgeTypeNames.Full(field.Type, contract, payload: true)).Append(' ')
                .Append(BridgeNames.Identifier(parameters[field.Ordinal]));
        }

        source.AppendLine(")");
        source.AppendLine("    {");
        foreach (BridgeFieldModel field in model.Fields)
        {
            source.Append("        this.").Append(BridgeNames.Identifier(field.Name)).Append(" = ")
                .Append(BridgeNames.Identifier(parameters[field.Ordinal])).AppendLine(";");
        }

        source.AppendLine("    }");
        source.AppendLine();
        foreach (BridgeFieldModel field in model.Fields)
        {
            source.Append("    public ").Append(BridgeTypeNames.Full(field.Type, contract, payload: true)).Append(' ')
                .Append(BridgeNames.Identifier(field.Name)).AppendLine(" { get; }");
        }

        source.AppendLine("}");
        source.AppendLine();
    }

    /// <summary>
    /// Assigns one deterministic, collision-free constructor parameter name per
    /// field, in ascending ordinal order.
    /// </summary>
    private static Dictionary<uint, string> ParameterNames(BridgeDataModel model)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (BridgeFieldModel field in model.Fields)
        {
            declared.Add(field.Name);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<uint, string>();
        foreach (BridgeFieldModel field in model.Fields)
        {
            string candidate = Camel(field.Name);
            while (used.Contains(candidate) || (declared.Contains(candidate) && candidate != field.Name))
            {
                candidate += "Value";
            }

            used.Add(candidate);
            result.Add(field.Ordinal, candidate);
        }

        return result;
    }

    private static void EmitEnumGuard(StringBuilder source, BridgeEnumModel model)
    {
        string type = model.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        source.Append("    internal static bool IsDefined").Append(BridgeCodecEmitter.Sanitize(type)).AppendLine("(int value)");
        source.AppendLine("    {");
        source.AppendLine("        switch (value)");
        source.AppendLine("        {");
        foreach (BridgeEnumMemberModel member in model.Members)
        {
            source.Append("            case ").Append(BridgeTypeNames.Number(member.Value)).AppendLine(": return true;");
        }

        source.AppendLine("            default: return false;");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitDataCodec(StringBuilder source, BridgeContractModel contract, BridgeDataModel model)
    {
        string name = BridgeNames.Value(model);
        int temp = 0;
        source.Append("    internal static bool TryWrite").Append(name).Append("(ref ").Append(BridgeTypeNames.Writer)
            .Append(" __bridgeWriter, ").Append(name).AppendLine("? value)");
        source.AppendLine("    {");
        source.AppendLine("        if (value is null) { return __bridgeWriter.TryWriteNull(); }");
        source.Append("        if (!__bridgeWriter.TryBeginData(").Append(BridgeTypeNames.Number(model.Id)).Append("UL, ")
            .Append(BridgeTypeNames.Number(model.Fields.Count)).AppendLine(", out int __bridgeScope)) { return false; }");
        foreach (BridgeFieldModel field in model.Fields)
        {
            source.Append("        if (!__bridgeWriter.TryWriteFieldOrdinal(").Append(BridgeTypeNames.Number(field.Ordinal))
                .AppendLine("U)) { return false; }");
            BridgeCodecEmitter.EmitWrite(
                source, contract, field.Type, "value." + BridgeNames.Identifier(field.Name), "        ", "return false;", payload: true, ref temp);
        }

        source.AppendLine("        return __bridgeWriter.TryEndData(__bridgeScope);");
        source.AppendLine("    }");
        source.AppendLine();

        temp = 0;
        source.Append("    internal static bool TryRead").Append(name).Append("(ref ").Append(BridgeTypeNames.Reader)
            .Append(" __bridgeReader, out ").Append(name).AppendLine("? value)");
        source.AppendLine("    {");
        source.AppendLine("        value = null;");
        source.AppendLine("        if (!__bridgeReader.TryPeekTag(out byte __bridgeTag)) { return false; }");
        source.Append("        if (__bridgeTag == ").Append(BridgeTypeNames.Tag).AppendLine(".Null) { return __bridgeReader.TryReadNull(); }");
        source.Append("        if (!__bridgeReader.TryReadDataHeader(").Append(BridgeTypeNames.Number(model.Id)).AppendLine("UL, out int __bridgeFieldCount)) { return false; }");
        source.Append("        if (__bridgeFieldCount != ").Append(BridgeTypeNames.Number(model.Fields.Count)).AppendLine(") { return false; }");
        var locals = new List<string>();
        for (int index = 0; index < model.Fields.Count; index++)
        {
            BridgeFieldModel field = model.Fields[index];
            string local = "__bridgeField" + BridgeTypeNames.Number(index);
            locals.Add(local);
            source.Append("        ").Append(BridgeTypeNames.Full(field.Type, contract, payload: true)).Append(' ')
                .Append(local).AppendLine(" = default!;");
            source.Append("        if (!__bridgeReader.TryReadFieldOrdinal(").Append(BridgeTypeNames.Number(field.Ordinal))
                .AppendLine("U)) { return false; }");
            BridgeCodecEmitter.EmitRead(
                source, contract, field.Type, local, "        ", "return false;", payload: true, ref temp);
        }

        source.AppendLine("        if (!__bridgeReader.TryEndContainer()) { return false; }");
        source.Append("        value = new ").Append(name).Append('(').Append(string.Join(", ", locals)).AppendLine(");");
        source.AppendLine("        return true;");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static string Camel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);
}






