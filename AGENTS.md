# Vela 项目级 Agent 规范

## 适用范围

本文件适用于 Vela 仓库及其所有子目录。开始任务前先阅读本文件、`README.md` 和 `CONTRIBUTING.md`。

本规范的作用域仅限当前仓库；全局 Agent 配置、用户目录配置和其他项目保持原样。

## 项目背景

- 项目：Vela，Windows 11 / WSL2 / VHDX 压缩工作流 TUI。
- 技术栈：C#、.NET 10、Terminal.Gui、Windows 原生工具。
- 默认构建目标：`win-x64`。
- 用户界面：键盘优先、等宽字体、中文界面、TUI 内查看运行日志。

## 开发原则

- 先理解现有数据流和状态机，再修改实现。
- 优先补充测试，再实现最小变更。
- 保持目标锁定、只读预检、影响评估、二次确认、worker 二次校验和日志归档这些不变量。
- Core 层保持平台无关；Windows 原生能力放在 `Vela.Windows`。
- TUI 展示层只消费经过边界清洗和长度限制的数据。
- 核心模型优先使用不可变记录；Terminal.Gui 控件状态可通过既有视图 API 更新。

## C# 与格式

- 遵循根目录 `.editorconfig`。
- 使用 4 个空格缩进、UTF-8、LF 和文件级命名空间。
- 保持 Nullable 开启，并让分析器警告继续参与构建。
- 公共方法和类型使用明确的可访问性修饰符。
- 优先使用现有项目模式，避免为简单问题引入新框架或依赖。

## 注释与文档规范

### 公共 API

公共类型和公共成员使用 C# XML 文档注释：

- `<summary>`：说明职责和主要行为。
- `<param>`：说明参数含义和边界。
- `<returns>`：说明返回值语义；异步方法描述最终结果，而不是 `Task` 本身。
- `<exception>`：说明属于 API 契约的异常。
- `<remarks>`：说明副作用、不变量、线程或生命周期约束。
- `<inheritdoc />`：复用接口或基类文档，避免重复内容。

XML 文档使用完整句子，并以句号结尾。需要引用类型、成员或参数时使用 `<see cref="..." />`、`<paramref name="..." />`。

示例：

```csharp
/// <summary>
/// Estimates the space that can be reclaimed from the selected VHDX.
/// </summary>
/// <param name="distroName">The selected WSL distribution name.</param>
/// <param name="vhdxPath">The absolute path to the target VHDX file.</param>
/// <returns>A read-only estimate of the reclaimable space.</returns>
/// <remarks>
/// The estimate does not start WSL or invoke DiskPart.
/// </remarks>
public Task<CompactionImpactEstimate> EstimateAsync(
    string distroName,
    string vhdxPath,
    CancellationToken cancellationToken);
```

### 普通注释

普通 `//` 注释只解释代码之外的信息：

- 为什么采用当前实现。
- 状态机、并发或生命周期约束。
- 外部副作用和执行边界。
- 安全校验、降级路径和异常处理原因。

逐行翻译代码会增加噪声；命名不清或结构复杂时应优先改善代码本身。

### 注释语言

用户界面文案一律使用中文。注释语言按位置区分：

- `src/` 下的注释使用英文。唯一例外是**逐字引用**中文字面量时——例如格式化函数返回的 `"未知"`，或 diskpart 输出里要匹配的 `"成功"` 标记；引用之外的叙述仍用英文。
- `tests/` 下的注释**随文件**：沿用所在文件既有的语言，不在同一文件里混用。中文注释已在 `tests/Vela.Tests/Windows/` 的多数文件以及少数其他测试文件中确立，其余测试文件为英文。

判断依据是文件现状，而不是目录名——`tests/Vela.Tests/Windows/` 里中文注释和英文注释的文件并存。续写既有文件时以该文件为准；新建文件时参照同目录中最贴近的同类文件。

## TUI 规则

- 菜单 01 负责实例选择和目标级只读预检。
- 选中的目标必须贯穿预检、影响评估、确认和执行链路。
- `Enter` 用于确认当前步骤；`Esc` 返回上一步；`R` 和 `r` 都触发只读重扫；左右方向键切换工作流步骤；`Tab` 在工作区首页切换导航栏与主列表焦点；`1` 和 `2` 直接跳转对应模块。
- 执行压缩前必须显示具体的预计可回收空间、停止范围和目标 VHDX 摘要。
- 压缩流程使用两次 `Y/y` 确认；运行期间锁定导航输入。
- 日志必须在 Vela TUI 内查看，不通过打开日志目录替代日志页面。
- 视图必须覆盖 `160×45`、`120×35`、`100×30`、`80×24` 和 `60×16` 等参考尺寸。

## 执行边界

- 预检路径只读取证据，不停止 WSL、不终止发行版、不调用 DiskPart。
- 自动化测试使用 fake adapter 和项目内测试数据，避免触发真实压缩。
- 真实 WSL 或 DiskPart 操作前，先核对目标、VHDX、停止范围和影响面板。
- worker 必须重新读取关键映射并校验请求目标，父进程快照只能作为上下文。
- 原始路径、RunId、异常堆栈和 native output 只进入受信任日志，不进入普通用户展示投影。

## 测试与验证

提交前运行：

```powershell
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Release --no-restore
dotnet test .\Vela.sln -c Release --no-build --no-restore
```

变更 TUI 时，补充对应的宽屏、窄屏、Enter、Esc、R/r、左右方向键和只读守卫测试。

变更执行流程时，补充成功、超时、DiskPart 阶段、取消、日志终态和目标隔离测试。

覆盖率门槛：`Vela.Core`、`Vela.Application`、`Vela.Windows`、`Vela.Tui` 四个程序集的 line coverage 均保持在 80% 以上。覆盖率过滤必须同时包含这四个程序集，否则 `scripts/Verify-Coverage.ps1` 会因缺少 package 而失败。

## Git 与交付

- 使用 Conventional Commits，例如 `fix: preserve locked compaction target`。
- 提交前检查 `git diff`、`git status` 和测试结果。
- 不提交 `artifacts/`、临时截图、运行日志、本地配置和个人环境文件。
- 任何 README 截图必须来自真实 Release TUI；图片保持独立文件，避免把多个画面合成为一张不可维护的大图。
