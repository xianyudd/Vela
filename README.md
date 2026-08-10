# Vela

Vela 是面向 Windows 11 的键盘优先终端工具，用于盘点 WSL 发行版与 VHDX 状态、执行只读预检，并在明确影响范围和确认后协调 VHDX 压缩流程。

## 文档阅读顺序

实施、排障和验收前按以下顺序阅读：

1. [docs/agent-handoff.md](docs/agent-handoff.md)
2. [docs/development-environment.md](docs/development-environment.md)
3. [docs/architecture.md](docs/architecture.md)
4. [docs/implementation-plan.md](docs/implementation-plan.md)
5. [docs/testing-and-release.md](docs/testing-and-release.md)

## 开发工作目录

在 **Developer PowerShell for VS 2022** 中进入项目根：

~~~powershell
Set-Location "D:\Jason\Documents\Workspace\vs2022\repo\Vela"
~~~

首版使用 .NET SDK 9.0.305。框架划分为 **Vela.Core**（net9.0）以及 **Vela.Windows**、**Vela.Tui**、**Vela.Tests**（均为 net9.0-windows）。

## 日常发布入口

Task 13 经用户确认后会将单文件发布物安装为：

~~~text
D:\DevTools\Vela\Vela.exe
~~~

发布配置位于 src\Vela.Tui\Properties\PublishProfiles\win-x64-singlefile.pubxml，固定为 win-x64、自包含、单文件且不裁剪。纯命令发布与验证只写入项目内 artifacts\publish\win-x64\；确认交付目录前不会写入 D:\DevTools\Vela\。

开发期不向该项目外目录写入；发布前先完成项目内 artifacts\publish\win-x64\Vela.exe 的验证。

## TUI 交互与运行日志

`TuiApplication` 是唯一输入所有者：每次串行读取一个键，将其交给当前 typed page controller，再执行显式异步 effect；Profile、Recent、Confirmation 等次级页面不拥有独立读键循环，也不会启动后台 `ReadKey`。只有 frame 状态变化时才重绘。键位固定为：

- ↑ / ↓：移动当前菜单或次级列表选择；
- Enter：执行菜单项、切换 Profile 或打开最近运行详情；
- Esc：返回次级页面/取消确认，在主菜单退出；
- 首启、执行压缩和涉及执行目标的 Profile 编辑/删除确认：逐字符输入，必须精确输入大写 `YES` 后按 Enter；
- Profile 管理：`N` 新建、`E` 编辑、`D` 删除；最近运行详情：`O` 打开当前可信日志目录。

`FrameRenderer` 为交互输出与重定向输出复用同一 composition：宽度 `<80` 时只保留目标、状态、当前焦点和上下文帮助，`80–119` 时纵向堆叠导航与证据，`>=120` 时使用左右工作区；低于 22 行时限制列表行数。重定向模式只输出一个确定性 frame，不清屏、不读取输入。

renderer-facing state 只包含本地化标签、configured/resolved/mapped 状态、数值证据和受控错误。TUI 不接收或显示原始 VHDX/注册表/运行目录/日志路径、RunId、原始异常、native command output 或 raw enum name。Profile 的 VHDX 字段采用 write-only 编辑：旧路径永不回显，新输入仅显示字符数。`Succeeded` 显示为“成功”，`CompletedWithNoReclaim` 显示为“完成但未回收空间”。

首次启动在创建数据根前只展示受控初始化摘要，不泄露原始文件系统路径，只有精确 `YES` 才继续。Profile 管理支持选择、新建、编辑、删除（至少保留一个，当前 Profile 不能直接删除）和持久化当前选择。最近运行内部最多读取 20 个可信 RunId 目录，列表和详情只显示安全投影；损坏或缺失 `summary.json` 的记录显示为“损坏”。详情页可显示结果、时间、耗时、回收字节和日志是否可用，`O` 通过内部可信 RunId capability 打开对应日志目录，但 frame 不携带该 RunId 或路径。主菜单 `OpenLogs` 只打开受信任的数据根日志目录。

发布版的每次运行使用：

~~~text
%LocalAppData%\Vela\logs\<RunId>\
~~~

目录包含 events.ndjson、run.log 和 summary.json。开发与测试注入项目内的 `artifacts\test-data\`，避免项目外写入。

父 TUI 轮询 worker journal，使用 sequence 游标；轮询支持取消、默认五分钟 timeout，以及连续读取失败达到阈值后的 `ReadFailed`。取消或 timeout 只改变父界面状态，不伪造 worker 终态。Compact 启动前使用项目数据根下的 `compact.lock` 做 single-worker gate；检测到可信活动 RunId 时返回 `AlreadyRunning`，UAC 取消、启动失败和创建失败路径写入确定终态。

## 预检与执行

**预检**仅采集发行版、Lxss 映射、VHDX 和宿主盘快照；它使用只读适配器，不触发 WSL 停止、发行版终止或 DiskPart compact。

**执行压缩**先展示档案身份、VHDX 已配置状态、Global 或 Distro 影响范围及运行中发行版，不在 frame 中显示原始目标路径。输入精确大写 **YES** 后，父 TUI 创建 RunId 日志，提升权限 worker 以 Distro 重新解析 Lxss 映射并严格核对 VHDX 路径。真实停止和 DiskPart compact 属于最终人工验收，由用户在影响面板确认后发起。

## 基线命令

~~~powershell
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Debug
dotnet test .\Vela.sln -c Debug
~~~

发布、coverage gate 和人工验收命令以 [docs/testing-and-release.md](docs/testing-and-release.md) 为准。

## 质量门禁

提交前使用锁定依赖执行 Release 验证；构建与测试输出统一写入 `artifacts\`：

~~~powershell
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Release --no-restore
dotnet test .\Vela.sln -c Release --no-build
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore `
  -p:CollectCoverage=true `
  -p:CoverletOutput=.\artifacts\coverage\coverage `
  -p:CoverletOutputFormat=cobertura `
  -p:Include='[Vela.Core]*,[Vela.Windows]*' `
  -p:ExcludeByFile='**/Program.cs' `
  -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=minimum
~~~

Coverage gate 要求 Vela.Core 与 Vela.Windows 的 line coverage 均不低于 80%，并保持零编译警告。测试项目的公共 using 集中在 `tests\Vela.Tests\GlobalUsings.cs`；`.editorconfig` 统一 C# 格式、命名和换行规则。

## 旧工具归档

legacy\powershell\ 中的文件是从桌面旧工具逐字节归档的历史行为对照，不属于 Vela 运行时或开发期测试入口。开发自动化只使用项目内 fake adapter、无害 helper process、只读预检和 artifacts\ 下的测试数据；归档脚本不应作为 Vela 的日常执行路径。
