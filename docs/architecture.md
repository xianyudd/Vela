# Vela 架构设计

## 1. 产品目标与范围

Vela 是一个单入口、键盘优先的 Win11 TUI 工具。它将现有 wsl.ps1 的诊断、日志、WSL 盘点和 VHDX 压缩流程迁移为 C# 原生实现，并把压缩目标的选择收紧为“发行版注册表映射与 worker 二次校验一致”。

首版功能契约：

1. 展示 WSL 发行版、注册表映射、VHDX 文件大小、稀疏标志与宿主盘可用空间。
2. 支持 Global 与 Distro 两种运行范围。
3. 提供只读预检，生成完整诊断证据。
4. 在目标、影响范围和二次确认明确后启动提升权限 worker。
5. worker 以 Distro 为键重新解析 VHDX，严格核对请求路径后才进入 WSL 停止、DiskPart detail vdisk 与 compact vdisk 阶段。
6. 为每次运行保存实时事件流、易读日志和 JSON 摘要。
7. 管理多个配置档案；初始档案为：

~~~text
Distro: Ubuntu-24.04
VHDX:   D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx
Mode:   Global
Timeout: 45 seconds
~~~

首版范围没有计划任务、云同步、远程主机管理或自动定期执行。

## 2. 最终 TUI 效果

TUI 采用克制、工业化的中文运维控制台。交互界面由 Terminal.Gui 承载：`Program.cs` 构造 `VelaTerminalShell` 与 `VelaTerminalHost`，shell 持有唯一一个导航列表，事件驱动重绘。工作区、影响评估、确认、运行进度与日志页共享这一个 shell，不启动嵌套读键循环。

Spectre.Console 与 `FrameRenderer` 只服务两条非交互路径：首启确认帧，以及 `Console.IsInputRedirected` 时的单帧确定性输出（不清屏、不读键）。`TuiApplication` 与 `TuiReducer` 是纯模型层，有完整测试覆盖，但不在生产启动路径上。

~~~text
╭─ Vela ───────────────────────────────────────────────────────────╮
│ VELA  ·  ✓ Ubuntu-24.04 预检通过  ·  ✔ ① 选择实例 ─ > ② 环境预检   │
│──────────────────────────────────────────────────────────────────│
│ 工作区              │ 实例列表（3）                                │
│ 检查 / 执行 / 追溯   │  发行版          当前体积   状态             │
│                    │ ▸ Ubuntu-24.04    12.4 GiB  ● 准备就绪       │
│ > 01  工作区        │   docker-desktop   0.1 GiB  ● 准备就绪       │
│   02  日志归档      │   Debian           3.2 GiB  ▲ 运行中         │
╰──────────────────────────────────────────────────────────────────╯
~~~

- 导航列表只有两项：`01 工作区`（实例选择、预检、影响评估、执行）和 `02 日志归档`。`MainMenu` 仍定义 6 个 `MainMenuAction`，但 `VelaTerminalShell` 把可见项过滤为 `Preflight` 与 `OpenLogs`；`ManageProfiles`、`RecentRuns` 与 `ExecuteCompaction` 当前在生产界面没有入口。
- ↑、↓ 在导航与实例列表内移动；←、→ 切换工作流步骤；Enter 锁定目标或进入下一步；Esc 返回上一层；`R` / `r` 重新运行只读预检。
- 首启确认逐字符读取，只有精确大写 `YES` 加 Enter 才接受，输入最多 16 个字符。压缩流程使用两次 `Y`（`AcceptsSingleKey`，忽略大小写），不要求输入 YES。
- 布局按 `VelaLayoutMetrics` 决策：宽 ≥ 80 且高 ≥ 20 为双栏，否则单栏；宽 ≥ 120 显示证据栏，≥ 140 显示日志分析栏。参考尺寸 `160×45`、`120×35`、`100×30`、`80×24`、`60×16` 均有测试覆盖。
- 预检结果只呈现 mapped/configured/resolved 状态、文件与宿主盘数值证据、运行中的发行版和受控提示。
- 影响评估显示当前物理体积、预计压缩后体积和预计可回收空间；第一次 `Y` 进入确认页，第二次 `Y` 执行。
- 普通权限 TUI 保持打开并轮询确定的运行目录；提升权限 worker 只追加该目录的事件流。
- 结束时显示安全结果投影；`Succeeded` 与 `CompletedWithNoReclaim` 分别显示为“成功”和“完成但未回收空间”。

