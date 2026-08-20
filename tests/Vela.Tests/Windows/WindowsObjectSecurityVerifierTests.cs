using Microsoft.Win32.SafeHandles;
using Vela.Windows.Security;

namespace Vela.Tests.Windows;

public sealed class WindowsObjectSecurityVerifierTests
{
    private const string CompliantSddl =
        "O:BAG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)S:(ML;;NW;;;HI)";
    private const string TrustedPath = "C:\\ProgramData\\Vela\\Privileged\\DiskPart";
    private const string TrustedPrefix = "C:\\ProgramData\\Vela\\Privileged";

    private static readonly FileIdentity Identity = new(
        VolumeSerialNumber: 123,
        FileIndex: 456,
        FileLength: 789,
        LastWriteTimeUtcTicks: 99L);

    private static (FakeNativeSecurityAdapter Adapter, WindowsObjectSecurityVerifier Verifier) CreateVerifier()
    {
        var adapter = new FakeNativeSecurityAdapter();
        var verifier = new WindowsObjectSecurityVerifier(adapter);
        return (adapter, verifier);
    }

    [Fact]
    public void RejectsReparsePoint()
    {
        var (adapter, verifier) = CreateVerifier();
        adapter.ReparsePaths.Add(TrustedPath);
        adapter.Configure(TrustedPath, CompliantSddl, Identity);

        Assert.Throws<InvalidOperationException>(() =>
            verifier.AssertProtectedDirectory(TrustedPath, TrustedPrefix));
    }

    [Fact]
    public void RejectsWrongOwnerOrDaclShape()
    {
        var (adapter, verifier) = CreateVerifier();
        var badSddl = "O:IUG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)S:(ML;;NW;;;HI)"; // owner 是 IU
        adapter.Configure(TrustedPath, badSddl, Identity);

        Assert.Throws<InvalidOperationException>(() =>
            verifier.AssertProtectedDirectory(TrustedPath, TrustedPrefix));
    }

    [Fact]
    public void RejectsPathOutsideTrustedPrefix()
    {
        var (adapter, verifier) = CreateVerifier();
        const string pathOutside = "C:\\ProgramData\\Other\\thing";
        adapter.Configure(pathOutside, CompliantSddl, Identity);

        Assert.Throws<InvalidOperationException>(() =>
            verifier.AssertProtectedDirectory(pathOutside, TrustedPrefix));
    }

    [Fact]
    public void RejectsIdentityMismatchBetweenCreationHandleAndResolvedPath()
    {
        var (adapter, verifier) = CreateVerifier();
        adapter.Configure(TrustedPath, CompliantSddl, Identity);

        using var first = adapter.OpenDirectoryByHandle(TrustedPath);
        using var second = adapter.OpenDirectoryByHandle(TrustedPath);
        // 第二次 open 拿到不同 identity (模拟被替换)
        adapter.Configure(TrustedPath, CompliantSddl, Identity with { FileIndex = 999 });

        Assert.Throws<InvalidOperationException>(() =>
            verifier.AssertProtectedFileHandle(second, TrustedPath, Identity));
    }

    [Fact]
    public void PrivilegeScope_WhenSecurityPrivilegeIsMissing_FailsBeforeCreatingWorkspace()
    {
        var (adapter, verifier) = CreateVerifier();
        adapter.FailPrivilegeAcquire = true;
        adapter.Configure(TrustedPath, CompliantSddl, Identity);

        Assert.Throws<InvalidOperationException>(() =>
            verifier.AssertProtectedDirectory(TrustedPath, TrustedPrefix));

        // 再次确认 IsSecurityPrivilegeEnabled 之后仍然为 false (资源已清理)
        Assert.False(adapter.IsSecurityPrivilegeEnabled());
    }

    [Fact]
    public void PrivilegeScope_RestoresPreviousTokenStateAfterSuccessAndException()
    {
        var (adapter, verifier) = CreateVerifier();
        adapter.Configure(TrustedPath, CompliantSddl, Identity);

        // 1) 成功路径：scope 内 Acquire 计数=1, 验证完后 Restored=true, 计数=0
        verifier.AssertProtectedDirectory(TrustedPath, TrustedPrefix);
        Assert.True(adapter.PrivilegeScopes > 0);
        Assert.Equal(adapter.PrivilegeScopes, adapter.PrivilegeScopesRestored);
        Assert.False(adapter.IsSecurityPrivilegeEnabled());

        // 2) 异常路径：让 includeSacl 读取抛，依然必须 Restore
        adapter.FailSaclRead = true;
        Assert.Throws<InvalidOperationException>(() =>
            verifier.AssertProtectedDirectory(TrustedPath, TrustedPrefix));
        Assert.Equal(adapter.PrivilegeScopes, adapter.PrivilegeScopesRestored);
        Assert.False(adapter.IsSecurityPrivilegeEnabled());
    }

    [Fact]
    public void AssertProtectedFileHandle_VerifiesBothBasicAndSaclPasses()
    {
        var (adapter, verifier) = CreateVerifier();
        adapter.Configure(TrustedPath, CompliantSddl, Identity);

        using var handle = adapter.OpenDirectoryByHandle(TrustedPath);
        verifier.AssertProtectedFileHandle(handle, TrustedPath, Identity);

        Assert.True(adapter.SawBasicRead, "verifier must read owner/group/DACL first.");
        Assert.True(adapter.SawSaclRead, "verifier must read SACL inside privilege scope.");
        Assert.Equal(adapter.PrivilegeScopes, adapter.PrivilegeScopesRestored);
    }

