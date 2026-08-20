using System.Collections.Frozen;
using System.Reflection;

namespace Vela.Tests.Architecture;

public sealed class ApplicationAssemblyDependencyTests
{
    private static readonly FrozenSet<string> ApplicationForbiddenAssemblyNames =
        new[]
        {
            "Spectre.Console",
            "Terminal.Gui",
            "Microsoft.Win32.Registry",
            "System.Diagnostics.Process",
            "Vela.Windows",
            "Vela.Tui"
        }.ToFrozenSet(StringComparer.Ordinal);

    [Fact]
    public void ApplicationAssembly_HasNoTerminalGuiSpectreOrWindowsReferences()
    {
        var appAssembly = Assembly.Load("Vela.Application");
        var referencedAssemblyNames = appAssembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name)
            .Where(static name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbiddenAssemblyName in ApplicationForbiddenAssemblyNames)
        {
            Assert.DoesNotContain(forbiddenAssemblyName, referencedAssemblyNames);
        }
    }

    [Fact]
    public void CoreAssembly_HasNoApplicationReference()
    {
        var coreAssembly = Assembly.Load("Vela.Core");
        var referencedAssemblyNames = coreAssembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name)
            .Where(static name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Vela.Application", referencedAssemblyNames);
    }
}
