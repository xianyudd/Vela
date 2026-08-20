namespace Vela.Windows.Security;

/// <summary>
/// Verifies that privileged Vela file-system objects meet the security bar:
/// protected SDDL, no reparse points, stable file identity, canonical paths.
/// </summary>
public sealed class WindowsObjectSecurityVerifier
{
    private readonly INativeSecurityAdapter _adapter;

    public WindowsObjectSecurityVerifier(INativeSecurityAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
    }

    /// <summary>
    /// Verifies the directory at <paramref name="path"/> meets the privileged
    /// descriptor bar, is not a reparse point, and resolves to a canonical
    /// path under the expected anchor.
    /// </summary>
    /// <exception cref="InvalidOperationException">On any mismatch (fail-closed).</exception>
    public void AssertProtectedDirectory(string path, string expectedCanonicalPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCanonicalPrefix);

        using var handle = _adapter.OpenDirectoryByHandle(path);
        AssertProtectedHandle(handle, expectedCanonicalPrefix, isFile: false);
    }

    /// <summary>
    /// Verifies a file handle meets the privileged bar. Optionally supplies the
    /// earlier capture identity to detect mid-flight replacement.
    /// </summary>
    /// <exception cref="InvalidOperationException">On any mismatch (fail-closed).</exception>
    public void AssertProtectedFileHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity? expectedEarlierIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCanonicalPath);

        AssertNotReparse(handle);

        var identity = _adapter.GetFileIdentity(handle);
        if (expectedEarlierIdentity is not null && !identity.Equals(expectedEarlierIdentity))
        {
            throw new InvalidOperationException(
                "Privileged file identity changed between creation and re-verification.");
        }

        var canonical = _adapter.GetFinalPathName(handle);
        if (!PathEquals(canonical, expectedCanonicalPath))
        {
            throw new InvalidOperationException(
                $"Privileged path '{expectedCanonicalPath}' resolves to unexpected canonical '{canonical}'.");
        }

        AssertPrivilegedDescriptor(handle, requireHighIntegrity: true);
    }

    /// <summary>
    /// Verifies a directory handle meets the privileged bar.
    /// </summary>
    /// <exception cref="InvalidOperationException">On any mismatch (fail-closed).</exception>
    public void AssertProtectedDirectoryHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string expectedCanonicalPrefix)
    {
        ArgumentNullException.ThrowIfNull(handle);
        AssertProtectedHandle(handle, expectedCanonicalPrefix, isFile: false);
    }

    private void AssertProtectedHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string expectedCanonicalPrefix,
        bool isFile)
    {
        AssertNotReparse(handle);

        var canonical = _adapter.GetFinalPathName(handle);
        EnsureCanonicalPrefix(canonical, expectedCanonicalPrefix);

        AssertPrivilegedDescriptor(handle, requireHighIntegrity: true);
    }

    private void AssertNotReparse(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (_adapter.IsReparsePoint(handle))
        {
            throw new InvalidOperationException(
                "Path contains a reparse point (symlink/junction) — refusing to operate on it.");
        }
    }

    private void EnsureCanonicalPrefix(string canonical, string expectedCanonicalPrefix)
    {
        var prefix = expectedCanonicalPrefix.EndsWith('\\')
            ? expectedCanonicalPrefix
            : expectedCanonicalPrefix + "\\";

        var matches =
            canonical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonical.TrimEnd('\\'), expectedCanonicalPrefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        if (!matches)
        {
            throw new InvalidOperationException(
                $"Canonical path '{canonical}' is outside the trusted prefix '{expectedCanonicalPrefix}'.");
        }
    }

    private void AssertPrivilegedDescriptor(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        bool requireHighIntegrity)
    {
        // First pass: owner+group+DACL only (READ_CONTROL suffices).
        var sddlBasic = _adapter.ReadSecurityDescriptorSddl(handle, includeSacl: false);
        if (!WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(sddlBasic, requireHighIntegrity: false))
        {
            throw new InvalidOperationException(
                $"Object security descriptor '{sddlBasic}' does not match the privileged shape.");
        }

        if (!requireHighIntegrity)
        {
            return;
        }

        // Second pass: requires SACL read; bracket with the security privilege scope
        // so the token mutation is always restored, even on failure.
        using (WindowsTokenPrivilegeScope.Acquire(_adapter))
        {
            var sddlWithSacl = _adapter.ReadSecurityDescriptorSddl(handle, includeSacl: true);
            if (!WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(sddlWithSacl, requireHighIntegrity: true))
            {
                throw new InvalidOperationException(
                    $"Object security descriptor '{sddlWithSacl}' is missing the required high-integrity label.");
            }
        }

        // Scope restore guarantees the privilege is no longer held here.
        if (_adapter.IsSecurityPrivilegeEnabled())
        {
            throw new InvalidOperationException(
                "SeSecurityPrivilege was not restored after the SACL read.");
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
