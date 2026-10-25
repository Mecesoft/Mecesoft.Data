using System.Reflection;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.Tests;

public sealed class PublicApiSecurityTests
{
    [Fact]
    public void PublicApiDoesNotExposeQueryableOrBypassFlags()
    {
        Type[] exportedTypes = typeof(IReadRepository<,>).Assembly.GetExportedTypes();
        IEnumerable<MethodInfo> methods = exportedTypes.SelectMany(type => type.GetMethods());
        IEnumerable<PropertyInfo> properties = exportedTypes.SelectMany(type => type.GetProperties());

        Assert.DoesNotContain(methods, method => ContainsQueryable(method.ReturnType));
        Assert.DoesNotContain(methods, method => method.GetParameters().Any(parameter => ContainsQueryable(parameter.ParameterType)));
        Assert.DoesNotContain(properties, property => ContainsQueryable(property.PropertyType));

        string[] forbiddenParameterNames =
        [
            "includeDeleted",
            "ignoreTenant",
            "ignoreSoftDelete",
            "ignoreQueryFilters",
            "bypass",
        ];

        Assert.DoesNotContain(
            methods.SelectMany(method => method.GetParameters()),
            parameter => forbiddenParameterNames.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoPublicMethodAcceptsTenantScopeAsAParameter()
    {
        Type[] exportedTypes = typeof(IReadRepository<,>).Assembly.GetExportedTypes();
        IEnumerable<ParameterInfo> parameters = exportedTypes
            .Where(type => !type.IsGenericType || type.GetGenericTypeDefinition() != typeof(TenantScope<>))
            .SelectMany(type => type.GetMethods())
            .SelectMany(method => method.GetParameters());

        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType.IsGenericType
                && parameter.ParameterType.GetGenericTypeDefinition() == typeof(TenantScope<>));
    }

    [Fact]
    public void SoftDeleteAuthorizationReturnsOnlyAnOperationEcho()
    {
        MethodInfo method = typeof(ISoftDeleteAuthorizer<,,>).GetMethod("AuthorizeAsync")!;
        Type resultType = method.ReturnType.GetGenericArguments()[0];

        Assert.True(resultType.IsGenericType);
        Assert.Equal(typeof(DataResult<>), resultType.GetGenericTypeDefinition());
        Assert.Equal(typeof(SoftDeleteOperation), resultType.GetGenericArguments()[0]);
        Assert.DoesNotContain(
            typeof(ISoftDeleteAuthorizer<,,>).Assembly.GetExportedTypes(),
            type => type.Name == "SoftDeleteAccess");
    }

    [Fact]
    public void RepositoryMutationRequiresLoadKeyConcurrencyAndMutation()
    {
        MethodInfo update = typeof(IRepository<,>).GetMethod("UpdateAsync")!;
        Type[] parameterTypes = update.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Equal(4, parameterTypes.Length);
        Assert.True(parameterTypes[0].IsGenericParameter);
        Assert.Equal(typeof(ConcurrencyToken), parameterTypes[1]);
        Assert.Equal(typeof(EntityMutation<>), parameterTypes[2].GetGenericTypeDefinition());
        Assert.Equal(typeof(CancellationToken), parameterTypes[3]);
        Assert.Null(typeof(IRepository<,>).GetMethod("DeleteAsync"));
    }

    [Fact]
    public void SoftDeleteMutationRequiresKeyAndConcurrencyToken()
    {
        MethodInfo[] methods = typeof(ISoftDeleteRepository<,>).GetMethods()
            .Where(method => method.Name is "SoftDeleteAsync" or "RestoreAsync")
            .ToArray();

        Assert.Equal(2, methods.Length);
        Assert.All(methods, method =>
        {
            Type[] parameterTypes = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            Assert.Equal(3, parameterTypes.Length);
            Assert.True(parameterTypes[0].IsGenericParameter);
            Assert.Equal(typeof(ConcurrencyToken), parameterTypes[1]);
            Assert.Equal(typeof(CancellationToken), parameterTypes[2]);
        });
    }

    [Fact]
    public void EveryAbstractionAsyncMethodAcceptsCancellationToken()
    {
        MethodInfo[] asynchronousMethods = typeof(IReadRepository<,>).Assembly
            .GetExportedTypes()
            .Where(type => type.IsInterface)
            .SelectMany(type => type.GetMethods())
            .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(asynchronousMethods);
        Assert.All(
            asynchronousMethods,
            method => Assert.Contains(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(CancellationToken)));
    }

    private static bool ContainsQueryable(Type type)
    {
        if (type == typeof(IQueryable) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>)))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(ContainsQueryable);
    }
}
