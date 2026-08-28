using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drivers;
using Terminal.Gui.Time;
using Vela.Tui.Application;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;
using Vela.Tui.Views;

namespace Vela.Tests.Tui;

public sealed class VelaTerminalThemeTests
{
    [Fact]
    public void Register_exposes_the_visual_roles_used_by_the_shell()
    {
        VelaTerminalTheme.Register();

        var schemes = new[]
        {
            VelaTerminalTheme.Shell,
            VelaTerminalTheme.Header,
            VelaTerminalTheme.Heading,
            VelaTerminalTheme.Navigation,
            VelaTerminalTheme.Footer,
            VelaTerminalTheme.Badge,
            VelaTerminalTheme.TableHeader,
            VelaTerminalTheme.Divider,
            VelaTerminalTheme.AttentionPanel,
            VelaTerminalTheme.ErrorPanel
        };

        Assert.All(schemes, scheme => Assert.True(
            SchemeManager.TryGetScheme(scheme, out _),
            $"Expected visual role '{scheme}' to be registered."));
    }

    [Fact]
    public void Shell_uses_a_structured_terminal_frame_and_read_only_badge()
    {
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        app.Driver!.SetScreenSize(160, 45);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.ShowOverview();
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Contains("READ-ONLY", rendered, StringComparison.Ordinal);
            Assert.Contains("工作区", rendered, StringComparison.Ordinal);
            Assert.Contains("导航 / 操作", rendered, StringComparison.Ordinal);
            Assert.Contains("╭", rendered, StringComparison.Ordinal);
            Assert.Contains("╮", rendered, StringComparison.Ordinal);
        }
        finally
        {
            app.End(session!);
        }
    }

    private static Vela.Core.Models.Profile CreateProfile() =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Ubuntu 24.04 on D",
            "Ubuntu-24.04",
            @"D:\WSL\Ubuntu-24.04\ext4.vhdx",
            Vela.Core.Models.ShutdownMode.Global,
            TimeSpan.FromSeconds(45));
}
