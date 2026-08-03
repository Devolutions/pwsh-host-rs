#nullable enable

using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Devolutions.MultiPwsh.LiveContract.Generator;

/// <summary>
/// Turns an annotated declaration into a fully resolved <see cref="BridgeContractModel"/>
/// or into actionable diagnostics. Nothing that is not explicitly accepted here
/// reaches emission.
/// </summary>
internal static class BridgeContractAnalyzer
{
    internal const string ContractAttribute = "Devolutions.PowerShell.Ffi.LiveObjects.BridgeContractAttribute";
    internal const string ObjectAttribute = "Devolutions.PowerShell.Ffi.LiveObjects.BridgeObjectAttribute";
    internal const string MemberAttribute = "Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberAttribute";
    internal const string EventAttribute = "Devolutions.PowerShell.Ffi.LiveObjects.BridgeEventAttribute";
    internal const string ReliableEventAttribute = "Devolutions.PowerShell.Ffi.LiveObjects.BridgeReliableEventAttribute";
    internal const string DataAttribute = "Devolutions.PowerShell.Ffi.LiveObjects.BridgeDataAttribute";
    internal const string FieldAttribute = "Devolutions.PowerShell.Ffi.LiveObjects.BridgeFieldAttribute";
    internal const string BoundAttribute = "Devolutions.PowerShell.Ffi.LiveObjects.BridgeBoundAttribute";
    internal const string EnumAttribute = "Devolutions.PowerShell.Ffi.LiveObjects.BridgeEnumAttribute";
    internal const string ComInterfaceAttribute = "System.Runtime.InteropServices.Marshalling.GeneratedComInterfaceAttribute";
    internal const string GuidAttribute = "System.Runtime.InteropServices.GuidAttribute";
    internal const string FlagsAttribute = "System.FlagsAttribute";

    private enum Position
    {
        Member,
        ListElement,
        DataField,
        DataFieldListElement,
    }

    private sealed class Bounds
    {
        internal int MaximumUtf8Bytes;
        internal int MaximumByteCount;
        internal int MaximumCollectionCount;
        internal ulong ResultObjectId;
    }

    /// <summary>
    /// The allow-list types resolved by symbol rather than by name. A closed
    /// allow-list validated by string matching is not closed: a namespace leaf
    /// comparison accepts <c>Acme.System.Guid</c>, and emission would then
    /// silently substitute the real type.
    /// </summary>
    private sealed class WellKnownTypes
    {
        internal WellKnownTypes(Compilation compilation)
        {
            Guid = compilation.GetTypeByMetadataName("System.Guid");
            ReadOnlyList = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1");
        }

        internal INamedTypeSymbol? Guid { get; }

        internal INamedTypeSymbol? ReadOnlyList { get; }

        internal bool IsGuid(ITypeSymbol type) =>
            Guid is not null && SymbolEqualityComparer.Default.Equals(type, Guid);

        internal bool TryGetListElement(ITypeSymbol type, out ITypeSymbol? element)
        {
            element = null;
            if (ReadOnlyList is null ||
                type is not INamedTypeSymbol { TypeArguments.Length: 1 } named ||
                !SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, ReadOnlyList))
            {
                return false;
            }

