using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Authorization;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

internal sealed class AllowAllGlobalWriteAuthorizer : IGlobalWriteAuthorizer
{
    public ValueTask<DataResult<bool>> AuthorizeAsync(
        GlobalWriteAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(DataResult.Succeeded(true));
    }
}

internal static class GlobalWriteTestSupport
{
    public static async Task AuthorizeAsync(
        MecesoftDbContext context,
        object entity,
        TenantOperation operation)
    {
        var coordinator = new GlobalWriteCoordinator(
            new AllowAllGlobalWriteAuthorizer());
        DataResult<bool> result = await coordinator.AuthorizeAsync(
            context,
            entity,
            operation);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"The test global write authorization failed: {result.Failure}");
        }
    }
}
