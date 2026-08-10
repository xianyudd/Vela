using System.Collections.Generic;
using System.Collections.Immutable;
using Vela.Core.Models;
using Vela.Tui;
using Vela.Tui.Application;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;

namespace Vela.Tests.Tui;

public sealed class TuiApplicationTests
{
    [Fact]
    public async Task RunAsync_WhenActionThrows_RendersFailureAndReturnsToMenu()
    {
        var input = new FakeTuiInput(
            new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false));
        var sink = new RecordingFrameSink();
        var application = new TuiApplication(
            CreateFrame(),
            input,
            sink,
            (action, _, _) => action == MainMenuAction.Preflight
                ? Task.FromException<bool>(new InvalidOperationException("secret"))
                : Task.FromResult(true));

        await application.RunAsync();

        Assert.Contains(sink.Frames, frame => frame.Progress is { State: RunProgressState.Failed, Message: "操作失败，已返回主菜单。" });
        Assert.DoesNotContain(sink.Frames, frame => frame.Progress.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WhenActionIsCancelled_RendersCancellationAndReturnsToMenu()
    {
        var input = new FakeTuiInput(
            new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false));
        var sink = new RecordingFrameSink();
        var application = new TuiApplication(
            CreateFrame(),
            input,
            sink,
            (action, _, _) => action == MainMenuAction.Preflight
                ? Task.FromException<bool>(new OperationCanceledException())
                : Task.FromResult(true));

        await application.RunAsync();

        Assert.Contains(sink.Frames, frame => frame.Progress is { State: RunProgressState.Cancelled });
    }


    [Fact]
    public async Task RunAsync_RendersInitialFrameAndUpdatesFrameAfterAction()
    {
        var input = new FakeTuiInput(
            new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false));
        var sink = new RecordingFrameSink();
        var initialFrame = CreateFrame();

        var application = new TuiApplication(
            initialFrame,
            input,
            sink,
            async (action, context, _) =>
            {
                if (action == MainMenuAction.Preflight)
                {
                    await context.RenderAsync(frame => frame with
                    {
                        Progress = new RunProgressViewModel(
                            RunProgressState.Succeeded,
                            "预检完成。",
                            100)
                    });
                    return false;
                }

                return true;
            });

        await application.RunAsync();

