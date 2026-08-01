using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Devolutions.PowerShell.Ffi;

Assembly facadeAssembly = typeof(PowerShell).Assembly;
Type nativeMethodsType = facadeAssembly.GetType("Devolutions.PowerShell.Ffi.NativeMethods", throwOnError: true)!;
const BindingFlags PublicDeclared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
const BindingFlags StaticNonPublic = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
const BindingFlags InstanceDeclared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

var publicBaseline = new List<string>();
foreach (Type type in facadeAssembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
{
    publicBaseline.Add($"facade:type:{type.FullName}");

    foreach (ConstructorInfo constructor in type.GetConstructors(PublicDeclared).OrderBy(constructor => constructor.ToString(), StringComparer.Ordinal))
    {
        publicBaseline.Add($"facade:ctor:{type.FullName}::{constructor}");
    }

    foreach (PropertyInfo property in type.GetProperties(PublicDeclared).OrderBy(property => property.Name, StringComparer.Ordinal))
    {
        publicBaseline.Add($"facade:property:{type.FullName}::{property}");
    }

    foreach (MethodInfo method in type.GetMethods(PublicDeclared).Where(method => !method.IsSpecialName).OrderBy(method => method.ToString(), StringComparer.Ordinal))
    {
        publicBaseline.Add($"facade:method:{type.FullName}::{method}");
    }

    foreach (FieldInfo field in type.GetFields(PublicDeclared).Where(field => !field.IsSpecialName).OrderBy(field => field.Name, StringComparer.Ordinal))
    {
        publicBaseline.Add($"facade:field:{type.FullName}::{field}");
    }
}

NativeStructInspection[] nativeStructs = facadeAssembly.GetTypes()
    .Where(type =>
        type.Namespace is "Devolutions.PowerShell.Ffi" or "Devolutions.PowerShell.Ffi.LiveObjects" &&
        type.IsValueType &&
        type.Name.StartsWith("Native", StringComparison.Ordinal))
    .OrderBy(type => type.Name, StringComparer.Ordinal)
    .Select(type => new NativeStructInspection(
        type.Name,
        Marshal.SizeOf(type),
        type.GetFields(InstanceDeclared)
            .OrderBy(field => field.MetadataToken)
            .Select(field => new NativeFieldInspection(
                field.Name,
                Marshal.OffsetOf(type, field.Name).ToInt64(),
                GetManagedTypeName(field.FieldType)))
            .ToArray()))
    .ToArray();

Type statusType = facadeAssembly.GetType("Devolutions.PowerShell.Ffi.PowerShellFfiStatus", throwOnError: true)!;
var statuses = Enum.GetNames(statusType)
    .ToDictionary(name => name, name => Convert.ToInt32(Enum.Parse(statusType, name)), StringComparer.Ordinal);

NativeImportInspection[] nativeImports = nativeMethodsType.GetMethods(StaticNonPublic)
    .Select(method =>
    {
        CustomAttributeData? libraryImport = method.CustomAttributes.SingleOrDefault(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.InteropServices.LibraryImportAttribute");
        if (libraryImport is null)
        {
            return null;
        }

        CustomAttributeNamedArgument entryPoint = libraryImport.NamedArguments.Single(argument => argument.MemberName == "EntryPoint");
        return new NativeImportInspection((string)entryPoint.TypedValue.Value!, GetManagedTypeName(method.ReturnType));
    })
    .Where(import => import is not null)
    .Select(import => import!)
    .OrderBy(import => import.EntryPoint, StringComparer.Ordinal)
    .ToArray();

ValidateAbiCompatibility(facadeAssembly, StaticNonPublic);
ValidatePowerShellValuePager();

var inspection = new FacadeInspection(
    facadeAssembly.GetReferencedAssemblies().Select(reference => reference.Name!).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
    publicBaseline,
    nativeStructs,
    statuses,
    nativeImports);

Console.WriteLine(JsonSerializer.Serialize(inspection));

static string GetManagedTypeName(Type type)
{
    if (type.IsPointer)
    {
        return $"{GetManagedTypeName(type.GetElementType()!)}*";
    }

    return type == typeof(UIntPtr) ? "System.UIntPtr" : type.FullName!;
}

static void ValidateAbiCompatibility(Assembly facadeAssembly, BindingFlags staticNonPublic)
{
    Type powerShellType = facadeAssembly.GetType("Devolutions.PowerShell.Ffi.PowerShell", throwOnError: true)!;
    Type abiInfoType = facadeAssembly.GetType("Devolutions.PowerShell.Ffi.NativeAbiInfo", throwOnError: true)!;
    MethodInfo? ensureSupportedAbi = powerShellType.GetMethod(
        "EnsureSupportedAbi",
        staticNonPublic,
        binder: null,
        types: [abiInfoType],
        modifiers: null);
    if (ensureSupportedAbi is null)
    {
        throw new InvalidOperationException("The facade must retain an ABI validation overload that accepts NativeAbiInfo.");
    }

    const ulong allRequiredFeatures = 0x1FFFDFF;
    ensureSupportedAbi.Invoke(null, [CreateAbiInfo(abiInfoType, allRequiredFeatures, abiVersion: 2, minimumCompatibleAbiVersion: 2)]);
    for (int bit = 0; bit <= 24; bit++)
    {
        if ((allRequiredFeatures & (1UL << bit)) == 0)
        {
            continue;
        }

        if (!IsRejected(
                ensureSupportedAbi,
                CreateAbiInfo(abiInfoType, allRequiredFeatures ^ (1UL << bit), abiVersion: 2, minimumCompatibleAbiVersion: 2)))
        {
            throw new InvalidOperationException($"Facade ABI validation accepted an absent required feature bit {bit}.");
        }
    }

    if (!IsRejected(ensureSupportedAbi, CreateAbiInfo(abiInfoType, allRequiredFeatures, abiVersion: 1, minimumCompatibleAbiVersion: 1)))
    {
        throw new InvalidOperationException("Facade ABI validation accepted an incompatible ABI version.");
    }

    if (!IsRejected(ensureSupportedAbi, CreateAbiInfo(abiInfoType, allRequiredFeatures, abiVersion: 3, minimumCompatibleAbiVersion: 2)))
    {
        throw new InvalidOperationException("Facade ABI validation accepted an incompatible ABI version.");
    }

    if (!IsRejected(ensureSupportedAbi, CreateAbiInfo(abiInfoType, allRequiredFeatures, abiVersion: 2, minimumCompatibleAbiVersion: 3)))
    {
        throw new InvalidOperationException("Facade ABI validation accepted an incompatible minimum ABI version.");
    }

}

static object CreateAbiInfo(Type abiInfoType, ulong featureFlags, uint abiVersion, uint minimumCompatibleAbiVersion)
{
    object instance = Activator.CreateInstance(abiInfoType)!;
    const BindingFlags instanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
    abiInfoType.GetField("Size", instanceNonPublic)!.SetValue(instance, 24U);
    abiInfoType.GetField("AbiVersion", instanceNonPublic)!.SetValue(instance, abiVersion);
    abiInfoType.GetField("FeatureFlags", instanceNonPublic)!.SetValue(instance, featureFlags);
    abiInfoType.GetField("MinimumCompatibleAbiVersion", instanceNonPublic)!.SetValue(instance, minimumCompatibleAbiVersion);
    abiInfoType.GetField("Reserved", instanceNonPublic)!.SetValue(instance, 0U);
    return instance;
}

static bool IsRejected(MethodInfo ensureSupportedAbi, object abiInfo)
{
    try
    {
        ensureSupportedAbi.Invoke(null, [abiInfo]);
        return false;
    }
    catch (TargetInvocationException exception) when (exception.InnerException is NotSupportedException)
    {
        return true;
    }
}

static void ValidatePowerShellValuePager()
{
    using var pager = new PowerShellValuePager(new PowerShellValuePagerOptions(1, 1));
    pager.Write(PowerShellValue.String("value"));
    pager.Complete();

    PowerShellValuePage page = pager.Read(0);
    if (!page.IsTerminal || page.IsComplete || page.NextSequence != 1 || page.Records.Count != 1)
    {
        throw new InvalidOperationException("PowerShellValuePager did not return the expected terminal page.");
    }

    pager.Acknowledge(page.NextSequence);
    page = pager.Read(page.NextSequence);
    if (!page.IsComplete || page.Records.Count != 0 || !pager.GetCompletion().IsComplete)
    {
        throw new InvalidOperationException("PowerShellValuePager did not require acknowledgement before completion.");
    }
}

internal sealed record FacadeInspection(
    string[] References,
    List<string> PublicBaseline,
    NativeStructInspection[] NativeStructs,
    Dictionary<string, int> Statuses,
    NativeImportInspection[] NativeImports);

internal sealed record NativeStructInspection(string Name, int Size, NativeFieldInspection[] Fields);

internal sealed record NativeFieldInspection(string Name, long Offset, string TypeName);

internal sealed record NativeImportInspection(string EntryPoint, string ReturnType);
