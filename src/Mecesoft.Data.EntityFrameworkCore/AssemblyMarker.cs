using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

[assembly: InternalsVisibleTo("Mecesoft.Data.EntityFrameworkCore.Tests")]

namespace Mecesoft.Data.EntityFrameworkCore;

internal static class AssemblyMarker
{
    internal static Assembly RelationalAssembly => typeof(RelationalDatabaseFacadeExtensions).Assembly;
}
