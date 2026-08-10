using System.Collections.Immutable;
using System.Text;
using Vela.Core.Contracts;
using Vela.Tests.Fakes;
using Vela.Windows.Processes;
using Vela.Windows.Wsl;

namespace Vela.Tests.Windows;

public sealed class WslClientTests
{
    [Fact]
    public async Task GetInstalledInventoryAsync_ParsesEnglishVerboseInventoryAndVersions()
    {
        var runner = CreateRunner(
            "  NAME                   STATE           VERSION",
            "* Ubuntu-24.04           Running         2",
            "  Debian                 Stopped         1");
        var paths = new NativeToolPaths();
        var client = new WslClient(runner, paths);

        var inventory = await client.GetInstalledInventoryAsync(CancellationToken.None);

        Assert.Collection(
            inventory.Distributions,
            distro =>
            {
                Assert.Equal("Ubuntu-24.04", distro.Name);
                Assert.Equal(WslDistributionState.Running, distro.State);
                Assert.Equal(2, distro.Version);
                Assert.True(distro.IsDefault);
            },
            distro =>
            {
                Assert.Equal("Debian", distro.Name);
                Assert.Equal(WslDistributionState.Stopped, distro.State);
                Assert.Equal(1, distro.Version);
                Assert.False(distro.IsDefault);
            });
        Assert.Equal(
            new[] { "--list", "--verbose" },
            Assert.Single(runner.Invocations).Arguments);
        Assert.Equal(paths.WslExePath, runner.Invocations[0].ExecutablePath);
        Assert.Equal(Encoding.Unicode, runner.Invocations[0].OutputEncoding);
    }

    [Fact]
    public async Task GetInstalledInventoryAsync_ParsesChineseVerboseInventory()
    {
        var runner = CreateRunner(
            "  名称                   状态             版本",
            "* Ubuntu-24.04           正在运行         2",
            "  Debian                 已停止           1");
        var client = new WslClient(runner, new NativeToolPaths());

        var inventory = await client.GetInstalledInventoryAsync(CancellationToken.None);

        Assert.Collection(
            inventory.Distributions,
            distro =>
            {
                Assert.Equal(WslDistributionState.Running, distro.State);
                Assert.Equal(2, distro.Version);
                Assert.True(distro.IsDefault);
            },
            distro =>
            {
                Assert.Equal(WslDistributionState.Stopped, distro.State);
                Assert.Equal(1, distro.Version);
                Assert.False(distro.IsDefault);
            });
    }

    [Fact]
    public async Task GetInstalledInventoryAsync_ParsesUtf16LeRedirectedOutput()
    {
        var runner = CreateRunner(
            Utf16LeAsByteCharacters("  NAME                   STATE           VERSION"),
            Utf16LeAsByteCharacters("* Ubuntu-24.04           Running         2"));
        var client = new WslClient(runner, new NativeToolPaths());

        var inventory = await client.GetInstalledInventoryAsync(CancellationToken.None);

        var distro = Assert.Single(inventory.Distributions);
        Assert.Equal("Ubuntu-24.04", distro.Name);
        Assert.Equal(WslDistributionState.Running, distro.State);
        Assert.Equal(2, distro.Version);
        Assert.True(distro.IsDefault);
    }

    [Fact]
    public async Task GetRunningInventoryAsync_ParsesQuietOutputAndUsesExactArguments()
    {
        var runner = CreateRunner("Ubuntu-24.04", "  docker-desktop  ", string.Empty);
        var paths = new NativeToolPaths();
        var client = new WslClient(runner, paths);

        var inventory = await client.GetRunningInventoryAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "Ubuntu-24.04", "docker-desktop" },
            inventory.Distributions.Select(static distro => distro.Name));
        Assert.All(
            inventory.Distributions,
            static distro =>
            {
                Assert.Equal(WslDistributionState.Running, distro.State);
                Assert.Null(distro.Version);
                Assert.False(distro.IsDefault);
            });
        Assert.Equal(
            new[] { "--list", "--running", "--quiet" },
            Assert.Single(runner.Invocations).Arguments);
        Assert.Equal(paths.WslExePath, runner.Invocations[0].ExecutablePath);
    }

    [Fact]
    public async Task WorkerActions_UseFixedWslArguments()
    {
        var runner = CreateRunner();
        var paths = new NativeToolPaths();
        var client = new WslClient(runner, paths);

        var shutdownResult = await client.ShutdownAllAsync(CancellationToken.None);
        var terminateResult = await client.TerminateDistroAsync("Ubuntu-24.04", CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.Succeeded, shutdownResult.Status);
        Assert.Equal(ProcessExecutionStatus.Succeeded, terminateResult.Status);
        Assert.Equal(2, runner.InvocationCount);
        Assert.Equal(paths.WslExePath, runner.Invocations[0].ExecutablePath);
        Assert.Equal(new[] { "--shutdown" }, runner.Invocations[0].Arguments);
        Assert.Equal(paths.WslExePath, runner.Invocations[1].ExecutablePath);
        Assert.Equal(new[] { "--terminate", "Ubuntu-24.04" }, runner.Invocations[1].Arguments);
    }

    [Fact]
    public async Task GetInstalledInventoryAsync_WhenNativeCommandFails_Throws()
    {
        var runner = new FakeProcessRunner
        {
            ThrowOnInvocation = false,
            Result = CreateResult(ProcessExecutionStatus.Failed, exitCode: 1)
        };
        var client = new WslClient(runner, new NativeToolPaths());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetInstalledInventoryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetInstalledInventoryAsync_MapsUnrecognizedStateToUnknown()
    {
        var runner = CreateRunner(
            "  NAME                   STATE           VERSION",
            "  Ubuntu-24.04           Starting        2");
        var client = new WslClient(runner, new NativeToolPaths());

        var inventory = await client.GetInstalledInventoryAsync(CancellationToken.None);

        var distro = Assert.Single(inventory.Distributions);
        Assert.Equal("Ubuntu-24.04", distro.Name);
        Assert.Equal(WslDistributionState.Unknown, distro.State);
        Assert.Equal(2, distro.Version);
    }

    [Fact]
    public async Task TerminateDistroAsync_WhenNameContainsControlCharacter_DoesNotInvokeWsl()
    {
        var runner = CreateRunner();
        var client = new WslClient(runner, new NativeToolPaths());

        var result = await client.TerminateDistroAsync("Ubuntu-24.04\r\n--shutdown", CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.LaunchFailed, result.Status);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task TerminateDistroAsync_WhenAlreadyCancelled_PropagatesCancellationBeforeValidation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = CreateRunner();
        var client = new WslClient(runner, new NativeToolPaths());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.TerminateDistroAsync("bad\r\nname", cancellation.Token));

        Assert.Equal(0, runner.InvocationCount);
    }

    private static FakeProcessRunner CreateRunner(params string[] output) =>
        new()
        {
            ThrowOnInvocation = false,
            Result = CreateResult(ProcessExecutionStatus.Succeeded, exitCode: 0, output)
        };

    private static ProcessExecutionResult CreateResult(
        ProcessExecutionStatus status,
        int? exitCode,
        params string[] output) =>
        new(
            status,
            exitCode,
            ImmutableArray.CreateRange(output),
            ImmutableArray<string>.Empty,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static string Utf16LeAsByteCharacters(string value) =>
        string.Concat(Encoding.Unicode.GetBytes(value).Select(static value => (char)value));
}
