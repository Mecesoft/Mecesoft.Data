namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "MECESOFT_SQLSERVER_TEST_CONNECTION";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            Skip = $"Set {ConnectionVariable} to run SQL Server integration tests.";
        }
    }
}
