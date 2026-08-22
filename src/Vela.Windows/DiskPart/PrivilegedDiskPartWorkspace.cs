using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Vela.Windows.Security;

namespace Vela.Windows.DiskPart;

/// <summary>
/// Real privileged workspace rooted under
/// <c>%ProgramData%\Vela\Privileged\DiskPart\&lt;run-id&gt;\vela-diskpart-&lt;nonce&gt;.txt</c>.
///
/// Creation applies the final protected SDDL atomically, capture file identity,
/// then pins a read-only handle while the lease is alive. Cleanup is best-effort.
/// </summary>
public sealed class PrivilegedDiskPartWorkspace : IPrivilegedDiskPartWorkspace
{
    private const string AnchorFolderName = "Vela";
    private const string PrivilegedFolderName = "Privileged";
    private const string DiskPartFolderName = "DiskPart";
    private const string ScriptFilePrefix = "vela-diskpart-";

    private readonly INativeSecurityAdapter _adapter;
    private readonly WindowsObjectSecurityVerifier _verifier;

    public PrivilegedDiskPartWorkspace()
        : this(new WindowsNativeSecurityAdapter())
    {
    }

    public PrivilegedDiskPartWorkspace(INativeSecurityAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
        _verifier = new WindowsObjectSecurityVerifier(adapter);
    }

    public async Task<IPrivilegedDiskPartScriptLease> CreateScriptAsync(
        Guid runId,
        string script,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("The run identifier must not be empty.", nameof(runId));
        }
        ArgumentNullException.ThrowIfNull(script);
        cancellationToken.ThrowIfCancellationRequested();

        var root = ComputeRootPath();
        var runSegment = runId.ToString("D", CultureInfo.InvariantCulture);
        var runDir = Path.Combine(root, runSegment);
        EnsureDirectoryChainWithDescriptor(runDir);

        var nonce = GenerateNonce();
        var fileName = $"{ScriptFilePrefix}{nonce}.txt";
        var scriptPath = Path.Combine(runDir, fileName);

        // 1) Create file atomically with CREATE_NEW + SDDL, write bytes, flush.
        var writeHandle = _adapter.OpenFileHandle(
            scriptPath,
            FileAccess.Write,
            FileShare.Read,
            FileMode.CreateNew,
            WindowsSecurityDescriptorFactory.CreatePrivilegedFileSddl());
        FileIdentity creationIdentity;
        try
        {
            var bytes = Encoding.ASCII.GetBytes(script);
            _adapter.WriteAllBytesAndFlush(writeHandle, bytes);
            creationIdentity = _adapter.GetFileIdentity(writeHandle);
        }
        finally
        {
            writeHandle.Dispose();
        }

        // 2) Reopen read-only pin and confirm identity matches the creation handle.
        SafeFileHandle pin = _adapter.OpenFileHandle(
            scriptPath,
            FileAccess.Read,
            FileShare.Read,
            FileMode.Open,
            sddl: null);
        try
        {
            var reopenIdentity = _adapter.GetFileIdentity(pin);
            if (!reopenIdentity.Equals(creationIdentity))
            {
                throw new InvalidOperationException(
                    "Script file identity changed between creation and pin reopen.");
            }

            // 3) Verify file handle (canonical path + protected descriptor).
            _verifier.AssertProtectedFileHandle(pin, scriptPath, creationIdentity);

            // 4) Verify parent directory chain.
            _verifier.AssertProtectedDirectory(runDir, root);
            _verifier.AssertProtectedDirectory(root, GetTrustedPrefix());
        }
        catch
        {
            pin.Dispose();
            throw;
        }

