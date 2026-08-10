using Vela.Core.Models;
using Vela.Tui.ProgramModes;

namespace Vela.Tui.Application;

/// <summary>Maps durable journal facts to display state; it never starts or controls a worker.</summary>
public static class RunProgressMapper
{
    public static RunProgressViewModel FromEvent(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return new RunProgressViewModel(
            RunProgressState.Running,
            $"{TuiDisplayText.LabelForPhase(@event.Phase)} / {TuiDisplayText.LabelForOperation(@event.OperationName)}",
            Percent: null);
    }

    public static RunProgressViewModel FromTerminal(RunJournalPollResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status != RunJournalPollStatus.Terminal)
        {
            return new RunProgressViewModel(
                result.Status switch
                {
                    RunJournalPollStatus.Cancelled => RunProgressState.Cancelled,
                    RunJournalPollStatus.TimedOut => RunProgressState.TimedOut,
                    RunJournalPollStatus.ReadFailed => RunProgressState.ReadFailed,
                    _ => RunProgressState.Failed
                },
                TuiDisplayText.LabelForPollStatus(result.Status),
                Percent: null);
        }

        var terminal = result.TerminalResult ?? TerminalResult.WorkerInterrupted;
        var succeeded = terminal is TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim;
        return new RunProgressViewModel(
            succeeded ? RunProgressState.Succeeded : RunProgressState.Failed,
            $"运行终态：{TuiDisplayText.LabelForTerminal(terminal)}。",
            Percent: null);
    }
}
