using Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

public sealed class PostgresSchemaResolutionTests
{
    [PostgresFact]
    public async Task SearchPathResolvesTheActualSchemaNotPublicFallback()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync();
        await database.ExecuteAsync(
            """
            CREATE SCHEMA custom;
            CREATE COLLATION mecesoft_ci
                (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
            CREATE TABLE public."TenantEntities" (
                "Id" uuid PRIMARY KEY,
                "TenantId" text COLLATE "C" NOT NULL,
                "Name" text NOT NULL);
            CREATE TABLE custom."TenantEntities" (
                "Id" uuid PRIMARY KEY,
                "TenantId" text COLLATE mecesoft_ci NOT NULL,
                "Name" text NOT NULL);
            """);

        using var publicContext = new PostgresPropertyOnlyProductionDbContext(
            database.OptionsWithProductionTenancyAndSearchPath<
                PostgresPropertyOnlyProductionDbContext>("public"),
            new TenantScopeStore(),
            [new TenantEntityMapping<PgProductionTextEntity, string>(
                item => item.TenantId)],
            []);
        publicContext.ValidateTenantSecurity();

        using var customContext = new PostgresPropertyOnlyProductionDbContext(
            database.OptionsWithProductionTenancyAndSearchPath<
                PostgresPropertyOnlyProductionDbContext>("custom,public"),
            new TenantScopeStore(),
            [new TenantEntityMapping<PgProductionTextEntity, string>(
                item => item.TenantId)],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => customContext.ValidateTenantSecurity());

        Assert.Contains(
            "deterministic",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
