using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

internal sealed class CapturingCommandInterceptor : DbCommandInterceptor
{
    private readonly List<string> _commands = [];

    public IReadOnlyList<string> Commands => _commands;

    public void Reset() => _commands.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        _commands.Add(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        _commands.Add(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }
}
