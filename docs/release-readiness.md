# Vela 发布准备清单

这份清单记录公开仓库、真实 TUI 展示和首个二进制 Release 之间还剩的工作。当前源码已经推送到 `origin/main`；下面的状态以仓库现状为准。

## 已完成

- [x] README 顶部 Hero / Demo 排版。
- [x] Product Tour 双列截图与宽幅 Console Log 截图。
- [x] 当前 Release TUI 的真实只读截图。
- [x] 真实截图序列 Demo GIF。
- [x] 目标选择、目标锁定、预检、影响评估、日志归档和预计可回收空间的文档说明。

## 首个 Release 前必须完成

- [x] 采集真实的只读交互 GIF 素材：实例列表 → Enter → 预检详情 → 影响评估 → 日志归档。
- [x] 捕获真实影响评估页，展示目标发行版、当前体积、访客已用空间和“预计可回收空间”数值。
- [x] 捕获第二次 `Y` 确认页，确认页面只展示当前锁定目标和影响范围。
- [x] 导出当前 Release 的运行进度页和完成结果状态帧；真实物理压缩验收另列为独立事项。
- [ ] 完成一次真实物理压缩的 Win11 / WSL 人工验收，并补充成功态截图。
- [ ] 创建第一个 GitHub Release，上传 self-contained `Vela.exe`。
- [ ] 为发布物记录 SHA256，并在 README 提供下载入口。
- [x] 让 Windows CI 完成 restore、Release build、全量 test 和 coverage gate。
- [x] 确定并提交仓库开源许可文件。

## 仓库质量补齐

- [x] README 提供源码运行、发布构建、日志位置和键盘交互说明。
- [x] 真实截图不再拼成单张产品大图；每个展示素材可单独替换。
- [x] 更新当前使用文档中的 SDK / TFM 与项目基线，并标记历史实施计划。
- [x] 为贡献流程提供构建、测试、只读 TUI 验收和提交规范。
- [x] 为安全问题提供私下报告入口和最小复现信息模板。
- [x] 添加 CI 状态徽章，并在 README 标明当前 public preview 与 Release 状态。

## 截图采集边界

截图使用 Win11 Release 构建和固定终端画布。只读素材允许浏览实例、预检、影响评估、日志和历史记录；执行态素材需要单独确认目标、影响范围和两次 `Y`，不把执行操作放进自动化 README 采集流程。

每一组截图需要同时记录：

```text
构建：Release / Vela.Tui.dll
画布：178 × 42 或发布验收时记录的实际尺寸
目标：当前锁定的发行版
路径：截图覆盖的 TUI 页面顺序
状态：真实 PASS / BLOCKED / RUNNING / COMPLETED 结果
```

## 发布验证命令

在 Windows Developer PowerShell 中运行：

```powershell
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Release --no-restore
dotnet test .\Vela.sln -c Release --no-build --no-restore
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore -p:CollectCoverage=true -p:CoverletOutput=.\..\..\artifacts\coverage\coverage -p:CoverletOutputFormat=cobertura -p:Include="[Vela.Core]*%2C[Vela.Windows]*" -p:ExcludeByFile="**/Program.cs"
pwsh -NoProfile -File .\scripts\Verify-Coverage.ps1
```
