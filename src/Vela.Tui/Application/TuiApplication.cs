using System.Collections.Immutable;
using Spectre.Console;
using Vela.Application.Profiles;
using Vela.Core.Models;
using Vela.Tui.Application;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;

namespace Vela.Tui;

public enum ConfirmationInputStatus
{
    Accepted,
    Rejected,
    Cancelled
}

public sealed record ConfirmationInputResult(
    ConfirmationInputStatus Status,
    string Response);

public enum TuiScreen
{
    Dashboard,
    ProfileList,
    ProfileEdit,
    RecentRuns,
    RecentRunDetail,
    Confirmation,
    Running,
    Result
}

public abstract record TuiPageViewModel(TuiScreen Screen);

public sealed record DashboardPageViewModel(TuiScreen CurrentScreen = TuiScreen.Dashboard)
    : TuiPageViewModel(CurrentScreen);

public sealed record ProfileListPageViewModel(ProfileManagementViewModel Profiles)
    : TuiPageViewModel(TuiScreen.ProfileList);

public enum ProfileEditField
{
    DisplayName,
    DistroName,
    VhdxPath,
    ShutdownMode,
    ShutdownTimeout
}

public sealed record ProfileEditPageViewModel(
    string Title,
    ProfileEditField Field,
    string FieldLabel,
    string DisplayValue,
    bool Sensitive,
    string? ValidationError)
    : TuiPageViewModel(TuiScreen.ProfileEdit);

public sealed record RecentRunListItemViewModel(
    DateTimeOffset? StartedAtUtc,
    string ProfileDisplayName,
    OperationIntent? Intent,
    TerminalResult? TerminalResult,
    long? ReclaimedBytes,
    bool IsMalformed,
    string? ErrorMessage);

public sealed record RecentRunsPageViewModel(
    ImmutableArray<RecentRunListItemViewModel> Entries,
    int SelectedIndex,
    string? ErrorMessage)
    : TuiPageViewModel(TuiScreen.RecentRuns);

public sealed record RecentRunDetailPageViewModel(
    bool IsMalformed,
    string ProfileDisplayName,
    OperationIntent? Intent,
    TerminalResult? TerminalResult,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    TimeSpan? Elapsed,
    long? ReclaimedBytes,
    bool LogsAvailable,
    string? ErrorMessage)
    : TuiPageViewModel(TuiScreen.RecentRunDetail);

public sealed record ConfirmationPageViewModel(
    string Prompt,
    string Response)
    : TuiPageViewModel(TuiScreen.Confirmation);

public interface ITuiInput
{
    ConsoleKeyInfo ReadKey();

    ConsoleKeyInfo ReadKey(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ReadKey();
        cancellationToken.ThrowIfCancellationRequested();
        return key;
    }
}

public sealed class SpectreTuiInput : ITuiInput
{
    private readonly IAnsiConsoleInput _input;

    public SpectreTuiInput(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _input = console.Input;
    }

    public ConsoleKeyInfo ReadKey() =>
        _input.ReadKey(intercept: true)
        ?? throw new InvalidOperationException("Interactive input ended unexpectedly.");

    public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ReadKey();
        cancellationToken.ThrowIfCancellationRequested();
        return key;
    }
}

public interface ITuiFrameSink
{
    void Render(TuiFrameViewModel frame);
}

public sealed class SpectreTuiFrameSink : ITuiFrameSink
{
    private readonly IAnsiConsole _console;
    private readonly FrameRenderer _renderer;

    public SpectreTuiFrameSink(IAnsiConsole console, FrameRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(renderer);
        _console = console;
        _renderer = renderer;
    }

    public void Render(TuiFrameViewModel frame) => _renderer.Render(_console, frame);
}

public delegate Task<bool> TuiActionHandler(
    MainMenuAction action,
    TuiApplicationContext context,
    CancellationToken cancellationToken);

