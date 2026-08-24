# Vela 测试、验收与发布手册

以下命令只把构建、coverage、publish 和测试数据写入项目内 `artifacts\`；本次文档验证不执行真实 WSL、DiskPart、WSL 停止或 VHDX 压缩。没有 native Windows 证据时，P6 只能记录为 `BLOCKED`，不能用 WSL/WSL2 结果替代。只有最终人工验收才允许在明确影响面板和用户确认后执行真实动作。
## 1. 验证原则

Vela 将真实 Windows 原生命令封装在 Vela.Windows。Core 工作流通过 fake adapter 覆盖；测试层次如下：

~~~text
Unit tests         纯模型、验证、状态机、输出解析、配置、日志
Integration tests  真实文件系统 + 无害 helper process + 发布 EXE 启动
Manual acceptance  真实 Win11 / WSL 环境下的只读预检与用户确认后的最终流程
~~~

开发测试中的执行阶段使用 fake IWslClient、fake IDiskPartClient、fake IProcessRunner。这样覆盖状态、超时、错误与日志分支，同时保持测试可重复。

## 2. 单元测试矩阵

| 组件 | 必测情形 | 关键断言 |
| --- | --- | --- |
| ProfileValidator | 空名称、相对路径、后缀、控制字符、ASCII、timeout 边界、有效路径 | 返回明确且可呈现的验证结果。 |
| Profile / OperationRequest | 值相等、RunId、不可变复制、无映射 override | 业务对象没有共享可变状态。 |
| PreflightWorkflow | 成功、映射不一致、发行版缺失、检查异常 | 执行阶段计数为 0。 |
| CompactionWorkflow | 成功、超时、detail 异常、compact 异常、0 B 回收、句柄探测 Held / Free / Unknown / 抛异常 | 每个退出点写最终 summary；Held 时 diskpart 调用数为零，其余一律放行。 |
| VhdxHandleProbe | 无占用、独占占用、共享读占用、探测后立即释放、文件不存在、路径不可探测、取消 | 只有 Win32 32/33 判 Held，其余为 Unknown；探测无副作用。 |
| NativeToolPaths / ProcessRunner | 三个绝对原生命令路径、参数边界、超时 | 命令由固定路径和 ArgumentList 构造。 |
| LxssProfileResolver / WslClient | 映射、清单、中英文输出、参数生成 | registry path 与参数数组精确。 |
| VhdxInspector | 文件快照、盘快照、sparse unknown | 数值与 nullable sparse 语义正确。 |
| DiskPartScriptBuilder | 路径、ASCII、命令顺序、临时文件清理 | detail vdisk 始终先于 compact vdisk。 |
| FileRunJournal | RunId 目录、NDJSON、序列号、完整行读取、summary、异常收尾、保留期 | JSON 可反序列化，worker 可追加。 |
| JsonProfileStore | 初始配置、迁移、原子保存 | JSON 完整且替换原子化。 |
| OperationRequestStore / UacWorkerLauncher | request、固定 worker 参数、UAC 取消 | pending 路径由 RunId 派生。 |
| WorkerMode | 管理员身份、额外参数、RunId、映射二次校验、非交互分支 | 失败时动作调用数为 0。 |
| TUI application | `TuiApplication` 单一串行读键所有权、typed page controller、状态变化才重绘、↑↓/Enter/Esc、confirmation Backspace/16 字符上限、exact `YES`、取消前/读键期间 cancellation | 任意时刻最多一个同步 read；无关键 no-op；取消不 dispatch key、不泄漏异常。 |
| FrameRenderer / display boundary | `<80`、`80–119`、`>=120` 宽度边界，低高度预算，interactive/redirected 同一 composition，CJK/combining/markup/CSI/OSC/control hostile text | 单帧 redirected 不清屏；任何 frame 均不含 raw path、RunId、raw exception、native output 或 raw enum name。 |
| ProfileService / secondary TUI | Profile 选择、新建、编辑、删除约束；write-only VHDX 编辑；typed ShutdownMode；invariant `5–300` timeout；RecentRuns 最多 20 条、损坏 summary、详情和 TUI 内日志查看；OpenLogs | CRUD 持久化且通过 `ProfileValidator`；执行目标变化必须 exact `YES`；路径不越出 AppPaths 根且不进入 frame。 |
| RunJournalPoller | sequence cursor、foreign RunId、gap/duplicate/nonmonotonic、invalid terminal、取消、timeout、连续读取失败及复位、callback exactly-once/order/exception/cancellation | 不排序修复损坏 journal；取消/超时不伪造 worker 终态；ReadFailed 在阈值后确定返回。 |
| CompactRunGate / coordinator | 同时 Compact、可信活动 RunId、失效 gate、UAC 取消/启动失败 | single-worker gate 阻止第二个 worker；失败路径释放或保留可诊断状态。 |

## 3. 常用验证命令

在 Developer PowerShell for VS 2022：

~~~powershell
Set-Location 'D:\Jason\Documents\Workspace\vs2022\repo\Vela'

# 依赖锁定与开发构建
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Debug

# 全量测试
dotnet test .\Vela.sln -c Debug

# 预检工作流
dotnet test .\tests\Vela.Tests --filter FullyQualifiedName~PreflightWorkflowTests

# Global / Distro 范围规则
dotnet test .\tests\Vela.Tests --filter FullyQualifiedName~CompactionWorkflowTests

# 配置和日志持久化
dotnet test .\tests\Vela.Tests --filter 'FullyQualifiedName~JsonProfileStore|FullyQualifiedName~FileRunJournal'

# 强制 80% line coverage gate，分别统计 Core 与 Windows
# 先生成 Cobertura 报告，再使用独立脚本检查两个程序集
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release -p:CollectCoverage=true -p:CoverletOutput=.\..\..\artifacts\coverage\coverage -p:CoverletOutputFormat=cobertura -p:Include="[Vela.Core]*%2C[Vela.Windows]*" -p:ExcludeByFile="**/Program.cs"
pwsh -NoProfile -File .\scripts\Verify-Coverage.ps1
~~~

验收线：

~~~text
Vela.Core 与 Vela.Windows 的 line coverage ≥ 80%
所有测试绿色
零编译警告
locked restore 成功
~~~

## 4. TUI 非破坏验收

先验证不执行操作的交互与输出契约：

| 检查项 | 预期 |
| --- | --- |
| TUI 启动 | Vela — WSL VHDX Compact 标题和主菜单正确显示。 |
| 响应式布局 | `<80` 只保留目标/状态/焦点/帮助；`80–119` 纵向堆叠；`>=120` 左导航右工作区；低高度列表有界。 |
| 输入所有权 | 主菜单、Profile、Recent 和 confirmation 共用一个串行读键入口；快速按键、Esc、Ctrl+C 后无重复消费或 orphan read。 |
| Profile 编辑 | 旧 VHDX 路径不回显，新路径只显示字符数；ShutdownMode 用方向键选择；timeout 只接受 5–300 整数。 |
| 确认 | 首启/档案确认只接受精确大写 `YES`；压缩流程使用两次 `Y`；Esc 均取消。 |
| 安全投影 | frame 不显示 raw VHDX/registry/run/log path、RunId、raw exception、native output 或 raw enum name。 |
| 终态标签 | “成功”与“完成但未回收空间”保持不同显示。 |
| redirected | 只输出一个确定性 frame，不清屏、不读输入。 |

只读预检人工验收再检查下列项目：

| 检查项 | 预期 |
| --- | --- |
| 默认档案 | Ubuntu-24.04、VHDX 已配置、Global、45 秒。 |
| 注册表映射 | 只显示 matched/mismatched/not found/failed 状态，不显示 Lxss BasePath。 |
| WSL 清单 | 运行中的发行版和受控清单信息可见。 |
| VHDX 快照 | 文件长度、最后写入时间、稀疏标志、宿主盘容量和可用空间可见，不显示路径。 |
| 日志 | journal 内部创建 `logs\<RunId>\events.ndjson`、`run.log`、`summary.json`；UI 只显示日志是否可用。 |
| 错误显示 | 路径或映射问题显示 bounded、本地化错误；原始异常只进入日志。 |

与迁移的旧脚本预检结果对照：

~~~powershell
pwsh -ExecutionPolicy Bypass -File .\legacy\powershell\wsl.ps1 -WhatIf
~~~

对照重点：内部可信日志中的目标路径、发行版名、VHDX 字节数、宿主盘可用空间和运行中的发行版；TUI 只核对相应状态与数值安全投影，日志文字无须逐字一致。

## 5. 执行流程人工验收

最终动作验收由用户在影响面板确认后自行发起。验收顺序：

1. 在 TUI 中选择“执行压缩”。
2. 核对 Profile 身份、VHDX 已配置状态、Global / Distro 范围、正在运行的发行版与影响提示；原始目标路径只在可信配置/日志中核对，不要求 UI 回显。
3. 在影响预览按 `Y` 进入确认页，再按 `Y` 确认 UAC worker 启动。
4. 观察父 TUI 轮询的 logs\<RunId>\events.ndjson 持续增加。
5. 检查 worker 分支跳过主菜单和确认提示，只向同一 journal 追加事件与退出码。
6. 检查 worker 再次写入管理员身份、映射验证和压缩前快照。
7. 若结果为 `DiskPartPreflightFailed` 且诊断为 `TargetVhdxInUse`（TUI 显示「目标 VHDX 仍被占用」）：这是预期的诚实失败，不是缺陷。按诊断正文处理——Distro 范围改用 Global 范围或先执行 `wsl --shutdown`；Global 范围排查 WSL 之外的占用者。参见 docs/architecture.md 5.3。
8. 检查 DiskPart detail 记录与最终 summary。
9. 对比可信日志中的压缩前后 VHDX 文件长度、宿主盘可用空间与 `reclaimedBytes`。
10. 在“最近运行记录”页确认 status、elapsed time、reclaimed bytes 和日志可用状态；需要原始路径、RunId 或 native output 时通过受信任日志 capability 追溯。

一次运行后的结果类别：

~~~text
Succeeded
CompletedWithNoReclaim
ValidationFailed
ShutdownTimedOut
DiskPartPreflightFailed
DiskPartCompactFailed
WorkerInterrupted
CancelledBeforeElevation
~~~

CompletedWithNoReclaim 表示流程完成且 VHDX 长度差为 0 B。

## 6. 发布配置

### 6.1 Publish profile 核心属性

~~~xml
<PropertyGroup>
  <AssemblyName>Vela</AssemblyName>
  <TargetFramework>net10.0-windows</TargetFramework>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <PublishTrimmed>false</PublishTrimmed>
  <DebugType>embedded</DebugType>
</PropertyGroup>
~~~

PublishTrimmed=false 是首版决策：Spectre.Console、诊断输出、反序列化模型和 Windows adapter 保持完整，便于定位问题。自包含 EXE 包含运行时；IncludeNativeLibrariesForSelfExtract=true 将原生运行时库纳入单文件。[Microsoft Learn：单文件部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

### 6.2 发布命令

~~~powershell
Set-Location 'D:\Jason\Documents\Workspace\vs2022\repo\Vela'
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Release --no-restore
dotnet test .\Vela.sln -c Release --no-build
dotnet publish .\src\Vela.Tui\Vela.Tui.csproj -c Release --no-restore -p:PublishProfile=win-x64-singlefile -o .\artifacts\publish\win-x64
~~~

如果锁定文件缺少 `win-x64` runtime metadata，必须先在受控分支中显式更新并审查 lock file；验收命令本身不得隐式改写依赖锁定文件。发布输出：

~~~text
artifacts\publish\win-x64\Vela.exe
~~~

publish profile 把 `AssemblyName` 从 `Vela.Tui` 覆写为 `Vela`。若让 publish 与普通构建共用 `artifacts\build\`，它会在那里留下一个 `Vela.exe` 并使普通构建产物 `Vela.Tui.exe` 消失——而 `scripts\open-vela-tui.cmd` 启动的正是 `Vela.Tui.exe`，于是一次 publish 就会打断启动脚本，更糟的是可能让人跑到上一次 publish 留下的陈旧 `Vela.exe`。因此 `Directory.Build.props` 按全局属性 `PublishProfile` 分流输出目录：

~~~text
artifacts\build\<Project>\          普通 build 与 test
artifacts\publish-build\<Project>\  仅 publish 的中间产物
~~~

隔离写在 props 里而不是命令行参数上，所以上面的发布命令无需附加任何参数，也不存在“忘了加”的情况。

发布候选的重定向 smoke check（不触发预检动作或 WSL 停止）：

~~~powershell
Get-Item .\artifacts\publish\win-x64\Vela.exe | Select-Object FullName,Length,LastWriteTime
Get-FileHash .\artifacts\publish\win-x64\Vela.exe -Algorithm SHA256
cmd.exe /c .\artifacts\publish\win-x64\Vela.exe < NUL
~~~

重定向输入路径使用不清屏的纯单帧输出，避免无控制台时访问 cursor/buffer；首启未确认时应输出首启确认帧并以非零状态返回，已有项目内测试数据配置时应输出主菜单帧并返回 0。该 smoke 只验证 EXE 启动和重定向语义，不触发预检动作、WSL 停止或 DiskPart。真实预检选择和影响面板仍由最终人工验收执行。

## 7. 交付目录

发布候选的稳定入口（完成发布任务后创建）：

~~~text
D:\DevTools\Vela\
├─ Vela.exe
├─ README.md
└─ logs-link.txt
~~~

运行配置与完整记录：

~~~text
%LocalAppData%\Vela\
├─ config.json
├─ pending\
└─ logs\<RunId>\
~~~

这使发布 EXE 的位置与用户配置、请求和日志彼此独立。桌面 WSL2-VHDX-Compact 目录在 legacy\powershell 迁移哈希、预检对照和最终验收完成后进入移除清单。

开发与测试中的 AppPaths 指向项目内 artifacts\test-data\ 或 fixture 目录。发布版首次创建 %LocalAppData%\Vela 前显示完整数据路径，由用户确认；D:\DevTools\Vela 的发布写入同样在 Task 13 显示目标和覆盖文件后执行。

## 8. 最终交付检查表

- [ ] Vela.sln 在 Visual Studio 2022 正常打开。
- [ ] Debug 与 Release 构建完成且零警告。
- [ ] locked restore、xUnit 全量测试和 80% coverage gate 通过。
- [ ] 发布 profile 生成项目内 `artifacts\publish\win-x64\Vela.exe`，并记录文件大小与 SHA256。
- [ ] 重定向 smoke 输出首启/主菜单帧且不因无控制台清屏失败；未确认首启返回非零，已有配置返回 0。
- [ ] D:\DevTools\Vela\Vela.exe 是日常入口。
- [ ] 首次启动建立本地配置目录与默认档案。
- [ ] 预检生成三类运行记录。
- [ ] 配置编辑后重启仍保留档案。
- [ ] UAC worker 完成后留下最终 summary 和退出码。
- [ ] Global / Distro 的参数与停止条件均由 workflow 测试覆盖。
- [ ] Distro 范围的真实限制已记录：`--terminate` 不从共享工具 VM 卸载 vhdx，故它只适用于本工具 VM 生命周期内未启动过的发行版（docs/architecture.md 5.3）。
- [ ] diskpart 之前的句柄探测测试通过：Held 终止且 diskpart 调用数为零，Free / Unknown / 抛异常均放行。
- [ ] worker 运行时跳过主菜单和确认提示，父 TUI 保持唯一交互入口。
- [ ] worker 的映射不一致测试证明动作适配器调用数为零。
- [ ] single-worker gate 的并发 Compact 测试通过。
- [ ] RunJournalPoller 的取消、timeout、ReadFailed 和 sequence 游标测试通过。
- [ ] `legacy\powershell\wsl.ps1` 和 `legacy\powershell\README.md` 保留旧工具行为对照。
- [ ] D:\DevTools\Vela\README.md 说明启动、菜单键位、日志位置、结果类别和排障路径。
- [ ] 所有开发期生成文件位于项目根或 artifacts\；项目外写入都有确认记录。