            element = named.TypeArguments[0];
            return true;
        }
    }

    /// <summary>
    /// Names the generator reserves for its own locals. Declaring one would make
    /// generated code fail with a raw compiler error instead of a diagnostic.
    /// </summary>
    private const string ReservedPrefix = "__bridge";

    private static bool IsReservedName(string name) =>
        name.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase);

    internal static BridgeContractModel? Analyze(
        INamedTypeSymbol root,
        IReadOnlyList<INamedTypeSymbol> declarations,
        Compilation compilation,
        List<Diagnostic> diagnostics)
    {
        var well = new WellKnownTypes(compilation);
        bool valid = true;
        AttributeData? contractAttribute = GetAttribute(root, ContractAttribute);
        if (contractAttribute is null ||
            contractAttribute.ConstructorArguments.Length != 4 ||
            contractAttribute.ConstructorArguments[0].Value is not string contractId ||
            string.IsNullOrWhiteSpace(contractId) ||
            contractAttribute.ConstructorArguments[1].Value is not int major ||
            major < 1 || major > ushort.MaxValue ||
            contractAttribute.ConstructorArguments[2].Value is not int minor ||
            minor < 0 || minor > ushort.MaxValue ||
            contractAttribute.ConstructorArguments[3].Value is not string transportId ||
            !Guid.TryParse(transportId, out Guid transportGuid) ||
            transportGuid == Guid.Empty)
        {
            diagnostics.Add(Diagnostic.Create(
                BridgeContractDiagnostics.InvalidContract,
                Location(root),
                root.Name,
                "declare a non-empty contract identity, a major version in 1..65535, a minor version in 0..65535, and a non-empty transport interface IID"));
            return null;
        }

        if (root.ContainingType is not null)
        {
            diagnostics.Add(Diagnostic.Create(
                BridgeContractDiagnostics.InvalidContract, Location(root), root.Name, "nested interfaces are not supported"));
            valid = false;
        }

        INamedTypeSymbol? transport = FindTransportInterface(declarations, transportGuid);
        if (transport is null)
        {
            diagnostics.Add(Diagnostic.Create(
                BridgeContractDiagnostics.InvalidContract,
                Location(root),
                root.Name,
                $"declare a partial [GeneratedComInterface] interface with [Guid(\"{transportGuid:D}\")] and the required Invoke/CloseLease shape; a source generator cannot see another generator's output, so the COM declaration must exist in source"));
            valid = false;
        }

        var ordinals = new Dictionary<uint, string>();
        List<BridgeEnumModel> enums = AnalyzeEnums(declarations, diagnostics, ref valid);
        List<BridgeDataModel> data = AnalyzeData(declarations, enums, well, diagnostics, ref valid);
        List<BridgeObjectModel> objects = AnalyzeObjects(declarations, ordinals, diagnostics, ref valid);

        if (objects.Count > BridgeLimits.MaximumObjects)
        {
            diagnostics.Add(Diagnostic.Create(
                BridgeContractDiagnostics.ExceededLimit,
                Location(root),
                root.Name,
                $"a contract declares at most {BridgeLimits.MaximumObjects} bridge objects"));
            valid = false;
        }

        if (!objects.Any(model => SymbolEqualityComparer.Default.Equals(model.Symbol, root)))
        {
            diagnostics.Add(Diagnostic.Create(
                BridgeContractDiagnostics.InvalidContract, Location(root), root.Name, "the root must also declare a non-zero [BridgeObject] identifier"));
            return null;
        }

        foreach (BridgeObjectModel model in objects)
        {
            AnalyzeMembers(model, objects, data, enums, well, ordinals, diagnostics, ref valid);
        }

        if (!valid)
        {
            return null;
        }

        var contract = new BridgeContractModel(
            root,
            root.ContainingNamespace.IsGlobalNamespace ? string.Empty : root.ContainingNamespace.ToDisplayString(),
            contractId,
            major,
            minor,
            transportGuid.ToString("D").ToUpperInvariant(),
            transport!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            objects,
            data,
            enums);

        if (!ValidateDataGraph(contract, diagnostics) ||
            !ValidateGraph(contract, diagnostics) ||
            !ValidateBudget(contract, diagnostics))
        {
            return null;
        }

        BridgeContractDescriptor.Compute(contract);
        return contract;
    }

    private static INamedTypeSymbol? FindTransportInterface(IReadOnlyList<INamedTypeSymbol> declarations, Guid id)
    {
        foreach (INamedTypeSymbol candidate in declarations)
        {
            if (candidate.TypeKind != TypeKind.Interface ||
                !HasAttribute(candidate, ComInterfaceAttribute) ||
                GetAttribute(candidate, GuidAttribute) is not { ConstructorArguments.Length: 1 } guid ||
                guid.ConstructorArguments[0].Value is not string text ||
                !Guid.TryParse(text, out Guid parsed) ||
                parsed != id)
            {
                continue;
            }

            if (HasTransportShape(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasTransportShape(INamedTypeSymbol candidate)
    {
        IMethodSymbol? invoke = candidate.GetMembers("Invoke").OfType<IMethodSymbol>().FirstOrDefault();
        IMethodSymbol? close = candidate.GetMembers("CloseLease").OfType<IMethodSymbol>().FirstOrDefault();
        if (invoke is null || close is null ||
            invoke.ReturnType.SpecialType != SpecialType.System_Int32 ||
            close.ReturnType.SpecialType != SpecialType.System_Int32 ||
            invoke.Parameters.Length != 9 ||
            close.Parameters.Length != 2)
        {
            return false;
        }

        SpecialType[] expected =
        [
            SpecialType.System_UInt64,
            SpecialType.System_UInt32,
            SpecialType.System_UInt64,
            SpecialType.System_UInt32,
            SpecialType.System_IntPtr,
            SpecialType.System_Int32,
            SpecialType.System_IntPtr,
            SpecialType.System_Int32,
            SpecialType.System_Int32,
        ];
        for (int index = 0; index < expected.Length; index++)
        {
            if (invoke.Parameters[index].Type.SpecialType != expected[index])
            {
                return false;
            }
        }

        return invoke.Parameters[8].RefKind == RefKind.Out &&
            close.Parameters[0].Type.SpecialType == SpecialType.System_UInt64 &&
            close.Parameters[1].Type.SpecialType == SpecialType.System_UInt32;
    }

    private static List<BridgeEnumModel> AnalyzeEnums(
        IReadOnlyList<INamedTypeSymbol> declarations,
        List<Diagnostic> diagnostics,
        ref bool valid)
    {
        var models = new List<BridgeEnumModel>();
        var ids = new HashSet<ulong>();
        foreach (INamedTypeSymbol symbol in declarations.Where(candidate => HasAttribute(candidate, EnumAttribute)))
        {
            AttributeData attribute = GetAttribute(symbol, EnumAttribute)!;
            if (symbol.TypeKind != TypeKind.Enum ||
                attribute.ConstructorArguments.Length != 1 ||
                attribute.ConstructorArguments[0].Value is not ulong id ||
                id == 0 ||
                !ids.Add(id))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEnum, symbol, "declare a globally unique, non-zero enumeration identifier on an enum");
                continue;
            }

            if (symbol.EnumUnderlyingType?.SpecialType != SpecialType.System_Int32)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEnum, symbol, "the underlying type must be int");
                continue;
            }

            if (HasAttribute(symbol, FlagsAttribute))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEnum, symbol, "[Flags] is not supported because a combined value equals no declared member, so the closed allow-list cannot validate it");
                continue;
            }

            var model = new BridgeEnumModel(symbol, id);
            var values = new HashSet<int>();
            bool memberValid = true;
            foreach (IFieldSymbol field in symbol.GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue))
            {
                if (field.ConstantValue is not int value || !values.Add(value))
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEnum, field, "enumeration members must have distinct int values and no aliases");
                    memberValid = false;
                    continue;
                }

                model.Members.Add(new BridgeEnumMemberModel(field.Name, value));
            }

            if (!memberValid)
            {
                continue;
            }

            if (model.Members.Count == 0 || model.Members.Count > BridgeLimits.MaximumEnumMembers)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEnum, symbol, $"declare between 1 and {BridgeLimits.MaximumEnumMembers} members");
                continue;
            }

            model.Members.Sort(static (left, right) => left.Value.CompareTo(right.Value));
            models.Add(model);
        }

        models.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return models;
    }

    private static List<BridgeDataModel> AnalyzeData(
        IReadOnlyList<INamedTypeSymbol> declarations,
        List<BridgeEnumModel> enums,
        WellKnownTypes well,
        List<Diagnostic> diagnostics,
        ref bool valid)
    {
        var models = new List<BridgeDataModel>();
        var ids = new HashSet<ulong>();
        foreach (INamedTypeSymbol symbol in declarations.Where(candidate => HasAttribute(candidate, DataAttribute)))
        {
            AttributeData attribute = GetAttribute(symbol, DataAttribute)!;
            if (symbol.TypeKind != TypeKind.Interface ||
                symbol.ContainingType is not null ||
                symbol.TypeParameters.Length != 0 ||
                symbol.Interfaces.Length != 0 ||
                attribute.ConstructorArguments.Length != 1 ||
                attribute.ConstructorArguments[0].Value is not ulong id ||
                id == 0 ||
                !ids.Add(id))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidData, symbol, "declare a globally unique, non-zero identifier on a non-nested, non-generic interface with no base interfaces");
                continue;
            }

            models.Add(new BridgeDataModel(symbol, id));
        }

        models.Sort(static (left, right) => left.Id.CompareTo(right.Id));

        // Fields are resolved after every data identifier is known, so a field may
        // reference any declared enumeration and no ordering dependency exists.
        foreach (BridgeDataModel model in models)
        {
            var ordinals = new HashSet<uint>();
            foreach (ISymbol member in model.Symbol.GetMembers())
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet })
                {
                    continue;
                }

                if (member is not IPropertySymbol property)
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidData, member, "a data contract declares only get-only properties");
                    continue;
                }

                AttributeData? field = GetAttribute(property, FieldAttribute);
                if (field is null ||
                    field.ConstructorArguments.Length != 1 ||
                    field.ConstructorArguments[0].Value is not uint ordinal ||
                    ordinal == 0 ||
                    !ordinals.Add(ordinal))
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidData, member, "every data property requires [BridgeField] with a non-zero ordinal unique within its data contract");
                    continue;
                }

                if (property.IsIndexer || property.IsStatic || property.SetMethod is not null || property.GetMethod is null)
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidData, member, "data fields are instance get-only properties and never indexers");
                    continue;
                }

                var bounds = new Bounds
                {
                    MaximumUtf8Bytes = GetNamedInt(field, "MaximumUtf8Bytes"),
                    MaximumByteCount = GetNamedInt(field, "MaximumByteCount"),
                    MaximumCollectionCount = GetNamedInt(field, "MaximumCollectionCount"),
                    ResultObjectId = GetNamedULong(field, "ResultObjectId"),
                };
                BridgeTypeRef? type = ResolveType(
                    property.Type, bounds, Position.DataField, models, enums, well, member, diagnostics, ref valid);
                if (type is not null)
                {
                    model.Fields.Add(new BridgeFieldModel(member, member.Name, ordinal, type));
                }
            }

            if (model.Fields.Count == 0 || model.Fields.Count > BridgeLimits.MaximumDataFields)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidData, model.Symbol, $"declare between 1 and {BridgeLimits.MaximumDataFields} fields");
                continue;
            }

            model.Fields.Sort(static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        }

        return models;
    }

    private static List<BridgeObjectModel> AnalyzeObjects(
        IReadOnlyList<INamedTypeSymbol> declarations,
        Dictionary<uint, string> ordinals,
        List<Diagnostic> diagnostics,
        ref bool valid)
    {
        var models = new List<BridgeObjectModel>();
        var ids = new HashSet<ulong>();
        foreach (INamedTypeSymbol symbol in declarations.Where(candidate => HasAttribute(candidate, ObjectAttribute)))
        {
            AttributeData attribute = GetAttribute(symbol, ObjectAttribute)!;
            if (symbol.TypeKind != TypeKind.Interface ||
                symbol.ContainingType is not null ||
                symbol.TypeParameters.Length != 0 ||
                attribute.ConstructorArguments.Length != 1 ||
                attribute.ConstructorArguments[0].Value is not ulong id ||
                id == 0 ||
                !ids.Add(id))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidObject, symbol, "declare a globally unique, non-zero identifier on a non-nested, non-generic interface");
                continue;
            }

            if (symbol.Interfaces.Length != 0)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidObject, symbol, "interface inheritance across the contract boundary is not supported");
                continue;
            }

            uint releaseId = GetNamedUInt(attribute, "ReleaseId");
            if (releaseId == 0 || ordinals.ContainsKey(releaseId))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidRelease, symbol, "declare a non-zero ReleaseId that is unique across every ordinal in the contract");
                continue;
            }

            ordinals.Add(releaseId, symbol.Name + ".Release");
            models.Add(new BridgeObjectModel(symbol, id, releaseId));
        }

        models.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return models;
    }

    private static void AnalyzeMembers(
        BridgeObjectModel owner,
        List<BridgeObjectModel> objects,
        List<BridgeDataModel> data,
        List<BridgeEnumModel> enums,
        WellKnownTypes well,
        Dictionary<uint, string> ordinals,
        List<Diagnostic> diagnostics,
        ref bool valid)
    {
        foreach (ISymbol member in owner.Symbol.GetMembers())
        {
            if (member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet })
            {
                continue;
            }

            if (member is IEventSymbol)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidMember, member, "CLR events are not supported; declare a [BridgeEvent] void method so no delegate crosses the boundary");
                continue;
            }

            if (member.IsStatic)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidMember, member, "static members are not supported");
                continue;
            }

            if (IsReservedName(member.Name))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidMember, member, $"member '{member.Name}' uses the reserved '{ReservedPrefix}' prefix, which the generator uses for its own locals");
                continue;
            }

            if (member is IPropertySymbol property)
            {
                AnalyzeProperty(owner, property, objects, data, enums, well, ordinals, diagnostics, ref valid);
                continue;
            }

            if (member is IMethodSymbol method)
            {
                AnalyzeMethod(owner, method, objects, data, enums, well, ordinals, diagnostics, ref valid);
                continue;
            }

            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidMember, member, "only properties, methods, and [BridgeEvent] methods are supported");
        }

        if (owner.Members.Count > BridgeLimits.MaximumMembersPerObject)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.ExceededLimit, owner.Symbol, $"an object declares at most {BridgeLimits.MaximumMembersPerObject} member records");
        }

        owner.Members.Sort(static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
    }

    private static void AnalyzeProperty(
        BridgeObjectModel owner,
        IPropertySymbol property,
        List<BridgeObjectModel> objects,
        List<BridgeDataModel> data,
        List<BridgeEnumModel> enums,
        WellKnownTypes well,
        Dictionary<uint, string> ordinals,
        List<Diagnostic> diagnostics,
        ref bool valid)
    {
        AttributeData? attribute = GetAttribute(property, MemberAttribute);
        if (attribute is null ||
            attribute.ConstructorArguments.Length != 1 ||
            attribute.ConstructorArguments[0].Value is not uint ordinal ||
            ordinal == 0 ||
            ordinals.ContainsKey(ordinal))
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidMember, property, "every property requires [BridgeMember] with a non-zero ordinal unique across the whole contract");
            return;
        }

        if (property.GetMethod is null)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidMember, property, "write-only properties are not supported");
            return;
        }

        var bounds = new Bounds
        {
            MaximumUtf8Bytes = GetNamedInt(attribute, "MaximumUtf8Bytes"),
            MaximumByteCount = GetNamedInt(attribute, "MaximumByteCount"),
            MaximumCollectionCount = GetNamedInt(attribute, "MaximumCollectionCount"),
            ResultObjectId = GetNamedULong(attribute, "ResultObjectId"),
        };
        byte permission = GetNamedByte(attribute, "Permission");
        byte mutation = GetNamedByte(attribute, "Mutation");
        ulong errorDataId = GetNamedULong(attribute, "ErrorDataId");
        if (permission == 0)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidAuthorization, property, "declare an explicit Permission; it is an input to the authorizer and never a substitute for it");
            return;
        }

        if (mutation != 0)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidAuthorization, property, "a getter must declare Mutation.None; use SetterMutation for the setter");
            return;
        }

        if (errorDataId != 0 && !data.Any(model => model.Id == errorDataId))
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidData, property, "ErrorDataId does not resolve to a declared [BridgeData] contract");
            return;
        }

        if (property.IsIndexer)
        {
            AnalyzeIndexer(owner, property, ordinal, bounds, permission, errorDataId, objects, data, enums, well, ordinals, diagnostics, ref valid);
            return;
        }

        BridgeTypeRef? type = ResolveType(property.Type, bounds, Position.Member, data, enums, well, property, diagnostics, ref valid);
        if (type is null)
        {
            return;
        }

        if (type.Tag == BridgeTag.Handle && !objects.Any(model => model.Id == type.TypeId))
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.UnresolvedObject, property, "the declared ResultObjectId does not match a declared [BridgeObject]");
            return;
        }

        ordinals.Add(ordinal, owner.Name + "." + property.Name);
        owner.Members.Add(new BridgeMemberModel(
            owner, property, property.Name, ordinal, BridgeRecordKind.Getter, 0, permission, errorDataId, 0, type, []));

        uint setterId = GetNamedUInt(attribute, "SetterId");
        if (property.SetMethod is null)
        {
            if (setterId != 0)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidSetter, property, "a getter-only property cannot declare SetterId");
            }

            return;
        }

        if (setterId == 0 || ordinals.ContainsKey(setterId))
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidSetter, property, "a mutable property requires a non-zero SetterId unique across the whole contract");
            return;
        }

        byte setterPermission = GetNamedByte(attribute, "SetterPermission");
        byte setterMutation = GetNamedByte(attribute, "SetterMutation");
        if (setterPermission is 0 or 1)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidAuthorization, property, "declare SetterPermission as Write or Execute; a read permission can never authorize a setter");
            return;
        }

        if (setterMutation == 0)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidAuthorization, property, "declare SetterMutation as Direct or Staged");
            return;
        }

        if (setterMutation == 2)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidAuthorization, property, "SetterMutation.Staged is rejected until the staged-intent coordinator exposes a programmatic stage/validate/commit entry point; declare Direct or remove the setter");
            return;
        }

        if (type.Tag is BridgeTag.List or BridgeTag.Handle)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidSetter, property, "collection and handle properties are read-only; mutate them through a declared method");
            return;
        }

        ordinals.Add(setterId, owner.Name + "." + property.Name + ".set");
        owner.Members.Add(new BridgeMemberModel(
            owner,
            property,
            property.Name,
            setterId,
            BridgeRecordKind.Setter,
            setterMutation,
            setterPermission,
            errorDataId,
            0,
            new BridgeTypeRef(BridgeTag.Null, null),
            [new BridgeParameterModel("value", type)]));
    }

    private static void AnalyzeIndexer(
        BridgeObjectModel owner,
        IPropertySymbol property,
        uint ordinal,
        Bounds bounds,
        byte permission,
        ulong errorDataId,
        List<BridgeObjectModel> objects,
        List<BridgeDataModel> data,
        List<BridgeEnumModel> enums,
        WellKnownTypes well,
        Dictionary<uint, string> ordinals,
        List<Diagnostic> diagnostics,
        ref bool valid)
    {
        if (property.SetMethod is not null ||
            property.Parameters.Length != 1 ||
            property.Parameters[0].Type.SpecialType != SpecialType.System_Int32 ||
            !IsValidCollectionBound(bounds.MaximumCollectionCount))
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.MissingBound, property, $"an indexer must be get-only, take one int, and declare MaximumCollectionCount between 1 and {BridgeLimits.MaximumCollectionCount}");
            return;
        }

        BridgeTypeRef? element = ResolveType(property.Type, bounds, Position.Member, data, enums, well, property, diagnostics, ref valid);
        if (element is null)
        {
            return;
        }

        if (element.Tag == BridgeTag.List)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.UnsupportedType, property, "an indexer element cannot itself be a collection");
            return;
        }

        if (element.Tag == BridgeTag.Handle && !objects.Any(model => model.Id == element.TypeId))
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.UnresolvedObject, property, "the declared ResultObjectId does not match a declared [BridgeObject]");
            return;
        }

        ordinals.Add(ordinal, owner.Name + ".this[]");
        owner.Members.Add(new BridgeMemberModel(
            owner,
            property,
            "get_Item",
            ordinal,
            BridgeRecordKind.Method,
            0,
            permission,
            errorDataId,
            0,
            element,
            [new BridgeParameterModel("index", new BridgeTypeRef(BridgeTag.Int32, property.Parameters[0].Type))]));
    }

    private static void AnalyzeMethod(
        BridgeObjectModel owner,
        IMethodSymbol method,
        List<BridgeObjectModel> objects,
        List<BridgeDataModel> data,
        List<BridgeEnumModel> enums,
        WellKnownTypes well,
        Dictionary<uint, string> ordinals,
        List<Diagnostic> diagnostics,
        ref bool valid)
    {
        AttributeData? eventAttribute = GetAttribute(method, EventAttribute);
        AttributeData? reliableEventAttribute = GetAttribute(method, ReliableEventAttribute);
        AttributeData? memberAttribute = GetAttribute(method, MemberAttribute);
        if ((eventAttribute is not null && reliableEventAttribute is not null) ||
            ((eventAttribute is not null || reliableEventAttribute is not null) && memberAttribute is not null))
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEvent, method, "a method declares exactly one of [BridgeMember], [BridgeEvent], or [BridgeReliableEvent]");
            return;
        }

        AttributeData? attribute = reliableEventAttribute ?? eventAttribute ?? memberAttribute;
        if (attribute is null ||
            attribute.ConstructorArguments.Length != 1 ||
            attribute.ConstructorArguments[0].Value is not uint ordinal ||
            ordinal == 0 ||
            ordinals.ContainsKey(ordinal))
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidMember, method, "every method requires [BridgeMember], [BridgeEvent], or [BridgeReliableEvent] with a non-zero ordinal unique across the whole contract");
            return;
        }

        if (method.MethodKind != MethodKind.Ordinary || method.TypeParameters.Length != 0)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidMember, method, "generic and non-ordinary methods are not supported");
            return;
        }

        if (method.Parameters.Length > BridgeLimits.MaximumParameters)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.ExceededLimit, method, $"a method declares at most {BridgeLimits.MaximumParameters} parameters");
            return;
        }

        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.UnsupportedType, method, "ref, out, and in parameters are not supported");
                return;
            }

            if (IsReservedName(parameter.Name))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidMember, method, $"parameter '{parameter.Name}' uses the reserved '{ReservedPrefix}' prefix, which the generator uses for its own locals");
                return;
            }
        }

        var bounds = new Bounds
        {
            MaximumUtf8Bytes = GetNamedInt(attribute, "MaximumUtf8Bytes"),
            MaximumByteCount = GetNamedInt(attribute, "MaximumByteCount"),
            MaximumCollectionCount = GetNamedInt(attribute, "MaximumCollectionCount"),
            ResultObjectId = GetNamedULong(attribute, "ResultObjectId"),
        };

        var parameters = new List<BridgeParameterModel>();
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            AttributeData? bound = GetAttribute(parameter, BoundAttribute);
            var parameterBounds = new Bounds
            {
                MaximumUtf8Bytes = bound is null ? 0 : GetNamedInt(bound, "MaximumUtf8Bytes"),
                MaximumByteCount = bound is null ? 0 : GetNamedInt(bound, "MaximumByteCount"),
                MaximumCollectionCount = bound is null ? 0 : GetNamedInt(bound, "MaximumCollectionCount"),
                ResultObjectId = bound is null ? 0 : GetNamedULong(bound, "ResultObjectId"),
            };
            BridgeTypeRef? resolved = ResolveType(parameter.Type, parameterBounds, Position.Member, data, enums, well, method, diagnostics, ref valid);
            if (resolved is null)
            {
                return;
            }

            if (resolved.Tag == BridgeTag.Handle && !objects.Any(model => model.Id == resolved.TypeId))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.UnresolvedObject, method, "a handle parameter must declare [BridgeBound(ResultObjectId = ...)] matching a declared [BridgeObject]");
                return;
            }

            parameters.Add(new BridgeParameterModel(parameter.Name, resolved));
        }

        if (eventAttribute is not null || reliableEventAttribute is not null)
        {
            if (!method.ReturnsVoid)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEvent, method, "an event method returns void; events are one-way and accept no reply");
                return;
            }

            if (reliableEventAttribute is not null)
            {
                if (!method.ReturnsVoid)
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEvent, method, "a reliable event method returns void; it never replies into the pipeline");
                    return;
                }

                int maximumRetainedEvents = GetNamedInt(reliableEventAttribute, "MaximumRetainedEvents");
                if (maximumRetainedEvents is < 1 or > BridgeLimits.MaximumReliableEvents)
                {
                    Report(
                        diagnostics,
                        ref valid,
                        BridgeContractDiagnostics.MissingBound,
                        method,
                        $"a reliable event declares MaximumRetainedEvents between 1 and {BridgeLimits.MaximumReliableEvents}");
                    return;
                }

                byte reliablePermission = GetNamedByte(reliableEventAttribute, "Permission");
                if (reliablePermission == 0)
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidAuthorization, method, "declare an explicit Permission; it is an input to the authorizer and never a substitute for it");
                    return;
                }

                if (ordinal > ushort.MaxValue)
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEvent, method, "a reliable event ordinal must fit in 16 bits so it can form the broker frame kind");
                    return;
                }

                ordinals.Add(ordinal, owner.Name + "." + method.Name);
                var reliable = new BridgeMemberModel(
                    owner,
                    method,
                    method.Name,
                    ordinal,
                    BridgeRecordKind.ReliableEvent,
                    0,
                    reliablePermission,
                    0,
                    GetNamedULong(reliableEventAttribute, "OrderingKey"),
                    new BridgeTypeRef(BridgeTag.Null, null),
                    parameters)
                {
                    MaximumRetainedEvents = maximumRetainedEvents,
                };
                owner.Members.Add(reliable);
                return;
            }

            if (ordinal > ushort.MaxValue)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEvent, method, "an event ordinal must fit in 16 bits so it can form the broker frame kind");
                return;
            }

            ordinals.Add(ordinal, owner.Name + "." + method.Name);
            owner.Members.Add(new BridgeMemberModel(
                owner,
                method,
                method.Name,
                ordinal,
                BridgeRecordKind.Event,
                0,
                (byte)3,
                0,
                GetNamedULong(eventAttribute!, "OrderingKey"),
                new BridgeTypeRef(BridgeTag.Null, null),
                parameters));
            return;
        }

        byte permission = GetNamedByte(attribute, "Permission");
        byte mutation = GetNamedByte(attribute, "Mutation");
        ulong errorDataId = GetNamedULong(attribute, "ErrorDataId");
        if (permission == 0)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidAuthorization, method, "declare an explicit Permission; it is an input to the authorizer and never a substitute for it");
            return;
        }

        if (mutation != 0 && permission == 1)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidAuthorization, method, "a mutating method cannot declare a read permission");
            return;
        }

        if (mutation == 2)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidAuthorization, method, "Mutation.Staged is rejected until the staged-intent coordinator exposes a programmatic stage/validate/commit entry point; declare Direct or None");
            return;
        }

        if (errorDataId != 0 && !data.Any(model => model.Id == errorDataId))
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidData, method, "ErrorDataId does not resolve to a declared [BridgeData] contract");
            return;
        }

        BridgeTypeRef result;
        if (method.ReturnsVoid)
        {
            result = new BridgeTypeRef(BridgeTag.Null, null);
        }
        else
        {
            BridgeTypeRef? resolved = ResolveType(method.ReturnType, ReturnBounds(method, bounds), Position.Member, data, enums, well, method, diagnostics, ref valid);
            if (resolved is null)
            {
                return;
            }

            if (resolved.Tag == BridgeTag.Handle && !objects.Any(model => model.Id == resolved.TypeId))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.UnresolvedObject, method, "the declared ResultObjectId does not match a declared [BridgeObject]");
                return;
            }

            result = resolved;
        }

        ordinals.Add(ordinal, owner.Name + "." + method.Name);
        owner.Members.Add(new BridgeMemberModel(
            owner, method, method.Name, ordinal, BridgeRecordKind.Method, mutation, permission, errorDataId, 0, result, parameters));
    }

    private static BridgeTypeRef? ResolveType(
        ITypeSymbol type,
        Bounds bounds,
        Position position,
        List<BridgeDataModel> data,
        List<BridgeEnumModel> enums,
        WellKnownTypes well,
        ISymbol owner,
        List<Diagnostic> diagnostics,
        ref bool valid)
    {
        bool nullable = false;
        ITypeSymbol resolved = type;
        if (resolved is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableValue)
        {
            nullable = true;
            resolved = nullableValue.TypeArguments[0];
        }
        else if (resolved.IsReferenceType)
        {
            switch (resolved.NullableAnnotation)
            {
                case NullableAnnotation.Annotated:
                    nullable = true;
                    break;
                case NullableAnnotation.None:
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.UnsupportedType, owner, "declare the contract in an enabled nullable context; an unannotated reference type would make the descriptor depend on project settings and break Host/Payload hash parity");
                    return null;
                default:
                    break;
            }
        }

        if (DescribeBannedType(resolved) is { } banned)
        {
            Report(diagnostics, ref valid, BridgeContractDiagnostics.UnsupportedType, owner, banned);
            return null;
        }

        BridgeTypeRef? result = ResolveCore(resolved, bounds, position, data, enums, well, owner, diagnostics, ref valid);
        if (result is not null)
        {
            result.IsNullable = nullable;
        }

        return result;
    }

    private static BridgeTypeRef? ResolveCore(
        ITypeSymbol type,
        Bounds bounds,
        Position position,
        List<BridgeDataModel> data,
        List<BridgeEnumModel> enums,
        WellKnownTypes well,
        ISymbol owner,
        List<Diagnostic> diagnostics,
        ref bool valid)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                return new BridgeTypeRef(BridgeTag.Bool, type);
            case SpecialType.System_Int32:
                return new BridgeTypeRef(BridgeTag.Int32, type);
            case SpecialType.System_Int64:
                return new BridgeTypeRef(BridgeTag.Int64, type);
            case SpecialType.System_Double:
                return new BridgeTypeRef(BridgeTag.Double, type);
            case SpecialType.System_String:
                if (!IsValidByteBound(bounds.MaximumUtf8Bytes))
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.MissingBound, owner, $"a string position requires MaximumUtf8Bytes between 1 and {BridgeLimits.MaximumUtf8Bytes}");
                    return null;
                }

                return new BridgeTypeRef(BridgeTag.Utf8String, type) { MaximumBytes = bounds.MaximumUtf8Bytes };
            default:
                break;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            BridgeEnumModel? model = enums.Find(candidate => SymbolEqualityComparer.Default.Equals(candidate.Symbol, type));
            if (model is null)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidEnum, owner, "an enumeration position requires a declared [BridgeEnum] type");
                return null;
            }

            return new BridgeTypeRef(BridgeTag.Enum32, type) { TypeId = model.Id };
        }

        if (type is IArrayTypeSymbol array)
        {
            if (array.Rank != 1 || array.ElementType.SpecialType != SpecialType.System_Byte)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.UnsupportedType, owner, "arrays are not supported; the only accepted array is byte[] in an opaque bytes position");
                return null;
            }

            if (!IsValidByteBound(bounds.MaximumByteCount))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.MissingBound, owner, $"a byte[] position requires MaximumByteCount between 1 and {BridgeLimits.MaximumUtf8Bytes}");
                return null;
            }

            return new BridgeTypeRef(BridgeTag.Bytes, type) { MaximumBytes = bounds.MaximumByteCount };
        }

        if (well.IsGuid(type))
        {
            return new BridgeTypeRef(BridgeTag.Guid, type);
        }

        if (well.TryGetListElement(type, out ITypeSymbol? listElement))
        {
            if (position is Position.ListElement or Position.DataFieldListElement)
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.UnsupportedType, owner, "a collection element cannot itself be a collection");
                return null;
            }

            if (!IsValidCollectionBound(bounds.MaximumCollectionCount))
            {
                Report(diagnostics, ref valid, BridgeContractDiagnostics.MissingBound, owner, $"a collection position requires MaximumCollectionCount between 1 and {BridgeLimits.MaximumCollectionCount}");
                return null;
            }

            Position elementPosition = position == Position.DataField ? Position.DataFieldListElement : Position.ListElement;
            BridgeTypeRef? resolvedElement = ResolveType(listElement!, bounds, elementPosition, data, enums, well, owner, diagnostics, ref valid);
            if (resolvedElement is null)
            {
                return null;
            }

            return new BridgeTypeRef(BridgeTag.List, type)
            {
                MaximumCount = bounds.MaximumCollectionCount,
                Element = resolvedElement,
            };
        }

        if (type is INamedTypeSymbol named)
        {
            if (named.TypeKind == TypeKind.Interface && HasAttribute(named, DataAttribute))
            {
                if (position == Position.DataField)
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidData, owner, "a data field cannot directly contain another data contract; only one bounded list of flat data rows is supported");
                    return null;
                }

                BridgeDataModel? model = data.Find(candidate => SymbolEqualityComparer.Default.Equals(candidate.Symbol, named));
                if (model is null)
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidData, owner, "the referenced data contract is not declared with a valid [BridgeData] identifier");
                    return null;
                }

                return new BridgeTypeRef(BridgeTag.Data, type) { TypeId = model.Id };
            }

            if (named.TypeKind == TypeKind.Interface && HasAttribute(named, ObjectAttribute))
            {
                if (position is Position.DataField or Position.DataFieldListElement)
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.InvalidData, owner, "a data field cannot carry an object handle; a data contract is a copied value and must not depend on lease-scoped identity");
                    return null;
                }

                AttributeData attribute = GetAttribute(named, ObjectAttribute)!;
                if (attribute.ConstructorArguments.Length != 1 ||
                    attribute.ConstructorArguments[0].Value is not ulong objectId ||
                    objectId == 0 ||
                    bounds.ResultObjectId != objectId)
                {
                    Report(diagnostics, ref valid, BridgeContractDiagnostics.UnresolvedObject, owner, "a handle position requires ResultObjectId equal to the referenced [BridgeObject] identifier");
                    return null;
                }

                return new BridgeTypeRef(BridgeTag.Handle, type) { TypeId = objectId };
            }
        }

        Report(diagnostics, ref valid, BridgeContractDiagnostics.UnsupportedType, owner, $"the type '{type.Name}' is not part of the closed bridge value system");
        return null;
    }

    private static bool ValidateGraph(BridgeContractModel contract, List<Diagnostic> diagnostics)
    {
        var depths = new Dictionary<ulong, int> { [contract.RootObject.Id] = 1 };
        var queue = new Queue<BridgeObjectModel>();
        queue.Enqueue(contract.RootObject);
        bool valid = true;
        while (queue.Count > 0)
        {
            BridgeObjectModel current = queue.Dequeue();
            int depth = depths[current.Id];
            foreach (ulong referenced in EnumerateHandles(current, contract))
            {
                BridgeObjectModel? next = contract.Objects.Find(model => model.Id == referenced);
                if (next is null || depths.ContainsKey(referenced))
                {
                    continue;
                }

                if (depth + 1 > BridgeLimits.MaximumGraphDepth)
                {
                    diagnostics.Add(Diagnostic.Create(
                        BridgeContractDiagnostics.ExceededLimit,
                        Location(next.Symbol),
                        next.Name,
                        $"the object graph is deeper than {BridgeLimits.MaximumGraphDepth} levels from the root"));
                    valid = false;
                    continue;
                }

                depths[referenced] = depth + 1;
                queue.Enqueue(next);
            }
        }

        foreach (BridgeObjectModel model in contract.Objects)
        {
            if (!depths.ContainsKey(model.Id))
            {
                diagnostics.Add(Diagnostic.Create(
                    BridgeContractDiagnostics.InvalidObject,
                    Location(model.Symbol),
                    model.Name,
                    "the object is unreachable from the contract root"));
                valid = false;
            }
        }

        return valid;
    }

    /// <summary>
    /// Permits a page's bounded list of flat rows while retaining a finite,
    /// acyclic copied-data graph. Direct data nesting and nested lists are
    /// rejected earlier; this pass prevents a list edge from creating recursive
    /// codecs or an unbounded encoded-size calculation.
    /// </summary>
    private static bool ValidateDataGraph(BridgeContractModel contract, List<Diagnostic> diagnostics)
    {
        var visiting = new HashSet<ulong>();
        var visited = new HashSet<ulong>();
        bool valid = true;

        foreach (BridgeDataModel model in contract.Data)
        {
            Visit(model, depth: 1);
        }

        return valid;

        void Visit(BridgeDataModel model, int depth)
        {
            if (visited.Contains(model.Id))
            {
                return;
            }

            if (!visiting.Add(model.Id))
            {
                diagnostics.Add(Diagnostic.Create(
                    BridgeContractDiagnostics.InvalidData,
                    Location(model.Symbol),
                    model.Name,
                    "a bounded data-row list cannot form a recursive data graph"));
                valid = false;
                return;
            }

            foreach (BridgeFieldModel field in model.Fields)
            {
                foreach (ulong referenced in EnumerateDataReferences(field.Type))
                {
                    if (!contract.DataById.TryGetValue(referenced, out BridgeDataModel? next))
                    {
                        valid = false;
                        continue;
                    }

                    if (depth + 1 > BridgeLimits.MaximumGraphDepth)
                    {
                        diagnostics.Add(Diagnostic.Create(
                            BridgeContractDiagnostics.ExceededLimit,
                            Location(field.Symbol),
                            model.Name,
                            $"the copied data graph is deeper than {BridgeLimits.MaximumGraphDepth} levels"));
                        valid = false;
                        continue;
                    }

                    Visit(next, depth + 1);
                }
            }

            visiting.Remove(model.Id);
            visited.Add(model.Id);
        }
    }

    private static IEnumerable<ulong> EnumerateDataReferences(BridgeTypeRef type)
    {
        if (type.Tag == BridgeTag.Data)
        {
            yield return type.TypeId;
        }
        else if (type.Tag == BridgeTag.List && type.Element is not null)
        {
            foreach (ulong referenced in EnumerateDataReferences(type.Element))
            {
                yield return referenced;
            }
        }
    }

    private static IEnumerable<ulong> EnumerateHandles(BridgeObjectModel owner, BridgeContractModel contract)
    {
        foreach (BridgeMemberModel member in owner.Members)
        {
            foreach (ulong id in EnumerateHandles(member.Result, contract))
            {
                yield return id;
            }

            foreach (BridgeParameterModel parameter in member.Parameters)
            {
                foreach (ulong id in EnumerateHandles(parameter.Type, contract))
                {
                    yield return id;
                }
            }
        }
    }

    private static IEnumerable<ulong> EnumerateHandles(BridgeTypeRef type, BridgeContractModel contract)
    {
        switch (type.Tag)
        {
            case BridgeTag.Handle:
                yield return type.TypeId;
                break;
            case BridgeTag.List:
                foreach (ulong id in EnumerateHandles(type.Element!, contract))
                {
                    yield return id;
                }

                break;
            case BridgeTag.Data:
                if (contract.DataById.TryGetValue(type.TypeId, out BridgeDataModel? model))
                {
                    foreach (BridgeFieldModel field in model.Fields)
                    {
                        foreach (ulong id in EnumerateHandles(field.Type, contract))
                        {
                            yield return id;
                        }
                    }
                }

                break;
            default:
                break;
        }
    }

    private static bool ValidateBudget(BridgeContractModel contract, List<Diagnostic> diagnostics)
    {
        bool valid = true;
        foreach (BridgeObjectModel model in contract.Objects)
        {
            foreach (BridgeMemberModel member in model.Members)
            {
                int request = member.MaximumRequestBytes(contract.DataById);
                int reply = member.MaximumReplyBytes(contract.DataById);
                if (request <= BridgeLimits.MaximumFrameBytes && reply <= BridgeLimits.MaximumFrameBytes)
                {
                    continue;
                }

                diagnostics.Add(Diagnostic.Create(
                    BridgeContractDiagnostics.ExceededLimit,
                    Location(member.Symbol),
                    model.Name + "." + member.Name,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "its declared bounds allow a {0}-byte request and a {1}-byte reply, and a frame is at most {2} bytes; lower MaximumCollectionCount or MaximumUtf8Bytes",
                        request > BridgeLimits.MaximumFrameBytes ? "greater than " + BridgeLimits.MaximumFrameBytes.ToString(CultureInfo.InvariantCulture) : request.ToString(CultureInfo.InvariantCulture),
                        reply > BridgeLimits.MaximumFrameBytes ? "greater than " + BridgeLimits.MaximumFrameBytes.ToString(CultureInfo.InvariantCulture) : reply.ToString(CultureInfo.InvariantCulture),
                        BridgeLimits.MaximumFrameBytes)));
                valid = false;
            }
        }

        return valid;
    }

    private static string? DescribeBannedType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Dynamic || type.SpecialType == SpecialType.System_Object)
        {
            return "object and dynamic are not supported; declare a closed bridge type";
        }

        if (type.TypeKind == TypeKind.Delegate)
        {
            return "delegates are not supported; no callback ever crosses the boundary";
        }

        if (type.TypeKind == TypeKind.Pointer || type.TypeKind == TypeKind.FunctionPointer)
        {
            return "pointers and function pointers are not supported";
        }

        if (type.SpecialType is SpecialType.System_Decimal or SpecialType.System_DateTime or SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
        {
            return $"'{type.Name}' is not part of the closed bridge value system; use int, long, double, or a bounded string";
        }

        // Everything below only improves the message. The security property comes
        // from the allow-list above, which is matched by SpecialType, TypeKind, or
        // symbol comparison, so an unlisted type is rejected by fall-through even
        // when nothing here recognises it. These checks are therefore scoped to
        // their real namespaces, so a legitimate contract type is never rejected
        // with a misleading reason.
        string name = type.Name;
        string @namespace = type.ContainingNamespace is null || type.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : type.ContainingNamespace.ToDisplayString();
        if (@namespace == "System.Threading.Tasks" && name is "Task" or "ValueTask")
        {
            return "asynchronous return types are not supported; the transport is already request/reply";
        }

        if (@namespace == "System.Management.Automation" || @namespace.StartsWith("System.Management.Automation.", StringComparison.Ordinal))
        {
            return $"'{name}' is a PowerShell engine type; no SMA type crosses the bridge";
        }

        if (@namespace == "System" && name is "Type" or "DateTimeOffset" or "TimeSpan")
        {
            return $"'{name}' is not part of the closed bridge value system; use int, long, double, or a bounded string";
        }

        if ((@namespace == "System.Security" && name == "SecureString") ||
            (@namespace == "System.Net" && name is "NetworkCredential" or "CredentialCache"))
        {
            return "secret and credential material never crosses the bridge";
        }

        return null;
    }



    private static bool IsValidByteBound(int value) => value > 0 && value <= BridgeLimits.MaximumUtf8Bytes;

    private static bool IsValidCollectionBound(int value) => value > 0 && value <= BridgeLimits.MaximumCollectionCount;

    /// <summary>
    /// Resolves the bound for a method's return position. A
    /// <c>[return: BridgeBound]</c> declaration wins over the member-level
    /// bound, because a bound is never inherited across positions.
    /// </summary>
    private static Bounds ReturnBounds(IMethodSymbol method, Bounds memberBounds)
    {
        AttributeData? bound = method.GetReturnTypeAttributes().FirstOrDefault(attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(), BoundAttribute, StringComparison.Ordinal));
        return bound is null
            ? memberBounds
            : new Bounds
            {
                MaximumUtf8Bytes = GetNamedInt(bound, "MaximumUtf8Bytes"),
                MaximumByteCount = GetNamedInt(bound, "MaximumByteCount"),
                MaximumCollectionCount = GetNamedInt(bound, "MaximumCollectionCount"),
                ResultObjectId = GetNamedULong(bound, "ResultObjectId"),
            };
    }

    private static void Report(
        List<Diagnostic> diagnostics,
        ref bool valid,
        DiagnosticDescriptor descriptor,
        ISymbol symbol,
        string detail)
    {
        diagnostics.Add(Diagnostic.Create(descriptor, Location(symbol), symbol.Name, detail));
        valid = false;
    }

    internal static Location Location(ISymbol symbol) =>
        symbol.Locations.FirstOrDefault() ?? Microsoft.CodeAnalysis.Location.None;

    internal static bool HasAttribute(ISymbol symbol, string metadataName) => GetAttribute(symbol, metadataName) is not null;

    internal static AttributeData? GetAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().FirstOrDefault(attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal));

    private static uint GetNamedUInt(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is uint value ? value : 0;

    private static ulong GetNamedULong(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is ulong value ? value : 0;

    private static int GetNamedInt(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is int value ? value : 0;

    private static byte GetNamedByte(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is int value && value is >= 0 and <= 255
            ? (byte)value
            : (byte)0;
}

