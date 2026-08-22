using Vela.Windows.Security;

namespace Vela.Tests.Windows;

public sealed class WindowsSecurityDescriptorFactoryTests
{
    private const string Compliant =
        "O:BAG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)S:(ML;;NW;;;HI)";

    [Fact]
    public void CreatesExpectedSddlForAdministratorsSystemOnlyHighIntegrity()
    {
        var sddl = WindowsSecurityDescriptorFactory.CreatePrivilegedDirectorySddl();

        Assert.StartsWith("O:BA", sddl, StringComparison.Ordinal);
        Assert.Contains("G:SY", sddl, StringComparison.Ordinal);
        Assert.Contains("D:PAI", sddl, StringComparison.Ordinal);
        Assert.Contains("(A;;FA;;;SY)", sddl, StringComparison.Ordinal);
        Assert.Contains("(A;;FA;;;BA)", sddl, StringComparison.Ordinal);
        Assert.Contains("S:(ML;;NW;;;HI)", sddl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("O:BA")]                                     // 不完整
    [InlineData("O:BAD:PAI(A;;FA;;;SY)")]                    // 只有 SY
    [InlineData("O:BAD:PAI(A;;FA;;;BA)")]                    // 只有 BA
    [InlineData("")]                                         // 空
    [InlineData("   ")]                                      // 空白
    [InlineData("O:IAD:PAI(A;;FA;;;SY)(A;;FA;;;BA)S:(ML;;NW;;;HI)")] // 错误 owner
    public void RejectsMalformedOrIncompleteDescriptors(string sddl)
    {
        Assert.False(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(sddl, requireHighIntegrity: true));
    }

    [Fact]
    public void RejectsDirectoryWithInheritanceOrCurrentUserAce()
    {
        // DACL 允许继承 (未 P) — 必须拒绝
        var inherit = "O:BAG:SYD:AI(A;;FA;;;SY)(A;;FA;;;BA)S:(ML;;NW;;;HI)";
        Assert.False(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(inherit, requireHighIntegrity: true));

        // 含有 current user (IU) ACE — 必须拒绝
        var iuAce = "O:BAG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;IU)S:(ML;;NW;;;HI)";
        Assert.False(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(iuAce, requireHighIntegrity: true));
    }

    [Fact]
    public void AcceptsCompliantDescriptor()
    {
        Assert.True(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(Compliant, requireHighIntegrity: true));
    }

    [Fact]
    public void WhenHighIntegrityNotRequired_AllowsDescriptorWithoutSacl()
    {
        const string noSacl = "O:BAG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)";
        Assert.True(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(noSacl, requireHighIntegrity: false));
    }

    [Fact]
    public void WhenHighIntegrityRequired_RejectsMissingOrLowIntegrity()
    {
        const string noSacl = "O:BAG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)";
        Assert.False(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(noSacl, requireHighIntegrity: true));

        const string lowIntegrity = "O:BAG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)S:(ML;;NW;;;LW)";
        Assert.False(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(lowIntegrity, requireHighIntegrity: true));
    }

    [Fact]
    public void AcceptsProtectedDaclReadBackWithoutAutoInheritFlag()
    {
        // Windows 不会在显式受保护 (P) 的 DACL 上持久化 AI 位, 所以真机把对象回读成
        // "D:P" 而不是写入时的 "D:PAI"。合规校验必须接受这个回读形态, 否则每次创建
        // privileged 目录后的自校验都会把自己刚建出来的对象判为不合规, 压缩永远走不过
        // DiskPart preflight。
        const string readBackWithSacl =
            "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:(ML;;NW;;;HI)";
        Assert.True(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(readBackWithSacl, requireHighIntegrity: true));

        const string readBackDaclOnly = "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)";
        Assert.True(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(readBackDaclOnly, requireHighIntegrity: false));
    }

    // ---------------------------------------------------------------------
    // 真机规范化语料。下面每个字符串都是在本机用 Win32 GetSecurityInfo +
    // ConvertSecurityDescriptorToStringSecurityDescriptor 实测过的形态 (或由实测
    // 规律直接推出的等价形态)。这些用例存在的意义: 合规校验必须一次性认全所有
    // 规范化差异, 而不是每上线一次露出一层。
    // ---------------------------------------------------------------------

    [Theory]
    // DACL: 写入 D:PAI, 回读 D:P (AI 不在受保护 DACL 上持久化)
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // 系统 ACL 自带控制位 AI
    [InlineData("O:BAG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // 受保护的系统 ACL + 带继承标志的 ML ACE (实测在 C:\ 上就是这个形态);
    // ML ACE 的继承标志不削弱本对象的标签, 必须容忍
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:P(ML;OINPIO;NW;;;HI)")]
    // owner/group/trustee/标签级别渲染成裸 SID (不是每个 well-known SID 都有别名)
    [InlineData("O:S-1-5-32-544G:S-1-5-18D:P(A;;FA;;;S-1-5-18)(A;;FA;;;S-1-5-32-544)S:AI(ML;;NW;;;S-1-16-12288)")]
    // 权限渲染成数值掩码而不是 FA 别名 (0x1f01ff == FILE_ALL_ACCESS)
    [InlineData("O:BAG:SYD:P(A;;0x1f01ff;;;SY)(A;;0x1F01FF;;;BA)S:AI(ML;;NW;;;HI)")]
    // ACE 顺序颠倒
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;FA;;;SY)S:AI(ML;;NW;;;HI)")]
    // AR (auto-inherit required) 也可能出现在控制位里
    [InlineData("O:BAG:SYD:PARAI(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    public void AcceptsMeasuredReadBackShapes(string sddl)
    {
        Assert.True(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(sddl, requireHighIntegrity: true),
            $"真机回读形态被误判为不合规: {sddl}");
    }

    [Theory]
    // 缺 P: 继承来的 ACE 可以放宽访问
    [InlineData("O:BAG:SYD:AI(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // 第三个 ACE
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)S:AI(ML;;NW;;;HI)")]
    // 只剩一个 ACE
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)S:AI(ML;;NW;;;HI)")]
    // 同一个 trustee 出现两次 (凑不出 SY+BA 两方)
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // DACL ACE 带 ID (继承而来) — 不是我们写的, 拒绝
    [InlineData("O:BAG:SYD:P(A;ID;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // DACL ACE 带 OICI (会传播给子对象) — 拒绝
    [InlineData("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // deny ACE 冒充
    [InlineData("O:BAG:SYD:P(D;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // 非完全控制 (0x1200a9 = 读+执行)
    [InlineData("O:BAG:SYD:P(A;;0x1200a9;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    [InlineData("O:BAG:SYD:P(A;;FRFX;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // 空 DACL / NULL DACL
    [InlineData("O:BAG:SYD:PS:AI(ML;;NW;;;HI)")]
    [InlineData("O:BAG:SYD:NO_ACCESS_CONTROLS:AI(ML;;NW;;;HI)")]
    // 缺 group
    [InlineData("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // owner 是别人 (裸 SID 形态也要拒)
    [InlineData("O:S-1-5-18G:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // 标签级别不够 (中/低完整性)
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;ME)")]
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;S-1-16-8192)")]
    // 有 S: 段但没有 ACE
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:")]
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI")]
    // 审计 ACE 顶替了强制标签 ACE
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(AU;SAFA;FA;;;WD)")]
    // 标签 ACE 之外还多一条
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)(AU;SA;FA;;;WD)")]
    // 条件 ACE (7 段) — 解析不了就拒绝, 不去猜
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(XA;;FA;;;BA;(Member_of{SID(BA)}))S:AI(ML;;NW;;;HI)")]
    // 括号不配对
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // 段标记前有内容
    [InlineData("junkO:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    // 同一段出现两次
    [InlineData("O:BAO:SYG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)")]
    public void RejectsNonCompliantReadBackShapes(string sddl)
    {
        Assert.False(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(sddl, requireHighIntegrity: true),
            $"不合规描述符被放过了: {sddl}");
    }

    [Fact]
    public void BasicPass_AcceptsOwnerGroupDaclOnlyAndRefusesAnUnexpectedSystemAcl()
    {
        // 第一趟只请求 owner+group+DACL, 回读里就不该有 S: 段。
        const string ownerGroupDacl = "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)";
        Assert.True(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(ownerGroupDacl, requireHighIntegrity: false));

        // 出现 S: 说明读的方式和调用方以为的不一样 — 宁可失败, 不要少校验。
        const string withSystemAcl = "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)";
        Assert.False(
            WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(withSystemAcl, requireHighIntegrity: false));
    }

    [Fact]
    public void AuthoredSddlSurvivesItsOwnRoundTripThroughWindowsNormalisation()
    {
        // 端到端不变量: 我们写下去的 SDDL, 经过真机规范化后必须还能通过自校验。
        // 这一条不成立时, privileged 目录一建出来就自判不合规, DiskPart preflight
        // 永远走不过 —— 这正是之前那一串失败的形状。
        var authored = WindowsSecurityDescriptorFactory.CreatePrivilegedDirectorySddl();

        Assert.True(WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(
            WindowsSddlNormalization.AsReadBack(authored, includeIntegrityLabel: false),
            requireHighIntegrity: false));

        Assert.True(WindowsSecurityDescriptorFactory.IsPrivilegedDescriptorCompliant(
            WindowsSddlNormalization.AsReadBack(authored, includeIntegrityLabel: true),
            requireHighIntegrity: true));
    }

    [Fact]
    public void NormalisationHelper_ModelsTheMeasuredReadBackForms()
    {
        var authored = WindowsSecurityDescriptorFactory.CreatePrivilegedDirectorySddl();

        Assert.Equal(
            "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)S:AI(ML;;NW;;;HI)",
            WindowsSddlNormalization.AsReadBack(authored, includeIntegrityLabel: true));

        Assert.Equal(
            "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)",
            WindowsSddlNormalization.AsReadBack(authored, includeIntegrityLabel: false));

        // 幂等: 已经规范化过的串再过一次不变 (fake 里 store/read 各调一次)。
        var once = WindowsSddlNormalization.AsStoredByWindows(authored);
        Assert.Equal(once, WindowsSddlNormalization.AsStoredByWindows(once));

        // 非受保护 DACL 上的 AI 是真实存在的, 不该被剥掉。
        Assert.Equal(
            "O:BAG:SYD:AI(A;;FA;;;SY)",
            WindowsSddlNormalization.AsStoredByWindows("O:BAG:SYD:AI(A;;FA;;;SY)"));
    }
}
