# WSL2-VHDX-Compact

关闭 WSL 后压缩指定发行版的 `ext4.vhdx`，释放宿主机磁盘空间。

## 当前能力

- 默认先校验 WSL 发行版与 VHDX 的注册表映射，避免选错磁盘。
- 在任何关闭操作前校验 VHDX 路径是否可被 DiskPart 稳定解析。
- 支持全局关闭 WSL，或只终止指定发行版。
- 轮询 WSL 停止状态，确认文件句柄已释放后才调用 DiskPart。
- 调用 Windows 原生 `diskpart compact vdisk`，兼容未安装 Hyper-V 模块的系统。
- 默认要求输入 `YES`；`-Force` 才跳过该确认。
- 提供 `-WhatIf` 预检：不关闭 WSL、不调用 DiskPart；仍会写入诊断日志和 JSON 摘要。
- 每次运行生成详细文本日志和 JSON 摘要，记录命令输出、退出码、VHDX/磁盘前后状态和异常详情。

## 当前默认目标

```powershell
$Distro = 'Ubuntu-24.04'
$Vhdx = 'D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx'
$ShutdownMode = 'Global'
```

## 环境要求

- Windows 10/11
- PowerShell 7+
- WSL 2
- 实际压缩时需要管理员权限；脚本会自动请求 UAC 提权

## 使用方式

先执行只读预检：

```powershell
pwsh -File .\wsl.ps1 -WhatIf
```

验证脚本语法和 `-WhatIf` 的只读契约：

```powershell
pwsh -ExecutionPolicy Bypass -File .\tests\Verify-WhatIf.ps1
pwsh -ExecutionPolicy Bypass -File .\tests\Verify-RelaunchArguments.ps1 # 不触发 UAC、WSL 或 DiskPart
```

执行默认压缩：

```powershell
pwsh -File .\wsl.ps1
```

常用参数：

```powershell
# 仅终止目标发行版。目标 VHDX 若仍被其他组件占用，Global 更可靠。
pwsh -File .\wsl.ps1 -ShutdownMode Distro -Distro Ubuntu-24.04

# 自定义 VHDX 和日志目录。
pwsh -File .\wsl.ps1 `
  -Vhdx 'D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx' `
  -LogDir 'D:\Logs\WSL2-VHDX-Compact'

# 调整关机等待上限。
pwsh -File .\wsl.ps1 -ShutdownTimeoutSeconds 90

# 跳过 YES 确认，适合已完成预检后的自动化任务。
pwsh -File .\wsl.ps1 -Force
```

`-AllowVhdxMismatch` 仅供明确知道目标发行版与传入 VHDX 不同的场景使用；正常运行保持默认校验。

## 日志与排障

默认日志目录：

```text
.\logs
```

每次运行会产生：

```text
wsl-compact-yyyyMMdd-HHmmss.log
wsl-compact-yyyyMMdd-HHmmss.summary.json
```

文本日志记录：

- 运行参数、PowerShell 版本、管理员状态与 WSL 版本
- 发行版/VHDX 映射校验
- 运行中的 WSL 发行版和关闭轮询过程
- 每个 `wsl.exe`、`fsutil.exe`、`diskpart.exe` 调用的参数、输出、耗时、退出码
- VHDX 文件长度、稀疏标志、目标盘可用空间的前后对比
- DiskPart 预检和完整异常信息

JSON 摘要便于后续机器处理或对比多次运行结果。

## 行为边界

- `Global` 会关闭全部 WSL 实例，包括 Docker Desktop、Podman 服务和其他 WSL 终端。
- 脚本只执行 WSL 关闭和 Windows VHDX 压缩；它不删除 Linux 文件，也不运行 Linux 文件系统 TRIM。
- `compact vdisk` 要求动态 VHDX 已分离或以只读方式附加；脚本关闭 WSL 后记录 DiskPart 预检输出，供排查文件占用和 VHDX 状态使用。
- DiskPart 完成但 VHDX 大小不变是可能结果，表示当前没有可回收块；日志会明确标记该状态。
- `.wslconfig` 的 `sparseVhd=true` 默认仅影响新建 VHDX。已有 VHDX 仍可使用本脚本进行离线压缩。

## 文件说明

```text
wsl.ps1              主脚本
tests/               `-WhatIf` 只读冒烟验证
```

运行时会在 `-LogDir`（默认为脚本同级的 `logs/`）下生成日志与 JSON 摘要；该目录不纳入版本控制。
