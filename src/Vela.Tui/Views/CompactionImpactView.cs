using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vela.Tui.Application;
using Vela.Tui.Rendering;

namespace Vela.Tui.Views;

/// <summary>
/// The second-step projection from the final design.  It only renders the
/// selected target and the estimator result; execution remains owned by the
/// shell/program workflow.
/// </summary>
public sealed class CompactionImpactView : View
{
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly FrameView _comparisonPanel;
    private readonly Label _currentCaption;
    private readonly Label _currentValue;
    private readonly Label _arrow;
    private readonly Label _method;
    private readonly Label _projectedCaption;
    private readonly Label _projectedValue;
    private readonly FrameView _releasePanel;
    private readonly Label _releaseTitle;
    private readonly Label _releaseSupport;
    private readonly Label _releaseValue;
    private readonly Label _prompt;
    private string _targetName = string.Empty;
    private string _currentSize = "尚未读取";
    private string _projectedSize = "计算中…";
    private string _reclaimableSize = "计算中…";
    private bool _hasEstimate;

    public CompactionImpactView()
    {
        CanFocus = false;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _title = CreateLabel(VelaTerminalTheme.Surface);
        _title.TextAlignment = Alignment.Center;
        _title.Text = "影响评估（Impact Assessment）";

        _subtitle = CreateLabel(VelaTerminalTheme.Muted);
        _subtitle.TextAlignment = Alignment.Center;

        _comparisonPanel = new FrameView
        {
            BorderStyle = Terminal.Gui.Drawing.LineStyle.Single,
            SchemeName = VelaTerminalTheme.SurfacePanel,
            CanFocus = false
        };
        _currentCaption = CreateLabel(VelaTerminalTheme.Muted);
        _currentCaption.TextAlignment = Alignment.Center;
        _currentValue = CreateLabel(VelaTerminalTheme.Surface);
        _currentValue.TextAlignment = Alignment.Center;
        _arrow = CreateLabel(VelaTerminalTheme.Info);
        _arrow.TextAlignment = Alignment.Center;
        _method = CreateLabel(VelaTerminalTheme.Muted);
        _method.TextAlignment = Alignment.Center;
        _projectedCaption = CreateLabel(VelaTerminalTheme.Muted);
        _projectedCaption.TextAlignment = Alignment.Center;
        _projectedValue = CreateLabel(VelaTerminalTheme.Success);
        _projectedValue.TextAlignment = Alignment.Center;

        _releasePanel = new FrameView
        {
            BorderStyle = Terminal.Gui.Drawing.LineStyle.Single,
            SchemeName = VelaTerminalTheme.Panel,
            CanFocus = false
        };
        _releaseTitle = CreateLabel(VelaTerminalTheme.Surface);
        _releaseSupport = CreateLabel(VelaTerminalTheme.Muted);
        _releaseValue = CreateLabel(VelaTerminalTheme.Info);
        _releaseValue.TextAlignment = Alignment.End;
        _releasePanel.Add(_releaseTitle, _releaseSupport, _releaseValue);

        _comparisonPanel.Add(
            _currentCaption,
            _currentValue,
            _arrow,
            _method,
            _projectedCaption,
            _projectedValue,
            _releasePanel);

        _prompt = CreateLabel(VelaTerminalTheme.Muted);
        _prompt.TextAlignment = Alignment.Center;

        Add(_title, _subtitle, _comparisonPanel, _prompt);
    }

