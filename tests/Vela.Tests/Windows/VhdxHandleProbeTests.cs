using Vela.Core.Contracts;
using Vela.Windows.Storage;

namespace Vela.Tests.Windows;

public sealed class VhdxHandleProbeTests
{
    [Fact]
    public async Task ProbeAsync_NoOtherHolder_ReportsFree()
    {
        using var temp = new TempVhdx();

        var state = await new VhdxHandleProbe().ProbeAsync(temp.Path, CancellationToken.None);

        Assert.Equal(VhdxHandleState.Free, state);
    }

    [Fact]
    public async Task ProbeAsync_AnotherHandleDeniesSharing_ReportsHeld()
    {
        using var temp = new TempVhdx();
        // 复现 WSL2 工具 VM 的占用形态:别人已持有文件且不共享,
        // 这正是 diskpart 的 compact vdisk 拿不到独占句柄的情形。
        using var holder = new FileStream(temp.Path, FileMode.Open, FileAccess.Read, FileShare.None);

        var state = await new VhdxHandleProbe().ProbeAsync(temp.Path, CancellationToken.None);

        Assert.Equal(VhdxHandleState.Held, state);
    }

    [Fact]
    public async Task ProbeAsync_HolderAllowsSharedRead_StillReportsHeldBecauseExclusiveOpenFails()
    {
        using var temp = new TempVhdx();
        // 即使占用者允许共享读,独占打开仍会失败——而 diskpart 需要的正是独占。
        using var holder = new FileStream(temp.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var state = await new VhdxHandleProbe().ProbeAsync(temp.Path, CancellationToken.None);

        Assert.Equal(VhdxHandleState.Held, state);
    }

    [Fact]
    public async Task ProbeAsync_ProbeReleasesItsHandleImmediately()
    {
        using var temp = new TempVhdx();
        var probe = new VhdxHandleProbe();

        Assert.Equal(VhdxHandleState.Free, await probe.ProbeAsync(temp.Path, CancellationToken.None));
        // 探测必须是无副作用的:第二次仍应为 Free,说明第一次没有把句柄留下来。
        Assert.Equal(VhdxHandleState.Free, await probe.ProbeAsync(temp.Path, CancellationToken.None));
    }

    [Fact]
    public async Task ProbeAsync_MissingFile_ReportsUnknownRatherThanHeld()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"vela-missing-{Guid.NewGuid():N}.vhdx");

        var state = await new VhdxHandleProbe().ProbeAsync(missing, CancellationToken.None);

        // FileNotFoundException 也派生自 IOException,必须按错误码而非异常类型判定,
        // 否则「文件不存在」会被误报成「被占用」。
        Assert.Equal(VhdxHandleState.Unknown, state);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ext4.vhdx")]
    [InlineData("D:\\logs\\run.log")]
    [InlineData("D:\\logs\\ext4.vhdx\u0000extra")]
    public async Task ProbeAsync_UnprobeablePath_ReportsUnknown(string path)
    {
        var state = await new VhdxHandleProbe().ProbeAsync(path, CancellationToken.None);

        // 相对路径、非 vhdx 后缀、含控制字符都不构成「有占用者」的证据。
        Assert.Equal(VhdxHandleState.Unknown, state);
    }

    [Fact]
    public async Task ProbeAsync_CancelledToken_Throws()
    {
        using var temp = new TempVhdx();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new VhdxHandleProbe().ProbeAsync(temp.Path, cancellation.Token));
    }

    private sealed class TempVhdx : IDisposable
    {
        public TempVhdx()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vela-probe-{Guid.NewGuid():N}.vhdx");
            File.WriteAllBytes(Path, new byte[64]);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // 清理失败不应污染测试结果。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
