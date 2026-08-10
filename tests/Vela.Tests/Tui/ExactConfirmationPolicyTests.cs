using Vela.Tui.Views;

namespace Vela.Tests.Tui;

public sealed class ExactConfirmationPolicyTests
{
    [Theory]
    [InlineData("YES", true)]
    [InlineData("yes", false)]
    [InlineData("YES ", false)]
    [InlineData("YＥS", false)]
    [InlineData(null, false)]
    public void Accepts_only_exact_ascii_yes(string? input, bool expected) =>
        Assert.Equal(expected, ExactConfirmationPolicy.IsAccepted(input));
}
