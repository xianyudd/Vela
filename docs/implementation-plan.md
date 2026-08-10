# Vela 实施计划

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development（有 subagent 时）或 superpowers:executing-plans 实施本计划。复选框用于持续记录进度。

**目标：** 构建单入口 C# Spectre.Console TUI “Vela”。它覆盖现有 PowerShell 工具的诊断、日志、WSL 盘点、VHDX 检查、确认、UAC worker 与 DiskPart 工作流，并以发行版注册表映射作为 Compact 目标的严格来源。

**架构：** 四项目解决方案：纯 Core、Windows 适配器、Spectre.Console 可执行项目、xUnit 测试项目。TUI 以普通权限运行预检；父进程按 RunId 创建持久日志，提升权限 worker 重新校验映射后追加同一日志流。

**技术栈：** C# / .NET 9、Visual Studio 2022 17.14、Spectre.Console、System.Text.Json、Microsoft.Win32.Registry、xUnit、coverlet、Git、self-contained 单文件 win-x64 发布。

---

## 实施不变量

1. 开始 C# 迁移时，将桌面旧工具的 wsl.ps1、README.md、Verify-WhatIf.ps1、Verify-RelaunchArguments.ps1 原样归档到 legacy\powershell；归档 SHA-256 与源文件一致后，桌面目录进入后续移除清单。
2. Preflight 请求的动作适配器调用数始终为零；测试以 fake adapter 的调用计数验证。
3. Compact worker 只接受 --worker --run-id <D 格式 GUID>。worker 按 Distro 从 HKCU Lxss 重新解析 ext4.vhdx，规范化后与请求路径严格相等时才调用 WSL 或 DiskPart。
4. 首版配置没有 AllowVhdxMismatch；映射差异作为预检结果展示，Compact 在验证阶段完成。
5. 原生命令固定为 %SystemRoot%\System32\wsl.exe、%SystemRoot%\System32\diskpart.exe、%SystemRoot%\System32\fsutil.exe；普通进程调用用 ProcessStartInfo.ArgumentList，杜绝 cmd.exe 命令文本拼接。
6. 父进程创建 %LocalAppData%\Vela\logs\<RunId> 和 RunCreated 事件；worker 只追加该 RunId 的 events.ndjson、run.log、summary.json。
7. 成功、验证失败、超时、UAC 取消、worker 失败都生成可读日志和 JSON 摘要。
8. 开发测试使用 fake adapter、无害 helper process 和只读预检；最终动作验收由用户查看影响面板并确认后自行发起。
9. 源码始终位于 D:\Jason\Documents\Workspace\vs2022\repo\Vela；D:\DevTools\Vela 只承载发布后的 Vela.exe 与日常使用说明。
10. 开发 agent 的默认写入根仅为项目目录；源码、测试、docs、legacy、lock file、临时资料与构建输出都位于该目录。项目外写入前展示目的、完整路径、创建或覆盖文件，等待用户确认。

## 计划文件图

~~~text
Vela/
├─ Vela.sln                                    # 已有空白解决方案
├─ .gitignore
├─ .editorconfig
├─ README.md
├─ global.json
├─ Directory.Build.props
├─ Directory.Packages.props
├─ src/
│  ├─ Vela.Core/
│  ├─ Vela.Windows/
│  └─ Vela.Tui/
├─ tests/
│  └─ Vela.Tests/
├─ docs/                                        # 已有交接、架构、计划与验收文档
├─ legacy/
│  └─ powershell/
│     ├─ wsl.ps1
│     ├─ README.md
│     └─ tests/
│        ├─ Verify-WhatIf.ps1
│        └─ Verify-RelaunchArguments.ps1
└─ artifacts/                                   # Git 忽略的本地输出
~~~

## Chunk 1：解决方案基线与质量护栏

### Task 1：初始化 Git、旧工具归档与 SDK 元数据

**文件：**

