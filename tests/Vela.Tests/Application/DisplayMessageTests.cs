using Vela.Application.Display;

namespace Vela.Tests.Application;

/// <summary>
/// Tests for display-safe message construction.
/// </summary>
public sealed class DisplayMessageTests
{
    [Fact]
    public void FromSanitized_SanitizesTextAndDefaultsToInfo()
    {
        var message = DisplayMessage.FromSanitized("正常消息");

        Assert.Equal("正常消息", message.Text);
        Assert.Equal(DisplayMessageSeverity.Info, message.Severity);
    }

    [Fact]
    public void FromSanitized_ReplacesInternalDetails()
    {
        var message = DisplayMessage.FromSanitized(
            @"读取 D:\WSL\ext4.vhdx 失败",
            DisplayMessageSeverity.Error);

        Assert.Equal("日志格式无效", message.Text);
        Assert.Equal(DisplayMessageSeverity.Error, message.Severity);
    }

    [Fact]
    public void FromSanitized_NullTextBecomesEmpty()
    {
        var message = DisplayMessage.FromSanitized(null);

        Assert.Equal(string.Empty, message.Text);
    }
}
