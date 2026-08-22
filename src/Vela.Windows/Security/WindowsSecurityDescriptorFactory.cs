using System.Globalization;

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

    /// <summary>FILE_ALL_ACCESS — what the "FA" rights alias expands to.</summary>
    private const uint FileAllAccess = 0x001F01FF;

    /// <summary>SYSTEM_MANDATORY_LABEL_NO_WRITE_UP — what the "NW" policy alias expands to.</summary>
    private const uint MandatoryLabelNoWriteUp = 0x00000001;

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
    /// Verifies the supplied SDDL grants exactly the privileged shape:
    /// owner <c>BUILTIN\Administrators</c>, primary group <c>NT AUTHORITY\SYSTEM</c>,
    /// a protected DACL holding nothing but SYSTEM:F and Administrators:F, and —
    /// when <paramref name="requireHighIntegrity"/> — a high-integrity
    /// no-write-up mandatory label.
    /// </summary>
    /// <remarks>
    /// This runs against the descriptor Windows <em>hands back</em>, which is not
    /// the string we authored: the OS re-renders a descriptor from its binary form
    /// and normalises it on the way out. Every check below therefore compares
    /// semantics, not spelling. Measured differences that this must tolerate:
    /// <list type="bullet">
    /// <item>The auto-inherit flag is not persisted on an explicitly protected
    /// DACL, so authored <c>D:PAI</c> reads back as <c>D:P</c>. Only the
    /// security-bearing <c>P</c> is required; other control flags are ignored.</item>
    /// <item>The SACL carries its own control flags — authored <c>S:(ML;…)</c>
    /// reads back as <c>S:AI(ML;…)</c>, and inherited labels appear as
    /// <c>S:P(ML;OINPIO;NW;;;HI)</c> — so label ACE flags are ignored too.</item>
    /// <item>Trustees and the label level may render as either an SDDL alias or a
    /// raw SID (<c>BA</c>/<c>S-1-5-32-544</c>), and rights either as an alias or a
    /// hex mask (<c>FA</c>/<c>0x1f01ff</c>). Both spellings are accepted.</item>
    /// </list>
    /// Everything that actually bears on access stays strict: the DACL must be
    /// protected, hold exactly two allow ACEs with no inheritance flags, and grant
    /// full access to nobody but SYSTEM and Administrators. Anything unparseable
    /// (including conditional ACEs) is rejected rather than guessed at.
    /// </remarks>
    public static bool IsPrivilegedDescriptorCompliant(string sddl, bool requireHighIntegrity)
    {
        if (string.IsNullOrWhiteSpace(sddl))
        {
            return false;
        }

        var normal = sddl.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (!TrySplitSections(normal, out var sections))
        {
            return false;
        }

        if (!sections.TryGetValue('O', out var owner) || !IsAdministrators(owner))
        {
            return false;
        }

        if (!sections.TryGetValue('G', out var group) || !IsLocalSystem(group))
        {
            return false;
        }

        if (!sections.TryGetValue('D', out var dacl) || !IsCompliantDacl(dacl))
        {
            return false;
        }

        var hasLabelSection = sections.TryGetValue('S', out var sacl);
        if (!requireHighIntegrity)
        {
            // The basic pass reads owner+group+DACL only. A system-ACL section here
            // means the read was wired up differently than the caller believes, so
            // refuse rather than silently verify less than was asked for.
            return !hasLabelSection;
        }

        return hasLabelSection && IsHighIntegrityLabel(sacl!);
    }

    /// <summary>
    /// Splits an SDDL string into its <c>O:</c>/<c>G:</c>/<c>D:</c>/<c>S:</c>
    /// sections, keyed by the section letter.
    /// </summary>
    /// <remarks>
    /// Section markers are only recognised outside parentheses. Scanning for the
    /// literal <c>"S:"</c> instead would mis-split any descriptor whose ACE list
    /// happens to contain the same two characters, and cannot tell a section
    /// marker from ACE content at all.
    /// </remarks>
    private static bool TrySplitSections(string sddl, out Dictionary<char, string> sections)
    {
        sections = new Dictionary<char, string>();
        var depth = 0;
        var key = '\0';
        var valueStart = 0;

        for (var i = 0; i < sddl.Length; i++)
        {
            var c = sddl[i];
            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
                continue;
            }

            if (depth != 0 || c is not ('O' or 'G' or 'D' or 'S'))
            {
                continue;
            }

            if (i + 1 >= sddl.Length || sddl[i + 1] != ':')
            {
                continue;
            }

            if (key != '\0')
            {
                if (!sections.TryAdd(key, sddl[valueStart..i]))
                {
                    return false;
                }
            }
            else if (i != 0)
            {
                // Content before the first section marker.
                return false;
            }

            key = c;
            i++;
            valueStart = i + 1;
        }

        return depth == 0 && key != '\0' && sections.TryAdd(key, sddl[valueStart..]);
    }

    private static bool IsCompliantDacl(string dacl)
    {
        var aceStart = dacl.IndexOf('(', StringComparison.Ordinal);
        if (aceStart < 0)
        {
            // No ACEs at all: either an empty or a NULL ("NO_ACCESS_CONTROL") DACL.
            return false;
        }

        // Only "P" (SE_DACL_PROTECTED) matters — it is what stops an inherited ACE
        // from widening access. "AI"/"AR" describe how the DACL was computed and
        // carry no rights of their own.
        if (!dacl[..aceStart].Contains('P', StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryParseAces(dacl[aceStart..], out var aces) || aces.Count != 2)
        {
            return false;
        }

        var sawSystem = false;
        var sawAdministrators = false;
        foreach (var ace in aces)
        {
            if (!string.Equals(ace.Type, "A", StringComparison.Ordinal))
            {
                return false;
            }

            // No inheritance, inherit-only, or inherited flags: each directory in
            // the chain is ACL'd explicitly, so a flagged ACE here did not come
            // from us.
            if (ace.Flags.Length != 0 ||
                ace.ObjectGuid.Length != 0 ||
                ace.InheritObjectGuid.Length != 0)
            {
                return false;
            }

            if (!IsFullAccess(ace.Rights))
            {
                return false;
            }

            if (IsLocalSystem(ace.Trustee))
            {
                if (sawSystem)
                {
                    return false;
                }
                sawSystem = true;
            }
            else if (IsAdministrators(ace.Trustee))
            {
                if (sawAdministrators)
                {
                    return false;
                }
                sawAdministrators = true;
            }
            else
            {
                return false;
            }
        }

        return sawSystem && sawAdministrators;
    }

    private static bool IsHighIntegrityLabel(string sacl)
    {
        var aceStart = sacl.IndexOf('(', StringComparison.Ordinal);
        if (aceStart < 0)
        {
            // "S:" with no ACE — the object carries no mandatory label.
            return false;
        }

        // Read through LABEL_SECURITY_INFORMATION this section holds mandatory-label
        // ACEs and nothing else, so more than one entry means an unexpected shape.
        if (!TryParseAces(sacl[aceStart..], out var aces) || aces.Count != 1)
        {
            return false;
        }

        var label = aces[0];
        return string.Equals(label.Type, "ML", StringComparison.Ordinal)
            && IsHighIntegrity(label.Trustee)
            && IsNoWriteUp(label.Rights);
    }

    /// <summary>
    /// Parses a run of <c>(type;flags;rights;objectGuid;inheritObjectGuid;trustee)</c>
    /// ACEs. Anything that is not exactly six fields — a conditional ACE, say —
    /// fails the parse so the caller rejects the descriptor.
    /// </summary>
    private static bool TryParseAces(string text, out List<Ace> aces)
    {
        aces = new List<Ace>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '(')
            {
                return false;
            }

            var close = text.IndexOf(')', i);
            if (close < 0)
            {
                return false;
            }

            var fields = text[(i + 1)..close].Split(';');
            if (fields.Length != 6)
            {
                return false;
            }

            aces.Add(new Ace(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5]));
            i = close + 1;
        }

        return aces.Count > 0;
    }

    private static bool IsAdministrators(string trustee) =>
        string.Equals(trustee, "BA", StringComparison.Ordinal) ||
        string.Equals(trustee, "S-1-5-32-544", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalSystem(string trustee) =>
        string.Equals(trustee, "SY", StringComparison.Ordinal) ||
        string.Equals(trustee, "S-1-5-18", StringComparison.OrdinalIgnoreCase);

    private static bool IsHighIntegrity(string trustee) =>
        string.Equals(trustee, "HI", StringComparison.Ordinal) ||
        string.Equals(trustee, "S-1-16-12288", StringComparison.OrdinalIgnoreCase);

    private static bool IsFullAccess(string rights) =>
        string.Equals(rights, "FA", StringComparison.Ordinal) ||
        (TryParseMask(rights, out var mask) && mask == FileAllAccess);

    private static bool IsNoWriteUp(string policy) =>
        policy.Contains("NW", StringComparison.Ordinal) ||
        (TryParseMask(policy, out var mask) && (mask & MandatoryLabelNoWriteUp) != 0);

    private static bool TryParseMask(string text, out uint mask)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(
                text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask);
        }

        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out mask);
    }

    private readonly record struct Ace(
        string Type,
        string Flags,
        string Rights,
        string ObjectGuid,
        string InheritObjectGuid,
        string Trustee);
}