renderer-facing state 不包含 raw VHDX/registry/run/log path、RunId、raw exception、native command output 或 raw enum name。内部 service/controller 可保留可信路径和 RunId 用于 I/O；`TuiDisplayText` 与 `DisplayTextSanitizer` 负责中文标签、ANSI CSI/OSC/control stripping、Spectre markup escaping 与 Unicode display-cell 截断。详细原始证据只在 journal/log 中追溯。

## 3. 解决方案结构与依赖方向

~~~text
Vela.sln
├─ src/
│  ├─ Vela.Core/                    # 纯业务模型、验证、工作流（net10.0）
│  │  ├─ Models/
│  │  ├─ Contracts/
│  │  ├─ Validation/
│  │  └─ Workflows/
│  ├─ Vela.Application/             # 平台无关的展示投影与状态编排
│  │  ├─ Display/                     # display-safe projection 与清洗
│  │  ├─ Profiles/                    # 档案服务与 IProfileStore 契约
│  │  ├─ Startup/                     # 首启初始化结果
│  │  └─ Tui/                         # 不可变 TUI 模型、reducer、effect
│  ├─ Vela.Windows/                 # Win11 原生适配器
│  │  ├─ Processes/
│  │  ├─ Wsl/
│  │  ├─ DiskPart/
│  │  ├─ Registry/
│  │  ├─ Storage/
│  │  ├─ Configuration/
│  │  ├─ Elevation/
│  │  ├─ Security/                    # SDDL、token 特权、对象安全校验
│  │  └─ Diagnostics/                 # AppPaths、日志 journal、保留策略
│  └─ Vela.Tui/                     # 可执行项目（Terminal.Gui 生产路径）
│     ├─ Views/                       # VelaTerminalShell 与各页面视图
│     ├─ Application/                 # view model、服务与协调器
│     ├─ Menu/                        # 菜单与确认 view model 工厂
│     ├─ Rendering/                   # 主题与 redirected FrameRenderer
│     ├─ ProgramModes/                # worker 模式与 journal 轮询
│     ├─ Properties/                  # launchSettings 与 publish profile
│     └─ Program.cs
└─ tests/
   └─ Vela.Tests/                   # Core、Application、Windows、TUI 测试
~~~

~~~text
Vela.Tui ─────────► Vela.Core
    │                   ▲
    ├─────────────► Vela.Application ──┘
    │                   ▲
    └─────────────► Vela.Windows ──────┘
~~~

即 `Vela.Application → Vela.Core`；`Vela.Windows → Vela.Core, Vela.Application`；`Vela.Tui → Vela.Core, Vela.Application, Vela.Windows`。`Vela.Application` 不引用 Terminal.Gui、Spectre.Console、注册表或进程 API，该约束由 `ApplicationAssemblyDependencyTests` 强制。

TUI 内部依赖方向为：可信 service/controller 持有路径和 RunId capability，display-safe projection 将其转换为 typed page state，`FrameRenderer` 只消费该投影。原始 native output 直接保留在 journal/log，不进入 `RunProgressViewModel`。

Vela.Core 不引用 Spectre.Console、注册表、WindowsIdentity、文件系统或进程 API；fake adapter 可完整覆盖工作流。

## 4. 核心模型与契约

### 4.1 不可变模型

~~~csharp
public sealed record Profile(
    Guid Id,
    string DisplayName,
    string DistroName,
    string VhdxPath,
    ShutdownMode ShutdownMode,
    TimeSpan ShutdownTimeout);

public sealed record OperationRequest(
    Guid RunId,
    Profile Profile,
    OperationIntent Intent);

public sealed record VhdxSnapshot(
    DateTimeOffset CapturedAtUtc,
    string Path,
    long FileLengthBytes,
    DateTimeOffset LastWriteUtc,
    bool? IsSparse,
    DriveSnapshot Drive);
~~~

关键枚举：

