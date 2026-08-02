using Vela.Windows.Processes;

namespace Vela.Tests.Windows;

public sealed class NativeToolPathsTests
{
    [Fact]
    public void Constructor_DerivesEachNativeToolFromSystemDirectory()
    {
        var paths = new NativeToolPaths();
        var systemDirectory = Environment.SystemDirectory;

        Assert.Equal(Path.Combine(systemDirectory, "wsl.exe"), paths.WslExePath);
        Assert.Equal(Path.Combine(systemDirectory, "diskpart.exe"), paths.DiskPartExePath);
        Assert.Equal(Path.Combine(systemDirectory, "fsutil.exe"), paths.FsutilExePath);
        Assert.True(Path.IsPathFullyQualified(paths.WslExePath));
        Assert.True(Path.IsPathFullyQualified(paths.DiskPartExePath));
        Assert.True(Path.IsPathFullyQualified(paths.FsutilExePath));
    }
}
