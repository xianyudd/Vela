# Vela 开发环境说明

## 1. 已验证的本机环境

| 项目 | 当前状态 | 用途 |
| --- | --- | --- |
| IDE | Visual Studio Community 2022 17.14.16 | 创建、调试、测试、发布。 |
| 解决方案格式 | Visual Studio 17.14.36518.9 | 已写入 Vela.sln。 |
| .NET SDK | 10.0.302 | 当前锁定的编译 SDK。 |
| MSBuild | Visual Studio 侧 17.14.23；dotnet CLI 报告 17.14.21 | VS 与命令行构建。 |
| Windows SDK | 10.0.22621.0、10.0.26100.0 | Win11 原生 API 与调试支持。 |
| PowerShell | 7.6.3 | 现有 PowerShell 工具行为对照与排障。 |
| Windows Terminal | 已安装 | Vela TUI 的推荐运行宿主。 |
| NuGet | nuget.org 已启用且连通 | 还原 Terminal.Gui、Spectre.Console、xUnit、coverlet。 |

当前 `global.json` 锁定 SDK 10.0.302，框架划分如下：

~~~text
Vela.Core         net10.0
Vela.Application  net10.0-windows
Vela.Windows      net10.0-windows
Vela.Tui          net10.0-windows
Vela.Tests        net10.0-windows
~~~

Core 保持跨平台且不引用 Windows API；其余项目显式使用 Windows TFM，以匹配注册表、WindowsIdentity 和 Windows 原生命令适配。Visual Studio 2022 17.14 与 .NET 10 是当前已验证组合。

参考：

- [Microsoft：.NET SDK、MSBuild 与 Visual Studio 版本对应](https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs)
- [Microsoft：.NET 生命周期](https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core)

## 2. 当前状态与目录职责

当前项目根已存在 Vela.sln、docs、.git、src、tests、legacy、artifacts、Directory.Build.props、Directory.Packages.props 和 global.json。

~~~text
源码与 Git 仓库
D:\Jason\Documents\Workspace\vs2022\repo\Vela\
├─ Vela.sln
├─ docs\
├─ src\                           # C# 源码
├─ tests\                         # xUnit 测试
├─ legacy\powershell\             # 旧工具行为对照
├─ artifacts\                     # 本地构建与发布输出，Git 忽略
├─ Directory.Build.props
├─ Directory.Packages.props
└─ global.json

稳定发布位置（发布任务完成后创建）
D:\DevTools\Vela\
├─ Vela.exe
├─ README.md
└─ logs-link.txt

用户配置、挂起请求和日志
%LocalAppData%\Vela\
├─ config.json
├─ pending\
└─ logs\<RunId>\
~~~

源码、测试和未来 Git 历史都位于 D 盘项目目录；最终程序位于 D:\DevTools\Vela。D 盘当前约剩余 37 GiB，足以覆盖首版构建、NuGet 缓存和单文件发布。bin、obj、artifacts、.vs 不纳入版本控制。

桌面旧目录将在迁移记录与最终验收完成后移除。迁移时保留以下原始文件到源码目录：

~~~text
C:\Users\Jason\Desktop\WSL2-VHDX-Compact\wsl.ps1
C:\Users\Jason\Desktop\WSL2-VHDX-Compact\README.md
C:\Users\Jason\Desktop\WSL2-VHDX-Compact\tests\Verify-WhatIf.ps1
C:\Users\Jason\Desktop\WSL2-VHDX-Compact\tests\Verify-RelaunchArguments.ps1
~~~

对应目标为 legacy\powershell\ 和 legacy\powershell\tests\。迁移任务包含 SHA-256 对照，完成后桌面目录才进入移除清单。

### 写入目录约束

开发 agent 的默认写入根是 D:\Jason\Documents\Workspace\vs2022\repo\Vela。源码、测试、docs、legacy、.git、packages.lock.json、bin、obj 和 artifacts 均位于该根；开发期间的临时资料统一放入 artifacts\。

项目外目录采用确认式写入：