~~~csharp
public enum OperationIntent { Preflight, Compact }
public enum ShutdownMode { Global, Distro }
public enum RunPhase { Validation, Inventory, Snapshot, AwaitingConfirmation,
    Elevation, Shutdown, DiskPartPreflight, Compacting, Completed, Failed }
~~~

首版没有 AllowVhdxMismatch。映射不一致可作为只读预检结果展示；Compact 工作流只接受注册表解析路径与请求路径规范化后严格相等的档案。

选择 `ShutdownMode.Distro` 时注意它的成功窗口很窄：它只在工具 VM 已释放目标 vhdx 时才可能压缩成功（见 5.3 的挂载模型），即目标发行版是当前 VM 生命周期内从未启动过的那一个。

### 4.2 应用层接口

`Vela.Core/Contracts` 定义全部平台无关端口：

~~~csharp
public interface IClock;
public interface ICompactionImpactEstimator;
public interface IDiskPartClient;
public interface IElevatedWorkerLauncher;
public interface ILxssProfileResolver;
public interface IOperationRequestStore;
public interface IProcessRunner;
public interface IRunJournal;
public interface IVhdxHandleProbe;
public interface IVhdxInspector;
public interface IWslClient;          // : IWslInventoryReader
public interface IWslInventoryReader;
~~~

`IProfileStore` 是例外：它描述档案持久化，属于应用层关注点，因此定义在 `Vela.Application/Profiles/IProfileStore.cs`，而不在 `Vela.Core/Contracts`。

`CompactionWorkflow` 只依赖其中 7 个端口：

~~~csharp
CompactionWorkflow(
    IWslClient,
    ILxssProfileResolver,
    IVhdxInspector,
    IDiskPartClient,
    IVhdxHandleProbe,
    IRunJournal,
    IClock,
    TimeSpan? pollInterval = null)
~~~

`IProcessRunner` 由 `Vela.Windows` 的适配器内部使用；`IElevatedWorkerLauncher` 与 `IOperationRequestStore` 由 TUI 的提权编排调用；`IProfileStore` 属于档案服务。它们都不是工作流的构造依赖。Preflight 在采集摘要后完成；Compact 仅在 worker 的二次验证完成后进入执行阶段。

## 5. 运行数据流

### 5.1 只读预检

~~~text
TUI 选择档案
  → ProfileValidator 验证字符串、路径形态与 timeout
  → ILxssProfileResolver 读取 HKCU\...\Lxss
  → IWslClient 列出已安装、运行中发行版和版本
  → IVhdxInspector 获取 FileInfo、DriveInfo、sparse 标志
  → IRunJournal 写 events.ndjson / run.log / summary.json
  → display-safe projection 转换为状态、标签与数值证据
  → TUI 呈现安全投影；原始路径、RunId 与 native output 留在日志
~~~

### 5.2 压缩流程

~~~text
TUI 展示共同预检与影响范围
  → 用户按 Y 进入二次确认，再按 Y 启动（单键，OrdinalIgnoreCase）
  → 父进程创建 logs\<RunId> 并写入 RunCreated 事件
  → IOperationRequestStore 原子写入 pending\<RunId>.json
  → IElevatedWorkerLauncher 以 runas 启动同一 Vela.exe
  → worker 只接受 --worker --run-id <D 格式 GUID>
  → worker 确认管理员身份、读取同一 RunId 的请求
  → worker 从 HKCU Lxss 按 Distro 重新解析 VHDX
  → 规范化后严格比较已解析路径与请求路径
  → worker 重跑完整预检
  → Global：%SystemRoot%\System32\wsl.exe --shutdown，轮询直到 running 清单为空
  → Distro：%SystemRoot%\System32\wsl.exe --terminate <Distro>，轮询直到目标发行版离开 running 清单
  → IVhdxHandleProbe: 独占打开目标 VHDX 做只读探测；被占用（Held）则终止于压缩条件检查并给出可执行诊断
  → IDiskPartClient: detail vdisk
  → IDiskPartClient: compact vdisk
  → 重新采集 VhdxSnapshot 与 DriveSnapshot
  → worker 追加 Summary、Completed / Failed 事件和退出码
  → 父 TUI 读取同一 events.ndjson 并呈现最终报告
~~~

测试、开发调试和文档验证采用 fake IProcessRunner、fake IWslClient、fake IDiskPartClient。压缩阶段是最终人工验收项目，由用户在影响面板确认后自行发起。

