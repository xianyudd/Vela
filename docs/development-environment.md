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
Vela.Core       net10.0
Vela.Windows    net10.0-windows
Vela.Tui        net10.0-windows
Vela.Tests      net10.0-windows
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

Directory.Build.props 应统一设置：

~~~xml
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<AnalysisLevel>latest</AnalysisLevel>
<Deterministic>true</Deterministic>
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
~~~

Directory.Packages.props 启用 NuGet Central Package Management。PackageReference 只保留 Include 和资产元数据；每个具体版本集中写到 PackageVersion。首次成功 restore 生成并提交 packages.lock.json，后续验证用 locked restore。

## 4. 依赖策略

| 依赖 | 角色 | 决策 |
| --- | --- | --- |
| Spectre.Console | TUI 菜单、表格、确认、状态、彩色日志 | 首版唯一运行时第三方 UI 依赖。 |
| xUnit | 单元测试 | 测试项目依赖。 |
| coverlet.collector | 覆盖率原始报告 | 测试项目依赖。 |
| coverlet.msbuild | 80% 覆盖率失败 gate | 测试项目依赖。 |
| System.Text.Json | 配置、NDJSON、摘要 JSON | .NET 自带。 |
| Microsoft.Win32.Registry | Lxss 注册表映射 | .NET / Windows API。 |

版本由 Task 2 从当前模板与 NuGet 解析结果迁移到 Directory.Packages.props，并由 lock file 固定；Task 9 用同一方式加入 Spectre.Console。

## 5. 首次环境验证命令

在 Win11 Developer PowerShell 中执行：

~~~powershell
dotnet --info
dotnet --list-sdks
dotnet nuget list source
git --version
~~~

预期可看到 SDK 9.0.305、启用的 nuget.org 源与 Git 版本信息。