- 创建：.gitignore、README.md、global.json、Directory.Build.props、Directory.Packages.props
- 创建：legacy\powershell\wsl.ps1、legacy\powershell\README.md
- 创建：legacy\powershell\tests\Verify-WhatIf.ps1、legacy\powershell\tests\Verify-RelaunchArguments.ps1

- [ ] **Step 1：在 Win11 Developer PowerShell 打开工作目录**

~~~powershell
Set-Location 'D:\Jason\Documents\Workspace\vs2022\repo\Vela'
~~~

- [ ] **Step 1a：确认本轮写入边界**

开发期新文件只写入当前项目根和其 artifacts 子目录。D:\DevTools\Vela、%LocalAppData%\Vela、桌面目录与任何其他项目外路径都在相应任务开始前呈现“目的、完整路径、创建 / 覆盖文件”并等待用户确认。

- [ ] **Step 2：初始化 Git 并核对当前基线**

~~~powershell
git init
git branch -M main
git status --short
~~~

预期：Vela.sln 和 docs\ 是待跟踪候选；.vs\ 是 Visual Studio 本地状态。在写入 .gitignore 前保留观察，写入后再次运行 git status --short，确认 .vs\ 未被暂存。

- [ ] **Step 3：归档桌面旧工具并验证哈希**

~~~powershell
$desktop = 'C:\Users\Jason\Desktop\WSL2-VHDX-Compact'
New-Item -ItemType Directory -Force '.\legacy\powershell\tests' | Out-Null
Copy-Item "$desktop\wsl.ps1" '.\legacy\powershell\wsl.ps1'
Copy-Item "$desktop\README.md" '.\legacy\powershell\README.md'
Copy-Item "$desktop\tests\Verify-WhatIf.ps1" '.\legacy\powershell\tests\Verify-WhatIf.ps1'
Copy-Item "$desktop\tests\Verify-RelaunchArguments.ps1" '.\legacy\powershell\tests\Verify-RelaunchArguments.ps1'
$pairs = @(@("$desktop\wsl.ps1", '.\legacy\powershell\wsl.ps1'), @("$desktop\README.md", '.\legacy\powershell\README.md'), @("$desktop\tests\Verify-WhatIf.ps1", '.\legacy\powershell\tests\Verify-WhatIf.ps1'), @("$desktop\tests\Verify-RelaunchArguments.ps1", '.\legacy\powershell\tests\Verify-RelaunchArguments.ps1'))
foreach ($pair in $pairs) { $source = (Get-FileHash $pair[0] -Algorithm SHA256).Hash; $copy = (Get-FileHash $pair[1] -Algorithm SHA256).Hash; if ($source -ne $copy) { throw "Migration hash mismatch: $($pair[0])" } }
~~~

记录桌面 logs 与 archive 为历史资料；源码运行时不依赖桌面目录。

- [ ] **Step 4：写入 .gitignore**

~~~gitignore
.vs/
bin/
obj/
artifacts/
TestResults/
*.user
*.suo
~~~

- [ ] **Step 5：固定 SDK 与公共编译属性**

创建 global.json：

~~~json
{
  "sdk": {
    "version": "9.0.305",
    "rollForward": "latestPatch"
  }
}
~~~

创建 Directory.Build.props：

~~~xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
    <Deterministic>true</Deterministic>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
~~~

- [ ] **Step 6：启用集中 NuGet 版本管理**

创建初始 Directory.Packages.props：

~~~xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup />
</Project>
~~~

Task 2 的 dotnet new 命令都使用 --no-restore。模板 PackageReference 的 Version 将在首次 restore 前迁移到此文件。

- [ ] **Step 7：写入根 README**

README 至少包含：项目简介、docs\ 阅读顺序、Developer PowerShell 工作目录、日常发布入口、日志根目录、预检与执行区别。

- [ ] **Step 8：提交基线**

~~~powershell
git add .
git commit -m "chore: initialize Vela solution metadata"
~~~

### Task 2：创建项目、迁移模板依赖并建立引用

**文件：**

