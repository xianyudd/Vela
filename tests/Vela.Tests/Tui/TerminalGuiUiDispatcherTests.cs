using static Terminal.Gui.App.Application;
using Terminal.Gui.Drivers;
using Terminal.Gui.Time;
using Terminal.Gui.Views;
using Vela.Tui.Application;

namespace Vela.Tests.Tui;

public sealed class TerminalGuiUiDispatcherTests
{
    [Fact]
    public void Constructor_rejects_null_application() =>
        Assert.Throws<ArgumentNullException>(() => new TerminalGuiUiDispatcher(null!));

    [Fact]
    public async Task Worker_post_executes_on_the_terminal_gui_main_thread()
    {
        var ready = new TaskCompletionSource<(TerminalGuiUiDispatcher Dispatcher, int ThreadId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
            app.Init(DriverRegistry.Names.ANSI);
            using var window = new Window();
            var session = app.Begin(window);
            try
            {
                ready.TrySetResult((new TerminalGuiUiDispatcher(app), app.MainThreadId!.Value));
                while (!invoked.Task.IsCompleted)
                {
                    app.TimedEvents!.RunTimers();
                    Thread.Sleep(1);
                }
            }
            finally
            {
                app.End(session!);
            }
        });
        uiThread.Start();

        var ui = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(() => ui.Dispatcher.Post(() => invoked.TrySetResult(Environment.CurrentManagedThreadId)));
        var executedOnThread = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(uiThread.Join);

        Assert.Equal(ui.ThreadId, executedOnThread);
    }
}