        Assert.Equal(2, sink.Frames.Count);
        Assert.Same(initialFrame.Menu, sink.Frames[0].Menu);
        Assert.Contains(
            sink.Frames,
            frame => frame.Progress is { State: RunProgressState.Succeeded, Message: "预检完成。" });
        Assert.Equal(RunProgressState.Succeeded, sink.Frames[^1].Progress.State);
    }

    [Fact]
    public async Task RunAsync_ConfirmationEffect_ProcessesOneKeyAtATimeAndAcceptsExactYes()
    {
        var input = new FakeTuiInput(
            Key(ConsoleKey.Enter),
            Character('Y', ConsoleKey.Y),
            Character('E', ConsoleKey.E),
            Character('S', ConsoleKey.S),
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.Escape));
        var sink = new RecordingFrameSink();
        var confirmation = new ConfirmationViewModel(
            "输入 YES 继续。",
            "YES",
            ImmutableArray<string>.Empty);
        ConfirmationInputResult? result = null;
        var application = new TuiApplication(
            CreateFrame(),
            input,
            sink,
            async (action, context, _) =>
            {
                if (action == MainMenuAction.Preflight)
                {
                    await context.RequestConfirmationAsync(
                        confirmation,
                        (confirmationResult, _, _) =>
                        {
                            result = confirmationResult;
                            return Task.CompletedTask;
                        });
                    return false;
                }

                return true;
            });

        await application.RunAsync();

        Assert.Equal(ConfirmationInputStatus.Accepted, result?.Status);
        Assert.Equal("YES", result?.Response);
        Assert.Contains(sink.Frames, frame => frame.Page is ConfirmationPageViewModel { Response: "Y" });
        Assert.Contains(sink.Frames, frame => frame.Page is ConfirmationPageViewModel { Response: "YE" });
        Assert.Contains(sink.Frames, frame => frame.Page is ConfirmationPageViewModel { Response: "YES" });
        Assert.Equal(6, input.ReadCount);
    }

    [Fact]
    public async Task RunAsync_ConfirmationEffect_BackspacePreservesExactSemantics()
    {
        var input = new FakeTuiInput(
            Key(ConsoleKey.Enter),
            Character('Y', ConsoleKey.Y),
            Character('E', ConsoleKey.E),
            Character('x', ConsoleKey.X),
            Key(ConsoleKey.Backspace),
            Character('S', ConsoleKey.S),
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.Escape));
        var sink = new RecordingFrameSink();
        ConfirmationInputResult? result = null;
        var application = new TuiApplication(
            CreateFrame(),
            input,
            sink,
            async (action, context, _) =>
            {
                if (action == MainMenuAction.Preflight)
                {
                    await context.RequestConfirmationAsync(
                        new ConfirmationViewModel(
                            "输入 YES 继续。",
                            "YES",
                            ImmutableArray<string>.Empty),
                        (confirmationResult, _, _) =>
                        {
                            result = confirmationResult;
                            return Task.CompletedTask;
                        });
                    return false;
                }

                return true;
            });

        await application.RunAsync();

        Assert.Equal(ConfirmationInputStatus.Accepted, result?.Status);
        Assert.Equal("YES", result?.Response);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("YES ")]
    public async Task RunAsync_ConfirmationEffect_RejectsNonExactInput(string response)
    {
        var keys = new List<ConsoleKeyInfo> { Key(ConsoleKey.Enter) };
        keys.AddRange(response.Select(character => Character(
            character,
            Enum.TryParse<ConsoleKey>(character.ToString(), true, out var key)
                ? key
                : ConsoleKey.Spacebar)));
        keys.Add(Key(ConsoleKey.Enter));
        keys.Add(Key(ConsoleKey.Escape));
        ConfirmationInputResult? result = null;
        var application = new TuiApplication(
            CreateFrame(),
            new FakeTuiInput(keys.ToArray()),
            new RecordingFrameSink(),
            async (action, context, _) =>
            {
                if (action == MainMenuAction.Preflight)
                {
                    await context.RequestConfirmationAsync(
                        new ConfirmationViewModel(
                            "输入 YES 继续。",
                            "YES",
                            ImmutableArray<string>.Empty),
                        (confirmationResult, _, _) =>
                        {
                            result = confirmationResult;
                            return Task.CompletedTask;
                        });
                    return false;
                }

                return true;
            });

        await application.RunAsync();

        Assert.Equal(ConfirmationInputStatus.Rejected, result?.Status);
        Assert.Equal(response, result?.Response);
    }

    [Fact]
    public async Task RunAsync_IrrelevantKey_DoesNotRenderAnotherFrame()
    {
        var input = new FakeTuiInput(
            Character('x', ConsoleKey.X),
            Key(ConsoleKey.Escape));
        var sink = new RecordingFrameSink();
        var application = new TuiApplication(
            CreateFrame(),
            input,
            sink,
            (_, _, _) => Task.FromResult(true));

        await application.RunAsync();

        Assert.Single(sink.Frames);
    }

    [Fact]
    public async Task RunAsync_ConfirmationEffect_BoundsResponseLength()
    {
        var keys = new List<ConsoleKeyInfo> { Key(ConsoleKey.Enter) };
        keys.AddRange(Enumerable.Repeat(Character('A', ConsoleKey.A), 20));
        keys.Add(Key(ConsoleKey.Enter));
        keys.Add(Key(ConsoleKey.Escape));
        ConfirmationInputResult? result = null;
        var application = new TuiApplication(
            CreateFrame(),
            new FakeTuiInput(keys.ToArray()),
            new RecordingFrameSink(),
            async (action, context, _) =>
            {
                if (action == MainMenuAction.Preflight)
                {
                    await context.RequestConfirmationAsync(
                        new ConfirmationViewModel(
                            "输入 YES 继续。",
                            "YES",
                            ImmutableArray<string>.Empty),
                        (confirmationResult, _, _) =>
                        {
                            result = confirmationResult;
                            return Task.CompletedTask;
                        });
                    return false;
                }

                return true;
            });

        await application.RunAsync();

        Assert.Equal(ConfirmationInputStatus.Rejected, result?.Status);
        Assert.Equal(new string('A', 16), result?.Response);
    }

    [Fact]
    public async Task RunAsync_PreCancelledToken_DoesNotReadInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var input = new FakeTuiInput(Key(ConsoleKey.Escape));
        var application = new TuiApplication(
            CreateFrame(),
            input,
            new RecordingFrameSink(),
            (_, _, _) => Task.FromResult(true));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => application.RunAsync(cancellation.Token));

        Assert.Equal(0, input.ReadCount);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringRead_DoesNotDispatchKey()
    {
        using var cancellation = new CancellationTokenSource();
        var input = new CancellingTuiInput(cancellation);
        var dispatched = false;
        var application = new TuiApplication(
            CreateFrame(),
            input,
            new RecordingFrameSink(),
            (_, _, _) =>
            {
                dispatched = true;
                return Task.FromResult(true);
            });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => application.RunAsync(cancellation.Token));

        Assert.Equal(1, input.ReadCount);
        Assert.False(dispatched);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, false, false, false);

    private static ConsoleKeyInfo Character(char character, ConsoleKey key) =>
        new(character, key, false, false, false);

    private static TuiFrameViewModel CreateFrame()
    {
        var profile = new Profile(
            Guid.Parse("64d3e392-c081-4f1c-a95b-a7d0980527dd"),
            "Ubuntu 24.04 on D",
            "Ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            ShutdownMode.Global,
            TimeSpan.FromSeconds(45));
        var menu = new MainMenu().ViewModel;

        return new TuiFrameViewModel(
            menu,
            DashboardViewModel.CreateInitial(profile),
            new RunProgressViewModel(RunProgressState.Idle, "预检尚未运行。", null));
    }

    private sealed class FakeTuiInput : ITuiInput
    {
        private readonly Queue<ConsoleKeyInfo> _keys;

        public FakeTuiInput(params ConsoleKeyInfo[] keys) => _keys = new(keys);

        public int ReadCount { get; private set; }

        public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ReadKey();
        }

        public ConsoleKeyInfo ReadKey()
        {
            ReadCount++;
            return _keys.Count == 0
                ? throw new InvalidOperationException("No fake input remains.")
                : _keys.Dequeue();
        }
    }

    private sealed class CancellingTuiInput : ITuiInput
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingTuiInput(CancellationTokenSource cancellation) =>
            _cancellation = cancellation;

        public int ReadCount { get; private set; }

        public ConsoleKeyInfo ReadKey() =>
            throw new InvalidOperationException("Cancellation-aware input is required.");

        public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            _cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Key(ConsoleKey.Enter);
        }
    }

    private sealed class RecordingFrameSink : ITuiFrameSink
    {
        public List<TuiFrameViewModel> Frames { get; } = [];

        public void Render(TuiFrameViewModel frame) => Frames.Add(frame);
    }

}
