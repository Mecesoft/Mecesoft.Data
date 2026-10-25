using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

[CollectionDefinition("SqlServerProduction", DisableParallelization = true)]
public sealed class SqlServerProductionVerifierGroup
    : ICollectionFixture<SqlServerProductionVerifierFixture>
{
}

public sealed class SqlServerProductionVerifierFixture : IAsyncLifetime
{
    private SqlServerTestDatabase? _bin2;
    private SqlServerTestDatabase? _utf8;
    private SqlServerTestDatabase? _quotedSchema;
    private SqlServerTestDatabase? _char;
    private SqlServerTestDatabase? _caseSensitive;
    private SqlServerTestDatabase? _encodingUnsafe;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(SqlServerFactAttribute.ConnectionVariable)))
        {
            return;
        }

        _bin2 = await SqlServerTestDatabase.CreateAsync();
        await _bin2.EnsureCreatedAsync<SqlServerBin2StringDbContext>(
            options => new SqlServerBin2StringDbContext(
                options,
                new TenantScopeStore(),
                SqlServerProductionStringMappings.Bin2TenantMappings(),
                []));

        _utf8 = await SqlServerTestDatabase.CreateAsync();
        await _utf8.EnsureCreatedAsync<SqlServerBin2Utf8StringDbContext>(
            options => new SqlServerBin2Utf8StringDbContext(
                options,
                new TenantScopeStore(),
                SqlServerProductionStringMappings.TenantMappings(),
                []));

        _quotedSchema = await SqlServerTestDatabase.CreateAsync();
        await _quotedSchema.CreateDatabaseAsync();
        await _quotedSchema.ExecuteAsync(
            "IF SCHEMA_ID(N'tenancy') IS NULL EXEC(N'CREATE SCHEMA tenancy');");
        await _quotedSchema.EnsureCreatedAsync<SqlServerQuotedSchemaStringDbContext>(
            options => new SqlServerQuotedSchemaStringDbContext(
                options,
                new TenantScopeStore(),
                SqlServerProductionStringMappings.TenantMappings(),
                []));

        _char = await SqlServerTestDatabase.CreateAsync();
        await _char.EnsureCreatedAsync<SqlServerCharStringDbContext>(
            options => new SqlServerCharStringDbContext(
                options,
                new TenantScopeStore(),
                SqlServerProductionStringMappings.TenantMappings(),
                []));

        _caseSensitive = await SqlServerTestDatabase.CreateAsync();
        await _caseSensitive.EnsureCreatedAsync<SqlServerCaseSensitiveStringDbContext>(
            options => new SqlServerCaseSensitiveStringDbContext(
                options,
                new TenantScopeStore(),
                SqlServerStringTestMappings.TenantMappings(),
                []));

        _encodingUnsafe = await SqlServerTestDatabase.CreateAsync();
        await _encodingUnsafe.EnsureCreatedAsync<SqlServerEncodingUnsafeStringDbContext>(
            options => new SqlServerEncodingUnsafeStringDbContext(
                options,
                new TenantScopeStore(),
                SqlServerProductionStringMappings.UnsafeEncodingMappings(),
                []));
    }

    public async Task DisposeAsync()
    {
        if (_bin2 is not null)
        {
            await _bin2.DisposeAsync<SqlServerBin2StringDbContext>(
                options => new SqlServerBin2StringDbContext(
                    options,
                    new TenantScopeStore(),
                    SqlServerProductionStringMappings.Bin2TenantMappings(),
                    []));
        }

        if (_utf8 is not null)
        {
            await _utf8.DisposeAsync<SqlServerBin2Utf8StringDbContext>(
                options => new SqlServerBin2Utf8StringDbContext(
                    options,
                    new TenantScopeStore(),
                    SqlServerProductionStringMappings.TenantMappings(),
                    []));
        }

        if (_quotedSchema is not null)
        {
            await _quotedSchema.DisposeAsync<SqlServerQuotedSchemaStringDbContext>(
                options => new SqlServerQuotedSchemaStringDbContext(
                    options,
                    new TenantScopeStore(),
                    SqlServerProductionStringMappings.TenantMappings(),
                    []));
        }

        if (_char is not null)
        {
            await _char.DisposeAsync<SqlServerCharStringDbContext>(
                options => new SqlServerCharStringDbContext(
                    options,
                    new TenantScopeStore(),
                    SqlServerProductionStringMappings.TenantMappings(),
                    []));
        }

        if (_caseSensitive is not null)
        {
            await _caseSensitive.DisposeAsync<SqlServerCaseSensitiveStringDbContext>(
                options => new SqlServerCaseSensitiveStringDbContext(
                    options,
                    new TenantScopeStore(),
                    SqlServerStringTestMappings.TenantMappings(),
                    []));
        }

        if (_encodingUnsafe is not null)
        {
            await _encodingUnsafe.DisposeAsync<SqlServerEncodingUnsafeStringDbContext>(
                options => new SqlServerEncodingUnsafeStringDbContext(
                    options,
                    new TenantScopeStore(),
                    SqlServerProductionStringMappings.UnsafeEncodingMappings(),
                    []));
        }
    }

    public SqlServerBin2StringDbContext CreateBin2Context()
    {
        SqlServerTestDatabase database = _bin2
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");

        return new SqlServerBin2StringDbContext(
            database.OptionsWithProductionTenancy<SqlServerBin2StringDbContext>(),
            new TenantScopeStore(),
            SqlServerProductionStringMappings.Bin2TenantMappings(),
            []);
    }

    public SqlServerBin2Utf8StringDbContext CreateUtf8Context()
    {
        SqlServerTestDatabase database = _utf8
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");

        return new SqlServerBin2Utf8StringDbContext(
            database.OptionsWithProductionTenancy<SqlServerBin2Utf8StringDbContext>(),
            new TenantScopeStore(),
            SqlServerProductionStringMappings.TenantMappings(),
            []);
    }

    public SqlServerBin2Utf8StringDbContext CreateUtf8Context(
        TenantScopeStore store)
    {
        SqlServerTestDatabase database = _utf8
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");

        return new SqlServerBin2Utf8StringDbContext(
            database.OptionsWithProductionTenancy<SqlServerBin2Utf8StringDbContext>(),
            store,
            SqlServerProductionStringMappings.TenantMappings(),
            []);
    }

    public SqlServerBin2StringDbContext CreateBin2Context(
        TenantScopeStore store)
    {
        SqlServerTestDatabase database = _bin2
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");

        return new SqlServerBin2StringDbContext(
            database.OptionsWithProductionTenancy<SqlServerBin2StringDbContext>(),
            store,
            SqlServerProductionStringMappings.Bin2TenantMappings(),
            []);
    }

    public SqlServerQuotedSchemaStringDbContext CreateQuotedSchemaContext()
    {
        SqlServerTestDatabase database = _quotedSchema
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");

        return new SqlServerQuotedSchemaStringDbContext(
            database.OptionsWithProductionTenancy<SqlServerQuotedSchemaStringDbContext>(),
            new TenantScopeStore(),
            SqlServerProductionStringMappings.TenantMappings(),
            []);
    }

    public SqlServerCharStringDbContext CreateCharContext()
    {
        SqlServerTestDatabase database = _char
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");

        return new SqlServerCharStringDbContext(
            database.OptionsWithProductionTenancy<SqlServerCharStringDbContext>(),
            new TenantScopeStore(),
            SqlServerProductionStringMappings.TenantMappings(),
            []);
    }

    public SqlServerEncodingUnsafeStringDbContext CreateEncodingUnsafeContext()
    {
        SqlServerTestDatabase database = _encodingUnsafe
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");

        return new SqlServerEncodingUnsafeStringDbContext(
            database.OptionsWithProductionTenancy<SqlServerEncodingUnsafeStringDbContext>(),
            new TenantScopeStore(),
            SqlServerProductionStringMappings.UnsafeEncodingMappings(),
            []);
    }

    public SqlServerCaseSensitiveStringDbContext CreateCaseSensitiveReferenceContext()
    {
        SqlServerTestDatabase database = _caseSensitive
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");

        return new SqlServerCaseSensitiveStringDbContext(
            database.Options<SqlServerCaseSensitiveStringDbContext>(),
            new TenantScopeStore(),
            SqlServerStringTestMappings.TenantMappings(),
            []);
    }
}
