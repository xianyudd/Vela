namespace Vela.Core.Models;

public static class TerminalResultSemantics
{
    public static bool IsSuccessful(TerminalResult result) =>
        result is TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim;

    public static int ToExitCode(TerminalResult result) =>
        result switch
        {
            TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim => 0,
            TerminalResult.ValidationFailed => 2,
            TerminalResult.ShutdownTimedOut => 3,
            TerminalResult.DiskPartPreflightFailed => 4,
            TerminalResult.DiskPartCompactFailed => 5,
            TerminalResult.WorkerInterrupted or TerminalResult.CancelledBeforeElevation => 10,
            _ => 10
        };

    public static TerminalResult NormalizeSummaryResult(RunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!IsSuccessful(summary.TerminalResult) || summary.ReclaimedBytes is not long reclaimedBytes)
        {
            return summary.TerminalResult;
        }

        return reclaimedBytes == 0
            ? TerminalResult.CompletedWithNoReclaim
            : TerminalResult.Succeeded;
    }

    public static bool IsTerminalOperation(string? operationName) =>
        operationName is "UacCancelled" or "UacLaunchFailed" or "WorkerCompleted" or "WorkerFailed";

    public static bool TryMapTerminalEvent(
        RunEvent @event,
        out TerminalResult terminalResult)
    {
        ArgumentNullException.ThrowIfNull(@event);
        terminalResult = default;

        switch (@event.OperationName)
        {
            case "UacCancelled":
                if (@event.Phase != RunPhase.Elevation ||
                    @event.Level != RunEventLevel.Error ||
                    !IsOptionalCompatibleExitCode(@event.ExitCode, TerminalResult.CancelledBeforeElevation))
                {
                    return false;
                }

                terminalResult = TerminalResult.CancelledBeforeElevation;
                return IsExplicitResultCompatible(@event, terminalResult);

            case "UacLaunchFailed":
                if (@event.Phase != RunPhase.Elevation ||
                    @event.Level != RunEventLevel.Error ||
                    !IsOptionalCompatibleExitCode(@event.ExitCode, TerminalResult.WorkerInterrupted))
                {
                    return false;
                }

                terminalResult = TerminalResult.WorkerInterrupted;
                return IsExplicitResultCompatible(@event, terminalResult);

            case "WorkerCompleted":
                if (@event.Phase != RunPhase.Completed ||
                    @event.Level != RunEventLevel.Information ||
                    @event.ExitCode != 0)
                {
                    return false;
                }

                terminalResult = @event.TerminalResult ?? TerminalResult.Succeeded;
                return IsSuccessful(terminalResult) &&
                       IsExplicitResultCompatible(@event, terminalResult);

            case "WorkerFailed":
                if (@event.Phase != RunPhase.Failed ||
                    @event.Level != RunEventLevel.Error)
                {
                    return false;
                }

                if (@event.TerminalResult is { } explicitResult)
                {
                    if (!Enum.IsDefined(explicitResult) ||
                        IsSuccessful(explicitResult) ||
                        @event.ExitCode != ToExitCode(explicitResult))
                    {
                        return false;
                    }

                    terminalResult = explicitResult;
                    return true;
                }

                return TryFromFailureExitCode(@event.ExitCode, out terminalResult);

            default:
                return false;
        }
    }

    private static bool TryFromFailureExitCode(
        int? exitCode,
        out TerminalResult terminalResult)
    {
        terminalResult = exitCode switch
        {
            2 => TerminalResult.ValidationFailed,
            3 => TerminalResult.ShutdownTimedOut,
            4 => TerminalResult.DiskPartPreflightFailed,
            5 => TerminalResult.DiskPartCompactFailed,
            10 => TerminalResult.WorkerInterrupted,
            _ => default
        };

        return exitCode is 2 or 3 or 4 or 5 or 10;
    }

    private static bool IsOptionalCompatibleExitCode(
        int? exitCode,
        TerminalResult terminalResult) =>
        exitCode is null || exitCode == ToExitCode(terminalResult);

    private static bool IsExplicitResultCompatible(
        RunEvent @event,
        TerminalResult expected) =>
        !@event.TerminalResult.HasValue ||
        (Enum.IsDefined(@event.TerminalResult.Value) &&
         @event.TerminalResult.Value == expected);
}
