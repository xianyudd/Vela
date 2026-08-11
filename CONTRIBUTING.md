# Contributing to Vela

感谢你关注 Vela。当前项目处于 public preview，贡献重点是可复现的 Windows TUI、预检证据、日志链路和测试质量。

## 开始前

- Windows 11
- .NET SDK 10.0.302（以 `global.json` 为准）
- Developer PowerShell for VS 2022 或 Windows Terminal
- WSL 目标仅用于人工验收；自动化测试使用 fake adapter 和项目内 `artifacts` 数据根

## 开发流程

1. 从 `main` 创建短生命周期分支。
2. 先补测试，再实现最小变更。
3. 保持目标锁定、预检只读、worker 二次校验和日志归档这些不变量。
4. 提交前运行：

   ```powershell
   dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
   dotnet build .\Vela.sln -c Release --no-restore
   dotnet test .\Vela.sln -c Release --no-build --no-restore
   ```

5. 变更 TUI 时，补充对应的窄屏、宽屏、Enter、Esc、R/r 和只读守卫验证。
6. 使用 Conventional Commits，例如 `fix: preserve locked compaction target`。

## TUI 与执行边界

- 预检路径只读取证据，不停止 WSL、不终止发行版、不调用 DiskPart。
- 截图和演示优先使用真实 Release 构建；不要用合成图片替代产品画面。
- 自动化测试不发起真实压缩。执行态人工验收必须先核对影响面板、目标和两次 `Y`。
- 不要把路径、RunId、异常堆栈或 native output 写入面向用户的展示投影。

## Pull Request 内容

PR 描述至少包含：

- 变更目标和影响范围
- 测试命令与结果
- TUI 变更的截图或录屏
- 是否影响日志格式、配置格式、发布参数或安全边界
- 未完成事项和后续清单
