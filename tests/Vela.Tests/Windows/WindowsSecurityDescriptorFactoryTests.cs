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
}
