using System.Collections.Immutable;
using Vela.Application.Display;

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
}