### 5.3 WSL2 磁盘挂载模型与 Distro 范围的真实限制

这是压缩能否成功的决定性前提，必须在设计与排查时优先考虑。

WSL2 的所有发行版共享**同一个工具 VM**。一个发行版的 ext4.vhdx 在该发行版**启动时**挂载到这个共享 VM，并一直保持挂载状态直到**该 VM 本身被销毁**：

| 操作 | 停止 init 与卸载文件系统 | 从工具 VM 卸载磁盘（释放文件句柄） |
| --- | --- | --- |
| wsl.exe --terminate \<Distro\> | 是 | **否** |
| wsl.exe --shutdown | 是（全部） | 是 |
| 所有发行版空闲超过 vmIdleTimeout（默认 60000 ms） | 是 | 是 |

由此得到两个必须写进文档的结论：

1. `--terminate` **不释放 vhdx 文件句柄**。运行中清单已经不含目标发行版（通常在 terminate 后约 150 ms 就满足），但文件仍被共享 VM 独占持有。因此「运行中清单已达目标」只是压缩的**必要而非充分**条件。
2. **Distro 范围因此无法压缩任何在当前工具 VM 生命周期内启动过的发行版。** 它只在目标发行版自上次 `wsl --shutdown` 以来从未启动过时才会成功——而在那种情况下 `--terminate` 本身就是空操作，Distro 范围并未带来任何额外作用。这解释了历史上偶发的 Distro 成功记录。

微软官方文档对所有 VHD 级操作（`--manage --resize`、手工 diskpart 扩容、`--mount` 与 e2fsck 修复）一律要求先执行 `wsl.exe --shutdown`；`--terminate` 从未被列为任何 VHD 操作的前置条件。

为了让这一限制**可见而非表现为神秘的 diskpart 报错**，worker 在 diskpart 之前插入一次只读的独占打开探测（见 5.2 流程中的 `IVhdxHandleProbe`）：

- 探测以 `FileShare.None` 打开目标 vhdx 后立即释放，不做任何写入。独占打开成功等价于 diskpart 能拿到它需要的独占句柄。
- 仅当 Win32 错误码为 `ERROR_SHARING_VIOLATION`(32) 或 `ERROR_LOCK_VIOLATION`(33) 时判定为 Held。文件不存在、ACL 拒绝等一律为 Unknown。
- **Held 之外的任何结论都放行**（fail-open）。Unknown 表示「探测不出结论」，不是证据；该闸门只能把原本晦涩的共享冲突转成明确诊断，绝不能让原本能成功的运行失败。

Held 时结果为 `DiskPartPreflightFailed`，并附带 `TargetVhdxInUse` 诊断（TUI 显示为「目标 VHDX 仍被占用」），诊断正文按范围给出不同的可执行建议：Distro 范围提示改用 Global 范围或先执行 `wsl --shutdown`；Global 范围提示占用者在 WSL 之外（杀毒、备份代理、资源管理器预览等）。

两者都会提及 WSL 原生的替代路径 `wsl --manage <Distro> --set-sparse true --allow-unsafe`。Vela **不自动执行**它：实测该开关默认被禁用，必须显式附加 `--allow-unsafe`，且微软为其标注了数据损坏风险，因此它只作为建议出现在诊断与文档中，由用户自行决定。相对地，`.wslconfig` 的 `[experimental] sparseVhd=true` 只对**新创建**的 VHD 生效，属于预防性手段而非对既有 vhdx 的补救。

## 6. Windows 原生适配层

### 6.1 进程执行

WindowsProcessRunner 通过一个 NativeToolPaths 组件解析固定绝对路径：

~~~text
%SystemRoot%\System32\wsl.exe
%SystemRoot%\System32\diskpart.exe
%SystemRoot%\System32\fsutil.exe
~~~

每个调用使用 ProcessStartInfo.ArgumentList 逐项传参，不经 cmd.exe 拼接命令行。普通进程调用设置 UseShellExecute=false，并异步捕获 stdout、stderr、退出码、耗时和开始/结束 UTC 时间。每条输出转换为 RunEvent。

