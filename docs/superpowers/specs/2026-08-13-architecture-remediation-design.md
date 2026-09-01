# Vela Terminal.Gui 架构收敛整改设计

**状态：** 已确认设计。

**日期：** 2026-08-13。

**适用版本：** `v0.1.0-preview.1` 之后的首个架构整改版本。

**决策：** 保留当前 Release 使用的 Terminal.Gui 界面，并将其收敛为唯一交互式 TUI 路径。

> **时点声明：** 本文按 2026-08-13 的仓库状态写成，是设计记录而非现状描述。文中「当前」一律指该日状态：
> 依赖图为整改前的三层结构（此后已加入 `Vela.Application`，成为四层五项目），Phase 0 的 432 项测试与
> 三程序集覆盖率基线也属当时快照（当前为 746 项测试、四程序集各 ≥80%）。设计结论仍然有效，
> 具体数字与分层请以 [architecture.md](../../architecture.md) 为准。

## 1. 背景

Vela 当前已经形成清晰的三层项目依赖：

```text
Vela.Tui ─────────────► Vela.Core
    │
    └─────────────────► Vela.Windows ─────────► Vela.Core
```

其中：

- `Vela.Core` 保存平台无关的模型、契约、验证和工作流。
- `Vela.Windows` 实现 WSL、注册表、VHDX、DiskPart、UAC、配置和日志等 Windows 能力。
- `Vela.Tui` 是组合根和用户交互层。

现有分层方向正确，目标锁定、只读预检、两次确认、worker 二次 Lxss 校验、单 worker gate 和 journal 完整性也已有较强的实现与测试基础。整改不重建这些能力，而是修复安全边界并消除交互层的双路径。

当前交互层同时保留两套结构：

1. `Program.cs + VelaTerminalShell`：真实 Release 使用的 Terminal.Gui 路径。
2. `TuiApplication + SpectreTuiInput + SpectreTuiFrameSink`：包含完整档案控制器但没有接入生产入口的旧路径。

这导致档案管理能力只存在于旧路径、架构文档与生产行为不一致，并增加测试覆盖错位和长期维护成本。

架构检查还识别出以下问题：

- DiskPart 高权限脚本写入用户临时目录，存在内容替换的 TOCTOU 风险。
- 非提权 worker 在管理员校验之前 claim pending request，可造成同用户任务抢占。
- renderer-facing state 携带并显示原始 VHDX 路径。
- TUI 把运行中的目标当作阻断项，与 worker 负责停止目标的产品流程冲突。
- `VelaTerminalShell` 等文件职责过多并超过项目文件规模约束。
- CI 尚未对 `Vela.Tui` 设置覆盖率门禁。
- 公共 API 文档规范缺少构建级约束。

## 2. 目标

本次整改实现以下目标：

1. 将 Terminal.Gui 确立为唯一交互式 TUI 框架。
2. 在生产 Terminal.Gui 路径中恢复完整的档案 N/E/D/Enter 管理能力。
3. 消除 DiskPart 脚本在提权边界上的内容替换窗口。
4. 在 request claim 之前验证 worker 管理员身份。
5. 从普通 TUI 投影中移除原始路径、RunId、native output 和异常堆栈。
6. 将“目标正在运行”表达为停止影响，而不是预检阻断。
7. 拆分大型交互和工作流文件，建立明确的页面、控制器和服务边界。
8. 将 `Vela.Tui` 纳入 80% line coverage 门禁，并逐步强制公共 API 文档。

## 3. 非目标

本次整改不包含：

- 改变 Core、Windows、TUI 三个生产项目的总体依赖方向。
- 更换 Terminal.Gui、重新设计视觉语言或重做 README 截图风格。
- 改变两次 `Y/y` 确认协议。
- 改变 operation request 或 journal 的持久化 schema。
- 改变现有 terminal result 和 worker 退出码。
- 引入通用依赖注入框架、MVVM 框架或新的 UI 框架。
- 引入计划任务、远程管理、云同步或后台自动压缩。
- 在自动化测试中调用真实 WSL 停止、DiskPart 或 VHDX 压缩。

## 4. 已考虑方案

### 4.1 方案 A：只做安全热修

先修 DiskPart、worker claim、路径投影和 running 语义，再把档案管理最小接入 Terminal.Gui；旧 TUI 路径继续保留。

优点是改动较小、可以快速消除高风险问题。缺点是双输入模型、双渲染路径和测试错位继续存在，之后仍需再次迁移。

