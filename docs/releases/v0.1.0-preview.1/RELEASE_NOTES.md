# Vela v0.1.0-preview.1

Vela 的首个公开预览版，提供 Windows 11 / WSL2 下的键盘优先 VHDX 工作流。

## 本版内容

- 多实例发现、实例选择与目标锁定。
- 只读预检：映射、VHDX、运行状态、日志和阻断项。
- 压缩影响评估，展示当前体积、预计体积和具体的预计可回收空间。
- 两次 `Y` 确认，第二次确认页显示锁定目标与影响范围。
- UAC worker、单 worker gate、目标二次校验和 TUI 内日志归档。
- self-contained `win-x64` 单文件发布物。

## 下载与校验

下载 Release 资产中的 `Vela-v0.1.0-preview.1-win-x64.exe`，并使用同目录的 `SHA256SUMS.txt` 校验：

```powershell
Get-FileHash .\Vela-v0.1.0-preview.1-win-x64.exe -Algorithm SHA256
```

## 当前边界

本版本的 Product Tour 使用 Win11 / tmux 实机只读画面，以及同一 Release 渲染器的运行进度和结果状态帧。真实物理压缩应在明确目标、影响范围和两次 `Y` 后进行人工验收。
