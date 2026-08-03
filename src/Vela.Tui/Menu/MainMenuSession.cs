namespace Vela.Tui.Menu;

public sealed class MainMenuSession
{
    private readonly Func<MainMenuAction> _selectAction;
    private readonly Func<MainMenuAction, Task<bool>> _handleActionAsync;

    public MainMenuSession(
        Func<MainMenuAction> selectAction,
        Func<MainMenuAction, Task<bool>> handleActionAsync)
    {
        ArgumentNullException.ThrowIfNull(selectAction);
        ArgumentNullException.ThrowIfNull(handleActionAsync);

        _selectAction = selectAction;
        _handleActionAsync = handleActionAsync;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = _selectAction();
            var shouldExit = await _handleActionAsync(action).ConfigureAwait(false);
            if (shouldExit)
            {
                return;
            }
        }
    }
}