### 4.2 方案 B：收敛到当前 Terminal.Gui 生产路径

先处理安全边界，然后把旧路径中已验证的档案管理语义迁移到 Terminal.Gui；完成行为等价后删除旧交互路径，并拆分大型文件、补齐质量门禁。

该方案复用当前 Release 界面、真实截图和现有 Terminal.Gui 测试，避免大规模视觉回归，同时解决当前的安全与架构问题。

### 4.3 方案 C：重建统一的 model-first TUI

重新抽象 page/controller/renderer，再重写 Terminal.Gui 外壳或恢复以 `TuiApplication` 为中心的架构。

该方案长期结构最整洁，但会触碰绝大多数 TUI 测试、键盘行为和截图，回归范围不适合当前 preview 整改。

### 4.4 决策

采用方案 B。Terminal.Gui 已经是发布路径和真实产品界面的基础，本次工作应围绕它收敛，而不是引入或切换 UI 技术。

## 5. 目标架构

### 5.1 运行时结构

```text
Program / Composition Root
│
├─ WorkerMode                              # 非交互式提升权限入口
│  ├─ AdministratorProbe
│  ├─ OperationRequestStore
│  ├─ LxssProfileResolver
│  └─ CompactionWorkflow
│
├─ Redirected / Startup Static Output      # Spectre.Console，仅静态单帧
│  └─ StaticFrameRenderer
│
└─ Terminal.Gui Interactive Application   # 唯一交互式 TUI
   ├─ VelaTerminalShell                    # 根窗口、页面宿主、会话导航
   ├─ TargetWorkflowController             # 选择、锁定、预检、影响、确认
   ├─ ProfileManagementController          # 档案 CRUD 与确认
   ├─ CompactionExecutionController        # UAC 启动、journal 轮询、终态
   ├─ LogArchiveController                 # 历史、日志、安全分析投影
   └─ Page Views                           # Terminal.Gui 控件与布局
```

`Spectre.Console` 可以继续用于重定向输出和启动失败时的确定性静态单帧，但不得持有交互式输入循环，也不得形成第二套页面状态机。现有 `FrameRenderer` 应在迁移完成后更名或缩小为静态输出职责。

### 5.2 职责规则

#### Composition Root

`Program.cs` 只负责：

- 解析普通模式与 worker 模式。
- 构造平台 adapter 和应用控制器。
- 建立 Terminal.Gui application 生命周期。
- 把控制器事件连接到页面宿主。

业务分支、档案编辑流程、journal 轮询细节和页面文本构造从 `Program.cs` 移出。

#### Shell

`VelaTerminalShell` 只负责：

- 根窗口和导航区域。
- 当前页面的挂载与替换。
- 全局输入路由和运行期间输入锁。
- 保存最小会话状态，例如当前页面和锁定目标标识。

Shell 不直接执行配置 I/O、journal I/O、UAC 启动或原生命令。

#### Controllers

控制器拥有页面工作流，但只通过接口访问持久化和平台能力。控制器输入和输出使用不可变 record，不直接持有 Terminal.Gui 控件。

每个控制器均满足：

- 一个公开职责。
- 输入边界显式验证。
- 异步操作接受 `CancellationToken`。
- 异常转为受控状态；详细异常只进入受信任日志。
- 可使用 fake service 独立测试。

#### Views

Terminal.Gui View 只消费 display-safe view model，并将按键或提交事件交给控制器。View 可以更新控件状态，但不读取磁盘、注册表或进程。

## 6. 安全设计

### 6.1 DiskPart 特权脚本工作区

DiskPart 仍使用 `/s <script>`，但脚本必须来自新的 `PrivilegedDiskPartWorkspace`，不再使用 `%TEMP%\Vela` 共享目录。

工作区要求：