- 创建：src\Vela.Core\Vela.Core.csproj
- 创建：src\Vela.Windows\Vela.Windows.csproj
- 创建：src\Vela.Tui\Vela.Tui.csproj
- 创建：tests\Vela.Tests\Vela.Tests.csproj
- 修改：Vela.sln、Directory.Packages.props

- [ ] **Step 1：创建项目，暂不执行 restore**

~~~powershell
New-Item -ItemType Directory -Force src, tests, artifacts | Out-Null
dotnet new classlib -n Vela.Core -o src/Vela.Core -f net9.0 --no-restore
dotnet new classlib -n Vela.Windows -o src/Vela.Windows -f net9.0 --no-restore
dotnet new console -n Vela.Tui -o src/Vela.Tui -f net9.0 --no-restore
dotnet new xunit -n Vela.Tests -o tests/Vela.Tests -f net9.0 --no-restore
~~~

- [ ] **Step 2：将项目加入现有解决方案**

~~~powershell
dotnet sln .\Vela.sln add .\src\Vela.Core\Vela.Core.csproj
dotnet sln .\Vela.sln add .\src\Vela.Windows\Vela.Windows.csproj
dotnet sln .\Vela.sln add .\src\Vela.Tui\Vela.Tui.csproj
dotnet sln .\Vela.sln add .\tests\Vela.Tests\Vela.Tests.csproj
~~~

- [ ] **Step 3：设置目标框架**

将 Vela.Windows.csproj、Vela.Tui.csproj、Vela.Tests.csproj 的 TargetFramework 设为 net9.0-windows；Vela.Core.csproj 保持 net9.0。

- [ ] **Step 4：在首次 restore 前迁移 xUnit 模板包版本**

1. 读取 tests\Vela.Tests\Vela.Tests.csproj 中模板产生的 Microsoft.NET.Test.Sdk、xunit、xunit.runner.visualstudio、coverlet.collector 的包名与 Version。
2. 将每个版本写为 Directory.Packages.props 的 PackageVersion。
3. 从测试项目的每个 PackageReference 删除 Version 属性或 Version 子元素，保留 PrivateAssets、IncludeAssets 等元数据。
4. 在测试项目添加 coverlet.msbuild 的 PackageReference（PrivateAssets=all）；为它选择与 coverlet.collector 兼容的确切版本，并在 Directory.Packages.props 增加对应 PackageVersion。
5. 确认所有 PackageReference 都由集中 PackageVersion 覆盖后，首次 restore 才可开始。

该步骤避免 Central Package Management 与模板内 Version 属性产生 NU1008 冲突。

- [ ] **Step 5：设置项目引用**

~~~powershell
dotnet add .\src\Vela.Windows\Vela.Windows.csproj reference .\src\Vela.Core\Vela.Core.csproj
dotnet add .\src\Vela.Tui\Vela.Tui.csproj reference .\src\Vela.Core\Vela.Core.csproj
dotnet add .\src\Vela.Tui\Vela.Tui.csproj reference .\src\Vela.Windows\Vela.Windows.csproj
dotnet add .\tests\Vela.Tests\Vela.Tests.csproj reference .\src\Vela.Core\Vela.Core.csproj
dotnet add .\tests\Vela.Tests\Vela.Tests.csproj reference .\src\Vela.Windows\Vela.Windows.csproj
dotnet add .\tests\Vela.Tests\Vela.Tests.csproj reference .\src\Vela.Tui\Vela.Tui.csproj
~~~

- [ ] **Step 6：写架构 smoke test**

测试反射检查 Vela.Core 程序集引用列表，其中没有 Spectre.Console、Microsoft.Win32.Registry、System.Diagnostics.Process。

- [ ] **Step 7：restore、生成 lock file、构建与测试**

~~~powershell
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Debug
dotnet test .\Vela.sln -c Debug
~~~

预期：build 与模板测试通过，相关 packages.lock.json 已生成并加入 Git。

- [ ] **Step 8：提交**

~~~powershell
git add .
git commit -m "chore: add layered Vela projects"
~~~

