using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Vela.Core.Models;
using Vela.Windows.Diagnostics;
using Vela.Windows.Elevation;

namespace Vela.Tests.Windows;

public sealed class CompactRunGateTests
{
    [Fact]
    public void TryAcquire_ReturnsLeaseThatReleasesTheExactGateIdempotently()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var gate = new CompactRunGate(paths);
        var request = CreateRequest();

        var result = gate.TryAcquire(request);

        Assert.Equal(CompactRunGateStatus.Acquired, result.Status);
        Assert.NotNull(result.Lease);
        Assert.True(File.Exists(paths.CompactGateFilePath));
        Assert.Equal(request.RunId, result.Lease!.RunId);

        result.Lease.Dispose();
        result.Lease.Dispose();

        Assert.False(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public void TryAcquire_ReportsASecondTrustedGateAsAlreadyRunningWithoutALease()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var firstGate = new CompactRunGate(paths);
        var secondGate = new CompactRunGate(paths);
        var firstRequest = CreateRequest();
        var secondRequest = CreateRequest() with
        {
            RunId = Guid.Parse("d9c54d6a-4f28-4a59-83c0-3d8bf3519df2")
        };

        var acquired = firstGate.TryAcquire(firstRequest);
        var alreadyRunning = secondGate.TryAcquire(secondRequest);

        Assert.Equal(CompactRunGateStatus.Acquired, acquired.Status);
        Assert.Equal(CompactRunGateStatus.AlreadyRunning, alreadyRunning.Status);
        Assert.Equal(firstRequest.RunId, alreadyRunning.ActiveRunId);
        Assert.Null(alreadyRunning.Lease);

        acquired.Lease!.Dispose();
    }

    [Fact]
    public void TryAcquire_RetainsMalformedGateAndRejectsAcquisition()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        File.WriteAllText(paths.CompactGateFilePath, "not-a-gate", Encoding.UTF8);

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        Assert.Equal(CompactRunGateStatus.Invalid, result.Status);
        Assert.Null(result.Lease);
        Assert.True(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public void TryAcquire_RejectsInvalidRequestAndDoesNotCreateGate()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var request = CreateRequest() with { Intent = OperationIntent.Preflight };

        var result = new CompactRunGate(paths).TryAcquire(request);

        Assert.Equal(CompactRunGateStatus.Invalid, result.Status);
        Assert.False(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public void TryAcquire_RecognizesATrustedPendingRequestAsAlreadyRunning()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var pendingRunId = Guid.Parse("01e7c5df-7566-4cf3-a0bd-ff7cbe76a4e2");
        Directory.CreateDirectory(paths.PendingDirectoryPath);
        File.WriteAllText(
            paths.GetPendingRequestFilePath(pendingRunId),
            JsonSerializer.Serialize(CreateRequest() with { RunId = pendingRunId }));

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        Assert.Equal(CompactRunGateStatus.AlreadyRunning, result.Status);
        Assert.Equal(pendingRunId, result.ActiveRunId);
        Assert.Null(result.Lease);
        Assert.False(File.Exists(paths.CompactGateFilePath));
    }

    // --- 陈旧锁自动回收:worker 崩溃后不得永久卡死压缩 ---

    [Fact]
    public void TryAcquire_回收持有者进程已消失的锁并成功获取()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var staleRunId = Guid.Parse("b1b9d4a1-2c3e-4f5a-8b7c-6d5e4f3a2b1c");
        WriteGate(paths, staleRunId, DeadProcessId, ownerStartTicksUtc: 1, DateTimeOffset.UtcNow);

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        // 持有者进程已不存在 → 锁必然是崩溃残留,自动回收后放行。
        Assert.Equal(CompactRunGateStatus.Acquired, result.Status);
        Assert.NotNull(result.Lease);
        result.Lease!.Dispose();
    }

    [Fact]
    public void TryAcquire_保留持有者仍存活的锁()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var activeRunId = Guid.Parse("c2c8e5b2-3d4f-4a6b-9c8d-7e6f5a4b3c2d");
        WriteGate(
            paths,
            activeRunId,
            Environment.ProcessId,
            CurrentProcessStartTicksUtc(),
            // 时间戳刻意做旧:只要持有者活着,压缩再慢也不能被抢锁。
            DateTimeOffset.UtcNow - TimeSpan.FromDays(3));

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        Assert.Equal(CompactRunGateStatus.AlreadyRunning, result.Status);
        Assert.Equal(activeRunId, result.ActiveRunId);
        Assert.Null(result.Lease);
        Assert.True(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public void TryAcquire_回收进程号被复用的锁()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var staleRunId = Guid.Parse("d3d7f6c3-4e5a-4b7c-8d9e-8f7a6b5c4d3e");
        // 进程号存在但启动时间不符 → 该号已被无关进程复用,原持有者已死。
        WriteGate(
            paths,
            staleRunId,
            Environment.ProcessId,
            ownerStartTicksUtc: 12345,
            DateTimeOffset.UtcNow);

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        Assert.Equal(CompactRunGateStatus.Acquired, result.Status);
        result.Lease!.Dispose();
    }

