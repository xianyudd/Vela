using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;

namespace Vela.Tests.Fakes;

public sealed class FakeWslClient : IWslClient
{
    private ImmutableArray<string> _terminatedDistros = ImmutableArray<string>.Empty;

    public WslInventory InstalledInventory { get; init; } = CreateEmptyInventory();

    public WslInventory RunningInventory { get; init; } = CreateEmptyInventory();

    public bool ThrowOnRead { get; init; }

    public bool ThrowOnAction { get; init; } = true;

    public Action<string>? OnInvoked { get; init; }

    public int InstalledInventoryCalls { get; private set; }

    public int RunningInventoryCalls { get; private set; }

    public int ShutdownAllCalls { get; private set; }

    public int TerminateDistroCalls => _terminatedDistros.Length;

    public ImmutableArray<string> TerminatedDistros => _terminatedDistros;

    public Task<WslInventory> GetInstalledInventoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstalledInventoryCalls++;
        OnInvoked?.Invoke("wsl.installed");

        if (ThrowOnRead)
        {
            throw new InvalidOperationException("Installed inventory read was configured to fail.");
        }

        return Task.FromResult(InstalledInventory);
    }

    public Task<WslInventory> GetRunningInventoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunningInventoryCalls++;
        OnInvoked?.Invoke("wsl.running");

        if (ThrowOnRead)
        {
            throw new InvalidOperationException("Running inventory read was configured to fail.");
        }

        return Task.FromResult(RunningInventory);
    }

    public Task<ProcessExecutionResult> ShutdownAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShutdownAllCalls++;
        OnInvoked?.Invoke("wsl.shutdown-all");
        ThrowWhenActionsAreForbidden();

        return Task.FromResult(CreateSucceededResult());
    }

    public Task<ProcessExecutionResult> TerminateDistroAsync(
        string distroName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _terminatedDistros = _terminatedDistros.Add(distroName);
        OnInvoked?.Invoke("wsl.terminate");
        ThrowWhenActionsAreForbidden();

        return Task.FromResult(CreateSucceededResult());
    }

    private void ThrowWhenActionsAreForbidden()
    {
        if (ThrowOnAction)
        {
            throw new InvalidOperationException("WSL actions must not be invoked by a read-only preflight.");
        }
    }

    private static WslInventory CreateEmptyInventory() => new(
        DateTimeOffset.UnixEpoch,
        ImmutableArray<WslDistribution>.Empty);

    private static ProcessExecutionResult CreateSucceededResult() => new(
        ProcessExecutionStatus.Succeeded,
        0,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);
}

public sealed class FakeLxssProfileResolver : ILxssProfileResolver
{
    private readonly Action<string>? _onInvoked;

    public FakeLxssProfileResolver(LxssProfileResolution resolution, Action<string>? onInvoked = null)
    {
        Resolution = resolution;
        _onInvoked = onInvoked;
    }

    public LxssProfileResolution Resolution { get; }

    public bool ThrowOnCall { get; init; }

    public int CallCount { get; private set; }

    public Task<LxssProfileResolution> ResolveAsync(
        string distroName,
        string requestedVhdxPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        _onInvoked?.Invoke("lxss.resolve");

        if (ThrowOnCall)
        {
            throw new InvalidOperationException("The resolver was configured not to be read.");
        }

        return Task.FromResult(Resolution);
    }
}

public sealed class FakeVhdxInspector : IVhdxInspector
{
    private readonly Action<string>? _onInvoked;

    public FakeVhdxInspector(VhdxInspectionResult inspection, Action<string>? onInvoked = null)
    {
        Inspection = inspection;
        _onInvoked = onInvoked;
    }

    public VhdxInspectionResult Inspection { get; }

    public bool ThrowOnCall { get; init; }

    public int CallCount { get; private set; }

    public Task<VhdxInspectionResult> InspectAsync(string vhdxPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        _onInvoked?.Invoke("vhdx.inspect");

        if (ThrowOnCall)
        {
            throw new InvalidOperationException("The inspector was configured not to be read.");
        }

        return Task.FromResult(Inspection);
    }
}

public sealed class FakeRunJournal : IRunJournal
{
    private readonly Action<string>? _onInvoked;
    private readonly List<Guid> _createdRunIds = new();
    private readonly List<RunEvent> _events = new();
    private readonly List<string> _operations = new();
    private RunSummary? _summary;
    private long _nextSequence;

    public FakeRunJournal(Action<string>? onInvoked = null)
    {
        _onInvoked = onInvoked;
    }

    public bool ThrowOnCreate { get; init; }

    public bool ThrowOnAppend { get; init; }

    public bool ThrowOnWriteSummary { get; init; }

    public int AppendCalls { get; private set; }

    public int OpenExistingRunCalls { get; private set; }

    public int SummaryWriteCalls { get; private set; }

    public ImmutableArray<Guid> CreatedRunIds => _createdRunIds.ToImmutableArray();

    public ImmutableArray<RunEvent> Events => _events.ToImmutableArray();

    public ImmutableArray<RunSummary> Summaries => _summary is null
        ? ImmutableArray<RunSummary>.Empty
        : ImmutableArray.Create(_summary);

    public ImmutableArray<string> Operations => _operations.ToImmutableArray();

    public Task<JournalOperationResult> CreateRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operations.Add("create");
        _onInvoked?.Invoke("journal.create");

        if (ThrowOnCreate)
        {
            throw new InvalidOperationException("private journal detail");
        }

        _createdRunIds.Add(runId);
        return Task.FromResult(JournalOperationResult.Success("C:\\Vela\\logs\\" + runId.ToString("D")));
    }

    public Task<JournalOperationResult> OpenExistingRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenExistingRunCalls++;
        _operations.Add("open-existing");
        _onInvoked?.Invoke("journal.open-existing");
        return Task.FromResult(JournalOperationResult.Success(null));
    }

    public Task<JournalAppendResult> AppendAsync(RunEventDraft eventDraft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendCalls++;
        _operations.Add($"append:{eventDraft.OperationName}");
        _onInvoked?.Invoke("journal.append");

        if (ThrowOnAppend)
        {
            throw new InvalidOperationException("private journal detail");
        }

        var @event = new RunEvent(
            ++_nextSequence,
            eventDraft.OccurredAtUtc,
            eventDraft.RunId,
            eventDraft.Phase,
            eventDraft.Level,
            eventDraft.OperationName,
            eventDraft.Arguments,
            eventDraft.ExitCode,
            eventDraft.Duration,
            eventDraft.Output,
            eventDraft.TerminalResult);
        _events.Add(@event);
        return Task.FromResult(JournalAppendResult.Success(@event));
    }

    public Task<JournalOperationResult> WriteSummaryAsync(RunSummary summary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SummaryWriteCalls++;
        _operations.Add("summary");
        _onInvoked?.Invoke("journal.summary");

        if (ThrowOnWriteSummary)
        {
            throw new InvalidOperationException("private journal detail");
        }

        _summary = summary;
        return Task.FromResult(JournalOperationResult.Success("C:\\Vela\\logs\\" + summary.RunId.ToString("D")));
    }

    public Task<JournalReadResult> ReadEventsAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new JournalReadResult(
                _events.Where(@event => @event.RunId == runId && @event.Sequence > afterSequence).ToImmutableArray()));
    }
}

public sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
