# Vela 测试、验收与发布手册

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
| CompactionWorkflow | 成功、超时、detail 异常、compact 异常、0 B 回收 | 每个退出点写最终 summary。 |
| NativeToolPaths / ProcessRunner | 三个绝对原生命令路径、参数边界、超时 | 命令由固定路径和 ArgumentList 构造。 |
| LxssProfileResolver / WslClient | 映射、清单、中英文输出、参数生成 | registry path 与参数数组精确。 |
| VhdxInspector | 文件快照、盘快照、sparse unknown | 数值与 nullable sparse 语义正确。 |
| DiskPartScriptBuilder | 路径、ASCII、命令顺序、临时文件清理 | detail vdisk 始终先于 compact vdisk。 |
| FileRunJournal | RunId 目录、NDJSON、序列号、完整行读取、summary、异常收尾、保留期 | JSON 可反序列化，worker 可追加。 |
| JsonProfileStore | 初始配置、迁移、原子保存 | JSON 完整且替换原子化。 |
| OperationRequestStore / UacWorkerLauncher | request、固定 worker 参数、UAC 取消 | pending 路径由 RunId 派生。 |
| WorkerMode | 管理员身份、额外参数、RunId、映射二次校验、非交互分支 | 失败时动作调用数为 0。 |
| TUI | 菜单、预检表格、确认词、进度、错误屏幕 | 文案准确，父 TUI 是唯一交互界面。 |

## 3. 常用验证命令

在 Developer PowerShell for VS 2022：

~~~powershell
Set-Location 'D:\Jason\Documents\Workspace\vs2022\repo\Vela'

# 依赖锁定与开发构建
dotnet restore .\Vela.sln --locked-mode
dotnet build .\Vela.sln -c Debug

# 全量测试
dotnet test .\Vela.sln -c Debug

# 预检工作流
dotnet test .\tests\Vela.Tests --filter FullyQualifiedName~PreflightWorkflowTests

# Global / Distro 范围规则
dotnet test .\tests\Vela.Tests --filter FullyQualifiedName~CompactionWorkflowTests

# 配置和日志持久化
dotnet test .\tests\Vela.Tests --filter 'FullyQualifiedName~JsonProfileStore|FullyQualifiedName~FileRunJournal'

# 强制 80% line coverage gate，仅统计 Core 与 Windows
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release -p:CollectCoverage=true -p:CoverletOutput=.\artifacts\coverage\coverage -p:CoverletOutputFormat=cobertura -p:Include='[Vela.Core]*,[Vela.Windows]*' -p:ExcludeByFile='**/Program.cs' -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=minimum
~~~

验收线：

~~~text
Vela.Core 与 Vela.Windows 的 line coverage ≥ 80%
所有测试绿色
零编译警告
locked restore 成功
~~~

## 4. 预检人工验收

发布候选 EXE 先运行预检，检查下列项目：

| 检查项 | 预期 |
| --- | --- |
| TUI 启动 | Vela — WSL VHDX Compact 标题和主菜单正确显示。 |
| 默认档案 | Ubuntu-24.04、D 盘 VHDX、Global、45 秒。 |
| 注册表映射 | 显示 Lxss BasePath 与期望 ext4.vhdx。 |
| WSL 清单 | 已安装、运行中、详细清单和版本信息可见。 |
| VHDX 快照 | 文件长度、最后写入时间、稀疏标志、盘符可用空间可见。 |
| 日志 | 创建 logs\<RunId>\events.ndjson、run.log、summary.json。 |
| 错误显示 | 路径或映射问题显示明确错误和日志目录。 |

与迁移的旧脚本预检结果对照：

~~~powershell
pwsh -ExecutionPolicy Bypass -File .\legacy\powershell\wsl.ps1 -WhatIf
~~~

对照重点：目标路径、发行版名、VHDX 字节数、D 盘可用空间和运行中的发行版；日志文字无须逐字一致。

## 5. 执行流程人工验收

最终动作验收由用户在影响面板确认后自行发起。验收顺序：

1. 在 TUI 中选择“执行压缩”。
2. 核对 Profile、VHDX 路径、Global / Distro 范围、正在运行的发行版与影响提示。
3. 输入 YES，确认 UAC worker 启动。
4. 观察父 TUI 轮询的 logs\<RunId>\events.ndjson 持续增加。
5. 检查 worker 分支跳过主菜单和确认提示，只向同一 journal 追加事件与退出码。
6. 检查 worker 再次写入管理员身份、映射验证和压缩前快照。
7. 检查 DiskPart detail 记录与最终 summary。
8. 对比压缩前后 VHDX 文件长度、宿主盘可用空间与 reclaimedBytes。
9. 在“最近运行记录”页确认 status、elapsed time、日志路径和错误字段。

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
  <TargetFramework>net9.0-windows</TargetFramework>
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
dotnet restore .\Vela.sln --locked-mode
dotnet test .\Vela.sln -c Release --no-restore
dotnet publish .\src\Vela.Tui\Vela.Tui.csproj -c Release --no-restore -p:PublishProfile=win-x64-singlefile -o .\artifacts\publish\win-x64
~~~

发布输出：

~~~text
artifacts\publish\win-x64\Vela.exe
~~~

## 7. 交付目录

Task 13 创建稳定入口：

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
- [ ] D:\DevTools\Vela\Vela.exe 是日常入口。
- [ ] 首次启动建立本地配置目录与默认档案。
- [ ] 预检生成三类运行记录。
- [ ] 配置编辑后重启仍保留档案。
- [ ] UAC worker 完成后留下最终 summary 和退出码。
- [ ] Global / Distro 的参数与停止条件均由 workflow 测试覆盖。
- [ ] worker 运行时跳过主菜单和确认提示，父 TUI 保持唯一交互入口。
- [ ] worker 的映射不一致测试证明动作适配器调用数为零。
- [ ] 旧 wsl.ps1、README、Verify-WhatIf.ps1、Verify-RelaunchArguments.ps1 已归档到 legacy\powershell。
- [ ] D:\DevTools\Vela\README.md 说明启动、菜单键位、日志位置、结果类别和排障路径。
- [ ] 所有开发期生成文件位于项目根或 artifacts\；项目外写入都有确认记录。
