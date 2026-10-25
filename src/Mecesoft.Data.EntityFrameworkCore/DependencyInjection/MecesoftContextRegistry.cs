namespace Mecesoft.Data.EntityFrameworkCore.DependencyInjection;

/// <summary>
/// Tracks the context types registered in one service collection so unkeyed unit-of-work
/// resolution can fail closed instead of selecting an arbitrary context.
/// </summary>
internal sealed class MecesoftContextRegistry
{
    private readonly HashSet<Type> _contextTypes = [];

    public void Register(Type contextType)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        _contextTypes.Add(contextType);
    }

    public Type[] GetContextTypes() => _contextTypes.ToArray();
}
