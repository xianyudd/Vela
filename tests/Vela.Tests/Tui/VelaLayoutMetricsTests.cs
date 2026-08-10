using Vela.Tui.Views;

namespace Vela.Tests.Tui;

public sealed class VelaLayoutMetricsTests
{
    [Theory]
    [InlineData(140, 40, VelaShellLayout.TwoPane, true)]
    [InlineData(120, 30, VelaShellLayout.TwoPane, true)]
    [InlineData(119, 30, VelaShellLayout.TwoPane, false)]
    [InlineData(80, 24, VelaShellLayout.TwoPane, false)]
    [InlineData(79, 24, VelaShellLayout.SinglePane, false)]
    [InlineData(140, 19, VelaShellLayout.SinglePane, false)]
    public void Calculate_uses_content_driven_breakpoints(
        int width,
        int height,
        VelaShellLayout expectedLayout,
        bool expectedEvidenceRail)
    {
        var metrics = VelaLayoutMetrics.Calculate(width, height);

        Assert.Equal(expectedLayout, metrics.Layout);
        Assert.Equal(expectedEvidenceRail, metrics.ShowEvidenceRail);
        Assert.True(metrics.AvailablePageRows >= 2);
    }

    [Fact]
    public void Calculate_clamps_invalid_dimensions_to_a_minimal_safe_layout()
    {
        var metrics = VelaLayoutMetrics.Calculate(0, 0);

        Assert.Equal(VelaShellLayout.SinglePane, metrics.Layout);
        Assert.False(metrics.ShowEvidenceRail);
        Assert.Equal(2, metrics.AvailablePageRows);
    }
}