1. 只在管理员身份验证通过的 worker 中创建。
2. 固定根路径为 `%ProgramData%\Vela\Privileged\DiskPart`；`%ProgramData%` 通过 `Environment.SpecialFolder.CommonApplicationData` 解析，后续固定段、受验证的 RunId 和随机 nonce 由代码派生，不接受外部目录输入。
3. 从受信任的 `%ProgramData%` 根开始逐级处理 `Vela`、`Privileged`、`DiskPart` 和每次运行目录。缺失目录通过 Windows access-control API 和 `SECURITY_ATTRIBUTES` 原子创建，使安全描述符在对象可见时已经生效；禁止先创建宽松目录再修补 ACL。
4. 固定目录已存在时，逐级检查 canonical path、对象类型、reparse 状态和完整安全描述符。只有与预期描述符完全一致时才复用；其余情况产生安全失败，不自动接管或修复该目录。
5. 目录和脚本文件的 owner 固定为 `BUILTIN\Administrators`，DACL 关闭继承并只授予 `SYSTEM` 和 `BUILTIN\Administrators` 完全控制；不包含当前用户 SID 的独立 ACE。
6. 目录和脚本文件必须具有 High mandatory integrity label，并设置 no-write-up 策略。owner、DACL 或 integrity label 任一不匹配都产生安全失败。
7. 创建和使用前逐级检查 reparse point；每次 detail/compact 使用新的 RunId/nonce 目录和随机文件名。
8. 脚本文件通过带相同受保护安全描述符的 Windows 原子创建调用建立，语义等同于 `FileMode.CreateNew`，禁止覆盖已有文件。
9. 文件使用 ASCII 编码。writer 完成写入后调用 `Flush(flushToDisk: true)` 并关闭写句柄。
10. 随后以 `FileAccess.Read` 和 `FileShare.Read` 重新打开只读 pin handle，复核 file identity、owner、DACL、High integrity label、普通文件类型和 reparse 状态。
11. 从复核完成到 DiskPart 进程退出始终保持 pin handle 打开。该共享模式允许 DiskPart 读取脚本，同时排除其他写入、删除和替换操作。
12. DiskPart 启动前再次验证所有目录和文件仍满足预期安全描述符；任何验证异常都以关闭方式结束。
13. 结束后执行 best-effort 清理；清理错误只写入受信任日志。
14. 任一安全验证失败时跳过 DiskPart 调用，并映射为现有 `DiskPartPreflightFailed` 终态。

DiskPart 脚本内容继续只接受 worker 严格校验后的 resolved VHDX path。`DiskPartScriptBuilder` 现有绝对路径、ASCII、扩展名、控制字符、引号和通配符检查保持不变。

### 6.2 Worker 校验顺序

新的 worker 顺序为：

```text
严格解析 --worker --run-id
  → 验证 AppPaths 派生关系
  → 验证管理员身份
  → 打开已有 journal
  → 原子 claim pending request
  → 验证 request RunId / intent / profile / source path
  → 按 Distro 重新解析 Lxss
  → 严格比较 resolved path 与 requested path
  → 执行共同预检
  → 创建特权 DiskPart 工作区
  → 停止目标 → detail → compact → 复测 → journal 终态
```

非管理员调用只返回确定的 validation exit code，不打开、claim、移动、消费或写入 pending request，也不向现有 journal 追加伪造终态。

### 6.3 展示数据边界

原始路径是应用能力，不是展示数据。整改后分成两个状态层：

```text
Trusted operation state
  Profile.VhdxPath / resolved path / RunId / native output / exception
       │
       ▼ explicit projection
Display-safe state
  distro label / ext4.vhdx / size / mapping status / stop scope / safe message
       │
       ▼
Terminal.Gui Views
```

renderer-facing record 不再包含以下字段：

- 完整 VHDX、注册表、日志或运行目录路径。
- RunId。
- exception、stack trace 或内部错误消息。
- native command stdout/stderr。
- 未映射的内部 enum 名称。

执行链仍保留内部 `LockedCompactionTarget`，其中包含创建 request 所需的可信路径。该对象只能由 controller/service 持有，不传入 Terminal.Gui View。

普通 TUI 的 VHDX 摘要固定由以下安全信息组成：

- 发行版显示名。
- 文件类型或固定文件名 `ext4.vhdx`。
- 当前体积。
- 映射状态、稀疏状态和宿主容量状态。

完整路径继续进入受信任 journal/log，日志归档页面只显示经过 `RunEventLogFormatter` 和 `TuiDisplayText` 清洗的摘要。

## 7. 档案管理设计

### 7.1 页面结构

Terminal.Gui 新增独立档案页面组合：

```text
ProfileManagementView
├─ profile list
├─ current marker
├─ safe target summary
└─ N / E / D / Enter / Esc commands

ProfileEditorView
├─ display name
├─ distro name
├─ write-only VHDX input
├─ shutdown mode selector
└─ timeout input
```

`ProfileManagementController` 复用现有 `IProfileService`、`ProfileDraft`、`ProfileValidator` 和 `JsonProfileStore`，但不复用旧的 ConsoleKey 页面控制器。

### 7.2 键盘契约