    [Fact]
    public void TryAcquire_同时清理陈旧锁与其残留的待处理请求()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var staleRunId = Guid.Parse("e4e6a7d4-5f6b-4c8d-9e0f-9a8b7c6d5e4f");
        WriteGate(paths, staleRunId, DeadProcessId, ownerStartTicksUtc: 1, DateTimeOffset.UtcNow);
        WritePendingRequest(paths, staleRunId);

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        // 崩溃现场是「锁 + 待处理请求」两件残留,必须一起清掉才能真正放行。
        Assert.Equal(CompactRunGateStatus.Acquired, result.Status);
        Assert.False(File.Exists(paths.GetPendingRequestFilePath(staleRunId)));
        result.Lease!.Dispose();
    }

    [Fact]
    public void TryAcquire_旧版两段格式锁未超时则保留()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var legacyRunId = Guid.Parse("f5f5b8e5-6a7c-4d9e-8f1a-0b9c8d7e6f5a");
        WriteLegacyGate(paths, legacyRunId, DateTimeOffset.UtcNow);

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        // 旧格式不带持有者信息,可能仍属在跑的旧版 worker → 只能靠超时兜底,
        // 不能一见旧格式就放锁,否则会并发压缩同一个 VHDX。
        Assert.Equal(CompactRunGateStatus.AlreadyRunning, result.Status);
        Assert.Equal(legacyRunId, result.ActiveRunId);
        Assert.True(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public void TryAcquire_回收超过存活上限的旧版两段格式锁()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var legacyRunId = Guid.Parse("a6a4c9f6-7b8d-4e0f-9a2b-1c0d9e8f7a6b");
        WriteLegacyGate(paths, legacyRunId, DateTimeOffset.UtcNow - TimeSpan.FromHours(12));

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        Assert.Equal(CompactRunGateStatus.Acquired, result.Status);
        result.Lease!.Dispose();
    }

    [Fact]
    public void TryAcquire_回收超过存活上限且无法判定持有者的锁()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var staleRunId = Guid.Parse("b7b3d0a7-8c9e-4f1a-8b3c-2d1e0f9a8b7c");
        // 进程号字段不可用(写为 0)→ 退回时间戳判定。
        WriteGate(
            paths,
            staleRunId,
            ownerProcessId: 0,
            ownerStartTicksUtc: null,
            DateTimeOffset.UtcNow - TimeSpan.FromHours(9));

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        Assert.Equal(CompactRunGateStatus.Acquired, result.Status);
        result.Lease!.Dispose();
    }

    [Fact]
    public void TryAcquire_写入的锁携带当前进程的存活信息()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var request = CreateRequest();

        var result = new CompactRunGate(paths).TryAcquire(request);

        var parts = File.ReadAllText(paths.CompactGateFilePath, new UTF8Encoding(false)).Split('|');
        Assert.Equal(5, parts.Length);
        Assert.Equal(request.RunId.ToString("D"), parts[0]);
        Assert.Equal(paths.GetRunDirectory(request.RunId), parts[1]);
        Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), parts[2]);
        Assert.Equal(CurrentProcessStartTicksUtc().ToString(CultureInfo.InvariantCulture), parts[3]);
        Assert.True(long.Parse(parts[4], CultureInfo.InvariantCulture) > 0);

        result.Lease!.Dispose();
    }

    // --- 启动期主动对账 ---

    [Fact]
    public void ReconcileStaleGate_清理陈旧锁与孤儿请求并报告数量()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var staleRunId = Guid.Parse("c8c2e1b8-9d0f-4a2b-8c4d-3e2f1a0b9c8d");
        WriteGate(paths, staleRunId, DeadProcessId, ownerStartTicksUtc: 1, DateTimeOffset.UtcNow);
        WritePendingRequest(paths, staleRunId);

        var result = new CompactRunGate(paths).ReconcileStaleGate();

        Assert.Equal(1, result.ReclaimedGates);
        Assert.Equal(1, result.ReclaimedPendingRequests);
        Assert.True(result.ReclaimedAnything);
        Assert.False(File.Exists(paths.CompactGateFilePath));
        Assert.False(File.Exists(paths.GetPendingRequestFilePath(staleRunId)));
    }

    [Fact]
    public void ReconcileStaleGate_无锁时清理孤儿请求()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var orphanRunId = Guid.Parse("d9d1f2c9-0e1a-4b3c-8d5e-4f3a2b1c0d9e");
        WritePendingRequest(paths, orphanRunId);

        var result = new CompactRunGate(paths).ReconcileStaleGate();

        // 协调器在待处理请求存在期间必定持锁;没有锁却有请求 → 一定是孤儿。
        Assert.Equal(0, result.ReclaimedGates);
        Assert.Equal(1, result.ReclaimedPendingRequests);
        Assert.False(File.Exists(paths.GetPendingRequestFilePath(orphanRunId)));
    }

    [Fact]
    public void ReconcileStaleGate_不动仍存活的锁与其请求()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var activeRunId = Guid.Parse("e0e0a3d0-1f2b-4c4d-8e6f-5a4b3c2d1e0f");
        WriteGate(paths, activeRunId, Environment.ProcessId, CurrentProcessStartTicksUtc(), DateTimeOffset.UtcNow);
        WritePendingRequest(paths, activeRunId);

        var result = new CompactRunGate(paths).ReconcileStaleGate();

        Assert.False(result.ReclaimedAnything);
        Assert.True(File.Exists(paths.CompactGateFilePath));
        Assert.True(File.Exists(paths.GetPendingRequestFilePath(activeRunId)));
    }

    [Fact]
    public void ReconcileStaleGate_保留格式损坏的锁()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        File.WriteAllText(paths.CompactGateFilePath, "not-a-gate", new UTF8Encoding(false));

        var result = new CompactRunGate(paths).ReconcileStaleGate();

        // 损坏的锁保留在原地,供人工排查;TryAcquire 会持续判 Invalid。
        Assert.False(result.ReclaimedAnything);
        Assert.True(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public void ReconcileStaleGate_目录干净时不报告任何回收()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);

        var result = new CompactRunGate(paths).ReconcileStaleGate();

        Assert.False(result.ReclaimedAnything);
    }

    [Fact]
    public void ReconcileStaleGate_清理陈旧锁后允许重新获取()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var staleRunId = Guid.Parse("f1f9b4e1-2a3c-4d5e-8f7a-6b5c4d3e2f1a");
        WriteGate(paths, staleRunId, DeadProcessId, ownerStartTicksUtc: 1, DateTimeOffset.UtcNow);
        var gate = new CompactRunGate(paths);

        gate.ReconcileStaleGate();
        var result = gate.TryAcquire(CreateRequest());

        Assert.Equal(CompactRunGateStatus.Acquired, result.Status);
        result.Lease!.Dispose();
    }

    // Windows 的进程号远小于该值,可稳定代表「已不存在的进程」。
    private static readonly int DeadProcessId = int.MaxValue - 1;

    private static long CurrentProcessStartTicksUtc()
    {
        using var current = Process.GetCurrentProcess();
        return current.StartTime.ToUniversalTime().Ticks;
    }

    private static void WriteGate(
        AppPaths paths,
        Guid runId,
        int ownerProcessId,
        long? ownerStartTicksUtc,
        DateTimeOffset createdAtUtc)
    {
        Directory.CreateDirectory(paths.RootDirectory);
        var content = string.Join(
            '|',
            runId.ToString("D"),
            paths.GetRunDirectory(runId),
            ownerProcessId.ToString(CultureInfo.InvariantCulture),
            ownerStartTicksUtc?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            createdAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(paths.CompactGateFilePath, content, new UTF8Encoding(false));
    }

    private static void WriteLegacyGate(AppPaths paths, Guid runId, DateTimeOffset lastWriteUtc)
    {
        Directory.CreateDirectory(paths.RootDirectory);
        File.WriteAllText(
            paths.CompactGateFilePath,
            $"{runId:D}|{paths.GetRunDirectory(runId)}",
            new UTF8Encoding(false));
        // 旧格式没有内嵌时间戳,年龄只能取自文件修改时间。
        File.SetLastWriteTimeUtc(paths.CompactGateFilePath, lastWriteUtc.UtcDateTime);
    }

    private static void WritePendingRequest(AppPaths paths, Guid runId)
    {
        Directory.CreateDirectory(paths.PendingDirectoryPath);
        File.WriteAllText(
            paths.GetPendingRequestFilePath(runId),
            JsonSerializer.Serialize(CreateRequest() with { RunId = runId }),
            new UTF8Encoding(false));
    }

    private static OperationRequest CreateRequest() =>
        new(
            Guid.Parse("6e7f3f8e-7c52-4224-9b85-9a7cfd71dc2e"),
            new Profile(
                Guid.Parse("26868c45-fd56-424b-9c75-47e1e998a563"),
                "Ubuntu 24.04 on D",
                "Ubuntu-24.04",
                @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
                ShutdownMode.Global,
                TimeSpan.FromSeconds(45)),
            OperationIntent.Compact);

    private sealed class TestRoot : IDisposable
    {
        private TestRoot(string path) => Path = path;

        public string Path { get; }

        public static TestRoot Create()
        {
            var path = System.IO.Path.Combine(
                FindRepositoryRoot(),
                "artifacts",
                "test-data",
                "compact-gate-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestRoot(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(System.IO.Path.Combine(directory.FullName, "Vela.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("The Vela repository root was not found.");
        }
    }
}
