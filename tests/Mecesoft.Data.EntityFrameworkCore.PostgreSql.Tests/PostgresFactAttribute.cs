namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "MECESOFT_POSTGRES_TEST_CONNECTION";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            Skip = $"Set {ConnectionVariable} to run PostgreSQL integration tests.";
        }
    }
}
