using System.Collections.Frozen;
using System.Reflection;

namespace Vela.Tests.Architecture;

public sealed class CoreAssemblyDependencyTests
{
    private static readonly FrozenSet<string> ForbiddenAssemblyNames =
        new[]
        {
            "Spectre.Console",
            "Microsoft.Win32.Registry",
            "System.Diagnostics.Process",
            "Vela.Windows",
            "Vela.Tui"
        }.ToFrozenSet(StringComparer.Ordinal);

    [Fact]
    public void CoreAssembly_DoesNotReferencePlatformOrTuiDependencies()
    {
        var coreAssembly = Assembly.Load("Vela.Core");
        var referencedAssemblyNames = coreAssembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name)
            .Where(static name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbiddenAssemblyName in ForbiddenAssemblyNames)
        {
            Assert.DoesNotContain(forbiddenAssemblyName, referencedAssemblyNames);
        }
    }
}
