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
                    : "ulong";
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
            BridgeTag.Handle => !payload,
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
            source.Append(indent).Append("    if (!writer.TryWriteNull()) { ").Append(onFail).AppendLine(" }");
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
                Guard(source, indent, "writer.TryWriteNull()", onFail);
                return;
            case BridgeTag.Bool:
                Guard(source, indent, $"writer.TryWriteBool({expression})", onFail);
                return;
            case BridgeTag.Int32:
                Guard(source, indent, $"writer.TryWriteInt32({expression})", onFail);
                return;
            case BridgeTag.Int64:
                Guard(source, indent, $"writer.TryWriteInt64({expression})", onFail);
                return;
            case BridgeTag.Double:
                Guard(source, indent, $"writer.TryWriteDouble({expression})", onFail);
                return;
            case BridgeTag.Guid:
                Guard(source, indent, $"writer.TryWriteGuid({expression})", onFail);
                return;
            case BridgeTag.Enum32:
                Guard(source, indent, $"writer.TryWriteEnum32((int)({expression}))", onFail);
                return;
            case BridgeTag.Utf8String:
                Guard(source, indent, $"writer.TryWriteString({expression}, {BridgeTypeNames.Number(type.MaximumBytes)})", onFail);
                return;
            case BridgeTag.Bytes:
                Guard(source, indent, $"writer.TryWriteBytes({expression}, {BridgeTypeNames.Number(type.MaximumBytes)})", onFail);
                return;
            case BridgeTag.Handle:
                Guard(
                    source,
                    indent,
                    payload
                        ? $"writer.TryWriteHandle({BridgeTypeNames.Number(type.TypeId)}UL, ({expression}).ObjectId)"
                        : $"writer.TryWriteHandle({BridgeTypeNames.Number(type.TypeId)}UL, {expression})",
                    onFail);
                return;
            case BridgeTag.Data:
                Guard(
                    source,
                    indent,
                    $"{BridgeNames.Codec(contract)}.TryWrite{BridgeNames.Value(contract.DataById[type.TypeId])}(ref writer, {expression})",
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
        string list = "list" + BridgeTypeNames.Number(id);
        string count = "count" + BridgeTypeNames.Number(id);
        string scope = "scope" + BridgeTypeNames.Number(id);
        string loop = "index" + BridgeTypeNames.Number(id);
        string item = "item" + BridgeTypeNames.Number(id);
        source.Append(indent).Append("var ").Append(list).Append(" = ").Append(expression).AppendLine(";");
        source.Append(indent).Append("int ").Append(count).Append(" = ").Append(list).AppendLine(".Count;");
        Guard(
            source,
            indent,
            $"writer.TryBeginList({count}, {BridgeTypeNames.Tag}.{TagName(type.Element!.Tag)}, {BridgeTypeNames.Number(type.MaximumCount)}, out int {scope})",
            onFail);
        source.Append(indent).Append("for (int ").Append(loop).Append(" = 0; ").Append(loop).Append(" < ").Append(count)
            .Append("; ").Append(loop).AppendLine("++)");
        source.Append(indent).AppendLine("{");
        source.Append(indent).Append("    var ").Append(item).Append(" = ").Append(list).Append('[').Append(loop).AppendLine("];");
        EmitWrite(source, contract, type.Element!, item, indent + "    ", onFail, payload, ref temp);
        source.Append(indent).AppendLine("}");
        Guard(source, indent, $"writer.TryEndList({scope})", onFail);
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
            string tag = "tag" + BridgeTypeNames.Number(id);
            Guard(source, indent, $"reader.TryPeekTag(out byte {tag})", onFail);
            source.Append(indent).Append("if (").Append(tag).Append(" == ").Append(BridgeTypeNames.Tag).AppendLine(".Null)");
            source.Append(indent).AppendLine("{");
            Guard(source, indent + "    ", "reader.TryReadNull()", onFail);
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
        string local = "value" + BridgeTypeNames.Number(id);
        switch (type.Tag)
        {
            case BridgeTag.Null:
                Guard(source, indent, "reader.TryReadNull()", onFail);
                return;
            case BridgeTag.Bool:
                Guard(source, indent, $"reader.TryReadBool(out bool {local})", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Int32:
                Guard(source, indent, $"reader.TryReadInt32(out int {local})", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Int64:
                Guard(source, indent, $"reader.TryReadInt64(out long {local})", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Double:
                Guard(source, indent, $"reader.TryReadDouble(out double {local})", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Guid:
                Guard(source, indent, $"reader.TryReadGuid(out global::System.Guid {local})", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Enum32:
            {
                string enumType = type.Symbol!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                Guard(source, indent, $"reader.TryReadEnum32(out int {local})", onFail);
                Guard(source, indent, $"{BridgeNames.Codec(contract)}.IsDefined{Sanitize(enumType)}({local})", onFail);
                source.Append(indent).Append(target).Append(" = (").Append(enumType).Append(')').Append(local).AppendLine(";");
                return;
            }

            case BridgeTag.Utf8String:
                Guard(source, indent, $"reader.TryReadString({BridgeTypeNames.Number(type.MaximumBytes)}, out string? {local})", onFail);
                Guard(source, indent, $"{local} is not null", onFail);
                Assign(source, indent, target, local);
                return;
            case BridgeTag.Bytes:
                Guard(source, indent, $"reader.TryReadBytes({BridgeTypeNames.Number(type.MaximumBytes)}, out global::System.ReadOnlySpan<byte> {local})", onFail);
                source.Append(indent).Append(target).Append(" = ").Append(local).AppendLine(".ToArray();");
                return;
            case BridgeTag.Handle:
                Guard(source, indent, $"reader.TryReadHandle({BridgeTypeNames.Number(type.TypeId)}UL, out ulong {local})", onFail);
                Guard(source, indent, $"{local} != 0UL", onFail);
                if (payload)
                {
                    source.Append(indent).Append(target).Append(" = client.Resolve")
                        .Append(BridgeNames.Wrapper(contract.Objects.Find(model => model.Id == type.TypeId)!))
                        .Append('(').Append(local).AppendLine(");");
                }
                else
                {
                    Assign(source, indent, target, local);
                }

                return;
            case BridgeTag.Data:
            {
                string valueType = BridgeNames.Value(contract.DataById[type.TypeId]);
                Guard(
                    source,
                    indent,
                    $"{BridgeNames.Codec(contract)}.TryRead{valueType}(ref reader, out {valueType}? {local})",
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
        string count = "count" + BridgeTypeNames.Number(id);
        string element = "element" + BridgeTypeNames.Number(id);
        string buffer = "buffer" + BridgeTypeNames.Number(id);
        string loop = "index" + BridgeTypeNames.Number(id);
        string item = "item" + BridgeTypeNames.Number(id);
        string elementType = BridgeTypeNames.Full(type.Element!, contract, payload);
        Guard(
            source,
            indent,
            $"reader.TryReadListHeader({BridgeTypeNames.Number(type.MaximumCount)}, out int {count}, out byte {element})",
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
        Guard(source, indent, "reader.TryEndContainer()", onFail);
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
                .Append(BridgeNames.Identifier(Camel(field.Name)));
        }

        source.AppendLine(")");
        source.AppendLine("    {");
        foreach (BridgeFieldModel field in model.Fields)
        {
            source.Append("        ").Append(BridgeNames.Identifier(field.Name)).Append(" = ")
                .Append(BridgeNames.Identifier(Camel(field.Name))).AppendLine(";");
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
            .Append(" writer, ").Append(name).AppendLine("? value)");
        source.AppendLine("    {");
        source.AppendLine("        if (value is null) { return writer.TryWriteNull(); }");
        source.Append("        if (!writer.TryBeginData(").Append(BridgeTypeNames.Number(model.Id)).Append("UL, ")
            .Append(BridgeTypeNames.Number(model.Fields.Count)).AppendLine(", out int scope)) { return false; }");
        foreach (BridgeFieldModel field in model.Fields)
        {
            source.Append("        if (!writer.TryWriteFieldOrdinal(").Append(BridgeTypeNames.Number(field.Ordinal))
                .AppendLine("U)) { return false; }");
            BridgeCodecEmitter.EmitWrite(
                source, contract, field.Type, "value." + BridgeNames.Identifier(field.Name), "        ", "return false;", payload: true, ref temp);
        }

        source.AppendLine("        return writer.TryEndData(scope);");
        source.AppendLine("    }");
        source.AppendLine();

        temp = 0;
        source.Append("    internal static bool TryRead").Append(name).Append("(ref ").Append(BridgeTypeNames.Reader)
            .Append(" reader, out ").Append(name).AppendLine("? value)");
        source.AppendLine("    {");
        source.AppendLine("        value = null;");
        source.AppendLine("        if (!reader.TryPeekTag(out byte tag)) { return false; }");
        source.Append("        if (tag == ").Append(BridgeTypeNames.Tag).AppendLine(".Null) { return reader.TryReadNull(); }");
        source.Append("        if (!reader.TryReadDataHeader(").Append(BridgeTypeNames.Number(model.Id)).AppendLine("UL, out int fieldCount)) { return false; }");
        source.Append("        if (fieldCount != ").Append(BridgeTypeNames.Number(model.Fields.Count)).AppendLine(") { return false; }");
        var locals = new List<string>();
        for (int index = 0; index < model.Fields.Count; index++)
        {
            BridgeFieldModel field = model.Fields[index];
            string local = "field" + BridgeTypeNames.Number(index);
            locals.Add(local);
            source.Append("        ").Append(BridgeTypeNames.Full(field.Type, contract, payload: true)).Append(' ')
                .Append(local).AppendLine(" = default!;");
            source.Append("        if (!reader.TryReadFieldOrdinal(").Append(BridgeTypeNames.Number(field.Ordinal))
                .AppendLine("U)) { return false; }");
            BridgeCodecEmitter.EmitRead(
                source, contract, field.Type, local, "        ", "return false;", payload: true, ref temp);
        }

        source.AppendLine("        if (!reader.TryEndContainer()) { return false; }");
        source.Append("        value = new ").Append(name).Append('(').Append(string.Join(", ", locals)).AppendLine(");");
        source.AppendLine("        return true;");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static string Camel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);
}
