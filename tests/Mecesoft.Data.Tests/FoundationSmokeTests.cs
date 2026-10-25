using Mecesoft.Data.Abstractions;
using Mecesoft.Data.Abstractions.Repositories;

namespace Mecesoft.Data.Tests;

public sealed class FoundationSmokeTests
{
    [Fact]
    public void AbstractionsAssemblyIsProviderIndependent()
    {
        var assembly = typeof(AssemblyMarker).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.Equal("Mecesoft.Data.Abstractions", assembly.GetName().Name);
        Assert.DoesNotContain(references, name => name?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(references, IsConcreteProviderAssembly);
        Assert.Contains(typeof(IReadRepository<,>), assembly.GetExportedTypes());
        Assert.Contains(typeof(IRepository<,>), assembly.GetExportedTypes());
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