- `↑/↓`：移动档案选择。
- `Enter`：切换当前档案并触发新的只读预检。
- `N/n`：新建档案。
- `E/e`：编辑选中档案。
- `D/d`：请求删除选中档案。
- `Esc`：编辑时取消；列表页返回总览。

### 7.3 编辑与确认

- DisplayName 和 DistroName 使用长度限制与边界清洗。
- VHDX 字段保持 write-only：旧路径不进入 view model；输入过程只显示字符数和验证状态。
- ShutdownMode 使用 typed selector，只允许 `Global` 或 `Distro`。
- Timeout 使用 invariant integer，范围保持 5–300 秒。
- 编辑会改变 DistroName、VhdxPath 或 ShutdownMode 时，保存前要求精确大写 `YES` 加 Enter。
- 删除要求精确大写 `YES` 加 Enter。
- 至少保留一个档案；删除当前档案前必须先切换当前档案。
- 保存成功后返回新的不可变档案快照，不在原对象上修改。

档案切换、修改和删除成功后必须清除旧锁定目标、影响估算和确认请求，并启动新 generation 的只读预检，防止旧异步结果覆盖新档案状态。

## 8. 目标预检和运行状态语义

### 8.1 真正阻断项

只有以下条件阻止进入影响评估：

- Profile 验证失败。
- 目标发行版未安装。
- Lxss 映射不存在、读取失败或与请求路径不严格匹配。
- VHDX 不存在、检查失败或快照不可用。
- journal 不可创建或可信运行目录验证失败。
- 当前锁定目标与最新预检 generation/profile 不一致。

### 8.2 Running 是影响证据

目标处于 Running 状态不再是阻断项。它表示执行阶段需要停止目标：

- `ShutdownMode.Global`：影响面板显示全部当前运行发行版，worker 使用 `wsl --shutdown` 并等待 running inventory 为空。
- `ShutdownMode.Distro`：影响面板只强调选中目标，worker 使用 `wsl --terminate <Distro>` 并等待目标退出 running inventory。

预检仍然只读；从影响面板到第一次和第二次 `Y/y` 期间不执行停止动作。真正动作仍只发生在完成 worker 二次校验之后。

### 8.3 目标锁定不变量

同一个不可变目标标识必须贯穿：

```text
实例选择
  → 目标级只读预检
  → 影响估算
  → 第一次 Y
  → 第二次 Y
  → OperationRequest
  → worker Lxss 二次解析
  → CompactionWorkflow
```

任一页面 generation 变化、档案变化、锁定目标变化或 Lxss 映射变化都使旧确认请求失效。worker 只信任其重新读取的映射，父进程快照只用于上下文和展示。

## 9. 代码组织设计

目标文件组织如下；确切拆分顺序由后续实施计划确定：

```text
src/Vela.Tui/
├─ Application/
│  ├─ Profiles/
│  │  ├─ ProfileManagementController.cs
│  │  ├─ ProfileEditorState.cs
│  │  └─ ProfileViewModels.cs
│  ├─ TargetWorkflow/
│  │  ├─ TargetWorkflowController.cs
│  │  ├─ LockedCompactionTarget.cs
│  │  ├─ PreflightGateProjection.cs
│  │  └─ PreflightTargetProjection.cs
│  ├─ Compaction/
│  │  └─ CompactionExecutionController.cs
│  └─ Logs/
│     └─ LogArchiveController.cs
├─ Views/
│  ├─ VelaTerminalShell.cs
│  ├─ ProfileManagementView.cs
│  ├─ ProfileEditorView.cs
│  └─ existing focused page views
└─ Rendering/
   └─ StaticFrameRenderer.cs

src/Vela.Windows/
└─ DiskPart/
   ├─ DiskPartClient.cs
   ├─ DiskPartScriptBuilder.cs
   └─ PrivilegedDiskPartWorkspace.cs
```

拆分遵循以下限制：

- 公共函数控制在 50 行以内；复杂私有函数也按单一职责拆分。
- 新文件目标为 200–400 行，所有生产文件低于 800 行。
- View 不依赖 Windows adapter 具体类型。
- Windows 具体实现只在 composition root 或 Windows 项目内部出现。
- 核心状态优先使用 sealed immutable record。
- Terminal.Gui 控件状态通过既有 View API 更新。

`CompactionWorkflow` 中与 `PreflightWorkflow` 重复的 evidence collection 在安全与 UI 收敛稳定后提取为 Core 内部共享组件。该重构不得改变 journal 顺序、terminal result 或 adapter 调用顺序。

## 10. 迁移阶段

