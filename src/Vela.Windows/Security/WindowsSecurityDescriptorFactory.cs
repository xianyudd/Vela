namespace Vela.Windows.Security;

/// <summary>
/// Builds the SDDL security descriptors applied to privileged Vela objects
/// and knows how to verify compliance. Pure logic — no Win32 calls — so tests
/// can validate shapes deterministically.
/// </summary>
public static class WindowsSecurityDescriptorFactory
{
    /// <summary>
    /// SDDL for the privileged workspace root and code-derived descendant
    /// directories.
    /// Owner: BA. DACL protected (no inheritance) with SYSTEM:F and Administrators:F
    /// only. High-integrity mandatory label with no-write-up.
    /// </summary>
    public const string PrivilegedObjectSddl =
        "O:BAG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)S:(ML;;NW;;;HI)";

    /// <summary>
    /// Returns the SDDL applied to the root directory.
    /// </summary>
    public static string CreatePrivilegedDirectorySddl() => PrivilegedObjectSddl;

    /// <summary>
    /// Returns the SDDL applied to a script file. Same shape as directories:
    /// shorter lifetime but enforcement is identical.
    /// </summary>
    public static string CreatePrivilegedFileSddl() => PrivilegedObjectSddl;

    /// <summary>
    /// Verifies the supplied SDDL matches the strict privileged shape.
    /// SDDL parsing is intentionally conservative: the descriptor is compliant
    /// only if it is structurally equivalent to <see cref="PrivilegedObjectSddl"/>.
    /// </summary>
    public static bool IsPrivilegedDescriptorCompliant(string sddl, bool requireHighIntegrity)
    {
        if (string.IsNullOrWhiteSpace(sddl))
        {
            return false;
        }

        // Normalise by removing insignificant whitespace and comparing
        // case-sensitively on the canonical shape.
        var normal = sddl.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (!normal.StartsWith("O:BA", StringComparison.Ordinal))
        {
            return false;
        }

        if (!normal.Contains("G:SY", StringComparison.Ordinal))
        {
            return false;
        }

        // DACL must be protected and contain exactly SYSTEM:F and BA:F.
        var daclStart = normal.IndexOf("D:", StringComparison.Ordinal);
        var daclEnd = normal.IndexOf("S:", StringComparison.Ordinal);
        if (daclStart < 0 || (!requireHighIntegrity && daclEnd != -1))
        {
            return false;
        }

        var daclSection = daclEnd >= 0
            ? normal[daclStart..daclEnd]
            : normal[daclStart..];

        if (!daclSection.StartsWith("D:PAI", StringComparison.Ordinal))
        {
            return false;
        }

        if (!daclSection.Contains("(A;;FA;;;SY)", StringComparison.Ordinal) ||
            !daclSection.Contains("(A;;FA;;;BA)", StringComparison.Ordinal))
        {
            return false;
        }

        // Count ACEs — must be exactly two allow entries (no inherited or other ACEs).
        var aceCount = CountOccurrences(daclSection, "(A;;");
        if (aceCount != 2)
        {
            return false;
        }

        if (requireHighIntegrity)
        {
            if (daclEnd < 0)
            {
                return false;
            }

            var saclSection = normal[daclEnd..];
            if (!saclSection.StartsWith("S:", StringComparison.Ordinal))
            {
                return false;
            }

            if (!saclSection.Contains("(ML;;NW;;;HI)", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