| 位置 | 约定 |
| --- | --- |
| D:\DevTools\Vela | 仅在发布任务获得用户确认后创建与更新。 |
| %LocalAppData%\Vela | 仅由发布版首次启动后的目录确认创建；开发与测试通过注入 AppPaths 使用 artifacts\test-data。 |
| 桌面旧工具目录 | 只读迁移来源；归档副本位于项目 legacy\。 |
| 其他位置 | agent 先呈现目的、完整路径、创建或覆盖文件，等待用户确认。 |

## 3. 开发终端与 Visual Studio 设置

### 3.1 推荐入口

使用 Developer PowerShell for VS 2022 或 Visual Studio 内置终端：

~~~powershell
Set-Location 'D:\Jason\Documents\Workspace\vs2022\repo\Vela'
~~~

当前 WSL 会话经 Windows PowerShell 再调用 wsl.exe 的嵌套路径会出现 UtilBindVsockAnyPort:307。为保持构建链稳定，dotnet restore、build、test、debug、publish 都从 Win11 侧的 Developer PowerShell 或 Visual Studio 发起；WSL 是 Vela 的被管理目标。

### 3.2 Visual Studio 工作负载

在 Visual Studio Installer → 修改 → 工作负载中确认：

~~~text
.NET 桌面开发
~~~

该工作负载覆盖 C# 控制台项目、测试资源与常用 .NET 桌面工具。首轮创建项目后，用一次 restore、build、test 验证模板与 SDK。

### 3.3 公共编译与还原规则

Directory.Build.props 统一设置编译约束：

~~~xml
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<AnalysisLevel>latest</AnalysisLevel>
<Deterministic>true</Deterministic>
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
~~~

同一文件还固定 RID 与输出路径，这两组属性不是可选项：

~~~xml
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<SelfContained>false</SelfContained>
<AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>

<BaseOutputPath Condition="'$(PublishProfile)' == ''">artifacts\build\$(MSBuildProjectName)\</BaseOutputPath>
<BaseOutputPath Condition="'$(PublishProfile)' != ''">artifacts\publish-build\$(MSBuildProjectName)\</BaseOutputPath>
<BaseIntermediateOutputPath>artifacts\obj\$(MSBuildProjectName)\</BaseIntermediateOutputPath>
~~~

`RuntimeIdentifier` 固定为 `win-x64` 是防 NU1004 的核心机制。CI 以 `-r win-x64 --locked-mode` 还原，lock file 因此带 win-x64 段；若某次省略 RID 的裸 `dotnet build` 或 `dotnet test` 重新生成 lock file 并丢掉该段，CI 还原会以 NU1004 失败。在 props 里声明后，任何还原方式都产出 CI 期望的段，Visual Studio 的隐式还原也不会污染 lock file。