## Chunk 2：纯领域、验证与只读预检

### Task 3：定义不可变模型与结果词汇

**文件：**

- 创建：src\Vela.Core\Models\Profile.cs
- 创建：src\Vela.Core\Models\OperationRequest.cs
- 创建：src\Vela.Core\Models\VhdxSnapshot.cs
- 创建：src\Vela.Core\Models\RunEvent.cs
- 创建：src\Vela.Core\Models\RunSummary.cs
- 创建：tests\Vela.Tests\Core\ModelTests.cs

- [ ] **Step 1：写失败模型测试**

覆盖 Profile 值相等、RunId 往返、OperationRequest 只含 Compact 或 Preflight intent、字节差计算、终态枚举与不可变复制。Profile 没有 AllowVhdxMismatch 字段。

- [ ] **Step 2：添加 records 和 enums**

使用 sealed record；定义 intent、shutdown mode、phase、level、terminal result。terminal result 至少包括 Succeeded、CompletedWithNoReclaim、ValidationFailed、ShutdownTimedOut、DiskPartPreflightFailed、DiskPartCompactFailed、WorkerInterrupted、CancelledBeforeElevation。

- [ ] **Step 3：运行定向测试并提交**

~~~powershell
dotnet test .\tests\Vela.Tests --filter FullyQualifiedName~ModelTests
git add .
git commit -m "feat: add Vela run domain models"
~~~

### Task 4：实现档案与路径验证

**文件：**

- 创建：src\Vela.Core\Validation\ProfileValidator.cs
- 创建：src\Vela.Core\Validation\ValidationResult.cs
- 创建：tests\Vela.Tests\Core\ProfileValidatorTests.cs

- [ ] **Step 1：先写失败测试**

覆盖空发行版名、相对路径、缺少 .vhdx 后缀、NUL / CR / LF、非 ASCII DiskPart 路径、timeout 低于 5 秒、高于 300 秒、当前 Ubuntu 档案。

- [ ] **Step 2：实现纯验证**

验证器只处理字符串和边界；文件存在性、注册表映射、目录包含关系由 adapter-backed workflow 检查。

- [ ] **Step 3：运行测试并提交**

~~~powershell
dotnet test .\tests\Vela.Tests --filter FullyQualifiedName~ProfileValidatorTests
dotnet test .\Vela.sln
git add .
git commit -m "feat: validate Vela target profiles"
~~~

### Task 5：定义 ports 与只读 PreflightWorkflow

**文件：**

- 创建：src\Vela.Core\Contracts\*.cs
- 创建：src\Vela.Core\Workflows\PreflightWorkflow.cs
- 创建：src\Vela.Core\Workflows\WorkflowResult.cs
- 创建：tests\Vela.Tests\Fakes\FakeProcessRunner.cs
- 创建：tests\Vela.Tests\Fakes\FakeWslClient.cs
- 创建：tests\Vela.Tests\Fakes\FakeDiskPartClient.cs
- 创建：tests\Vela.Tests\Core\PreflightWorkflowTests.cs

- [ ] **Step 1：写关键失败测试**

用 fake adapters 建立 Preflight 请求。断言它记录 inventory、registry mapping、snapshot、日志事件，而 WSL action 与 DiskPart 调用计数均为零。

- [ ] **Step 2：添加 contracts**

至少定义 IWslClient、ILxssProfileResolver、IVhdxInspector、IRunJournal、IDiskPartClient、IProcessRunner、IClock。IWslClient 显式声明 ShutdownAllAsync（对应 %SystemRoot%\System32\wsl.exe --shutdown 并等待 running 清单为空）和 TerminateDistroAsync（对应 %SystemRoot%\System32\wsl.exe --terminate <Distro> 并等待目标离开 running 清单）。契约分开只读 inventory、snapshot 与执行阶段操作。

- [ ] **Step 3：实现预检工作流**

顺序为 validate → distro inventory → registry mapping → VHDX snapshot → running inventory → journal summary。Preflight 在此返回。

