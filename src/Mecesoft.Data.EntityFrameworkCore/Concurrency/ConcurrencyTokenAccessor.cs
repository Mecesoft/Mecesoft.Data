using System.Reflection;
using System.Security.Cryptography;
using Mecesoft.Data.Abstractions.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Mecesoft.Data.EntityFrameworkCore.Concurrency;

internal sealed class ConcurrencyTokenAccessor
{
    private readonly PropertyInfo _property;

    private ConcurrencyTokenAccessor(
        string propertyName,
        PropertyInfo property,
        bool isApplicationManaged)
    {
        PropertyName = propertyName;
        _property = property;
        IsApplicationManaged = isApplicationManaged;
    }

    public string PropertyName { get; }

    public bool IsApplicationManaged { get; }

    public static ConcurrencyTokenAccessor? Create(IEntityType entityType)
    {
        IProperty[] properties = entityType.GetProperties()
            .Where(property => property.IsConcurrencyToken)
            .ToArray();

        if (properties.Length == 0)
        {
            return null;
        }

        if (properties.Length != 1)
        {
            throw new ConcurrencyConfigurationException(
                $"Entity '{entityType.ClrType.Name}' must have exactly one concurrency token property.");
        }

        IProperty property = properties[0];
        Type tokenType = property.ClrType;
        if (tokenType != typeof(byte[])
            && tokenType != typeof(Guid)
            && tokenType != typeof(long)
            && tokenType != typeof(int)
            && tokenType != typeof(string))
        {
            throw new ConcurrencyConfigurationException(
                $"The concurrency token type '{tokenType.Name}' on '{entityType.ClrType.Name}' is not supported.");
        }

        bool isApplicationManaged = property.ValueGenerated switch
        {
            ValueGenerated.Never => true,
            ValueGenerated.OnAddOrUpdate => false,
            _ => throw new ConcurrencyConfigurationException(
                $"The concurrency token generation mode '{property.ValueGenerated}' on '{entityType.ClrType.Name}' is not supported."),
        };

        PropertyInfo propertyInfo = property.PropertyInfo
            ?? throw new ConcurrencyConfigurationException(
                $"The concurrency token on '{entityType.ClrType.Name}' must map to a CLR property.");

        return new ConcurrencyTokenAccessor(property.Name, propertyInfo, isApplicationManaged);
    }

    public ConcurrencyToken Read(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        object? value = _property.GetValue(entity);
        return ToToken(value);
    }

    public void Prepare(EntityEntry entry)
    {
        if (!IsApplicationManaged)
        {
            return;
        }

        if (entry.State == EntityState.Added && !IsDefault(entry.Entity))
        {
            return;
        }

        if (entry.State is EntityState.Added or EntityState.Modified)
        {
            object next = Next(entry.Entity);
            _property.SetValue(entry.Entity, next);
        }
    }

    public bool HasChanged(EntityEntry entry)
    {
        PropertyEntry property = entry.Property(PropertyName);
        return !ValuesEqual(property.OriginalValue, property.CurrentValue);
    }

    public static bool ValuesEqual(object? left, object? right)
    {
        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }

        return Equals(left, right);
    }

    private static ConcurrencyToken ToToken(object? value)
    {
        return value switch
        {
            byte[] bytes when bytes.Length > 0 => new ConcurrencyToken(Convert.ToBase64String(bytes)),
            Guid guid => new ConcurrencyToken(guid.ToString("D")),
            long number => new ConcurrencyToken(number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            int number => new ConcurrencyToken(number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            string text when !string.IsNullOrWhiteSpace(text) => new ConcurrencyToken(text),
            _ => throw new ConcurrencyConfigurationException(
                "The concurrency token value is missing or empty."),
        };
    }

    private object Next(object entity)
    {
        object? current = _property.GetValue(entity);
        try
        {
            return current switch
            {
                byte[] => RandomNumberGenerator.GetBytes(16),
                Guid => Guid.NewGuid(),
                long number => checked(number + 1),
                int number => checked(number + 1),
                string => Guid.NewGuid().ToString("N"),
                _ => throw new ConcurrencyConfigurationException(
                    "A new concurrency token could not be generated."),
            };
        }
        catch (OverflowException)
        {
            throw new ConcurrencyConfigurationException(
                "The concurrency token reached its maximum value.");
        }
    }

    private bool IsDefault(object entity)
    {
        object? current = _property.GetValue(entity);
        return current switch
        {
            null => true,
            byte[] bytes => bytes.Length == 0,
            Guid guid => guid == Guid.Empty,
            long number => number == 0,
            int number => number == 0,
            string text => string.IsNullOrWhiteSpace(text),
            _ => true,
        };
    }
}