UAC launcher 是独立例外：它启动当前 Vela.exe，设置 UseShellExecute=true、Verb="runas"，并将 --worker、--run-id、D 格式 GUID 作为三个固定参数边界传入。它从不接收路径或命令文本参数。

### 6.2 注册表、路径与目标约束

LxssProfileResolver 从以下位置读取 DistributionName 和 BasePath：

~~~text
HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss
~~~

预期 VHDX 是规范化后的 BasePath\ext4.vhdx。ProfileValidator 与 worker 共同保证：

- VHDX 是绝对路径、存在且扩展名为 .vhdx；
- 路径没有 NUL、CR、LF 等控制字符，并满足 DiskPart 的 ASCII 脚本编码约束；
- timeout 位于 5–300 秒；
- 压缩目标必须由 `DistroName` 与锁定实例一致的档案执行：锁定已建档案的实例时 TUI 自动切换到该档案；锁定无匹配档案的实例时阻止执行并提示先创建档案。当前档案的停止范围不再借给其他发行版；
- Compact worker 将注册表解析路径和请求路径做严格规范化比较；
- request 文件、运行目录和日志路径都由验证后的 RunId 在 AppPaths 根目录内派生；
- DiskPart 的目标路径只来自通过严格比较的已解析 VHDX。

### 6.3 DiskPart adapter

DiskPartClient 为每次调用创建唯一临时脚本，使用 ASCII 编码：

~~~text
select vdisk file="<absolute VHDX path>"
detail vdisk
exit
~~~

以及：

~~~text
select vdisk file="<absolute VHDX path>"
compact vdisk
exit
~~~

脚本创建、调用、输出解析和 finally 清理由同一对象负责。detail vdisk 输出完整进入运行日志；detail 阶段失败时 Compact 流程在此结束。

## 7. UAC、请求传输与跨进程进度

主 TUI 使用普通权限运行，预检、日志查看和档案编辑保持轻量。执行阶段使用下列确定协议：

1. 父进程生成 RunId，并创建 %LocalAppData%\Vela\logs\<RunId>；
2. 父进程写入第一个 RunCreated 事件和初始摘要；
3. 父进程原子写入 %LocalAppData%\Vela\pending\<RunId>.json；
4. launcher 使用 runas 启动 Vela.exe --worker --run-id <RunId>；
5. worker 解析的参数必须恰为该形式，RunId 为 D 格式 GUID；
6. worker 验证管理员身份、请求中的 RunId、Intent=Compact、请求文件派生路径和同一运行目录；
7. worker 重新解析 Lxss 映射并运行共同预检；只有路径严格相等时才调用 WSL 与 DiskPart adapter；
8. worker 只追加现有 events.ndjson、run.log、summary.json；父 TUI 只读轮询该目录；
9. worker 分支跳过主菜单、ReadLine 和确认提示；它只写 journal，并返回与最终 summary 对应的退出码；
10. worker 写入最终事件和摘要后消费 pending 请求；UAC 拒绝和启动异常由父进程写成确定的终态。

Compact 启动前，`CompactRunGate` 在同一受信任数据根内以 `compact.lock` 原子占位，防止同时存在多个 Compact worker；已存在且可验证的 RunId 返回 `AlreadyRunning`。无法验证的 marker 会保留并返回 `Invalid`，不会删除后重试；pending 文件只有在 JSON、RunId、Compact intent、Profile 和路径契约均通过验证后才视为活动请求。父 TUI 的 `RunJournalPoller` 以 sequence 游标按 journal 原始顺序增量读取，拒绝 foreign RunId、gap、duplicate、nonmonotonic sequence 和无效 canonical terminal event，不会通过排序修复损坏输入。默认轮询间隔为 100 ms、连续读取失败 3 次进入 ReadFailed、等待终态默认超时 5 分钟；成功读取会复位连续失败计数。事件 callback 串行、exactly once，并先于 terminal return；callback 异常变为安全的 `ReadFailed`。取消和超时保留已消费 cursor/事件，只返回父界面状态，不向 journal 伪造 worker 终态。`FileRunJournal` 只解析完整换行记录，并对读取 IOException 做有限重试。

这个协议使运行目录、请求、日志和实际执行的目标由同一个 RunId 关联。父界面的旧快照只用于显示，实际动作依据 worker 二次验证的结果。