### Phase 0：基线锁定

- 保存当前 432 项测试和三程序集覆盖率基线。
- 为当前生产入口、键盘契约和目标锁定补 architecture characterization tests。
- 明确 Terminal.Gui 是唯一 interactive shell。

### Phase 1：特权边界修复

- 引入 `PrivilegedDiskPartWorkspace`。
- 将 administrator probe 移到 journal/request mutation 之前。
- 保持 terminal result、worker exit code 和 journal schema 不变。

完成条件：同用户非提权进程对 pending request 没有 claim 权限，对高权限 DiskPart 脚本没有替换权限。

### Phase 2：Terminal.Gui 档案管理

- 先写 Terminal.Gui N/E/D/Enter 失败测试。
- 实现档案列表和编辑视图。
- 复用现有 profile application service 和验证规则。
- 覆盖 target-changing exact `YES` 与 write-only VHDX 输入。

完成条件：README 记录的档案操作全部可从 Release 生产路径完成。

### Phase 3：展示安全投影

- 分离 trusted operation state 和 display-safe state。
- 删除 renderer-facing record 的 raw path/RunId/native output 字段。
- 更新实例列表、目标详情、影响、确认、运行和结果页面。

完成条件：任何普通 TUI frame 都不包含完整路径、RunId、native output 或异常堆栈；受信任日志仍保存原始证据。

### Phase 4：Running 影响语义

- 把目标 running 从 blocker 改为 impact evidence。
- 在影响和确认页面显示 Global/Distro 停止范围。
- 保持两次 Y 和 worker fresh inventory 校验。

完成条件：满足其他门禁的 running 目标可以进入确认；动作 adapter 在第二次 Y 和 worker 校验前调用数始终为零。

### Phase 5：删除旧交互路径并拆分文件

- 将仍有价值的旧 `TuiApplication` 行为测试迁移到 Terminal.Gui controller/view tests。
- 删除不再被生产组合根引用的 input、page controller 和 interactive frame sink。
- 将 `FrameRenderer` 缩小并更名为静态渲染职责。
- 拆分 `VelaTerminalShell`、`PreflightOverviewViewModel`、`TuiServices` 和 `CompactionWorkflow`。

完成条件：生产项目只有一个交互式输入所有者；没有仅由测试维持的第二套交互状态机；生产文件满足规模约束。

### Phase 6：质量与文档门禁

- coverage 收集加入 `[Vela.Tui]*`，继续排除纯 composition root `Program.cs`。
- `Verify-Coverage.ps1` 分别要求 Core、Windows、TUI line coverage 不低于 80%。
- 清理不需要公开的类型，将其改为 internal。
- 为保留的公共类型和成员补完整 XML 文档。
- 公共 API 基线清理完成后启用文档生成和 CS1591 构建约束。
- 更新 `docs/architecture.md`、`docs/testing-and-release.md`、README 键位和安全说明。

完成条件：CI 同时约束三程序集；架构文档描述真实生产路径。

## 11. 测试策略

所有阶段遵循 RED → GREEN → IMPROVE，自动化测试使用 fake adapter 和项目内测试数据。

### 11.1 安全测试

- 预置 reparse/junction 的特权工作区触发安全失败。
- 当前用户拥有的工作区、错误 DACL、缺少 High integrity label 或错误 mandatory policy 均触发安全失败。
- 固定目录并发预创建时只接受安全描述符完全匹配的对象。
- DiskPart 脚本使用原子 `CreateNew` 语义、ASCII 和固定三行命令顺序。
- writer 关闭后建立只读 pin handle，DiskPart 模拟 reader 可读取，其他句柄的写入、删除、替换和 rename 请求均失败。
- Windows-only integration test 使用 restricted medium-integrity token 或 `AccessCheck` 验证其没有写入、删除、改名、`WRITE_DAC` 和 `WRITE_OWNER` 权限。
- owner、DACL、integrity label 和 file identity 在 DiskPart 启动前后均保持预期值。
- detail 失败时 compact 调用数为零。
- 非管理员 worker 不调用 journal open、claim、consume、Lxss 或 executor。
- 映射漂移时 WSL 和 DiskPart 调用数为零。

### 11.2 档案测试

- N/E/D 大小写键均可触发对应操作。
- Enter 切换当前档案并清理旧锁定状态。
- 编辑目标字段要求精确 `YES`；`yes`、`YES ` 和超长输入均不接受。
- VHDX 旧路径不进入编辑 view model 或 frame。
- timeout 边界覆盖 4、5、300、301 秒。
- 删除最后一个或当前档案返回安全错误状态。
- 配置保存失败时，内存 current profile 保持原值。

