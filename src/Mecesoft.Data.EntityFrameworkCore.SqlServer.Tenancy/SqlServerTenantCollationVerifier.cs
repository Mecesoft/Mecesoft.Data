using System.Data.Common;
using System.Globalization;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;

/// <summary>
/// SQL Server production tenant collation verifier.
/// </summary>
/// <remarks>
/// <para>
/// The verifier inspects the effective SQL Server column metadata and accepts only an
/// <c>nvarchar</c> user-table column using a <c>_BIN2</c> collation with at least 256 bytes or MAX.
/// All <c>varchar</c> columns, <c>_BIN2_UTF8</c>, computed, encrypted, view, synonym, and
/// user-defined-type mappings fail closed.
/// </para>
/// <para>
/// The verifier also checks database foreign-key delete actions against the EF model. A database
/// cascade, set-null, or set-default action that is not represented by the model fails closed.
/// </para>
/// <para>
/// This verifier proves configuration, not that existing rows contain canonical tenant
/// identifiers. SQL Server ignores trailing spaces in equality comparisons even with binary
/// collations, so legacy non-canonical rows still require a separate data audit. The verifier is a
/// trusted security component: an incorrect implementation can disable tenant isolation.
/// </para>
/// </remarks>
public sealed class SqlServerTenantCollationVerifier
    : ITenantCollationVerifier,
      ITrustedTenantCollationVerifier,
      ITenantRelationshipVerifier
{
    /// <summary>
    /// Gets the stateless verifier instance.
    /// </summary>
    public static SqlServerTenantCollationVerifier Instance { get; } = new();

    /// <summary>
    /// Initializes a SQL Server tenant collation verifier.
    /// </summary>
    public SqlServerTenantCollationVerifier()
    {
    }

    /// <inheritdoc />
    public DataResult<bool> Verify(
        MecesoftDbContext context,
        ITenantEntityMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mapping);

        if (context.TenantFilterAugmenter is not SqlServerTenantFilterExpressionAugmenter)
        {
            return Failure(
                mapping,
                "SQL Server exact tenant equality must be configured with the built-in "
                + "SqlServerTenantFilterExpressionAugmenter; a no-op or custom augmenter is not "
                + "accepted as SQL Server exactness.");
        }


        try
        {
            return VerifyCore(context, mapping);
        }
        catch (Exception)
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure(
                    $"The SQL Server tenant collation for '{mapping.EntityType.Name}' "
                    + "could not be verified."));
        }
    }

    private static DataResult<bool> VerifyCore(
        MecesoftDbContext context,
        ITenantEntityMapping mapping)
    {
        IEntityType? entityType = context.Model.FindEntityType(mapping.EntityType);
        if (entityType is null)
        {
            return Failure(
                mapping,
                "The tenant entity type is not part of the EF model.");
        }

        if (string.IsNullOrWhiteSpace(mapping.TenantPropertyName))
        {
            return Failure(
                mapping,
                "The tenant mapping does not select a direct tenant property.");
        }

        IProperty? property = entityType.FindProperty(mapping.TenantPropertyName);
        if (property is null || property.ClrType != typeof(string))
        {
            return Failure(
                mapping,
                "The tenant mapping must select a mapped string property.");
        }

        IColumnMapping[] tableMappings = property
            .GetTableColumnMappings()
            .ToArray();

        if (tableMappings.Length != 1)
        {
            return Failure(
                mapping,
                "The tenant property must map to exactly one SQL Server table column.");
        }

        IColumnMapping columnMapping = tableMappings[0];
        ITable table = columnMapping.TableMapping.Table;
        string tableName = table.Name;
        string columnName = columnMapping.Column.Name;
        if (string.IsNullOrWhiteSpace(tableName)
            || string.IsNullOrWhiteSpace(columnName))
        {
            return Failure(
                mapping,
                "The tenant table or column metadata could not be resolved.");
        }

        string? schema = table.Schema;
        if (string.IsNullOrWhiteSpace(schema))
        {
            schema = context.Model.GetDefaultSchema();
        }

        if (string.IsNullOrWhiteSpace(schema))
        {
            schema = ResolveActualSchema(context, tableName);
            if (string.IsNullOrWhiteSpace(schema))
            {
                return Failure(
                    mapping,
                    $"The actual SQL Server schema for table '{tableName}' could not be "
                    + "resolved unambiguously.");
            }
        }

        return VerifyCatalog(
            context,
            mapping,
            schema,
            tableName,
            columnName);
    }

    private static string? ResolveActualSchema(
        MecesoftDbContext context,
        string tableName)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
            opened = true;
        }

        try
        {
            using DbCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP (1) s.name
                FROM sys.objects AS o
                INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                WHERE o.object_id = OBJECT_ID(QUOTENAME(@table))
                """;
            AddParameter(command, "@table", tableName);
            object? value = command.ExecuteScalar();
            return value is DBNull or null
                ? null
                : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (opened)
            {
                connection.Close();
            }
        }
    }

    /// <inheritdoc />
    public DataResult<bool> VerifyRelationships(MecesoftDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return VerifyRelationshipsCore(context);
        }
        catch (Exception exception)
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure(
                    "The SQL Server foreign key relationship metadata could not be verified: "
                    + exception.Message));
        }
    }

    private static DataResult<bool> VerifyRelationshipsCore(
        MecesoftDbContext context)
    {
        var modelTables = new HashSet<string>(StringComparer.Ordinal);
        foreach (IEntityType entityType in context.Model.GetEntityTypes())
        {
            foreach (ITableMapping mapping in entityType.GetTableMappings())
            {
                ITable table = mapping.Table;
                string? schema = ResolveTableSchema(context, table);
                if (string.IsNullOrWhiteSpace(schema)
                    || string.IsNullOrWhiteSpace(table.Name))
                {
                    return RelationshipFailure(
                        "A mapped SQL Server table schema or name could not be resolved.");
                }

                modelTables.Add(TableKey(schema, table.Name));
            }
        }

        var expected = new List<ExpectedForeignKey>();
        foreach (IForeignKey foreignKey in context.Model
            .GetEntityTypes()
            .SelectMany(entityType => entityType.GetForeignKeys()))
        {
            IForeignKeyConstraint[] constraints =
                foreignKey.GetMappedConstraints().ToArray();
            if (constraints.Length == 0)
            {
                if (IsRowInternal(context, foreignKey))
                {
                    continue;
                }

                return RelationshipFailure(
                    $"The foreign key on '{foreignKey.DeclaringEntityType.Name}' is not mapped "
                    + "to a SQL Server constraint.");
            }

            foreach (IForeignKeyConstraint constraint in constraints)
            {
                ITable dependentTable = constraint.Table;
                ITable principalTable = constraint.PrincipalTable;
                string? dependentSchema = ResolveTableSchema(
                    context,
                    dependentTable);
                string? principalSchema = ResolveTableSchema(
                    context,
                    principalTable);
                string[] dependentColumns = constraint.Columns
                    .Select(column => column.Name)
                    .ToArray();
                string[] principalColumns = constraint.PrincipalColumns
                    .Select(column => column.Name)
                    .ToArray();

                if (string.IsNullOrWhiteSpace(dependentSchema)
                    || string.IsNullOrWhiteSpace(principalSchema)
                    || string.IsNullOrWhiteSpace(dependentTable.Name)
                    || string.IsNullOrWhiteSpace(principalTable.Name)
                    || dependentColumns.Length == 0
                    || principalColumns.Length == 0)
                {
                    return RelationshipFailure(
                        "A SQL Server foreign key schema, table, or column could not be resolved.");
                }

                expected.Add(
                    new ExpectedForeignKey(
                        constraint.Name ?? "<unnamed>",
                        dependentSchema,
                        dependentTable.Name,
                        dependentColumns,
                        principalSchema,
                        principalTable.Name,
                        principalColumns,
                        constraint.OnDeleteAction));
            }
        }

        List<ActualForeignKey> actual = ReadActualForeignKeys(context);
        var matched = new HashSet<ActualForeignKey>(
            ReferenceEqualityComparer.Instance);

        foreach (ExpectedForeignKey expectedForeignKey in expected)
        {
            ActualForeignKey[] matches = actual
                .Where(candidate =>
                    string.Equals(
                        candidate.DependentTableKey,
                        TableKey(expectedForeignKey.DependentSchema, expectedForeignKey.DependentTable),
                        StringComparison.Ordinal)
                    && string.Equals(
                        candidate.PrincipalTableKey,
                        TableKey(expectedForeignKey.PrincipalSchema, expectedForeignKey.PrincipalTable),
                        StringComparison.Ordinal)
                    && ColumnsEqual(
                        candidate.DependentColumns,
                        expectedForeignKey.DependentColumns)
                    && ColumnsEqual(
                        candidate.PrincipalColumns,
                        expectedForeignKey.PrincipalColumns))
                .ToArray();

            if (matches.Length == 0)
            {
                return RelationshipFailure(
                    $"The SQL Server foreign key '{expectedForeignKey.Name}' on "
                    + $"'{expectedForeignKey.DependentSchema}.{expectedForeignKey.DependentTable}' "
                    + "was not found.");
            }

            if (matches.Length > 1)
            {
                return RelationshipFailure(
                    $"The SQL Server foreign key '{expectedForeignKey.Name}' on "
                    + $"'{expectedForeignKey.DependentSchema}.{expectedForeignKey.DependentTable}' "
                    + "is ambiguous.");
            }

            ActualForeignKey match = matches[0];
            if (!IsSafeDeleteAction(
                expectedForeignKey.OnDeleteAction,
                match.OnDeleteAction))
            {
                return RelationshipFailure(
                    $"The SQL Server foreign key '{expectedForeignKey.Name}' has database "
                    + $"delete action '{match.OnDeleteAction}', which is not represented by the "
                    + $"EF delete behavior '{expectedForeignKey.OnDeleteAction}'.");
            }

            matched.Add(match);
        }

        foreach (ActualForeignKey actualForeignKey in actual)
        {
            if (matched.Contains(actualForeignKey))
            {
                continue;
            }

            bool touchesModel = modelTables.Contains(actualForeignKey.DependentTableKey)
                || modelTables.Contains(actualForeignKey.PrincipalTableKey);
            if (touchesModel)
            {
                return RelationshipFailure(
                    $"The SQL Server foreign key '{actualForeignKey.Name}' on "
                    + $"'{actualForeignKey.DependentSchema}.{actualForeignKey.DependentTable}' "
                    + "is not represented by the EF model.");
            }
        }

        return DataResult.Succeeded(true);
    }

    private static string? ResolveTableSchema(
        MecesoftDbContext context,
        ITable table)
    {
        if (!string.IsNullOrWhiteSpace(table.Schema))
        {
            return table.Schema;
        }

        string? defaultSchema = context.Model.GetDefaultSchema();
        if (!string.IsNullOrWhiteSpace(defaultSchema))
        {
            return defaultSchema;
        }

        return ResolveActualSchema(context, table.Name);
    }

    private static bool IsRowInternal(
        MecesoftDbContext context,
        IForeignKey foreignKey)
    {
        string? tableName = foreignKey.DeclaringEntityType.GetTableName();
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return false;
        }

        string? schema = foreignKey.DeclaringEntityType.GetSchema()
            ?? context.Model.GetDefaultSchema();
        try
        {
            return foreignKey.IsRowInternal(
                StoreObjectIdentifier.Table(tableName, schema));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<ActualForeignKey> ReadActualForeignKeys(
        MecesoftDbContext context)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
            opened = true;
        }

        try
        {
            using DbCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT fk.name AS constraint_name,
                       fk.delete_referential_action,
                       ds.name AS dependent_schema,
                       dt.name AS dependent_table,
                       ps.name AS principal_schema,
                       pt.name AS principal_table,
                       fkc.constraint_column_id,
                       dc.name AS dependent_column,
                       pc.name AS principal_column
                FROM sys.foreign_keys AS fk
                INNER JOIN sys.tables AS dt ON dt.object_id = fk.parent_object_id
                INNER JOIN sys.schemas AS ds ON ds.schema_id = dt.schema_id
                INNER JOIN sys.tables AS pt ON pt.object_id = fk.referenced_object_id
                INNER JOIN sys.schemas AS ps ON ps.schema_id = pt.schema_id
                INNER JOIN sys.foreign_key_columns AS fkc
                    ON fkc.constraint_object_id = fk.object_id
                INNER JOIN sys.columns AS dc
                    ON dc.object_id = fkc.parent_object_id
                   AND dc.column_id = fkc.parent_column_id
                INNER JOIN sys.columns AS pc
                    ON pc.object_id = fkc.referenced_object_id
                   AND pc.column_id = fkc.referenced_column_id
                ORDER BY fk.object_id, fkc.constraint_column_id
                """;

            using DbDataReader reader = command.ExecuteReader();
            var builders = new Dictionary<string, ActualForeignKeyBuilder>(
                StringComparer.Ordinal);

            while (reader.Read())
            {
                string name = reader.GetString(0);
                int actionValue = Convert.ToInt32(
                    reader.GetValue(1),
                    CultureInfo.InvariantCulture);
                ReferentialAction? action = actionValue switch
                {
                    0 => ReferentialAction.NoAction,
                    1 => ReferentialAction.Cascade,
                    2 => ReferentialAction.SetNull,
                    3 => ReferentialAction.SetDefault,
                    _ => null,
                };

                string dependentSchema = reader.GetString(2);
                string dependentTable = reader.GetString(3);
                string principalSchema = reader.GetString(4);
                string principalTable = reader.GetString(5);
                string dependentColumn = reader.GetString(7);
                string principalColumn = reader.GetString(8);
                string key = TableKey(dependentSchema, dependentTable)
                    + "\u001f" + name;

                if (!builders.TryGetValue(key, out ActualForeignKeyBuilder? builder))
                {
                    builder = new ActualForeignKeyBuilder(
                        name,
                        dependentSchema,
                        dependentTable,
                        principalSchema,
                        principalTable);
                    builders.Add(key, builder);
                }

                if (builder.Action is not null && builder.Action != action)
                {
                    throw new TenantConfigurationException(
                        "The SQL Server foreign key action metadata is inconsistent.");
                }

                builder.Action = action;
                builder.DependentColumns.Add(dependentColumn);
                builder.PrincipalColumns.Add(principalColumn);
            }

            return builders.Values
                .Select(builder => builder.Build())
                .ToList();
        }
        finally
        {
            if (opened)
            {
                connection.Close();
            }
        }
    }

    private static bool IsSafeDeleteAction(
        ReferentialAction expected,
        ReferentialAction actual)
    {
        return expected switch
        {
            ReferentialAction.Cascade => actual == ReferentialAction.Cascade,
            ReferentialAction.SetNull => actual == ReferentialAction.SetNull,
            ReferentialAction.Restrict or ReferentialAction.NoAction =>
                actual is ReferentialAction.Restrict or ReferentialAction.NoAction,
            _ => false,
        };
    }

    private static bool ColumnsEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(
                left[index],
                right[index],
                StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string TableKey(string schema, string table)
    {
        return schema + "\u001f" + table;
    }

    private static DataResult<bool> RelationshipFailure(string reason)
    {
        return DataResult.Failed<bool>(
            new TenantConfigurationFailure(
                $"SQL Server foreign key verification failed: {reason}"));
    }

    private sealed record ExpectedForeignKey(
        string Name,
        string DependentSchema,
        string DependentTable,
        IReadOnlyList<string> DependentColumns,
        string PrincipalSchema,
        string PrincipalTable,
        IReadOnlyList<string> PrincipalColumns,
        ReferentialAction OnDeleteAction);

    private sealed record ActualForeignKey(
        string Name,
        string DependentSchema,
        string DependentTable,
        string DependentTableKey,
        IReadOnlyList<string> DependentColumns,
        string PrincipalSchema,
        string PrincipalTable,
        string PrincipalTableKey,
        IReadOnlyList<string> PrincipalColumns,
        ReferentialAction OnDeleteAction);

    private sealed class ActualForeignKeyBuilder(
        string name,
        string dependentSchema,
        string dependentTable,
        string principalSchema,
        string principalTable)
    {
        public string Name { get; } = name;

        public string DependentSchema { get; } = dependentSchema;

        public string DependentTable { get; } = dependentTable;

        public string PrincipalSchema { get; } = principalSchema;

        public string PrincipalTable { get; } = principalTable;

        public ReferentialAction? Action { get; set; }

        public List<string> DependentColumns { get; } = [];

        public List<string> PrincipalColumns { get; } = [];

        public ActualForeignKey Build()
        {
            if (Action is null)
            {
                throw new TenantConfigurationException(
                    "The SQL Server foreign key delete action is missing.");
            }

            return new ActualForeignKey(
                Name,
                DependentSchema,
                DependentTable,
                TableKey(DependentSchema, DependentTable),
                DependentColumns.ToArray(),
                PrincipalSchema,
                PrincipalTable,
                TableKey(PrincipalSchema, PrincipalTable),
                PrincipalColumns.ToArray(),
                Action.Value);
        }
    }

    private static DataResult<bool> VerifyCatalog(
        MecesoftDbContext context,
        ITenantEntityMapping mapping,
        string schema,
        string tableName,
        string columnName)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
            opened = true;
        }

        try
        {
            using DbCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP (1)
                       o.type AS object_type,
                       c.is_computed,
                       c.encryption_type,
                       c.system_type_id,
                       c.collation_name,
                       c.max_length,
                       t.name AS type_name,
                       t.is_user_defined
                FROM sys.columns AS c
                INNER JOIN sys.objects AS o ON o.object_id = c.object_id
                INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                WHERE s.name = @schema
                  AND o.name = @table
                  AND c.name = @column
                  AND c.column_id > 0
                """;

            AddParameter(command, "@schema", schema);
            AddParameter(command, "@table", tableName);
            AddParameter(command, "@column", columnName);

            using DbDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return Failure(
                    mapping,
                    $"The SQL Server column '{schema}.{tableName}.{columnName}' "
                    + "could not be inspected.");
            }

            string objectType = reader.GetString(0).Trim();
            bool isComputed = reader.GetBoolean(1);
            bool isEncrypted = !reader.IsDBNull(2);
            int systemTypeId = Convert.ToInt32(
                reader.GetValue(3),
                CultureInfo.InvariantCulture);
            string? collation = reader.IsDBNull(4)
                ? null
                : reader.GetString(4);
            int maxLength = Convert.ToInt32(
                reader.GetValue(5),
                CultureInfo.InvariantCulture);
            string? typeName = reader.IsDBNull(6)
                ? null
                : reader.GetString(6);
            bool isUserDefined = reader.GetBoolean(7);

            if (!SqlServerTenantCollationRules.IsSupportedObjectType(objectType))
            {
                return Failure(
                    mapping,
                    $"The tenant object '{schema}.{tableName}' is not a user table "
                    + $"(object type '{objectType}').");
            }

            if (isComputed)
            {
                return Failure(
                    mapping,
                    $"The tenant column '{schema}.{tableName}.{columnName}' is computed.");
            }

            if (isEncrypted)
            {
                return Failure(
                    mapping,
                    $"The tenant column '{schema}.{tableName}.{columnName}' is encrypted.");
            }

            if (!SqlServerTenantCollationRules.IsSupportedStringType(
                typeName,
                systemTypeId,
                isUserDefined))
            {
                return Failure(
                    mapping,
                    $"The tenant column '{schema}.{tableName}.{columnName}' uses an "
                    + $"unsupported SQL Server type ('{typeName ?? "unknown"}', "
                    + $"system_type_id={systemTypeId}, user_defined={isUserDefined}).");
            }

            if (!SqlServerTenantCollationRules.IsSupportedBin2Collation(collation))
            {
                return Failure(
                    mapping,
                    $"The tenant column '{schema}.{tableName}.{columnName}' uses "
                    + $"'{collation ?? "unknown"}'; only _BIN2 or _BIN2_UTF8 is supported.");
            }

            if (!SqlServerTenantCollationRules.IsSupportedEncoding(typeName, collation))
            {
                return Failure(
                    mapping,
                    $"The tenant column '{schema}.{tableName}.{columnName}' uses the lossy "
                    + $"combination '{typeName ?? "unknown"}' + '{collation ?? "unknown"}'. "
                    + "Use nvarchar with _BIN2.");
            }

            if (!SqlServerTenantCollationRules.HasSufficientCapacity(typeName, maxLength))
            {
                return Failure(
                    mapping,
                    $"The tenant column '{schema}.{tableName}.{columnName}' has insufficient "
                    + $"capacity (max_length={maxLength}); nvarchar requires at least 256 bytes "
                    + "or MAX.");
            }

            return DataResult.Succeeded(true);
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
        DbCommand command,
        string name,
        object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static DataResult<bool> Failure(
        ITenantEntityMapping mapping,
        string reason)
    {
        return DataResult.Failed<bool>(
            new TenantConfigurationFailure(
                $"SQL Server tenant verification failed for "
                + $"'{mapping.EntityType.Name}.{mapping.TenantPropertyName}': {reason}"));
    }
}
