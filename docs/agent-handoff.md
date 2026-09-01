# Vela 实施 Agent 交接单

> 本文保留初始实施边界与架构约束。仓库已经完成初始化并包含 `src`、`tests`、`legacy` 和 Git 历史；当前发布前任务请查看 [发布准备清单](release-readiness.md)。

## 当前起点

项目根为：

~~~text
D:\Jason\Documents\Workspace\vs2022\repo\Vela
~~~

当前仓库包含 Vela.sln、源码、测试、文档、legacy 归档和 Git 历史。`artifacts` 是本地构建与测试输出目录，发布目录仍需单独执行发布任务后创建。阅读顺序建议为：release-readiness.md、development-environment.md、architecture.md、testing-and-release.md；implementation-plan.md 作为历史记录保留。

## 文件写入边界

实施 agent 的默认写入范围只有项目根目录：

~~~text
D:\Jason\Documents\Workspace\vs2022\repo\Vela\
~~~

源码、测试、文档、Git 元数据、legacy 归档、NuGet lock file、构建产物和临时调试资料都放在该根目录内；构建与测试输出统一进入 artifacts\。开发与测试使用可注入的 AppPaths 根，例如 artifacts\test-data\，因此不会在用户桌面、用户配置目录或其他工作区散落文件。

下列路径属于显式确认项。实施 agent 在首次写入前展示“目的、完整路径、将创建或覆盖的文件”，等待用户确认：

| 路径 | 允许时机 |
| --- | --- |
| D:\DevTools\Vela\ | Task 13 发布，用户确认交付目录后。 |
| %LocalAppData%\Vela\ | 发布版首次运行时，TUI 展示数据根目录并由用户确认后。 |
| C:\Users\Jason\Desktop\WSL2-VHDX-Compact\ | 只读行为对照来源；迁移副本写入项目内 legacy\，桌面移除由单独确认触发。 |
| 任何新的项目外路径 | 先显示目的、路径和影响文件，再等待用户确认。 |

每次外部写入确认使用以下最小记录：

~~~text
目的：
完整路径：
创建 / 覆盖文件：
~~~

## 实施范围

1. 以当前项目文件和 `global.json` 为事实基线，不按历史计划重新创建解决方案。
2. 新功能遵循先测试、再实现、再重构的 TDD 顺序。
3. 每项任务完成后运行文档指定的测试；发布前运行全量 build、test 与 coverage gate。
4. 逻辑里程碑使用 Conventional Commit；提交前检查 diff、秘密和工作区状态。
5. 交付 EXE 到 D:\DevTools\Vela 前单独确认目标路径和覆盖文件，源码始终留在项目根目录。

## 强制架构约束

- Vela.Core 使用 net10.0，且不引用 Windows API、Spectre.Console 或进程 API。
- Vela.Application、Vela.Windows、Vela.Tui、Vela.Tests 使用 net10.0-windows。
- Vela.Application 是平台无关的展示投影与状态层，不引用 Terminal.Gui、Spectre.Console、注册表或进程 API；该约束由 `tests/Vela.Tests/Architecture/ApplicationAssemblyDependencyTests.cs` 强制。
- 真实原生命令仅封装在 Vela.Windows，全部由固定绝对路径与 ArgumentList 调用。
- Compact worker 只接受 --worker --run-id <D 格式 GUID>，并根据 Distro 重新解析 Lxss VHDX；已解析路径与请求路径严格相等后才进入动作阶段。
- 运行目录固定为 %LocalAppData%\Vela\logs\<RunId>。父 TUI 创建首个事件并轮询；worker 只追加同一日志流。
- worker 跳过主菜单、ReadLine 和确认提示；父 TUI 是唯一交互与进度界面。Global 使用 %SystemRoot%\System32\wsl.exe --shutdown 并等待 running 清单为空；Distro 使用 %SystemRoot%\System32\wsl.exe --terminate <Distro> 并等待目标离开 running 清单。
- **WSL2 磁盘挂载限制（压缩成败的决定性前提）**：vhdx 在发行版启动时挂载到共享工具 VM，只有该 VM 销毁才卸载。`--terminate` 不释放文件句柄，所以「running 清单已达目标」只是必要条件；**Distro 范围无法压缩任何在当前工具 VM 生命周期内启动过的发行版**。详见 docs/architecture.md 5.3。
- diskpart 之前必须经过 IVhdxHandleProbe 的只读独占打开探测：Held 则终止为 DiskPartPreflightFailed 并给出 TargetVhdxInUse 诊断；Free 与 Unknown 一律放行（fail-open），Unknown 表示无结论而非证据。
- 开发自动化验证只使用 fake adapter、无害 helper process 和只读预检。真实动作阶段留给最终人工验收，由用户在影响面板确认后发起。
- 开发 agent 的所有新文件都写入项目根或其 artifacts 子目录；项目外写入走上方确认记录。

## 桌面旧工具迁移

在任何桌面目录移除动作前，将原始 wsl.ps1、README.md、Verify-WhatIf.ps1、Verify-RelaunchArguments.ps1 复制到 legacy\powershell 及其 tests 子目录，比较源与目标 SHA-256，并提交迁移记录。桌面日志和 archive 文件是历史资料，不纳入运行时依赖。

## 开始与完成命令

在 Developer PowerShell for VS 2022：

~~~powershell
Set-Location 'D:\Jason\Documents\Workspace\vs2022\repo\Vela'
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Debug --no-restore
dotnet test .\Vela.sln -c Debug --no-build --no-restore
~~~

还原始终带 `-r win-x64 --locked-mode`，与 CI 保持同一形式；理由见[开发环境说明](development-environment.md)第 3.3 节。

发布、`dotnet format` 门禁、coverage gate 和人工验收以 testing-and-release.md 的完整命令为准。
