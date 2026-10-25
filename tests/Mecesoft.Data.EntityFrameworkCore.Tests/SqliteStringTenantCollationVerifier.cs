using System.Data;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Test-only SQLite verifier. It inspects the declared table SQL and rejects non-binary collations
/// on the tenant column. SQLite's default BINARY collation is case-sensitive.
/// </summary>
internal sealed class SqliteStringTenantCollationVerifier : ITenantCollationVerifier
{
    public static SqliteStringTenantCollationVerifier Instance { get; } = new();

    public DataResult<bool> Verify(
        MecesoftDbContext context,
        ITenantEntityMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mapping);

        string? tableName = context.Model
            .FindEntityType(mapping.EntityType)?
            .GetTableName();
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure(
                    $"The tenant table for '{mapping.EntityType.Name}' could not be resolved."));
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
            command.CommandText =
                "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name";
            var nameParameter = command.CreateParameter();
            nameParameter.ParameterName = "$name";
            nameParameter.Value = tableName;
            command.Parameters.Add(nameParameter);

            string? tableSql = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(tableSql))
            {
                return DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The SQLite table '{tableName}' could not be inspected."));
            }

            string columnSegment = ExtractColumnSegment(
                tableSql,
                mapping.TenantPropertyName);
            if (columnSegment.Length == 0)
            {
                return DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The tenant column '{mapping.TenantPropertyName}' could not be inspected."));
            }

            if (columnSegment.Contains("COLLATE", StringComparison.OrdinalIgnoreCase)
                && !columnSegment.Contains("COLLATE BINARY", StringComparison.OrdinalIgnoreCase))
            {
                return DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The SQLite tenant column '{mapping.TenantPropertyName}' does not use a binary collation."));
            }

            return DataResult.Succeeded(true);
        }
        catch (Exception)
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure(
                    "The SQLite tenant collation could not be verified."));
        }
        finally
        {
            if (opened)
            {
                connection.Close();
            }
        }
    }

    private static string ExtractColumnSegment(string tableSql, string columnName)
    {
        int start = tableSql.IndexOf(
            $"\"{columnName}\"",
            StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            start = tableSql.IndexOf(columnName, StringComparison.OrdinalIgnoreCase);
        }

        if (start < 0)
        {
            return string.Empty;
        }

        int depth = 0;
        int end = start;
        for (int index = start; index < tableSql.Length; index++)
        {
            char current = tableSql[index];
            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                if (depth == 0)
                {
                    end = index;
                    break;
                }

                depth--;
            }
            else if (current == ',' && depth == 0)
            {
                end = index;
                break;
            }
        }

        return end > start
            ? tableSql[start..end]
            : tableSql[start..];
    }
}
