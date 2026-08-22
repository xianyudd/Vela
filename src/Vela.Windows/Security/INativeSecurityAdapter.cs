using Microsoft.Win32.SafeHandles;

namespace Vela.Windows.Security;

/// <summary>
/// Narrow boundary over the Win32 APIs required to create, verify and pin
/// privileged DiskPart workspace objects. All privileged/native calls hang
/// off this single adapter so unit tests can run on any OS with fakes.
/// </summary>
public interface INativeSecurityAdapter
{
    /// <summary>
    /// Acquires <c>SeSecurityPrivilege</c> on the current token for the
    /// lifetime of the returned scope. Restores the previous state on dispose.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the privilege cannot be enabled.</exception>
    IDisposable AcquireSecurityPrivilegeScope();

    /// <summary>
    /// Returns true when <c>SeSecurityPrivilege</c> is currently enabled on the
    /// calling token (used to assert restoration in tests).
    /// </summary>
    bool IsSecurityPrivilegeEnabled();

    /// <summary>
    /// Validates an SDDL security descriptor string without persisting anything.
    /// Throws when malformed.
    /// </summary>
    void ValidateSecurityDescriptor(string sddl);

    /// <summary>
    /// Creates the directory at <paramref name="path"/> — applying the supplied SDDL
    /// descriptor so the object is visible with the final protected descriptor.
    /// If the directory already exists, no descriptor change is applied.
    /// </summary>
    /// <returns>True when created; false when it already existed.</returns>
    bool CreateDirectoryWithDescriptor(string path, string sddl);

    /// <summary>
    /// Opens a directory handle with reparse-safe flags (<c>FILE_FLAG_BACKUP_SEMANTICS</c>
    /// | <c>FILE_FLAG_OPEN_REPARSE_POINT</c>) so reparse points are surfaced, not followed.
    /// </summary>
    SafeFileHandle OpenDirectoryByHandle(string path);

    /// <summary>
    /// Opens a file handle with the requested access/share/creation and
    /// <c>FILE_FLAG_OPEN_REPARSE_POINT</c>. When <paramref name="sddl"/> is non-null,
    /// it is applied at creation time (atomic).
    /// </summary>
    SafeFileHandle OpenFileHandle(
        string path,
        FileAccess access,
        FileShare share,
        FileMode mode,
        string? sddl);

    /// <summary>
    /// Returns true when the handle refers to a reparse point (symlink/junction).
    /// </summary>
    bool IsReparsePoint(SafeFileHandle handle);

    /// <summary>
    /// Returns the canonical identity (volume serial, file index, length, last write)
    /// of the object behind <paramref name="handle"/>.
    /// </summary>
    FileIdentity GetFileIdentity(SafeFileHandle handle);

    /// <summary>
    /// Returns the canonical path resolved from the handle (volume path, not reparse).
    /// </summary>
    string GetFinalPathName(SafeFileHandle handle);

    /// <summary>
    /// Reads the SDDL for the object behind <paramref name="handle"/>, requesting
    /// owner+group+DACL always, plus the mandatory integrity label when
    /// <paramref name="includeIntegrityLabel"/> is true.
    /// </summary>
    /// <remarks>
    /// The returned string is whatever the OS renders from the stored binary
    /// descriptor, which differs from the SDDL that was authored — see
    /// <see cref="WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant"/>
    /// for the normalisations callers must tolerate. Implementations must read the
    /// label through the label view rather than the audit view so the read stays
    /// possible with plain READ_CONTROL.
    /// </remarks>
    string ReadSecurityDescriptorSddl(SafeFileHandle handle, bool includeIntegrityLabel);

    /// <summary>
    /// Returns true when the supplied SDDL conforms to the Vela privileged shape:
    /// owner BUILTIN\Administrators, DACL protected (no inheritance) with exactly
    /// SYSTEM:F and Administrators:F, and high-integrity no-write-up label.
    /// </summary>
    bool IsPrivilegedDescriptorCompliant(string sddl, bool requireHighIntegrity);

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="handle"/> and flushes to
    /// disk (FlushFileBuffers).
    /// </summary>
    void WriteAllBytesAndFlush(SafeFileHandle handle, ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Best-effort delete for lease cleanup. Returns true on success.
    /// </summary>
    bool TryDeleteFile(string path);

    /// <summary>
    /// Best-effort directory delete for lease cleanup. Returns true on success.
    /// </summary>
    bool TryDeleteDirectory(string path);
}
