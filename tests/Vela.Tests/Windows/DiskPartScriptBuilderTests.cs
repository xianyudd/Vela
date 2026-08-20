using System.Text;
using Vela.Windows.DiskPart;

namespace Vela.Tests.Windows;

public sealed class DiskPartScriptBuilderTests
{
    private const string VhdxPath = "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx";

    [Fact]
    public void BuildDetailScript_UsesAsciiCrLfAndRequiredCommandOrder()
    {
        var builder = new DiskPartScriptBuilder();

        var script = builder.BuildDetailScript(VhdxPath);

        Assert.Equal(
            $"select vdisk file=\"{VhdxPath}\"\r\ndetail vdisk\r\nexit\r\n",
            script);
        Assert.DoesNotContain("\n", script.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.All(Encoding.ASCII.GetBytes(script), value => Assert.True(value < 128));
    }

    [Fact]
    public void BuildCompactScript_UsesAsciiCrLfAndRequiredCommandOrder()
    {
        var builder = new DiskPartScriptBuilder();

        var script = builder.BuildCompactScript(VhdxPath);

        Assert.Equal(
            $"select vdisk file=\"{VhdxPath}\"\r\ncompact vdisk\r\nexit\r\n",
            script);
        Assert.Equal(3, script.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData("relative\\ext4.vhdx")]
    [InlineData("D:\\DevTools\\WSL2\\ext4.img")]
    [InlineData("D:\\DevTools\\WSL2\\稀疏.vhdx")]
    [InlineData("D:\\DevTools\\WSL2\\ext4\r.vhdx")]
    public void BuildScript_RejectsPathOutsideDiskPartContract(string path)
    {
        var builder = new DiskPartScriptBuilder();

        Assert.Throws<ArgumentException>(() => builder.BuildDetailScript(path));
        Assert.Throws<ArgumentException>(() => builder.BuildCompactScript(path));
    }
}
