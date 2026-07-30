namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Marks a public DTO for compile-time generation of a strict copied
/// <see cref="PowerShellValue"/> property-bag projection.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class PowerShellDtoContractAttribute : Attribute
{
    public PowerShellDtoContractAttribute(int version = 1)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        Version = version;
    }

    public int Version { get; }

    /// <summary>
    /// Rejects properties that are not declared by the DTO contract.
    /// </summary>
    public bool RejectUnknownMembers { get; set; } = true;
}

/// <summary>
/// Configures the wire name and bounds for a DTO property.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PowerShellDtoMemberAttribute : Attribute
{
    public PowerShellDtoMemberAttribute(string? name = null)
    {
        Name = name;
    }

    public string? Name { get; }

    public bool Required { get; set; } = true;

    public int MaximumStringLength { get; set; } = 4096;

    public int MaximumCollectionCount { get; set; } = PowerShellValue.MaximumContainerEntries;
}