- [ ] **Step 4：添加错误路径测试**

覆盖发行版缺失、映射不一致、VHDX 缺失、sparse 查询不可用、日志创建失败。映射不一致生成预检失败摘要。

- [ ] **Step 5：运行与提交**

~~~powershell
dotnet test .\tests\Vela.Tests --filter FullyQualifiedName~PreflightWorkflowTests
git add .
git commit -m "feat: add read-only preflight workflow"
~~~

## Chunk 3：Windows adapter、配置与持久日志

### Task 6：实现固定路径的 WindowsProcessRunner

**文件：**

- 创建：src\Vela.Windows\Processes\NativeToolPaths.cs
- 创建：src\Vela.Windows\Processes\WindowsProcessRunner.cs
- 创建：src\Vela.Windows\Processes\ProcessResult.cs
- 创建：tests\Vela.Tests\Windows\NativeToolPathsTests.cs
- 创建：tests\Vela.Tests\Windows\WindowsProcessRunnerTests.cs

- [ ] **Step 1：写失败测试**

使用无害 child helper process 测试 stdout、stderr、退出码、ArgumentList 边界、耗时、取消和超时。为 NativeToolPaths 断言 wsl.exe、diskpart.exe、fsutil.exe 都由 Environment.SystemDirectory 加文件名得出。

- [ ] **Step 2：实现进程捕获**

普通进程调用设置 UseShellExecute=false，重定向输出，逐项填充 ArgumentList，等待退出并通过 IProgress<RunEvent> 发布输出。超时与非零退出码映射为结构化结果。

- [ ] **Step 3：运行与提交**

~~~powershell
dotnet test .\tests\Vela.Tests --filter FullyQualifiedName~WindowsProcessRunner
git add .
git commit -m "feat: add structured Windows process runner"
~~~

### Task 7：实现 Lxss、WSL 与 VHDX inspectors

**文件：**

- 创建：src\Vela.Windows\Registry\LxssProfileResolver.cs
- 创建：src\Vela.Windows\Wsl\WslClient.cs
- 创建：src\Vela.Windows\Storage\VhdxInspector.cs
- 创建：tests\Vela.Tests\Windows\LxssProfileResolverTests.cs
- 创建：tests\Vela.Tests\Windows\WslClientTests.cs
- 创建：tests\Vela.Tests\Windows\VhdxInspectorTests.cs

- [ ] **Step 1：写 parser、mapping 与参数测试**

使用 registry-like fixture 和 native-output fixture，覆盖中英文 fsutil 结果、已安装/运行中/verbose WSL 清单、版本和精确参数数组。

- [ ] **Step 2：实现 adapter**

LxssProfileResolver 读取当前用户的 Lxss key，规范化 BasePath\ext4.vhdx。WslClient 的 inventory 与 worker action 为显式分离方法。VhdxInspector 收集 FileInfo、DriveInfo、last write、sparse；稀疏查询失败映射为 bool? 和诊断事件。

- [ ] **Step 3：运行与提交**

~~~powershell
dotnet test .\tests\Vela.Tests --filter FullyQualifiedName~Windows
git add .
git commit -m "feat: inspect WSL registry and VHDX state"
~~~

### Task 8：实现 AppPaths、配置和可恢复 run journal

**文件：**

- 创建：src\Vela.Windows\Configuration\JsonProfileStore.cs
- 创建：src\Vela.Windows\Diagnostics\AppPaths.cs
- 创建：src\Vela.Windows\Diagnostics\FileRunJournal.cs
- 创建：tests\Vela.Tests\Windows\JsonProfileStoreTests.cs
- 创建：tests\Vela.Tests\Windows\FileRunJournalTests.cs

- [ ] **Step 1：写失败持久化测试**

覆盖初始档案、schema version、原子 config 替换、RunId 派生目录、NDJSON sequence、run.log、valid summary、活动 RunId 不参与日志保留清理，以及 reader 与 worker writer 并发时的完整行读取。

