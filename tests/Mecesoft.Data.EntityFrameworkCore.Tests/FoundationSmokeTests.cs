using Mecesoft.Data.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class FoundationSmokeTests
{
    [Fact]
    public void EntityFrameworkCoreAssemblyUsesRelationalWithoutConcreteProvider()
    {
        var assembly = typeof(AssemblyMarker).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.Equal("Mecesoft.Data.EntityFrameworkCore", assembly.GetName().Name);
        Assert.Contains("Microsoft.EntityFrameworkCore.Relational", references);
        Assert.Equal("Microsoft.EntityFrameworkCore.Relational", AssemblyMarker.RelationalAssembly.GetName().Name);
        Assert.DoesNotContain(references, IsConcreteProviderAssembly);
        Assert.Contains(typeof(MecesoftDbContext), assembly.GetExportedTypes());
    }

    private static bool IsConcreteProviderAssembly(string? assemblyName)
    {
        string[] providerPrefixes =
        [
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Microsoft.EntityFrameworkCore.Sqlite",
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            "Pomelo.EntityFrameworkCore.MySql",
            "MySql.EntityFrameworkCore",
            "Oracle.EntityFrameworkCore",
        ];

        return providerPrefixes.Any(prefix => assemblyName?.StartsWith(prefix, StringComparison.Ordinal) == true);
    }
}
