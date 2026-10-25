using System.Data;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

/// <summary>
/// PostgreSQL reference verifier. It inspects the effective column type and collation in the
/// PostgreSQL catalogs and accepts only deterministic, case-sensitive string comparisons.
/// Model annotations alone are not trusted.
/// </summary>
internal sealed class PostgresColumnCollationVerifier : ITenantCollationVerifier
{
    public static PostgresColumnCollationVerifier Instance { get; } = new();

    public DataResult<bool> Verify(
        MecesoftDbContext context,
        ITenantEntityMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mapping);

        var entityType = context.Model.FindEntityType(mapping.EntityType);
        string? tableName = entityType?.GetTableName();
        string schemaName = entityType?.GetSchema() ?? "public";

        if (string.IsNullOrWhiteSpace(tableName)
            || string.IsNullOrWhiteSpace(mapping.TenantPropertyName))
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure(
                    $"The tenant table or column for '{mapping.EntityType.Name}' could not be resolved."));
        }

        var connection = context.Database.GetDbConnection();
        bool opened = false;
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
            opened = true;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT t.typname AS type_name,
                       c.collname AS collation_name,
                       c.collisdeterministic AS is_deterministic
                FROM pg_attribute AS a
                INNER JOIN pg_class AS rel ON rel.oid = a.attrelid
                INNER JOIN pg_namespace AS ns ON ns.oid = rel.relnamespace
                INNER JOIN pg_type AS t ON t.oid = a.atttypid
                LEFT JOIN pg_collation AS c ON c.oid = a.attcollation
                WHERE ns.nspname = @schema
                  AND rel.relname = @table
                  AND a.attname = @column
                  AND a.attnum > 0
                  AND NOT a.attisdropped
                """;

            AddParameter(command, "@schema", schemaName);
            AddParameter(command, "@table", tableName);
            AddParameter(command, "@column", mapping.TenantPropertyName);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The tenant column '{mapping.TenantPropertyName}' could not be inspected."));
            }

            string typeName = reader.GetString(0);
            string? collationName = reader.IsDBNull(1) ? null : reader.GetString(1);
            bool? isDeterministic = reader.IsDBNull(2) ? null : reader.GetBoolean(2);

            if (!string.Equals(typeName, "text", StringComparison.Ordinal)
                && !string.Equals(typeName, "varchar", StringComparison.Ordinal))
            {
                return DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The tenant column '{mapping.TenantPropertyName}' uses the type '{typeName}', "
                        + "which is not a case-sensitive text collation."));
            }

            if (isDeterministic != true)
            {
                return DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The tenant column '{mapping.TenantPropertyName}' does not use a deterministic collation "
                        + $"(collation: '{collationName ?? "unknown"}')."));
            }

            return DataResult.Succeeded(true);
        }
        catch (Exception)
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure(
                    "The PostgreSQL tenant column collation could not be verified."));
        }
        finally
        {
            if (opened)
            {
                connection.Close();
            }
        }
    }

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
