using static Terminal.Gui.App.Application;
using Terminal.Gui.Drivers;
using Terminal.Gui.Time;
using Terminal.Gui.Views;
using Vela.Tui.Application;

namespace Vela.Tests.Tui;

public sealed class TerminalGuiUiDispatcherTests
{
    // The handshake waits exist only so a broken dispatcher fails instead of hanging.
    // Terminal.Gui start-up is cold-JIT work that a loaded CI runner can stretch out,
    // so a tight wait buys flakes rather than a faster signal. The pump deadline stays
    // above twice the wait, since the test spends two of them back to back and the
    // pump has to outlive both.
    private static readonly TimeSpan HandshakeBudget = TimeSpan.FromSeconds(30);

    private const int PumpDeadlineMilliseconds = 120_000;

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

                // The pump cannot key its exit on `invoked` alone: whenever one of the
                // waits below times out that task stays pending forever, and the loop
                // would spin for the life of the process. The deadline is several times
                // the waits, so a healthy run completes long before it matters.
                var deadline = Environment.TickCount64 + PumpDeadlineMilliseconds;
                while (!invoked.Task.IsCompleted && Environment.TickCount64 < deadline)
                {
                    app.TimedEvents!.RunTimers();
                    Thread.Sleep(1);
                }
            }
            finally
            {
                app.End(session!);
            }
        })
        {
            // Nothing joins this thread on the failure paths above, so it must not own
            // the process lifetime: a background thread can never outlive the run,
            // whichever host executes the assembly. VSTest tears its own test host down
            // regardless, so this is belt-and-braces rather than a hang fix.
            IsBackground = true
        };
        uiThread.Start();

        var ui = await ready.Task.WaitAsync(HandshakeBudget);
        await Task.Run(() => ui.Dispatcher.Post(() => invoked.TrySetResult(Environment.CurrentManagedThreadId)));
        var executedOnThread = await invoked.Task.WaitAsync(HandshakeBudget);

        Assert.True(await Task.Run(() => uiThread.Join(PumpDeadlineMilliseconds)));
        Assert.Equal(ui.ThreadId, executedOnThread);
    }
}
