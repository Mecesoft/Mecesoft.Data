using System.Data.Common;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;

/// <summary>
/// PostgreSQL production tenant collation verifier.
/// </summary>
/// <remarks>
/// <para>
/// The verifier resolves the actual EF table column and inspects PostgreSQL catalogs. It accepts
/// only deterministic <c>text</c>/<c>varchar</c> columns on regular or partitioned tables.
/// <c>citext</c>, <c>bpchar</c>, domains, generated columns, views, foreign tables, and
/// non-deterministic ICU collations fail closed.
/// </para>
/// <para>
/// PostgreSQL deterministic collations compare equal strings byte-wise and preserve trailing
/// spaces, so no trailing-space augmenter is needed. The verifier proves configuration only, not
/// stored data correctness. A custom tenant filter remains a trusted component.
/// </para>
/// </remarks>
public sealed class PostgreSqlTenantCollationVerifier
    : ITenantCollationVerifier,
      ITrustedTenantCollationVerifier,
      ITenantRelationshipVerifier
{
    /// <summary>
    /// Gets the stateless verifier instance.
    /// </summary>
    public static PostgreSqlTenantCollationVerifier Instance { get; } = new();

    /// <summary>
    /// Initializes a PostgreSQL tenant collation verifier.
    /// </summary>
    public PostgreSqlTenantCollationVerifier()
    {
    }

    /// <inheritdoc />
    public DataResult<bool> Verify(
        MecesoftDbContext context,
        ITenantEntityMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mapping);

        if (!context.HasTenantCollationVerification)
        {
            return Failure(
                mapping,
                "PostgreSQL tenant security must be configured with "
                + "UseMecesoftPostgreSqlTenancy before string tenants can be used.");
        }

        try
        {
            return VerifyCore(context, mapping);
        }
        catch (Exception)
        {
            return Failure(
                mapping,
                "The PostgreSQL tenant collation could not be verified.");
        }
    }

    private static DataResult<bool> VerifyCore(
        MecesoftDbContext context,
        ITenantEntityMapping mapping)
    {
        if (mapping.TenantIdType == typeof(object))
        {
            return Failure(
                mapping,
                "The tenant mapping must declare a concrete tenant identifier type.");
        }

        if (mapping.TenantIdType != typeof(string))
        {
            return DataResult.Succeeded(true);
        }

        if (mapping is not ITenantExactMapping)
        {
            return Failure(
                mapping,
                $"The string tenant mapping must implement {nameof(ITenantExactMapping)}.");
        }

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
                "The tenant property must map to exactly one PostgreSQL table column.");
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
                    $"The actual PostgreSQL schema for table '{tableName}' could not be "
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
                SELECT n.nspname
                FROM pg_class AS c
                INNER JOIN pg_namespace AS n ON n.oid = c.relnamespace
                WHERE c.relname = @table
                  AND pg_table_is_visible(c.oid)
                LIMIT 2
                """;
            AddParameter(command, "@table", tableName);
            using DbDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            string schema = reader.GetString(0);
            return reader.Read() ? null : schema;
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
        catch (Exception)
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure(
                    "The PostgreSQL foreign key relationship metadata could not be verified."));
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
                        "A mapped PostgreSQL table schema or name could not be resolved.");
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
                    + "to a PostgreSQL constraint.");
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
                        "A PostgreSQL foreign key schema, table, or column could not be resolved.");
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
                    $"The PostgreSQL foreign key '{expectedForeignKey.Name}' on "
                    + $"'{expectedForeignKey.DependentSchema}.{expectedForeignKey.DependentTable}' "
                    + "was not found.");
            }

            if (matches.Length > 1)
            {
                return RelationshipFailure(
                    $"The PostgreSQL foreign key '{expectedForeignKey.Name}' on "
                    + $"'{expectedForeignKey.DependentSchema}.{expectedForeignKey.DependentTable}' "
                    + "is ambiguous.");
            }

            ActualForeignKey match = matches[0];
            if (!IsSafeDeleteAction(
                expectedForeignKey.OnDeleteAction,
                match.OnDeleteAction))
            {
                return RelationshipFailure(
                    $"The PostgreSQL foreign key '{expectedForeignKey.Name}' has database "
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
                    $"The PostgreSQL foreign key '{actualForeignKey.Name}' on "
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
                SELECT c.conname,
                       c.confdeltype::text,
                       ns.nspname AS dependent_schema,
                       rel.relname AS dependent_table,
                       pns.nspname AS principal_schema,
                       pref.relname AS principal_table,
                       d.ord,
                       da.attname AS dependent_column,
                       pa.attname AS principal_column
                FROM pg_constraint AS c
                INNER JOIN pg_class AS rel ON rel.oid = c.conrelid
                INNER JOIN pg_namespace AS ns ON ns.oid = rel.relnamespace
                INNER JOIN pg_class AS pref ON pref.oid = c.confrelid
                INNER JOIN pg_namespace AS pns ON pns.oid = pref.relnamespace
                INNER JOIN LATERAL unnest(c.conkey)
                    WITH ORDINALITY AS d(attnum, ord) ON TRUE
                INNER JOIN LATERAL unnest(c.confkey)
                    WITH ORDINALITY AS p(attnum, ord) ON p.ord = d.ord
                INNER JOIN pg_attribute AS da
                    ON da.attrelid = c.conrelid
                   AND da.attnum = d.attnum
                INNER JOIN pg_attribute AS pa
                    ON pa.attrelid = c.confrelid
                   AND pa.attnum = p.attnum
                WHERE c.contype = 'f'
                ORDER BY c.oid, d.ord
                """;

            using DbDataReader reader = command.ExecuteReader();
            var builders = new Dictionary<string, ActualForeignKeyBuilder>(
                StringComparer.Ordinal);

            while (reader.Read())
            {
                string name = reader.GetString(0);
                string actionValue = reader.GetString(1);
                ReferentialAction? action = actionValue switch
                {
                    "a" => ReferentialAction.NoAction,
                    "r" => ReferentialAction.Restrict,
                    "c" => ReferentialAction.Cascade,
                    "n" => ReferentialAction.SetNull,
                    "d" => ReferentialAction.SetDefault,
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
                        "The PostgreSQL foreign key action metadata is inconsistent.");
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
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
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
                $"PostgreSQL foreign key verification failed: {reason}"));
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
                    "The PostgreSQL foreign key delete action is missing.");
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
                SELECT t.typname AS type_name,
                       c.collname AS collation_name,
                       c.collisdeterministic AS is_deterministic,
                       a.attgenerated::text AS generation,
                       rel.relkind::text AS relation_kind,
                       a.atttypmod AS type_modifier
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

            AddParameter(command, "@schema", schema);
            AddParameter(command, "@table", tableName);
            AddParameter(command, "@column", columnName);

            using DbDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return Failure(
                    mapping,
                    $"The PostgreSQL column '{schema}.{tableName}.{columnName}' "
                    + "could not be inspected.");
            }

            string? typeName = reader.IsDBNull(0)
                ? null
                : reader.GetString(0);
            string? collationName = reader.IsDBNull(1)
                ? null
                : reader.GetString(1);
            bool? isDeterministic = reader.IsDBNull(2)
                ? null
                : reader.GetBoolean(2);
            string? generation = reader.IsDBNull(3)
                ? null
                : reader.GetString(3);
            string? relationKind = reader.IsDBNull(4)
                ? null
                : reader.GetString(4);
            int typeModifier = reader.GetInt32(5);

            if (!PostgreSqlTenantCollationRules.IsSupportedRelationKind(
                relationKind))
            {
                return Failure(
                    mapping,
                    $"The tenant object '{schema}.{tableName}' is not a table "
                    + $"(relation kind '{relationKind ?? "unknown"}').");
            }

            if (!PostgreSqlTenantCollationRules.IsSupportedGeneration(generation))
            {
                return Failure(
                    mapping,
                    $"The tenant column '{schema}.{tableName}.{columnName}' is generated.");
            }

            if (!PostgreSqlTenantCollationRules.IsSupportedType(typeName))
            {
                return Failure(
                    mapping,
                    $"The tenant column '{schema}.{tableName}.{columnName}' uses "
                    + $"unsupported type '{typeName ?? "unknown"}'.");
            }

            if (!PostgreSqlTenantCollationRules.IsSupportedCapacity(
                typeName,
                typeModifier))
            {
                return Failure(
                    mapping,
                    $"The tenant column '{schema}.{tableName}.{columnName}' has insufficient "
                    + $"capacity (atttypmod={typeModifier}); use text, unlimited varchar, or "
                    + $"varchar({TenantIdValidation.MaxStringLength}) or larger.");
            }

            if (!PostgreSqlTenantCollationRules.IsSupportedDeterminism(
                isDeterministic))
            {
                return Failure(
                    mapping,
                    $"The tenant column '{schema}.{tableName}.{columnName}' does not use "
                    + $"a deterministic collation (collation: '{collationName ?? "unknown"}').");
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
                $"PostgreSQL tenant verification failed for "
                + $"'{mapping.EntityType.Name}.{mapping.TenantPropertyName}': {reason}"));
    }
}