## 8. 配置、请求与日志

### 8.1 配置

~~~text
%LocalAppData%\Vela\config.json
~~~

AppPaths 接受可注入根目录。发布版默认根为 %LocalAppData%\Vela；首次启动在创建 config.json、pending 或 logs 前只显示受控的初始化摘要，不在 TUI 中显示原始文件系统路径，并要求用户确认。开发、单元测试和集成测试注入项目内 artifacts\test-data\ 或 fixture 目录，所有开发期生成文件保持在项目根。

建议 schema：

~~~json
{
  "schemaVersion": 1,
  "lastProfileId": "GUID",
  "logRetentionDays": 90,
  "profiles": [
    {
      "id": "GUID",
      "displayName": "Ubuntu 24.04 on D",
      "distroName": "Ubuntu-24.04",
      "vhdxPath": "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx",
      "shutdownMode": "Global",
      "shutdownTimeoutSeconds": 45
    }
  ]
}
~~~

保存通过 config.json.tmp 写入、flush、原子替换完成。

### 8.2 运行日志

~~~text
%LocalAppData%\Vela\
├─ pending\<RunId>.json
└─ logs\<RunId>\
   ├─ events.ndjson
   ├─ run.log
   └─ summary.json
~~~

events.ndjson 面向实时消费，run.log 面向阅读，summary.json 面向历史对比和自动检查。每个事件至少包含 sequence、UTC 时间、RunId、阶段、级别、操作名称、参数数组、退出码、耗时和文本输出。事件 writer 以 FileMode.Append、FileAccess.Write、FileShare.Read 打开文件，把完整 UTF-8 JSON、换行符作为一个逻辑记录写入后 flush。父 TUI 只解析完整换行记录，对短暂共享冲突重试；半行保留到下一次轮询。日志保留清理跳过活动 RunId 对应的目录。

worker 退出码固定为：0 = Succeeded 或 CompletedWithNoReclaim，2 = ValidationFailed，3 = ShutdownTimedOut，4 = DiskPartPreflightFailed，5 = DiskPartCompactFailed，10 = 未处理 worker 异常。UAC 取消由父进程记录为 CancelledBeforeElevation。

## 9. 可靠性边界

| 场景 | 行为 |
| --- | --- |
| VHDX 不存在或路径格式异常 | 预检生成错误摘要。 |
| 发行版名称不存在 | 显示发现的发行版清单并保留日志。 |
| 注册表映射与请求路径不一致 | 预检记录差异；Compact 终止于验证阶段。 |
| worker 参数、RunId 或请求路径异常 | worker 写入 ValidationFailed，动作适配器调用数保持为零。 |
| UAC 被用户取消 | 父进程写入 CancelledBeforeElevation 和启动诊断。 |
| worker 未处于管理员身份 | worker 写入失败事件，流程停留在 Elevation。 |
| WSL 停止超时 | 记录仍在运行的发行版，结果为 ShutdownTimedOut。 |
| Global 范围 | 调用 %SystemRoot%\System32\wsl.exe --shutdown，轮询到 running 清单为空。 |
| Distro 范围 | 调用 %SystemRoot%\System32\wsl.exe --terminate <Distro>，轮询到目标发行版离开 running 清单。 |
| 目标 VHDX 仍被占用（句柄探测为 Held） | 跳过 diskpart，结果为 DiskPartPreflightFailed，诊断 TargetVhdxInUse 说明占用原因与替代方案。 |
| DiskPart 预检异常 | 跳过 compact，保留 detail 输出。 |
| compact 完成但长度未变 | 标记 CompletedWithNoReclaim，展示 0 B。 |
| 日志目录不可写 | 执行阶段以日志可写为前置条件，写入可用诊断。 |
| worker 意外结束 | 父界面读取最后事件与退出码，标记 WorkerInterrupted。 |

## 10. 首版完成定义

首版完成时，用户可从 D:\DevTools\Vela\Vela.exe 或 Windows Terminal 启动 Vela，在一个 TUI 中完成档案选择、预检、日志查看与确认后的压缩流程。每次运行留下可审阅的结构化证据；legacy\powershell 保留桌面旧工具、README 和验证脚本的行为对照副本。
