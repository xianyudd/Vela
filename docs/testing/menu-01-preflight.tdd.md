# 菜单 01「状态总览 / 预检」TDD 证据

来源：本轮交接计划《菜单 01「状态总览 / 预检」信息规划》。

| 保证 | 测试 | 类型 |
|---|---|---|
| 五项门禁按固定顺序投影，容量使用 GiB/TiB，稀疏状态本地化 | `PreflightOverviewViewModelTests` | 单元 |
| warning 通知会把预检结论映射为「预检未通过」，首页同时显示首条具体原因 | `AutomaticPreflightCoordinatorTests.Warning_notice_keeps_preflight_in_attention_state`、`PreflightOverviewViewModelTests.Home_projection_prioritizes_decision_context_over_internal_gate_names` | 集成 / 单元 |
| `R/r` 仅重跑当前菜单 01 的预检动作，不派发压缩 | `VelaTerminalShellTests.Menu_one_exposes_r_refresh_without_dispatching_a_compaction_action`、`VelaTerminalShellTests.Menu_one_accepts_shifted_uppercase_r_from_terminal_input` | 交互 |
| 锁定实例后，影响预览与确认页使用该实例的发行版和 VHDX；实例从新清单消失时锁定状态失效 | `CompactionTargetProfileFactoryTests`、`VelaTerminalShellTests.Locked_target_profile_and_preview_use_the_selected_instance`、`VelaTerminalShellTests.Locked_target_is_cleared_when_a_new_inventory_drops_that_instance` | 单元 / 交互 |
| 影响预览显示锁定目标的预计可回收空间具体值；优先读取 VHDX 内 ext4 已用块，WSL `df` 作为回退，并淘汰旧导航结果 | `WslCompactionImpactEstimatorTests`、`VelaTerminalShellTests.Compaction_preview_renders_the_estimated_reclaimable_space_for_the_locked_target`、`VelaTerminalShellTests.Compaction_preview_ignores_an_estimate_from_an_old_navigation_revision` | 单元 / 交互 |
| Y/y 在压缩影响预览中进入确认；确认后的 `OperationRequest`、worker journal 和完成页持续使用同一锁定目标 | `VelaTerminalShellTests.Action_preview_accepts_lowercase_and_shifted_uppercase_y`、`VelaTerminalShellTests.Run_state_views_keep_the_locked_target_and_fixed_action_bar`、`CompactionTargetProfileFactoryTests.CreateRequest_carries_the_locked_target_into_the_compact_operation` | 交互 / 集成边界 |
| 160×45、120×35、100×30、80×24、60×16 保留内容与固定底部操作条 | `VelaTerminalShellTests.Menu_one_visual_bands_keep_content_and_fixed_action_bar` | 视图 |
| 旧结果淘汰、异步预检 UI 更新与只读路径保持不变 | `VelaTerminalHostTests`、`WorkflowPreflightViewModelSourceTests` | 集成 |

宽屏首页采用「执行目标选择」结构：顶部只保留扫描结果和下一步说明，下面以实例表展示发行版、当前体积、VHDX 路径状态和 READY/RUNNING/BLOCKED 状态。实例数据来自只读预检返回的已安装发行版清单；目标发行版缺失时在提示中明确说明，实例数量仍以实际清单为准。
96 列以上显示体积和 VHDX 状态，72–95 列隐藏详细容量，72 列以下只保留当前目标与核心状态。表格选择、Enter 锁定目标、Tab 切换到左侧菜单、R 重跑预检均走 Terminal.Gui 事件链，底部操作条保持固定。
VHDX 原始路径不进入首页投影，只展示「已配置 / 未读取」状态；容量仍统一使用 GiB/TiB 格式化。状态使用文字与符号同时表达，不依赖颜色单独传达风险。

影响预览的估算口径为：`max(0, 当前 VHDX 文件长度 - ext4 根文件系统已用字节)`。估算器优先读取停止状态下仍可读的 VHDX 元数据与 ext4 superblock，不挂载、不启动发行版；格式不匹配时回退到只读 `wsl df`。执行完成页仍以 worker 的压缩前后快照差值作为实际回收空间。

验证记录：

- `dotnet test Vela.sln --no-restore --nologo`：432/432 通过。
- `dotnet test tests/Vela.Tests/Vela.Tests.csproj --no-restore --filter FullyQualifiedName~WslCompactionImpactEstimatorTests`：4/4 通过。
- `dotnet.exe test tests/Vela.Tests/Vela.Tests.csproj -c Release --no-restore --nologo -p:CollectCoverage=true -p:CoverletOutput=./../../artifacts/coverage/coverage -p:CoverletOutputFormat=cobertura`：全量测试与 coverage gate 以当前仓库结果为准。
- `dotnet restore Vela.sln --locked-mode --nologo`：所有项目均是最新的。
- Release Cobertura：`Vela.Core` 80.31%、`Vela.Windows` 82.05%；`scripts/Verify-Coverage.ps1` 通过。

核心流程审查追加保证：锁定目标的预检快照优先于清单旧体积；停止目标缺少离线 ext4 用量时不启动 WSL；worker/UAC 先发布 canonical terminal event，再写 summary；summary 持久化异常不会重写已发布终态；日志入口保留在 TUI 的“日志归档”，不启动外部目录查看器。

菜单 01 的 R/r 仍只触发只读预检；菜单 02 的影响预览从已锁定实例创建 `OperationRequest`，只有用户完成两次 Y 确认后才交给 `ElevatedOperationCoordinator`。父 TUI 轮询同一 RunId 的 worker journal，运行页只展示真实事件，完成页从可信 summary 显示耗时与实际回收空间；存储档案只保留显示名和停止范围等配置。