- [ ] **Step 2：实现配置 store**

生产默认使用 %LocalAppData%\Vela\config.json，首次创建前由 TUI 展示目录并确认。开发和测试注入项目内 artifacts\test-data\config.json；文件缺失时才创建初始档案。写入走 config.json.tmp、flush、原子替换。

- [ ] **Step 3：实现 journal 协议**

CreateRun(RunId) 创建 logs\<RunId> 并写 RunCreated；OpenExisting(RunId) 只打开该目录并从最后 sequence 继续追加。writer 以 FileMode.Append、FileAccess.Write、FileShare.Read 打开 events.ndjson，每个事件按“完整 UTF-8 JSON + 换行”写入并 flush。reader 只解析完整换行记录，对短暂共享冲突重试。summary 写入采用临时文件与替换。

- [ ] **Step 4：运行与提交**

~~~powershell
dotnet test .\tests\Vela.Tests --filter 'FullyQualifiedName~JsonProfileStore|FullyQualifiedName~FileRunJournal'
git add .
git commit -m "feat: persist profiles and run diagnostics"
~~~

## Chunk 4：TUI、UAC handoff 与 DiskPart workflow

### Task 9：加入 Spectre.Console shell

**文件：**

- 修改：Directory.Packages.props、src\Vela.Tui\Vela.Tui.csproj
- 创建：src\Vela.Tui\Menu\MainMenu.cs
- 创建：src\Vela.Tui\Application\DashboardViewModel.cs
- 创建：src\Vela.Tui\Rendering\FrameRenderer.cs
- 创建：tests\Vela.Tests\Tui\MainMenuTests.cs

- [ ] **Step 1：先集中登记 Spectre.Console**

选择当前 NuGet 稳定版，将它写为 Directory.Packages.props 中的 PackageVersion。Vela.Tui.csproj 只新增 PackageReference Include="Spectre.Console"，随后 restore 并确认新的 packages.lock.json。

- [ ] **Step 2：写失败 TUI 测试**

使用注入输入与 console adapter。断言菜单标签、Profile 标题、YES 提示、错误呈现和 progress 状态来自不可变 view model。

- [ ] **Step 3：实现菜单与预检展示**

提供 Preflight、Execute compaction、Manage profiles、Recent runs、Open logs、Exit。预检表格展示 profile、registry mapping、VHDX snapshot、drive snapshot、sparse、running distros、提示和日志目录。

- [ ] **Step 4：用 fake services 检查 TUI 并提交**

~~~powershell
dotnet run --project .\src\Vela.Tui\Vela.Tui.csproj
git add .
git commit -m "feat: add Vela terminal interface"
~~~

### Task 10：实现 OperationRequestStore、UAC launcher 与 worker mode

**文件：**

- 创建：src\Vela.Windows\Elevation\OperationRequestStore.cs
- 创建：src\Vela.Windows\Elevation\UacWorkerLauncher.cs
- 创建：src\Vela.Tui\ProgramModes\WorkerMode.cs
- 创建：tests\Vela.Tests\Windows\OperationRequestStoreTests.cs
- 创建：tests\Vela.Tests\Windows\UacWorkerLauncherTests.cs
- 创建：tests\Vela.Tests\Tui\WorkerModeTests.cs

- [ ] **Step 1：先写失败协议测试**

至少覆盖：

1. 父进程先创建 logs\<RunId> 与 RunCreated，再写 pending\<RunId>.json；
2. worker 命令参数精确为 --worker、--run-id、D 格式 GUID；
3. parser 对额外参数、非 GUID、RunId 不匹配、非 Compact intent、pending 根目录外路径给出 ValidationFailed；
4. worker 非管理员身份写入失败事件；
5. worker 从 Lxss 重解析的路径与请求路径不一致时，WSL 与 DiskPart action 计数为零；
6. UAC 取消映射为 CancelledBeforeElevation，普通启动错误写入日志；
7. worker 和父 TUI 使用同一个 events.ndjson，reader 跳过尚未换行的记录；
8. worker 跳过主菜单、ReadLine 和确认提示，并以约定退出码结束。

