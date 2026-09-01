# TUI read-only migration — TDD evidence

> Point-in-time record from the TUI read-only migration. Test counts, SDK
> versions, and coverage figures below describe that session, not the current
> repository. For current baselines see
> [testing-and-release.md](../testing-and-release.md).

Source intent: supplied TUI optimization and tmux read-only plan.

| Guarantee | Test | Type | Status |
|---|---|---|---|
| Wide and narrow layouts use one continuous navigation focus | `VelaTerminalShellTests.Navigation_has_one_focus_list_with_continuous_selection_in_every_layout` | unit | added |
| `yes` is rejected on the impact confirmation page without dispatching an operation | `VelaTerminalShellTests.Confirmation_page_shows_impact_and_rejects_non_exact_input_without_requesting_an_action` | unit | added |
| Journal event and terminal mapping has no synthetic percentage and keeps no-reclaim distinct | `RunProgressMapperTests` | unit | added |

RED/GREEN execution, first attempt: local Linux shell had no .NET SDK, and Windows PowerShell reported that `global.json` requested SDK `10.0.302` while only `9.0.305` was installed. No tests were run in that attempt and no result was asserted from it.

The optional `scripts/test-tui-readonly-tmux.sh` sends navigation, `yes`, `YES `, and Escape only. It never submits `YES`; when a test guard log is supplied it compares the log before and after the session.

Follow-up verification on 2026-08-10, after the requested SDK became available, ran on Windows .NET SDK 10.0.302: the full Debug and Release suites each passed 386 tests, and Release coverage stayed above the 80% line threshold. That run predates two later changes — the suite has since grown well past 386 tests, and `scripts/Verify-Coverage.ps1` now gates four assemblies (`Vela.Core`, `Vela.Windows`, `Vela.Application`, `Vela.Tui`) rather than the two checked here.
