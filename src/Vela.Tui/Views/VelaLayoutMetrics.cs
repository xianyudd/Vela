namespace Vela.Tui.Views;

/// <summary>Pure, content-driven geometry decisions shared by Terminal.Gui and tests.</summary>
public readonly record struct VelaLayoutMetrics(
    VelaShellLayout Layout,
    bool ShowEvidenceRail,
    int NavigationHeight,
    int AvailablePageRows)
{
    public const int TwoPaneWidth = 80;
    public const int EvidenceRailWidth = 120;
    public const int AnalysisRailWidth = 140;
    public const int TwoPaneHeight = 20;
    public const int NarrowContentWidth = 72;

    public static VelaLayoutMetrics Calculate(int width, int height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        var twoPane = width >= TwoPaneWidth && height >= TwoPaneHeight;
        var layout = twoPane ? VelaShellLayout.TwoPane : VelaShellLayout.SinglePane;
        var navigationHeight = twoPane ? 6 : (height < TwoPaneHeight ? 8 : 9);
        var availableRows = twoPane
            ? Math.Clamp(height - 9, 3, 20)
            : Math.Clamp(height - navigationHeight - 7, 2, 12);
        return new VelaLayoutMetrics(
            layout,
            twoPane && width >= EvidenceRailWidth,
            navigationHeight,
            availableRows);
    }
}
