using System.Collections.Immutable;
using System.Text;
using Vela.Core.Contracts;
using Vela.Windows.DiskPart;
using Vela.Windows.Processes;

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

    [Fact]
    public async Task DiskPartClient_WritesAsciiScriptInvokesDiskPartAndDeletesTemporaryFile()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "vela-diskpart-tests", Guid.NewGuid().ToString("D"));
        var runner = new RecordingProcessRunner();
        var client = new DiskPartClient(
            runner,
            new NativeToolPaths(),
            new DiskPartScriptBuilder(),
            tempDirectory);

        var result = await client.DetailVdiskAsync(VhdxPath, CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.Succeeded, result.Status);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(new NativeToolPaths().DiskPartExePath, invocation.ExecutablePath);
        Assert.Equal(["/s", runner.ObservedScriptPath], invocation.Arguments.ToArray());
        Assert.Equal(
            $"select vdisk file=\"{VhdxPath}\"\r\ndetail vdisk\r\nexit\r\n",
            runner.ObservedScript);
        Assert.All(runner.ObservedScriptBytes, value => Assert.True(value < 128));
        Assert.False(File.Exists(runner.ObservedScriptPath));
        Assert.False(Directory.Exists(tempDirectory));
    }

    [Fact]
    public async Task DiskPartClient_DeletesTemporaryFileWhenProcessRunnerThrows()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "vela-diskpart-tests", Guid.NewGuid().ToString("D"));
        var runner = new RecordingProcessRunner { ThrowOnInvocation = true };
        var client = new DiskPartClient(
            runner,
            new NativeToolPaths(),
            new DiskPartScriptBuilder(),
            tempDirectory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompactVdiskAsync(VhdxPath, CancellationToken.None));

        Assert.NotNull(runner.ObservedScriptPath);
        Assert.False(File.Exists(runner.ObservedScriptPath));
        Assert.False(Directory.Exists(tempDirectory));
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public bool ThrowOnInvocation { get; init; }

        public List<ProcessInvocation> Invocations { get; } = new();

        public string ObservedScriptPath { get; private set; } = string.Empty;

        public string ObservedScript { get; private set; } = string.Empty;

        public byte[] ObservedScriptBytes { get; private set; } = Array.Empty<byte>();

        public Task<ProcessExecutionResult> RunAsync(
            ProcessInvocation invocation,
            IProgress<ProcessOutput>? output,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(invocation);
            ObservedScriptPath = invocation.Arguments[1];
            ObservedScriptBytes = File.ReadAllBytes(ObservedScriptPath);
            ObservedScript = Encoding.ASCII.GetString(ObservedScriptBytes);

            if (ThrowOnInvocation)
            {
                throw new InvalidOperationException("runner failure");
            }

            return Task.FromResult(new ProcessExecutionResult(
                ProcessExecutionStatus.Succeeded,
                0,
                ImmutableArray.Create("detail output"),
                ImmutableArray<string>.Empty,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch));
        }
    }
}
