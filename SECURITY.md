# Security Policy

Vela 处理 WSL 发行版映射、VHDX 路径、提升权限 worker 和本地运行日志。安全问题请避免公开粘贴真实用户名、VHDX 路径、RunId、日志内容或主机信息。

## 报告方式

优先使用 GitHub 仓库的 **Private vulnerability reporting / Security Advisories** 创建私下报告。仓库启用该功能后，入口位于：

`https://github.com/xianyudd/Vela/security/advisories`

如果入口暂时不可用，请先创建不含敏感数据的 issue，仅描述“安全报告需要私下沟通”，不要公开漏洞细节。

## 报告内容

请提供：

- 受影响的 commit、版本或构建方式
- Windows / WSL / .NET 环境
- 可重复的最小步骤
- 预期结果与实际结果
- 是否涉及目标错配、权限提升、路径穿越、日志泄露或 worker 并发
- 已脱敏的日志片段或测试样例

## 当前安全边界

- 预检路径只采集证据，不触发 WSL 停止或 DiskPart。
- worker 会重新解析目标映射，并将解析路径与请求路径进行严格比对。
- 原始路径、RunId、异常堆栈和 native output 保留在受控日志，不进入普通 TUI 展示。
- 不要在 issue、PR 或 README 中提交真实凭据、令牌、私钥或完整主机日志。
