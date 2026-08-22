using System.Text;
using Vela.Windows.Diagnostics;

namespace Vela.Tests.Windows;

public sealed class RunLogRetentionTests
{
    [Fact]
    public void Prune_删除超过留存期的运行目录()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var oldRunId = Guid.Parse("2f0d6b1a-9c3d-4e5f-8a7b-6c5d4e3f2a1b");
        var directory = CreateRunDirectory(paths, oldRunId, DateTime.UtcNow - TimeSpan.FromDays(120));

        var result = new RunLogRetention(paths).Prune(retentionDays: 90);

        Assert.Equal(1, result.RemovedRunDirectories);
        Assert.True(result.RemovedAnything);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Prune_保留仍在留存期内的运行目录()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var recentRunId = Guid.Parse("3a1e7c2b-0d4e-4f6a-9b8c-7d6e5f4a3b2c");
        var directory = CreateRunDirectory(paths, recentRunId, DateTime.UtcNow - TimeSpan.FromDays(3));

        var result = new RunLogRetention(paths).Prune(retentionDays: 90);

        Assert.False(result.RemovedAnything);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void Prune_不删除受保护的当前运行目录()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var currentRunId = Guid.Parse("4b2f8d3c-1e5f-4a7b-8c9d-8e7f6a5b4c3d");
        // 本次启动自己的 run 目录时间戳可能被外部改旧,但绝不能自删。
        var directory = CreateRunDirectory(paths, currentRunId, DateTime.UtcNow - TimeSpan.FromDays(400));

        var result = new RunLogRetention(paths).Prune(retentionDays: 90, protectedRunId: currentRunId);

        Assert.False(result.RemovedAnything);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void Prune_忽略并非运行标识的目录()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var foreign = Path.Combine(paths.LogsDirectoryPath, "not-a-run-id");
        Directory.CreateDirectory(foreign);
        Directory.SetLastWriteTimeUtc(foreign, DateTime.UtcNow - TimeSpan.FromDays(500));

        var result = new RunLogRetention(paths).Prune(retentionDays: 90);

        // 不是我们命名的目录就绝不动手,失败关闭。
        Assert.False(result.RemovedAnything);
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void Prune_目录时间戳陈旧但内部文件新鲜时保留()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var runId = Guid.Parse("5c3a9e4d-2f6a-4b8c-9d0e-9f8a7b6c5d4e");
        var directory = CreateRunDirectory(paths, runId, DateTime.UtcNow - TimeSpan.FromDays(300));
        var logPath = paths.GetRunLogFilePath(runId);
        File.WriteAllText(logPath, "正在进行的运行", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(logPath, DateTime.UtcNow);
        // 写文件会更新目录时间戳,这里刻意再改旧,模拟「目录戳陈旧但运行仍在写日志」。
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - TimeSpan.FromDays(300));

        var result = new RunLogRetention(paths).Prune(retentionDays: 90);

        // 判定取目录与内部文件中最新的时间,正在进行的运行不能被清掉。
        Assert.False(result.RemovedAnything);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void Prune_留存天数非法时不做任何清理()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var runId = Guid.Parse("6d4b0f5e-3a7b-4c9d-8e1f-0a9b8c7d6e5f");
        var directory = CreateRunDirectory(paths, runId, DateTime.UtcNow - TimeSpan.FromDays(900));

        var result = new RunLogRetention(paths).Prune(retentionDays: 0);

        // 0 或负数意味着「不启用留存清理」,不能被解读成「全部删除」。
        Assert.Equal(RunLogRetentionResult.None, result);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void Prune_日志目录不存在时安全返回()
    {
        using var root = TestRoot.Create();

        var result = new RunLogRetention(new AppPaths(root.Path)).Prune(retentionDays: 90);

        Assert.False(result.RemovedAnything);
    }

    [Fact]
    public void Prune_一次清理多个过期目录并跳过新鲜目录()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var firstOld = Guid.Parse("7e5c1a6f-4b8c-4d0e-9f2a-1b0c9d8e7f6a");
        var secondOld = Guid.Parse("8f6d2b70-5c9d-4e1f-8a3b-2c1d0e9f8a7b");
        var fresh = Guid.Parse("9a7e3c81-6d0e-4f2a-9b4c-3d2e1f0a9b8c");
        CreateRunDirectory(paths, firstOld, DateTime.UtcNow - TimeSpan.FromDays(200));
        CreateRunDirectory(paths, secondOld, DateTime.UtcNow - TimeSpan.FromDays(91));
        var keep = CreateRunDirectory(paths, fresh, DateTime.UtcNow - TimeSpan.FromDays(89));

        var result = new RunLogRetention(paths).Prune(retentionDays: 90);

        Assert.Equal(2, result.RemovedRunDirectories);
        Assert.True(Directory.Exists(keep));
    }

    private static string CreateRunDirectory(AppPaths paths, Guid runId, DateTime lastWriteUtc)
    {
        var directory = paths.GetRunDirectory(runId);
        Directory.CreateDirectory(directory);
        Directory.SetLastWriteTimeUtc(directory, lastWriteUtc);
        return directory;
    }

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
                "run-log-retention-tests",
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

            throw new DirectoryNotFoundException("未找到 Vela 仓库根目录。");
        }
    }
}
