using Vela.Tui.Menu;

namespace Vela.Tests.Tui;

public sealed class MainMenuSessionTests
{
    [Fact]
    public async Task RunAsync_ReturnsToMenuAfterPreflightUntilExitIsSelected()
    {
        var actions = new Queue<MainMenuAction>(
            [MainMenuAction.Preflight, MainMenuAction.Exit]);
        var handled = new List<MainMenuAction>();
        var session = new MainMenuSession(
            () => actions.Dequeue(),
            action =>
            {
                handled.Add(action);
                return Task.FromResult(action == MainMenuAction.Exit);
            });

        await session.RunAsync();

        Assert.Equal(
            [MainMenuAction.Preflight, MainMenuAction.Exit],
            handled);
    }
}