public delegate Task TuiConfirmationHandler(
    ConfirmationInputResult result,
    TuiApplicationContext context,
    CancellationToken cancellationToken);

internal interface ITuiPageController
{
    Task HandleKeyAsync(
        ConsoleKeyInfo key,
        TuiApplicationContext context,
        CancellationToken cancellationToken);
}

internal sealed record TuiConfirmationRequest(
    ConfirmationViewModel Confirmation,
    TuiConfirmationHandler Handler,
    TuiFrameViewModel ReturnFrame);

public sealed class TuiApplicationContext
{
    private readonly ITuiFrameSink _sink;
    private TuiFrameViewModel _frame;

    internal TuiApplicationContext(
        TuiFrameViewModel frame,
        ITuiFrameSink sink)
    {
        _frame = frame;
        _sink = sink;
    }

    public TuiFrameViewModel Frame => _frame;

    internal ITuiPageController? RequestedPage { get; private set; }

    internal TuiConfirmationRequest? RequestedConfirmation { get; private set; }

    internal bool ReturnToMenuRequested { get; private set; }

    internal bool Rendered { get; private set; }

    public Task RenderAsync(Func<TuiFrameViewModel, TuiFrameViewModel> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var next = update(_frame) ?? throw new InvalidOperationException("The frame update returned null.");
        if (next == _frame)
        {
            return Task.CompletedTask;
        }

        _frame = next;
        _sink.Render(_frame);
        Rendered = true;
        return Task.CompletedTask;
    }

    internal void OpenPage(ITuiPageController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        RequestedPage = controller;
        RequestedConfirmation = null;
        ReturnToMenuRequested = false;
    }

    internal Task ReturnToMenuAsync(RunProgressViewModel progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        RequestedPage = null;
        RequestedConfirmation = null;
        ReturnToMenuRequested = true;
        return RenderAsync(frame => frame with
        {
            Page = new DashboardPageViewModel(),
            Progress = progress
        });
    }

    public Task RequestConfirmationAsync(
        ConfirmationViewModel confirmation,
        TuiConfirmationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(handler);

        var returnFrame = _frame;
        RequestedConfirmation = new TuiConfirmationRequest(
            confirmation,
            handler,
            returnFrame);
        RequestedPage = null;
        ReturnToMenuRequested = false;
        return RenderAsync(frame => frame with
        {
            Page = new ConfirmationPageViewModel(confirmation.Prompt, string.Empty),
            Progress = new RunProgressViewModel(
                RunProgressState.AwaitingConfirmation,
                "等待精确确认输入。",
                Percent: null)
        });
    }
}

public sealed class TuiApplication
{
    private const int MaxConfirmationLength = 16;

    private readonly ITuiInput _input;
    private readonly ITuiFrameSink _sink;
    private TuiActionHandler _handleActionAsync;
    private TuiFrameViewModel _frame;
    private ITuiPageController? _pageController;
    private ActiveConfirmation? _confirmation;
    private int _selectedIndex;

    public TuiApplication(
        TuiFrameViewModel initialFrame,
        ITuiInput input,
        ITuiFrameSink sink,
        TuiActionHandler handleActionAsync)
    {
        ArgumentNullException.ThrowIfNull(initialFrame);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(handleActionAsync);

        _input = input;
        _sink = sink;
        _handleActionAsync = handleActionAsync;
        _selectedIndex = NormalizeMenuIndex(initialFrame, initialFrame.SelectedMenuIndex);
        _frame = initialFrame with { SelectedMenuIndex = _selectedIndex };
    }

    public void SetFrame(TuiFrameViewModel frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _selectedIndex = NormalizeMenuIndex(frame, frame.SelectedMenuIndex);
        _frame = frame with { SelectedMenuIndex = _selectedIndex };
        _pageController = null;
        _confirmation = null;
    }

    public void SetActionHandler(TuiActionHandler handleActionAsync)
    {
        ArgumentNullException.ThrowIfNull(handleActionAsync);
        _handleActionAsync = handleActionAsync;
    }

