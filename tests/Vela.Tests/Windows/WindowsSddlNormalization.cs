namespace Vela.Tests.Windows;

/// <summary>
/// 把"我们写入的 SDDL"翻译成"Windows 回读时渲染出来的 SDDL"。
/// </summary>
/// <remarks>
/// fake adapter 必须经过这一层再把描述符交回被测代码。原因是一整串 bug 的共同根源:
/// 早先的 fake 逐字回显写入的 SDDL, 于是所有合规校验都只在"OS 不会做任何规范化"这个
/// 假设下被验证过, 真机上却每一层都不成立 —— 每修一层才露出下一层。
///
/// 这里建模的规范化行为都是在本机实测过的 (Win32 GetSecurityInfo +
/// ConvertSecurityDescriptorToStringSecurityDescriptor):
/// <list type="bullet">
/// <item>显式受保护的 DACL 不持久化 auto-inherit 位: 写入 <c>D:PAI</c> 回读成 <c>D:P</c>。</item>
/// <item>系统 ACL 自带控制位: 写入 <c>S:(ML;;NW;;;HI)</c> 回读成 <c>S:AI(ML;;NW;;;HI)</c>。</item>
/// <item>只请求 owner+group+DACL 时, 回读结果里根本没有 <c>S:</c> 段。</item>
/// </list>
/// </remarks>
internal static class WindowsSddlNormalization
{
    /// <summary>模拟 OS 存储并重新渲染一个我们刚写入的描述符。幂等。</summary>
    public static string AsStoredByWindows(string authored)
    {
        if (string.IsNullOrWhiteSpace(authored))
        {
            return authored;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var (key, value) in SplitSections(authored))
        {
            builder.Append(key).Append(':').Append(key switch
            {
                'D' => DropAutoInheritFromProtectedDacl(value),
                'S' => AddAutoInheritToSystemAcl(value),
                _ => value,
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// 模拟一次读取: <paramref name="includeIntegrityLabel"/> 为 false 时请求的是
    /// owner+group+DACL, 结果里不含 <c>S:</c> 段。
    /// </summary>
    public static string AsReadBack(string stored, bool includeIntegrityLabel)
    {
        var normalized = AsStoredByWindows(stored);
        if (includeIntegrityLabel)
        {
            return normalized;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var (key, value) in SplitSections(normalized))
        {
            if (key != 'S')
            {
                builder.Append(key).Append(':').Append(value);
            }
        }

        return builder.ToString();
    }

    private static string DropAutoInheritFromProtectedDacl(string dacl)
    {
        var aceStart = dacl.IndexOf('(', StringComparison.Ordinal);
        var flags = aceStart < 0 ? dacl : dacl[..aceStart];
        var aces = aceStart < 0 ? string.Empty : dacl[aceStart..];
        if (!flags.Contains('P', StringComparison.Ordinal))
        {
            return dacl;
        }

        return flags.Replace("AI", string.Empty, StringComparison.Ordinal) + aces;
    }

    private static string AddAutoInheritToSystemAcl(string sacl)
    {
        var aceStart = sacl.IndexOf('(', StringComparison.Ordinal);
        if (aceStart < 0)
        {
            return sacl;
        }

        var flags = sacl[..aceStart];
        return flags.Length == 0 ? "AI" + sacl : sacl;
    }

    /// <summary>按 <c>O:</c>/<c>G:</c>/<c>D:</c>/<c>S:</c> 拆段, 括号内不识别段标记。</summary>
    private static List<(char Key, string Value)> SplitSections(string sddl)
    {
        var sections = new List<(char, string)>();
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
                sections.Add((key, sddl[valueStart..i]));
            }

            key = c;
            i++;
            valueStart = i + 1;
        }

        if (key != '\0')
        {
            sections.Add((key, sddl[valueStart..]));
        }

        return sections;
    }
}