`BaseOutputPath` 按 `PublishProfile` 分流，避免 publish 与普通构建互相覆盖入口文件，原因见[测试与发布手册](testing-and-release.md)第 6.2 节；`BaseIntermediateOutputPath` 把中间产物移出项目目录，因此项目下不应出现 `bin\` 或 `obj\`。

Directory.Packages.props 启用 NuGet Central Package Management。PackageReference 只保留 Include 和资产元数据；每个具体版本集中写到 PackageVersion。首次成功 restore 生成并提交 packages.lock.json，后续验证用 locked restore。

### 3.4 在 Visual Studio 中启动与调试

打开 `Vela.sln` 后按以下约定操作。

**启动项目**：设为 `Vela.Tui`（右键 → 设为启动项目）。其余项目是类库或测试项目，不能作为启动目标。

**启动配置**：`src\Vela.Tui\Properties\launchSettings.json` 提供三个 profile，出现在工具栏的启动下拉框中：

| Profile | 参数 | 用途 |
| --- | --- | --- |
| `Vela TUI` | 无 | 完整交互界面，日常调试入口。 |
| `Vela TUI (--help)` | `--help` | 验证用法文本，立即退出。 |
| `Vela TUI (--version)` | `--version` | 验证版本文本，立即退出。 |

launchSettings.json 纳入版本控制，因此每个开发者拿到同一组 profile。这里不提供 `--worker` profile：worker 是压缩流程自己用 UAC 启动的子进程，手动启动会绕过确认与审计链路。

**必须以管理员身份运行 Visual Studio**。`src\Vela.Tui\app.manifest` 声明 `requestedExecutionLevel level="requireAdministrator"`，而 F5 由 Visual Studio 自身的令牌启动被调试进程，它不会弹 UAC。若 Visual Studio 未提权，F5 会直接报错，提示需要以管理员身份重启：

~~~text
Unable to start program ...\Vela.Tui.exe
The requested operation requires elevation.
~~~

这不是配置损坏，而是 manifest 的预期行为——Vela 需要提权令牌来驱动 diskpart 并校验 `%ProgramData%\Vela` 的 SACL 与完整性标签。把 Visual Studio 的快捷方式设为“以管理员身份运行”，或从提权的 Developer PowerShell 启动 `devenv.exe`。

**不要用 `dotnet run` 代替 F5 做提权路径的验证**。manifest 只嵌入 apphost（`Vela.Tui.exe`），`dotnet run` 下 `requireAdministrator` 不生效，压缩流程会失去唯一的提权入口。只读界面浏览用 `dotnet run` 没问题；涉及压缩执行必须走 `Vela.Tui.exe` 或 `scripts\open-vela-tui.cmd`。

**构建输出位置**：`Directory.Build.props` 把输出重定向到 `artifacts\build\<项目>\<配置>\`，中间产物重定向到 `artifacts\obj\<项目>\`。项目目录下不会出现 `bin\` 或 `obj\`。VS 的“清理解决方案”与 `dotnet clean` 都作用于重定向后的路径。

**测试资源管理器**：`dotnet test` 与 VS 测试资源管理器共用同一批 xUnit 测试，无需额外配置。首次打开若列表为空，先生成一次解决方案。

**终端画布**：Terminal.Gui 界面在 Windows Terminal 下表现最好。VS 的调试控制台窗口可以运行，但尺寸与配色受限，界面自适应验证请按 `160×45`、`120×35`、`100×30`、`80×24`、`60×16` 在 Windows Terminal 中核对。

## 4. 依赖策略

版本集中声明在 `Directory.Packages.props`，并由 `packages.lock.json` 固定：

| 依赖 | 版本 | 角色 | 引用项目 |
| --- | --- | --- | --- |
| Terminal.Gui | 2.4.5 | 生产交互界面框架：shell、面板、列表、主题 | Vela.Tui、Vela.Tests |
| Spectre.Console | 0.57.2 | 仅 redirected 与启动静态帧渲染 | Vela.Tui |
| xunit | 2.9.2 | 单元测试框架 | Vela.Tests |
| xunit.runner.visualstudio | 2.8.2 | VS 测试资源管理器与 `dotnet test` 适配 | Vela.Tests |
| Microsoft.NET.Test.Sdk | 17.12.0 | 测试主机与 trx logger | Vela.Tests |
| coverlet.collector | 6.0.2 | 覆盖率原始报告 | Vela.Tests |
| coverlet.msbuild | 6.0.2 | 覆盖率 gate 的 Cobertura 输出 | Vela.Tests |

`Terminal.Gui` 与 `Spectre.Console` 并存不是重复依赖：Terminal.Gui 承载交互 shell，Spectre 只在 `Console.IsInputRedirected` 分支和首启静态帧使用（`src/Vela.Tui/Program.cs`）。两者不共享布局代码。

以下能力来自 .NET 与 Windows TFM，不是 NuGet 包：`System.Text.Json`（配置、NDJSON、摘要）、`Microsoft.Win32.Registry`（Lxss 映射）、`System.Security.AccessControl`（SACL 与完整性标签校验）。

本地工具清单 `dotnet-tools.json` 声明 `terminalguidesigner` 2.4.5（`rollForward: false`，与 Terminal.Gui 包版本对齐），用 `dotnet tool restore` 获取。该工具只用于界面布局试验，不参与构建、测试或发布。

## 5. 首次环境验证命令

在 Win11 Developer PowerShell 中执行：

~~~powershell
dotnet --info
dotnet --list-sdks
dotnet nuget list source
git --version
~~~

预期可看到 `global.json` 锁定的 SDK 10.0.302（或其 `latestPatch` 范围内的补丁版本）、启用的 nuget.org 源与 Git 版本信息。本机当前安装 9.0.305 与 10.0.303，`rollForward: latestPatch` 会选中 10.0.303。
