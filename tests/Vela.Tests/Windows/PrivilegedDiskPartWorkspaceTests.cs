using Microsoft.Win32.SafeHandles;
using System.Text;
using Vela.Windows.DiskPart;
using Vela.Windows.Security;

namespace Vela.Tests.Windows;

public sealed class PrivilegedDiskPartWorkspaceTests
{
    private const string CompliantSddl =
        "O:BAG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)S:(ML;;NW;;;HI)";

    private static readonly Guid RunId = Guid.Parse("a12b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d");

    private static (FakeAdapter Adapter, PrivilegedDiskPartWorkspace Workspace) CreateWorkspace()
    {
        var adapter = new FakeAdapter();
        var ws = new PrivilegedDiskPartWorkspace(adapter);
        return (adapter, ws);
    }

    [Fact]
    public async Task CreateScriptAsync_UsesProgramDataPrivilegedRoot()
    {
        var (adapter, ws) = CreateWorkspace();

        await using var lease = await ws.CreateScriptAsync(RunId, "select vdisk file=\"D:\\p.vhdx\"", CancellationToken.None);

        var expected = PrivilegedDiskPartWorkspace.ComputeRootPath();
        Assert.StartsWith(expected, lease.ScriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(RunId.ToString("D"), lease.ScriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".txt", lease.ScriptPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateScriptAsync_WritesAsciiAndFlushesToDisk()
    {
        var (adapter, ws) = CreateWorkspace();
        const string script = "select vdisk file=\"D:\\p.vhdx\"\r\nattach vdisk readonly\r\n";

        await using var lease = await ws.CreateScriptAsync(RunId, script, CancellationToken.None);

        var write = adapter.Writes.Single();
        Assert.Equal(Encoding.ASCII.GetBytes(script), write.Bytes.ToArray());
        Assert.True(write.Flushed);
    }

    [Fact]
    public async Task CreateScriptAsync_UsesCreateNewAndRejectsExistingFile()
    {
        var (adapter, ws) = CreateWorkspace();
        adapter.FailOnCreateNewIfExists = true;

        // 同一 runId 两次相同 nonce 不可能 (nonce 是强随机), 但 file mode 必须 CREATE_NEW
        await using var lease = await ws.CreateScriptAsync(RunId, "x", CancellationToken.None);
        Assert.Contains(adapter.Creates, c => c.Mode == FileMode.CreateNew);
    }

    [Fact]
    public async Task CreateScriptAsync_ReopensReadOnlyPinWithFileShareReadOnly()
    {
        var (adapter, ws) = CreateWorkspace();

        await using var lease = await ws.CreateScriptAsync(RunId, "x", CancellationToken.None);

        var pinOpen = adapter.Creates.Last();
        Assert.Equal(FileAccess.Read, pinOpen.Access);
        Assert.Equal(FileShare.Read, pinOpen.Share);
        Assert.Equal(FileMode.Open, pinOpen.Mode);
    }

    [Fact]
    public async Task CreateScriptAsync_RejectsPreExistingDirectoryWithUnexpectedAcl()
    {
        var (adapter, ws) = CreateWorkspace();
        // 预先用错 SDDL 配置一个 root
        var root = PrivilegedDiskPartWorkspace.ComputeRootPath();
        adapter.PreConfigureSddl(root, "O:IUD:PAI(A;;FA;;;IU)"); // bad owner

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ws.CreateScriptAsync(RunId, "x", CancellationToken.None));
    }

    [Fact]
    public async Task CreateScriptAsync_RejectsReparseSegment()
    {
        var (adapter, ws) = CreateWorkspace();
        var root = PrivilegedDiskPartWorkspace.ComputeRootPath();
        adapter.ReparsePaths.Add(root);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ws.CreateScriptAsync(RunId, "x", CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_BestEffortDeletesScriptAndRunDirectory()
    {
        var (adapter, ws) = CreateWorkspace();

        string? scriptPath;
        string? runDir;
        await using (var lease = await ws.CreateScriptAsync(RunId, "x", CancellationToken.None))
        {
            scriptPath = lease.ScriptPath;
            runDir = Path.GetDirectoryName(scriptPath)!;
        }

        Assert.Contains(scriptPath, adapter.DeletedFiles);
        Assert.Contains(runDir, adapter.DeletedDirs);
    }

    [Fact]
    public async Task CreateScriptAsync_WhenEmptyRunId_ThrowsArgumentException()
    {
        var (adapter, ws) = CreateWorkspace();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ws.CreateScriptAsync(Guid.Empty, "x", CancellationToken.None));

        Assert.Empty(adapter.Creates);
    }

    // --- Fake adapter ------------------------------------------------------------

    private sealed class FakeAdapter : INativeSecurityAdapter
    {
        private readonly Dictionary<string, string> _sddl = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<SafeFileHandle, string> _handlePath = new();

        public List<(string Path, FileAccess Access, FileShare Share, FileMode Mode)> Creates { get; } = new();
        public List<(byte[] Bytes, bool Flushed)> Writes { get; } = new();
        public List<string> DeletedFiles { get; } = new();
        public List<string> DeletedDirs { get; } = new();
        public HashSet<string> ReparsePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FailOnCreateNewIfExists { get; set; }

        public void PreConfigureSddl(string path, string sddl) => _sddl[path] = sddl;

        public IDisposable AcquireSecurityPrivilegeScope() => new NoOp();
        public bool IsSecurityPrivilegeEnabled() => false;
        public void ValidateSecurityDescriptor(string sddl) { }

        public bool CreateDirectoryWithDescriptor(string path, string sddl)
        {
            if (_sddl.ContainsKey(path))
            {
                return false; // 已存在
            }
            _sddl[path] = sddl;
            return true;
        }

        public SafeFileHandle OpenDirectoryByHandle(string path)
        {
            // 如果这个 path 还没配置, 给合规默认
            if (!_sddl.ContainsKey(path))
            {
                _sddl[path] = CompliantSddl;
            }
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            File.WriteAllText(tmp, "x");
            var handle = File.OpenHandle(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _handlePath[handle] = path;
            return handle;
        }

        public SafeFileHandle OpenFileHandle(string path, FileAccess access, FileShare share, FileMode mode, string? sddl)
        {
            Creates.Add((path, access, share, mode));

            if (mode == FileMode.CreateNew && !_sddl.ContainsKey(path))
            {
                if (sddl is not null)
                {
                    _sddl[path] = sddl;
                }
            }
            else if (mode == FileMode.CreateNew && _sddl.ContainsKey(path) && FailOnCreateNewIfExists)
            {
                throw new InvalidOperationException("File already exists");
            }

            if (!_sddl.ContainsKey(path) && mode == FileMode.Open && sddl is null)
            {
                // 第二次 open (read pin) — path 必须存在
                _sddl[path] = CompliantSddl;
            }

            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            File.WriteAllText(tmp, "x");
            var handle = File.OpenHandle(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _handlePath[handle] = path;
            return handle;
        }

        public bool IsReparsePoint(SafeFileHandle handle)
            => _handlePath.TryGetValue(handle, out var p) && ReparsePaths.Contains(p);

        public FileIdentity GetFileIdentity(SafeFileHandle handle)
        {
            if (!_handlePath.TryGetValue(handle, out var path))
            {
                throw new InvalidOperationException("unknown handle");
            }
            // 通过 path 给稳定 identity — 同 path 得同 identity
            var index = (long)path.GetHashCode(StringComparison.Ordinal);
            return new FileIdentity(VolumeSerialNumber: 42, FileIndex: index, FileLength: 10, LastWriteTimeUtcTicks: 1);
        }

        public string GetFinalPathName(SafeFileHandle handle)
            => _handlePath.TryGetValue(handle, out var p) ? p : throw new InvalidOperationException("unknown handle");

        public string ReadSecurityDescriptorSddl(SafeFileHandle handle, bool includeSacl)
        {
            if (!_handlePath.TryGetValue(handle, out var p) || !_sddl.TryGetValue(p, out var sddl))
            {
                throw new InvalidOperationException("unknown path");
            }
            if (includeSacl)
            {
                return sddl;
            }
            var i = sddl.IndexOf("S:", StringComparison.Ordinal);
            return i < 0 ? sddl : sddl[..i];
        }

        public bool IsPrivilegedDescriptorCompliant(string sddl, bool requireHighIntegrity)
            => WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(sddl, requireHighIntegrity);

        public void WriteAllBytesAndFlush(SafeFileHandle handle, ReadOnlySpan<byte> bytes)
        {
            Writes.Add((bytes.ToArray(), Flushed: true));
        }

        public bool TryDeleteFile(string path)
        {
            DeletedFiles.Add(path);
            _sddl.Remove(path);
            return true;
        }

        public bool TryDeleteDirectory(string path)
        {
            DeletedDirs.Add(path);
            _sddl.Remove(path);
            return true;
        }

        private sealed class NoOp : IDisposable { public void Dispose() { } }
    }
}