    public void Apply(
        string targetName,
        string currentSize,
        string projectedSize,
        string reclaimableSize,
        bool hasEstimate)
    {
        _targetName = TuiDisplayText.Sanitize(targetName, 64);
        _currentSize = TuiDisplayText.Sanitize(currentSize, 32);
        _projectedSize = TuiDisplayText.Sanitize(projectedSize, 32);
        _reclaimableSize = TuiDisplayText.Sanitize(reclaimableSize, 32);
        _hasEstimate = hasEstimate;

        _subtitle.Text = string.IsNullOrWhiteSpace(_targetName)
            ? "先在 01 预检结果中锁定一个目标。"
            : $"目标：{_targetName} · 根据当前物理体积和根文件系统已用空间计算。";
        _currentCaption.Text = "当前物理体积";
        _currentValue.Text = _currentSize;
        _arrow.Text = "❯❯❯";
        _method.Text = "Sparse diskpart";
        _projectedCaption.Text = "预计压缩后体积";
        _projectedValue.Text = _hasEstimate ? _projectedSize : "计算中…";
        _releaseTitle.Text = "预计可回收空间";
        _releaseSupport.Text = _hasEstimate
            ? "当前物理体积 − 根文件系统已用空间"
            : _reclaimableSize is "采集失败" or "暂不可用"
                ? "目标使用量读取失败，暂未得到数值"
            : "等待目标使用量采集完成";
        _releaseValue.Text = _reclaimableSize;
        _prompt.Text = "按  [Y]  确认执行物理压缩    按  [Esc]  返回预检结果";
        SetNeedsLayout();
        SetNeedsDraw();
    }

    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        base.OnSubViewLayout(args);
        Arrange(Viewport.Width, Viewport.Height);
    }

    private void Arrange(int width, int height)
    {
        if (width < 30 || height < 5)
        {
            _comparisonPanel.Visible = false;
            _releasePanel.Visible = false;
            Place(_title, 1, 0, Math.Max(1, width - 2), 1);
            Place(_subtitle, 1, 1, Math.Max(1, width - 2), 1);
            Place(_prompt, 1, Math.Max(2, height - 1), Math.Max(1, width - 2), 1);
            return;
        }

        _title.Visible = true;
        _subtitle.Visible = true;
        _comparisonPanel.Visible = true;
        _releasePanel.Visible = true;
        _prompt.Visible = true;

        var contentWidth = Math.Max(24, Math.Min(106, width - 4));
        var contentX = Math.Max(0, (width - contentWidth) / 2);
        var titleY = Math.Max(0, Math.Min(2, height / 8));
        var subtitleY = titleY + 2;
        var cardY = subtitleY + 2;
        var cardHeight = Math.Min(18, Math.Max(13, height - cardY - 4));
        if (cardY + cardHeight > height - 2)
        {
            cardY = Math.Max(2, height - cardHeight - 2);
        }

        Place(_title, contentX, titleY, contentWidth, 1);
        Place(_subtitle, contentX, subtitleY, contentWidth, 1);
        Place(_comparisonPanel, contentX, cardY, contentWidth, cardHeight);

        var sideWidth = Math.Max(12, (contentWidth - 12) / 2);
        var currentX = 2;
        var projectedX = contentWidth - sideWidth - 2;
        var valueY = Math.Min(5, Math.Max(3, cardHeight / 3));
        Place(_currentCaption, currentX, 2, sideWidth, 1);
        Place(_currentValue, currentX, valueY, sideWidth, 1);
        Place(_projectedCaption, projectedX, 2, sideWidth, 1);
        Place(_projectedValue, projectedX, valueY, sideWidth, 1);
        Place(_arrow, Math.Max(1, contentWidth / 2 - 7), valueY - 1, 14, 1);
        Place(_method, Math.Max(1, contentWidth / 2 - 11), valueY + 2, 22, 1);

        var releaseX = 3;
        var releaseWidth = Math.Max(18, contentWidth - 6);
        var releaseY = Math.Max(7, cardHeight - 7);
        Place(_releasePanel, releaseX, releaseY, releaseWidth, 6);
        Place(_releaseTitle, 2, 1, Math.Max(1, releaseWidth - 26), 1);
        Place(_releaseSupport, 2, 2, Math.Max(1, releaseWidth - 26), 1);
        Place(_releaseValue, Math.Max(1, releaseWidth - 23), 1, 21, 2);
        Place(_prompt, contentX, cardY + cardHeight + 1, contentWidth, 1);
    }

    private static Label CreateLabel(string scheme) => new()
    {
        SchemeName = scheme,
        CanFocus = false
    };

    private static void Place(View view, int x, int y, int width, int height)
    {
        view.X = Math.Max(0, x);
        view.Y = Math.Max(0, y);
        view.Width = Math.Max(1, width);
        view.Height = Math.Max(1, height);
    }
}
