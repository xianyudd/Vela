# TUI read-only migration — TDD evidence

Source intent: supplied TUI optimization and tmux read-only plan.

| Guarantee | Test | Type | Status |
|---|---|---|---|
| Wide and narrow layouts use one continuous navigation focus | `VelaTerminalShellTests.Navigation_has_one_focus_list_with_continuous_selection_in_every_layout` | unit | added |
| `yes` is rejected on the impact confirmation page without dispatching an operation | `VelaTerminalShellTests.Confirmation_page_shows_impact_and_rejects_non_exact_input_without_requesting_an_action` | unit | added |
| Journal event and terminal mapping has no synthetic percentage and keeps no-reclaim distinct | `RunProgressMapperTests` | unit | added |

RED/GREEN execution: local Linux shell has no .NET SDK; Windows PowerShell reports SDK `10.0.302` requested by `global.json`, while only `9.0.305` is installed. Tests were not run and no result is asserted here.

The optional `scripts/test-tui-readonly-tmux.sh` sends navigation, `yes`, `YES `, and Escape only. It never submits `YES`; when a test guard log is supplied it compares the log before and after the session.

Follow-up verification on 2026-08-10 used the installed Windows .NET SDK 10.0.302: the full Debug and Release suites each passed 361 tests; Release coverage remained above the Core/Windows 80% line threshold.