        return await Task.FromResult<IPrivilegedDiskPartScriptLease>(
            new PrivilegedDiskPartScriptLease(
                scriptPath,
                runDir,
                pin,
                creationIdentity,
                this));
    }

    /// <summary>
    /// Computes <c>%ProgramData%\Vela\Privileged\DiskPart</c>.
    /// </summary>
    public static string ComputeRootPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            throw new InvalidOperationException(
                "CommonApplicationData (ProgramData) could not be resolved; cannot create a privileged workspace.");
        }

        return Path.Combine(
            programData,
            AnchorFolderName,
            PrivilegedFolderName,
            DiskPartFolderName);
    }

    public static string GetTrustedPrefix()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, AnchorFolderName);
    }

    /// <summary>
    /// Materialises every directory from the trusted anchor down to
    /// <paramref name="leafPath"/>, inclusive.
    /// </summary>
    /// <remarks>
    /// Win32 <c>CreateDirectoryW</c> does not create intermediate directories, so
    /// the chain has to be walked one segment at a time: asking for the leaf on a
    /// machine that has never hosted the workspace fails with
    /// ERROR_PATH_NOT_FOUND. Each segment is created and verified before its
    /// child is created, so a hijacked ancestor stops the walk instead of being
    /// silently adopted as the parent of a new privileged object.
    /// </remarks>
    private void EnsureDirectoryChainWithDescriptor(string leafPath)
    {
        foreach (var segment in EnumerateAnchoredChain(leafPath))
        {
            EnsureDirectoryWithDescriptor(segment);
        }
    }

    /// <summary>
    /// Returns the directory chain from <see cref="GetTrustedPrefix"/> down to
    /// <paramref name="leafPath"/>, anchor first.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// When <paramref name="leafPath"/> does not sit inside the trusted anchor.
    /// </exception>
    private static IReadOnlyList<string> EnumerateAnchoredChain(string leafPath)
    {
        var anchor = Normalize(GetTrustedPrefix());
        var current = Normalize(leafPath);
        var chain = new List<string>();

        while (!string.Equals(current, anchor, StringComparison.OrdinalIgnoreCase))
        {
            chain.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Length >= current.Length)
            {
                throw new InvalidOperationException(
                    $"Privileged path '{leafPath}' is outside the trusted anchor '{anchor}'.");
            }

            current = parent;
        }

        chain.Add(anchor);
        chain.Reverse();
        return chain;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private void EnsureDirectoryWithDescriptor(string path)
    {
        // CreateDirectoryWithDescriptor returns false when it already exists;
        // in that case we still verify descriptor + reparse + canonical form.
        _adapter.CreateDirectoryWithDescriptor(
            path,
            WindowsSecurityDescriptorFactory.CreatePrivilegedDirectorySddl());

        _verifier.AssertProtectedDirectory(path, GetTrustedPrefix());
    }

    private static string GenerateNonce()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    /// <summary>
    /// Re-verifies the pinned script and its parents before handing the path
    /// back to the caller.
    /// </summary>
    internal void ReVerify(string scriptPath, string runDir, SafeFileHandle pin, FileIdentity identity)
    {
        _verifier.AssertProtectedFileHandle(pin, scriptPath, identity);
        _verifier.AssertProtectedDirectory(runDir, ComputeRootPath());
        _verifier.AssertProtectedDirectory(ComputeRootPath(), GetTrustedPrefix());
    }

    internal void Cleanup(string scriptPath, string runDir)
    {
        // Best-effort cleanup: failures here are non-fatal (lease ownership ends).
        try
        {
            _adapter.TryDeleteFile(scriptPath);
        }
        catch
        {
            // ignore
        }

        try
        {
            _adapter.TryDeleteDirectory(runDir);
        }
        catch
        {
            // ignore
        }
    }
}

internal sealed class PrivilegedDiskPartScriptLease : IPrivilegedDiskPartScriptLease
{
    private readonly SafeFileHandle _pin;
    private readonly FileIdentity _identity;
    private readonly PrivilegedDiskPartWorkspace _owner;
    private readonly string _runDir;
    private bool _disposed;

    public PrivilegedDiskPartScriptLease(
        string scriptPath,
        string runDir,
        SafeFileHandle pin,
        FileIdentity identity,
        PrivilegedDiskPartWorkspace owner)
    {
        ScriptPath = scriptPath;
        _runDir = runDir;
        _pin = pin;
        _identity = identity;
        _owner = owner;
    }

    public string ScriptPath { get; }

    public ValueTask VerifyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _owner.ReVerify(ScriptPath, _runDir, _pin, _identity);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            _pin.Dispose();
        }
        finally
        {
            _owner.Cleanup(ScriptPath, _runDir);
        }
        return ValueTask.CompletedTask;
    }
}
