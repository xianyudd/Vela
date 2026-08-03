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

## 运行日志

发布版的每次运行使用：

~~~text
%LocalAppData%\Vela\logs\<RunId>\
~~~

目录包含 events.ndjson、run.log 和 summary.json。首次创建发布版数据根前，TUI 会展示完整路径并等待确认。开发与测试注入项目内的 artifacts\test-data\，避免项目外写入。

## 预检与执行

**预检**仅采集发行版、Lxss 映射、VHDX 和宿主盘快照；它使用只读适配器，不触发 WSL 停止、发行版终止或 DiskPart compact。

**执行压缩**先展示精确档案、VHDX 路径、Global 或 Distro 影响范围及运行中发行版。输入 **YES** 后，父 TUI 创建 RunId 日志，提升权限 worker 以 Distro 重新解析 Lxss 映射并严格核对 VHDX 路径。真实停止和 DiskPart compact 属于最终人工验收，由用户在影响面板确认后发起。

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