- [ ] **Step 2：实现 request store**

写入 pending\<RunId>.json 时使用临时文件、flush、原子替换。worker 以已验证的 RunId 组装固定 pending 路径，读取一次，写完最终事件和 summary 后消费该请求。

- [ ] **Step 3：实现 launcher**

使用当前可执行文件路径、ProcessStartInfo、UseShellExecute=true、Verb="runas"。用 ArgumentList 添加固定三个参数边界；UAC 取消与 launch 失败均转换为 journal terminal event。

- [ ] **Step 4：实现 WorkerMode 与确认屏幕**

WorkerMode 验证管理员身份、request、RunId、AppPaths 包含关系、Lxss 映射和共同预检。它跳过主菜单、ReadLine 与确认提示，只运行 workflow、追加 journal，并映射终态为退出码：0（Succeeded 或 CompletedWithNoReclaim）、2（ValidationFailed）、3（ShutdownTimedOut）、4（DiskPartPreflightFailed）、5（DiskPartCompactFailed）、10（未处理 worker 异常）。确认屏幕显示 mode、全部运行中发行版、VHDX 路径与影响提示，并要求精确 YES 后才调用 launcher。普通权限 TUI 持续轮询同一 journal。

- [ ] **Step 5：运行与提交**

~~~powershell
dotnet test .\tests\Vela.Tests --filter 'FullyQualifiedName~UacWorkerLauncher|FullyQualifiedName~OperationRequestStore|FullyQualifiedName~WorkerMode'
git add .
git commit -m "feat: add elevated Vela worker handoff"
~~~

### Task 11：实现 DiskPart workflow 与最终结果

**文件：**

- 创建：src\Vela.Windows\DiskPart\DiskPartScriptBuilder.cs
- 创建：src\Vela.Windows\DiskPart\DiskPartClient.cs
- 创建：src\Vela.Core\Workflows\CompactionWorkflow.cs
- 创建：tests\Vela.Tests\Windows\DiskPartScriptBuilderTests.cs
- 创建：tests\Vela.Tests\Core\CompactionWorkflowTests.cs

- [ ] **Step 1：写 script-builder 失败测试**

覆盖绝对路径、ASCII、CR/LF、临时文件、detail vdisk 排序、compact vdisk 排序、finally 清理和生成文件编码。

- [ ] **Step 2：写 workflow 失败测试**

覆盖成功、Global 的 --shutdown 与空 running 清单等待、Distro 的 --terminate <Distro> 与目标离开 running 清单等待、shutdown timeout、detail error、compact error、0 B 回收、提升后 mapping 改变、journal 最终收尾和异常。每个真实执行端口用 fake adapter，所有 action 只在路径严格校验通过后发生。

- [ ] **Step 3：实现 orchestration**

worker 顺序为：重新验证 → before snapshot → action request → wait state → detail vdisk → compact → after snapshot → final result。每一阶段写 journal event，任何异常产生 summary。

- [ ] **Step 4：运行全量测试并提交**

~~~powershell
dotnet test .\Vela.sln -c Debug
git add .
git commit -m "feat: orchestrate VHDX compaction workflow"
~~~

## Chunk 5：质量 gate、发布与交接

### Task 12：建立覆盖率、静态质量与 locked restore gate

**文件：**

- 创建：.editorconfig
- 创建或修改：tests\Vela.Tests\GlobalUsings.cs
- 修改：README.md、Directory.Packages.props、tests\Vela.Tests\Vela.Tests.csproj

- [ ] **Step 1：添加覆盖率 gate**

测试项目引用 coverlet.msbuild（PrivateAssets=all）。用如下命令让 Vela.Core 与 Vela.Windows 任一 line coverage 低于 80% 时直接失败：