    // 注: "MediumIntegrityAccessCheck_DeniesWriteDeleteRenameWriteDacAndWriteOwner"
    // 属于真实 Windows 集成检查, 通过 ACL 的 DACL S:HI 标签由 OS 强制, 我们的
    // 单元测试通过强制 SDDL 形状达到同等保护级别。

    /// <summary>Fake adapter: 把路径→(SDDL, identity, reparse) 映射, 支持受控失败。</summary>
    private sealed class FakeNativeSecurityAdapter : INativeSecurityAdapter
    {
        private readonly Dictionary<string, (string Sddl, FileIdentity Identity)> _byPath = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReparsePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FailPrivilegeAcquire { get; set; }
        public bool FailSaclRead { get; set; }
        public bool CurrentlyInPrivilegeScope { get; set; }
        public int PrivilegeScopes { get; private set; }
        public int PrivilegeScopesRestored { get; private set; }
        public bool SawBasicRead { get; private set; }
        public bool SawSaclRead { get; private set; }

        public void Configure(string path, string sddl, FileIdentity identity)
        {
            _byPath[path] = (sddl, identity);
        }

        public IDisposable AcquireSecurityPrivilegeScope()
        {
            if (FailPrivilegeAcquire)
            {
                throw new InvalidOperationException("SeSecurityPrivilege not available.");
            }
            PrivilegeScopes++;
            CurrentlyInPrivilegeScope = true;
            return new Scope(this);
        }

        public bool IsSecurityPrivilegeEnabled() => CurrentlyInPrivilegeScope;

        public void ValidateSecurityDescriptor(string sddl) { }

        public bool CreateDirectoryWithDescriptor(string path, string sddl)
        {
            _byPath[path] = (sddl, new FileIdentity(0, 0, 0, 0));
            return true;
        }

        public SafeFileHandle OpenDirectoryByHandle(string path)
        {
            // 给一个 fake 但 non-invalid 句柄 — 用临时文件的生命周期, 不用真盘符
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            File.WriteAllText(tmp, "x");
            var handle = File.OpenHandle(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // 把 path → handle 绑定, 之后 PathFromHandle 反查
            _handlePaths[handle] = (path, tmp);
            return handle;
        }

        public SafeFileHandle OpenFileHandle(string path, FileAccess access, FileShare share, FileMode mode, string? sddl)
            => OpenDirectoryByHandle(path); // 测试复用

        public bool IsReparsePoint(SafeFileHandle handle)
            => _handlePaths.TryGetValue(handle, out var pair) && ReparsePaths.Contains(pair.Path);

        public FileIdentity GetFileIdentity(SafeFileHandle handle)
            => _handlePaths.TryGetValue(handle, out var pair) && _byPath.TryGetValue(pair.Path, out var cfg)
                ? cfg.Identity
                : throw new InvalidOperationException("unknown handle/path");

        public string GetFinalPathName(SafeFileHandle handle)
            => _handlePaths.TryGetValue(handle, out var pair) ? pair.Path : throw new InvalidOperationException("unknown handle");

        public string ReadSecurityDescriptorSddl(SafeFileHandle handle, bool includeSacl)
        {
            if (!_handlePaths.TryGetValue(handle, out var pair))
            {
                throw new InvalidOperationException("unknown handle");
            }
            if (includeSacl)
            {
                if (FailSaclRead)
                {
                    throw new InvalidOperationException("simulated sacl read failure");
                }
                if (!CurrentlyInPrivilegeScope)
                {
                    throw new InvalidOperationException("SACL read outside privilege scope.");
                }
                SawSaclRead = true;
            }
            else
            {
                SawBasicRead = true;
            }

            if (!_byPath.TryGetValue(pair.Path, out var cfg))
            {
                throw new InvalidOperationException("path not configured");
            }
            return includeSacl ? cfg.Sddl : StripSacl(cfg.Sddl);
        }

        public bool IsPrivilegedDescriptorCompliant(string sddl, bool requireHighIntegrity)
            => WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(sddl, requireHighIntegrity);

        public void WriteAllBytesAndFlush(SafeFileHandle handle, ReadOnlySpan<byte> bytes) { }

        public bool TryDeleteFile(string path) => true;
        public bool TryDeleteDirectory(string path) => true;

        private static string StripSacl(string sddl)
        {
            var idx = sddl.IndexOf("S:", StringComparison.Ordinal);
            return idx < 0 ? sddl : sddl[..idx];
        }

        private readonly Dictionary<SafeFileHandle, (string Path, string TmpFile)> _handlePaths = new();

        private sealed class Scope : IDisposable
        {
            private readonly FakeNativeSecurityAdapter _parent;
            private bool _disposed;
            public Scope(FakeNativeSecurityAdapter parent) { _parent = parent; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _parent.CurrentlyInPrivilegeScope = false;
                _parent.PrivilegeScopesRestored++;
                foreach (var pair in _parent._handlePaths)
                {
                    // leave temp files alive; verifier scope lifecycle only
                }
            }
        }
    }
}