    public async Task<ConfirmationInputResult> RunConfirmationAsync(
        ConfirmationViewModel confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ConfirmationInputResult? result = null;
        var context = new TuiApplicationContext(_frame, _sink);
        await context.RequestConfirmationAsync(
            confirmation,
            (confirmationResult, _, _) =>
            {
                result = confirmationResult;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        ApplyContext(context, currentPage: null);

        await RunLoopAsync(
                () => result is not null,
                renderInitial: false,
                cancellationToken)
            .ConfigureAwait(false);
        return result
            ?? throw new InvalidOperationException("The confirmation ended without a result.");
    }

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        RunLoopAsync(
            static () => false,
            renderInitial: true,
            cancellationToken);

    private async Task RunLoopAsync(
        Func<bool> completed,
        bool renderInitial,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (renderInitial)
        {
            Render();
        }

        while (!completed())
        {
            if (await ProcessNextKeyAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<bool> ProcessNextKeyAsync(CancellationToken cancellationToken)
    {
        var key = _input.ReadKey(cancellationToken);
        if (_confirmation is not null)
        {
            await HandleConfirmationKeyAsync(key, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (_pageController is not null)
        {
            await HandlePageKeyAsync(key, cancellationToken).ConfigureAwait(false);
            return false;
        }

        return await HandleMenuKeyAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HandleMenuKeyAsync(
        ConsoleKeyInfo key,
        CancellationToken cancellationToken)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                MoveSelection(-1);
                return false;
            case ConsoleKey.DownArrow:
                MoveSelection(1);
                return false;
            case ConsoleKey.Enter:
                return await HandleSelectedActionAsync(cancellationToken).ConfigureAwait(false);
            case ConsoleKey.Escape:
                return await HandleActionAsync(MainMenuAction.Exit, cancellationToken).ConfigureAwait(false);
            default:
                return false;
        }
    }

    private async Task HandlePageKeyAsync(
        ConsoleKeyInfo key,
        CancellationToken cancellationToken)
    {
        var page = _pageController
            ?? throw new InvalidOperationException("The active page controller is unavailable.");
        var context = new TuiApplicationContext(_frame, _sink);
        try
        {
            await page.HandleKeyAsync(key, context, cancellationToken).ConfigureAwait(false);
            ApplyContext(context, page);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await context.ReturnToMenuAsync(new RunProgressViewModel(
                RunProgressState.Failed,
                "页面操作失败，已返回主菜单。",
                Percent: null)).ConfigureAwait(false);
            ApplyContext(context, page);
        }
    }

    private async Task HandleConfirmationKeyAsync(
        ConsoleKeyInfo key,
        CancellationToken cancellationToken)
    {
        var active = _confirmation
            ?? throw new InvalidOperationException("The active confirmation is unavailable.");

        if (key.Key == ConsoleKey.Escape)
        {
            await CompleteConfirmationAsync(
                new ConfirmationInputResult(ConfirmationInputStatus.Cancelled, active.Response),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            var status = MainMenu.IsConfirmationAccepted(active.Request.Confirmation, active.Response)
                ? ConfirmationInputStatus.Accepted
                : ConfirmationInputStatus.Rejected;
            await CompleteConfirmationAsync(
                new ConfirmationInputResult(status, active.Response),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = UpdateText(active.Response, key, MaxConfirmationLength);
        if (string.Equals(response, active.Response, StringComparison.Ordinal))
        {
            return;
        }

        _confirmation = active with { Response = response };
        _frame = _frame with
        {
            Page = new ConfirmationPageViewModel(active.Request.Confirmation.Prompt, response)
        };
        Render();
    }

    private async Task CompleteConfirmationAsync(
        ConfirmationInputResult result,
        CancellationToken cancellationToken)
    {
        var active = _confirmation
            ?? throw new InvalidOperationException("The active confirmation is unavailable.");
        _confirmation = null;
        _frame = active.Request.ReturnFrame;
        _pageController = active.ReturnPage;

        var context = new TuiApplicationContext(_frame, _sink);
        try
        {
            await active.Request.Handler(result, context, cancellationToken).ConfigureAwait(false);
            ApplyContext(context, _pageController);
            if (!context.Rendered)
            {
                Render();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _pageController = null;
            _frame = _frame with
            {
                Page = new DashboardPageViewModel(TuiScreen.Result),
                Progress = new RunProgressViewModel(
                    RunProgressState.Failed,
                    "确认后的操作失败，已返回主菜单。",
                    Percent: null)
            };
            Render();
        }
    }

    private void MoveSelection(int offset)
    {
        var count = _frame.Menu.Items.Length;
        if (count == 0)
        {
            return;
        }

        _selectedIndex = (_selectedIndex + offset + count) % count;
        _frame = _frame with { SelectedMenuIndex = _selectedIndex };
        Render();
    }

    private Task<bool> HandleSelectedActionAsync(CancellationToken cancellationToken)
    {
        if (_frame.Menu.Items.IsDefaultOrEmpty)
        {
            return Task.FromResult(false);
        }

        return HandleActionAsync(_frame.Menu.Items[_selectedIndex].Action, cancellationToken);
    }

    private async Task<bool> HandleActionAsync(
        MainMenuAction action,
        CancellationToken cancellationToken)
    {
        var context = new TuiApplicationContext(_frame, _sink);
        try
        {
            var shouldExit = await _handleActionAsync(action, context, cancellationToken).ConfigureAwait(false);
            ApplyContext(context, _pageController);
            return shouldExit;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _frame = context.Frame with
            {
                Page = new DashboardPageViewModel(TuiScreen.Result),
                SelectedMenuIndex = _selectedIndex,
                Progress = new RunProgressViewModel(
                    RunProgressState.Cancelled,
                    "操作已取消，已返回主菜单。",
                    Percent: null)
            };
            _pageController = null;
            _confirmation = null;
            Render();
            return false;
        }
        catch (Exception)
        {
            _frame = context.Frame with
            {
                Page = new DashboardPageViewModel(TuiScreen.Result),
                SelectedMenuIndex = _selectedIndex,
                Progress = new RunProgressViewModel(
                    RunProgressState.Failed,
                    "操作失败，已返回主菜单。",
                    Percent: null)
            };
            _pageController = null;
            _confirmation = null;
            Render();
            return false;
        }
    }

    private void ApplyContext(
        TuiApplicationContext context,
        ITuiPageController? currentPage)
    {
        var previousFrame = _frame;
        _frame = context.Frame with { SelectedMenuIndex = _selectedIndex };

        if (context.ReturnToMenuRequested)
        {
            _pageController = null;
            _confirmation = null;
        }
        else if (context.RequestedConfirmation is { } confirmation)
        {
            _confirmation = new ActiveConfirmation(
                confirmation,
                string.Empty,
                currentPage);
            _pageController = null;
        }
        else if (context.RequestedPage is { } page)
        {
            _pageController = page;
            _confirmation = null;
        }

        if (!context.Rendered && _frame != previousFrame)
        {
            Render();
        }
    }

    private static string UpdateText(
        string value,
        ConsoleKeyInfo key,
        int maxLength)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            return value.Length == 0 ? value : value[..^1];
        }

        return !char.IsControl(key.KeyChar) && value.Length < maxLength
            ? value + key.KeyChar
            : value;
    }

    private static int NormalizeMenuIndex(TuiFrameViewModel frame, int selectedIndex) =>
        frame.Menu.Items.IsDefaultOrEmpty
            ? 0
            : Math.Clamp(selectedIndex, 0, frame.Menu.Items.Length - 1);

    private void Render() => _sink.Render(_frame);

    private sealed record ActiveConfirmation(
        TuiConfirmationRequest Request,
        string Response,
        ITuiPageController? ReturnPage);
}