~~~powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release -p:CollectCoverage=true -p:CoverletOutput=.\artifacts\coverage\coverage -p:CoverletOutputFormat=cobertura -p:Include='[Vela.Core]*,[Vela.Windows]*' -p:ExcludeByFile='**/Program.cs' -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=minimum
~~~

- [ ] **Step 2：设置静态质量**

.editorconfig 设定 C# 格式、命名与换行规则。Review 每个进程启动、request 持久化、路径验证、日志序列化和 UI 输入边界；所有异常进入 run journal。

- [ ] **Step 3：验证锁定依赖**

~~~powershell
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Release
dotnet test .\Vela.sln -c Release
~~~

- [ ] **Step 4：提交**

~~~powershell
git add .
git commit -m "test: add Vela quality gates"
~~~

### Task 13：发布 self-contained 单文件 EXE

**文件：**

- 创建：src\Vela.Tui\Properties\PublishProfiles\win-x64-singlefile.pubxml
- 修改：README.md、testing-and-release.md
- 创建：D:\DevTools\Vela\README.md、D:\DevTools\Vela\logs-link.txt

- [ ] **Step 1：写 publish profile**

~~~xml
<Project>
  <PropertyGroup>
    <AssemblyName>Vela</AssemblyName>
    <TargetFramework>net9.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <PublishTrimmed>false</PublishTrimmed>
    <DebugType>embedded</DebugType>
  </PropertyGroup>
</Project>
~~~

- [ ] **Step 2：通过 profile 发布**

~~~powershell
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet test .\Vela.sln -c Release --no-restore
dotnet publish .\src\Vela.Tui\Vela.Tui.csproj -c Release --no-restore -p:PublishProfile=win-x64-singlefile -o .\artifacts\publish\win-x64
~~~

- [ ] **Step 3：在发布物上完成只读预检验收**

从 Windows Terminal 启动 artifacts\publish\win-x64\Vela.exe，选择预检，将 inventory 与 summary 字段同 legacy\powershell\wsl.ps1 -WhatIf 的结果对照。

- [ ] **Step 4：确认后创建稳定发布目录并复制交付物**

先向用户展示：

~~~text
目的：交付 Vela 日常启动入口
完整路径：D:\DevTools\Vela
创建 / 覆盖文件：Vela.exe、README.md、logs-link.txt
~~~

获得确认后执行：

~~~powershell
New-Item -ItemType Directory -Force 'D:\DevTools\Vela' | Out-Null
Copy-Item '.\artifacts\publish\win-x64\Vela.exe' 'D:\DevTools\Vela\Vela.exe' -Force
Set-Content -Path 'D:\DevTools\Vela\logs-link.txt' -Value '运行日志：%LocalAppData%\Vela\logs\<RunId>\'
~~~

将根 README 的日常启动、菜单键位、日志位置、结果类别和排障入口复制或精简为 D:\DevTools\Vela\README.md。

- [ ] **Step 5：最终人工验收**

按 testing-and-release.md 检查日志、档案编辑、预检和 UAC 进度。最终动作阶段是独立人工验收项，由用户阅读影响面板后自行确认。

- [ ] **Step 6：最终提交与 tag**

~~~powershell
git add .
git commit -m "feat: release Vela single-file TUI"
git tag v1.0.0
~~~

## Definition of Done

- Vela.sln 在 Visual Studio 2022 与 Developer PowerShell 构建为零警告。
- dotnet restore --locked-mode、Debug/Release build、全量 xUnit 与 80% coverage gate 都通过。
- 预检生成正确的 profile mapping、VHDX/drive snapshot、WSL inventory、事件流、易读日志和 JSON summary。
- 确认屏幕展示精确目标与 shutdown scope。
- UAC worker 严格验证 RunId、管理员身份、AppPaths、注册表映射和请求路径，并提供实时持久事件与最终退出结果。
- Vela.exe 是 self-contained 单一 win-x64 文件，发布到 D:\DevTools\Vela。
- legacy\powershell 保留旧 wsl.ps1、README 和两份验证脚本，桌面旧目录移除进入最终迁移检查表。