### 11.3 工作流与 TUI 测试

- Enter、Esc、R/r、左右方向键和两次 Y 保持原契约。
- Running 页面继续锁定导航输入。
- running target 可进入影响评估，但预检不调用动作 adapter。
- Global 和 Distro 显示不同停止范围。
- 锁定目标贯穿 preview、request 和 worker。
- 新 generation 的结果覆盖旧 generation；收到旧 generation 结果时直接丢弃。
- `160×45`、`120×35`、`100×30`、`80×24`、`60×16` 全部覆盖。
- frame 中不存在 raw path、RunId、native output、exception 或 ANSI/OSC 注入。

### 11.4 完整验证

```powershell
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Release --no-restore
dotnet test .\Vela.sln -c Release --no-build --no-restore
pwsh -NoProfile -File .\scripts\Verify-Coverage.ps1
```

TUI 变更还需要执行只读 tmux/Windows Terminal 验收；DiskPart 自动化测试只使用 fake runner，不触发真实压缩。

## 12. 错误处理与终态

- 用户输入错误显示中文安全消息，不包含路径或内部异常。
- service/controller 捕获非取消异常并记录可信上下文；View 只获得错误代码映射后的消息。
- `OperationCanceledException` 在 cancellation token 已取消时继续传播到生命周期所有者。
- DiskPart workspace 安全检查失败映射为 `DiskPartPreflightFailed`。
- UAC 取消继续映射为 `CancelledBeforeElevation`。
- journal terminal event 仍是权威生命周期标记，summary 是历史投影。
- 清理失败不影响已完成的压缩终态。

## 13. 兼容性与回滚

### 13.1 持久化兼容

本次整改不修改：

- `config.json` schema。
- pending operation request JSON。
- `events.ndjson`、`run.log` 和 `summary.json` schema。
- terminal result 和 worker exit code。

旧配置和历史日志无需迁移。

### 13.2 阶段回滚

每个阶段保持独立提交和完整测试：

1. `fix: harden elevated diskpart workspace`
2. `fix: validate worker elevation before request claim`
3. `feat: add terminal gui profile management`
4. `fix: keep raw paths out of tui projections`
5. `fix: treat running target as compaction impact`
6. `refactor: converge terminal gui interaction path`
7. `ci: enforce tui coverage and api documentation`

任一阶段出现回归时，可以回退该阶段而不修改持久化数据。旧交互路径只在 Terminal.Gui 达到行为等价且相关测试迁移完成后删除。

## 14. 审查检查点

### 安全检查点

- DiskPart 脚本目录是否真正阻止 medium-integrity 同用户写入。
- 脚本句柄在进程执行期间是否拒绝写入和删除。
- 非管理员 worker 是否保持 pending request 和 journal 不变。
- Lxss 二次解析与路径严格匹配是否仍在所有动作之前。

### 架构检查点

- Terminal.Gui 是否是唯一 interactive shell。
- View 是否只消费安全投影。
- controller 是否不依赖 Terminal.Gui 控件内部状态。
- Windows adapter 是否未进入 Core。
- 预检是否继续没有 shutdown、terminate 和 DiskPart 端口。

### 产品检查点

- N/E/D/Enter 档案能力是否与 README 一致。
- 两次 Y 是否仍是压缩唯一入口。
- running target 是否显示准确停止范围。
- 日志是否继续在 Vela TUI 内查看。
- 五个参考终端尺寸是否保持可用。

## 15. 完成定义

全部条件满足时，本设计完成：

1. Release 只存在一套 Terminal.Gui 交互状态机。
2. 档案管理可在该状态机中完成全部公开操作。
3. DiskPart 特权脚本满足固定根、Administrators owner、受保护 DACL、High integrity label、reparse、原子 CreateNew、file identity 和只读 pin handle 生命周期约束。
4. 非管理员 worker 不修改 operation request 或 journal。
5. 普通 TUI 不携带或显示原始路径及其他受信任证据。
6. running target 进入影响评估，而不是被要求手动停止。
7. 目标锁定、只读预检、两次确认、worker 二次校验和日志归档不变量全部保留。
8. Core、Windows 和 TUI line coverage 分别达到 80% 以上。
9. Release 构建零警告、全量测试通过，关键 reviewer 问题已处理。
10. 架构、测试和 README 文档与真实生产路径一致。
