using System.Data;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

/// <summary>
/// SQL Server reference verifier. It inspects the effective column collation in sys.columns and
/// requires a case-sensitive or binary collation. Model annotations alone are not trusted.
/// </summary>
internal sealed class SqlServerColumnCollationVerifier : ITenantCollationVerifier
{
    public static SqlServerColumnCollationVerifier Instance { get; } = new();

    public DataResult<bool> Verify(
        MecesoftDbContext context,
        ITenantEntityMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mapping);

        var entityType = context.Model.FindEntityType(mapping.EntityType);
        string? tableName = entityType?.GetTableName();
        string schemaName = entityType?.GetSchema() ?? "dbo";

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
                SELECT c.collation_name
                FROM sys.columns AS c
                INNER JOIN sys.objects AS o ON c.object_id = o.object_id
                INNER JOIN sys.schemas AS s ON o.schema_id = s.schema_id
                WHERE s.name = @schema
                  AND o.name = @table
                  AND c.name = @column
                """;

            var schemaParameter = command.CreateParameter();
            schemaParameter.ParameterName = "@schema";
            schemaParameter.Value = schemaName;
            command.Parameters.Add(schemaParameter);

            var tableParameter = command.CreateParameter();
            tableParameter.ParameterName = "@table";
            tableParameter.Value = tableName;
            command.Parameters.Add(tableParameter);

            var columnParameter = command.CreateParameter();
            columnParameter.ParameterName = "@column";
            columnParameter.Value = mapping.TenantPropertyName;
            command.Parameters.Add(columnParameter);

            string? collation = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(collation))
            {
                return DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The effective collation of '{schemaName}.{tableName}.{mapping.TenantPropertyName}' could not be verified."));
            }

            bool safe = collation.Contains("_CS_", StringComparison.OrdinalIgnoreCase)
                || collation.Contains("_BIN", StringComparison.OrdinalIgnoreCase);

            return safe
                ? DataResult.Succeeded(true)
                : DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The tenant column '{mapping.TenantPropertyName}' uses the non-case-sensitive collation '{collation}'."));
        }
        catch (Exception)
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure(
                    "The SQL Server tenant column collation could not be verified."));
        }
        finally
        {
            if (opened)
            {
                connection.Close();
            }
        }
    }
}
