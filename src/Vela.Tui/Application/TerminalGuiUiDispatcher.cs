using Terminal.Gui.App;

namespace Vela.Tui.Application;

public interface ITuiDispatcher
{
    void Post(Action action);
}

public sealed class TerminalGuiUiDispatcher : ITuiDispatcher
{
    private readonly IApplication _application;

    public TerminalGuiUiDispatcher(IApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _application.Invoke(action);
    }
}
