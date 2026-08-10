# 菜单 01「状态总览 / 预检」TDD 证据

来源：本轮交接计划《菜单 01「状态总览 / 预检」信息规划》。

| 保证 | 测试 | 类型 |
|---|---|---|
| 五项门禁按固定顺序投影，容量使用 GiB/TiB，稀疏状态本地化 | `PreflightOverviewViewModelTests` | 单元 |
| warning 通知会把预检结论映射为「预检未通过」，首页同时显示首条具体原因 | `AutomaticPreflightCoordinatorTests.Warning_notice_keeps_preflight_in_attention_state`、`PreflightOverviewViewModelTests.Home_projection_prioritizes_decision_context_over_internal_gate_names` | 集成 / 单元 |
| `R` 仅重跑当前菜单 01 的预检动作，不派发压缩 | `VelaTerminalShellTests.Menu_one_exposes_r_refresh_without_dispatching_a_compaction_action` | 交互 |
| 锁定实例后，影响预览与确认页使用该实例的发行版和 VHDX；实例从新清单消失时锁定状态失效 | `CompactionTargetProfileFactoryTests`、`VelaTerminalShellTests.Locked_target_profile_and_preview_use_the_selected_instance`、`VelaTerminalShellTests.Locked_target_is_cleared_when_a_new_inventory_drops_that_instance` | 单元 / 交互 |
| 160×45、120×35、100×30、80×24、60×16 保留内容与固定底部操作条 | `VelaTerminalShellTests.Menu_one_visual_bands_keep_content_and_fixed_action_bar` | 视图 |
| 旧结果淘汰、异步预检 UI 更新与只读路径保持不变 | `VelaTerminalHostTests`、`WorkflowPreflightViewModelSourceTests` | 集成 |

宽屏首页采用「执行目标选择」结构：顶部只保留扫描结果和下一步说明，下面以实例表展示发行版、当前体积、VHDX 路径状态和 READY/RUNNING/BLOCKED 状态。实例数据来自只读预检返回的已安装发行版清单；目标发行版缺失时在提示中明确说明，实例数量仍以实际清单为准。
96 列以上显示体积和 VHDX 状态，72–95 列隐藏详细容量，72 列以下只保留当前目标与核心状态。表格选择、Enter 锁定目标、Tab 切换到左侧菜单、R 重跑预检均走 Terminal.Gui 事件链，底部操作条保持固定。
VHDX 原始路径不进入首页投影，只展示「已配置 / 未读取」状态；容量仍统一使用 GiB/TiB 格式化。状态使用文字与符号同时表达，不依赖颜色单独传达风险。

验证记录：

- `dotnet test Vela.sln -c Debug --no-restore --nologo`：378/378 通过。
- `dotnet build Vela.sln -c Release --no-restore --nologo`：0 警告、0 错误。
- `dotnet test Vela.sln -c Release --no-restore --nologo`：378/378 通过。
- Release Cobertura：`Vela.Core` 80.33%、`Vela.Windows` 81.30%；`scripts/Verify-Coverage.ps1` 通过。

执行路径仍仅使用预检工作流；菜单 01 未创建压缩请求、未启动 worker，也未调用 WSL 停止或 DiskPart 压缩动作。菜单 02 的影响预览和确认页会从已锁定实例创建临时执行档案，发行版与 VHDX 路径均来自该实例；存储档案只保留显示名和停止范围等配置。
