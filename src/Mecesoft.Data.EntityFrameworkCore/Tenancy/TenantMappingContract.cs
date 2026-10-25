using System.Reflection;
using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Validates the CLR-level tenant contract for a mapping before EF model creation.
/// </summary>
/// <remarks>
/// EF model validation remains authoritative for table/column metadata. This guard closes the
/// model-cache gap where a second context could otherwise supply a different mapping set and skip
/// <c>OnModelCreating</c> validation for that context.
/// </remarks>
internal static class TenantMappingContract
{
    public static void Validate(ITenantEntityMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        Type entityType = mapping.EntityType
            ?? throw new TenantConfigurationException(
                "A tenant mapping must declare an entity type.");

        if (string.IsNullOrWhiteSpace(mapping.TenantPropertyName))
        {
            throw new TenantConfigurationException(
                $"The tenant mapping for '{entityType.Name}' must select a direct tenant property.");
        }

        Type[] contracts = entityType
            .GetInterfaces()
            .Where(type => type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(ITenantEntity<>))
            .ToArray();

        if (contracts.Length == 0)
        {
            throw new TenantConfigurationException(
                $"The tenant mapping entity type '{entityType.Name}' must implement ITenantEntity<T>.");
        }

        bool matched = false;
        foreach (Type contract in contracts)
        {
            PropertyInfo? contractProperty = contract.GetProperty(
                nameof(ITenantEntity<object>.TenantId));
            if (contractProperty is null)
            {
                continue;
            }

            PropertyInfo? implementedProperty = ResolveImplementedProperty(
                entityType,
                contract,
                contractProperty);

            if (implementedProperty is null)
            {
                continue;
            }

            bool propertyMatches = string.Equals(
                mapping.TenantPropertyName,
                implementedProperty.Name,
                StringComparison.Ordinal);

            bool typeMatches = mapping.TenantIdType == implementedProperty.PropertyType
                || (mapping.TenantIdType == typeof(object)
                    && implementedProperty.PropertyType == typeof(string));

            if (propertyMatches && typeMatches)
            {
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            throw new TenantConfigurationException(
                $"The tenant mapping for '{entityType.Name}' must select the "
                + "ITenantEntity<T>.TenantId contract property and declare its exact tenant "
                + "identifier type.");
        }
    }

    private static PropertyInfo? ResolveImplementedProperty(
        Type entityType,
        Type contract,
        PropertyInfo contractProperty)
    {
        MethodInfo? contractGetter = contractProperty.GetGetMethod();
        if (contractGetter is null)
        {
            return null;
        }

        InterfaceMapping map;
        try
        {
            map = entityType.GetInterfaceMap(contract);
        }
        catch (ArgumentException)
        {
            return null;
        }

        int index = Array.IndexOf(map.InterfaceMethods, contractGetter);
        if (index < 0)
        {
            return null;
        }

        MethodInfo target = map.TargetMethods[index];
        return target.DeclaringType?
            .GetProperties(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                candidate.GetGetMethod(nonPublic: true)?.MetadataToken
                    == target.MetadataToken
                && candidate.GetGetMethod(nonPublic: true)?.Module
                    == target.Module);
    }
}
