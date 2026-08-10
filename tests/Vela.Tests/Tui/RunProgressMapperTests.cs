using System.Collections.Immutable;
using Vela.Core.Models;
using Vela.Tui.Application;
using Vela.Tui.ProgramModes;

namespace Vela.Tests.Tui;

public sealed class RunProgressMapperTests
{
    [Fact]
    public void Event_mapping_projects_the_real_journal_phase_to_a_visible_progress_stage()
    {
        var progress = RunProgressMapper.FromEvent(new RunEvent(
            1, DateTimeOffset.UnixEpoch, Guid.NewGuid(), RunPhase.Validation,
            RunEventLevel.Information, "Inspecting", ImmutableArray<string>.Empty,
            null, null, null));

        Assert.Equal(RunProgressState.Running, progress.State);
        Assert.Equal(8, progress.Percent);
    }

    [Fact]
    public void Completed_with_no_reclaim_remains_a_distinct_successful_terminal_message()
    {
        var terminal = new RunEvent(
            2, DateTimeOffset.UnixEpoch, Guid.NewGuid(), RunPhase.Completed,
            RunEventLevel.Information, "WorkerCompleted", ImmutableArray<string>.Empty,
            0, TimeSpan.Zero, null, TerminalResult.CompletedWithNoReclaim);
        var progress = RunProgressMapper.FromTerminal(new RunJournalPollResult(
            true, ImmutableArray<RunEvent>.Empty, terminal, 2));

        Assert.Equal(RunProgressState.Succeeded, progress.State);
        Assert.Contains("完成但未回收空间", progress.Message);
        Assert.Equal(100, progress.Percent);
    }
}
