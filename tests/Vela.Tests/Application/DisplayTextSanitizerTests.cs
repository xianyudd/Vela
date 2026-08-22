using System.Collections.Immutable;
using Vela.Application.Display;
using Vela.Core.Contracts;

namespace Vela.Tests.Application;

/// <summary>
/// Tests for the display-boundary sanitization helpers.
/// </summary>
public sealed class DisplayTextSanitizerTests
{
    [Fact]
    public void SanitizeSingleLine_ReturnsEmptyForNullOrWhitespace()
    {
        Assert.Equal(string.Empty, DisplayTextSanitizer.SanitizeSingleLine(null));
        Assert.Equal(string.Empty, DisplayTextSanitizer.SanitizeSingleLine(string.Empty));
        Assert.Equal("  ", DisplayTextSanitizer.SanitizeSingleLine("  "));
    }

    [Fact]
    public void SanitizeSingleLine_TrimsPlainText()
    {
        Assert.Equal("正常日志内容", DisplayTextSanitizer.SanitizeSingleLine("  正常日志内容  "));
    }

    [Theory]
    [InlineData(@"操作路径 D:\WSL\ext4.vhdx")]
    [InlineData(@"C:\Program Files\Vela")]
    [InlineData("RunId = abcdef")]
    [InlineData("native output follows")]
    [InlineData("System.InvalidOperationException: boom")]
    [InlineData("   at Vela.Windows.Adapter.Run()")]
    public void SanitizeSingleLine_ReplacesInternalDetailsWithPlaceholder(string raw)
    {
        Assert.Equal("日志格式无效", DisplayTextSanitizer.SanitizeSingleLine(raw));
    }

    [Fact]
    public void SanitizeLines_ReturnsEmptyForNull()
    {
        Assert.True(DisplayTextSanitizer.SanitizeLines(null).IsEmpty);
    }

    [Fact]
    public void SanitizeLines_SanitizesAndBoundsResult()
    {
        var lines = new[]
        {
            "正常",
            @"D:\path\file.vhdx",
            "正常2",
            "正常3"
        };

        var sanitized = DisplayTextSanitizer.SanitizeLines(lines, maxLines: 10);

        Assert.Equal(4, sanitized.Length);
        Assert.Equal("正常", sanitized[0]);
        Assert.Equal("日志格式无效", sanitized[1]);
    }

    [Fact]
    public void SanitizeLines_RespectsMaxLines()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i}");

        var sanitized = DisplayTextSanitizer.SanitizeLines(lines, maxLines: 5);

        Assert.Equal(5, sanitized.Length);
    }

    // ------------------------------------------------------------------
    // Cell-bounded sanitize
    // ------------------------------------------------------------------

    [Fact]
    public void Sanitize_ReturnsEmptyForNullEmptyOrNonPositiveBudget()
    {
        Assert.Equal(string.Empty, DisplayTextSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, DisplayTextSanitizer.Sanitize(string.Empty));
        Assert.Equal(string.Empty, DisplayTextSanitizer.Sanitize("文本", maxCells: 0));
    }

    [Fact]
    public void Sanitize_StripsControlAndEscapeSequences()
    {
        // CSI and OSC sequences plus embedded control characters are removed.
        var raw = "前\u001b[31m红\u001b[0m后\u0007尾";
        Assert.Equal("前红后 尾", DisplayTextSanitizer.Sanitize(raw, maxCells: 240));
    }

    [Fact]
    public void Sanitize_TruncatesToCellBudgetWithEllipsis()
    {
        // Each CJK character is two cells; a 5-cell budget fits two CJK chars
        // plus the ellipsis (2 + 2 + 1 = 5).
        var truncated = DisplayTextSanitizer.Sanitize("一二三四五", maxCells: 5);
        Assert.Equal("一二…", truncated);
    }

    [Fact]
    public void Sanitize_ReturnsEllipsisForSingleCellBudget()
    {
        Assert.Equal("…", DisplayTextSanitizer.Sanitize("很长的一段文本", maxCells: 1));
    }

    // ------------------------------------------------------------------
    // Byte formatting
    // ------------------------------------------------------------------

    [Fact]
    public void FormatBytes_ReturnsUnknownForNullOrNegative()
    {
        Assert.Equal("未知", DisplayTextSanitizer.FormatBytes(null));
        Assert.Equal("未知", DisplayTextSanitizer.FormatBytes(-1));
    }

    [Fact]
    public void FormatBytes_FormatsGiBAndTiB()
    {
        Assert.Equal("0.00 GiB", DisplayTextSanitizer.FormatBytes(0));
        Assert.Equal("10.00 GiB", DisplayTextSanitizer.FormatBytes(10L * 1024 * 1024 * 1024));
        Assert.Equal("1.50 TiB", DisplayTextSanitizer.FormatBytes((long)(1.5 * 1024 * 1024 * 1024 * 1024)));
    }

    // ------------------------------------------------------------------
    // File name extraction
    // ------------------------------------------------------------------

    [Fact]
    public void FileNameOnly_ExtractsLastSegment()
    {
        Assert.Equal("ext4.vhdx", DisplayTextSanitizer.FileNameOnly(@"D:\WSL\Ubuntu\ext4.vhdx"));
        Assert.Equal("ext4.vhdx", DisplayTextSanitizer.FileNameOnly("ext4.vhdx"));
    }

    [Fact]
    public void FileNameOnly_ReturnsEmptyForNullOrWhitespace()
    {
        Assert.Equal(string.Empty, DisplayTextSanitizer.FileNameOnly(null));
        Assert.Equal(string.Empty, DisplayTextSanitizer.FileNameOnly("   "));
    }

    // ------------------------------------------------------------------
    // Localization helpers
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(true, "是")]
    [InlineData(false, "否")]
    public void FormatSparseState_LocalizesKnownStates(bool? value, string expected)
    {
        Assert.Equal(expected, DisplayTextSanitizer.FormatSparseState(value));
    }

    [Fact]
    public void FormatSparseState_ReturnsUnknownForNull()
    {
        Assert.Equal("未知", DisplayTextSanitizer.FormatSparseState(null));
    }

    [Theory]
    [InlineData(LxssResolutionStatus.Matched, "已匹配")]
    [InlineData(LxssResolutionStatus.Mismatched, "不匹配")]
    [InlineData(LxssResolutionStatus.NotFound, "未找到")]
    [InlineData(LxssResolutionStatus.Failed, "解析失败")]
    public void FormatMappingStatus_LocalizesKnownStates(LxssResolutionStatus status, string expected)
    {
        Assert.Equal(expected, DisplayTextSanitizer.FormatMappingStatus(status));
    }

    [Fact]
    public void FormatMappingStatus_FallsBackForUnknownEnumValue()
    {
        Assert.Equal("尚未检查", DisplayTextSanitizer.FormatMappingStatus((LxssResolutionStatus)999));
    }
}
