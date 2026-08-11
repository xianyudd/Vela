# Vela 文档索引

Vela 是一个面向 Win11 的单入口终端界面工具。它盘点 WSL 发行版与其 VHDX 状态，并在用户完成明确确认后，由提升权限 worker 执行 VHDX 压缩工作流。

| 文档 | 用途 |
| --- | --- |
| [agent-handoff.md](agent-handoff.md) | 交给实施 agent 的起点、当前仓库状态、边界和完成顺序。 |
| [development-environment.md](development-environment.md) | 已验证的 Win11 / Visual Studio / .NET 环境，以及目录约定。 |
| [architecture.md](architecture.md) | 产品范围、TUI、分层、数据流、UAC worker、日志与原生命令设计。 |
| [implementation-plan.md](implementation-plan.md) | 从当前空白解决方案到可发布 EXE 的逐步 TDD 实施清单。 |
| [testing-and-release.md](testing-and-release.md) | 自动化测试、覆盖率 gate、人工验收、单文件发布与交付清单。 |
| [release-readiness.md](release-readiness.md) | 公开仓库、真实截图、CI 和首个 Release 的剩余清单。 |

## 当前基线与目标路径

~~~text
当前源码基线
D:\Jason\Documents\Workspace\vs2022\repo\Vela
├─ Vela.sln
├─ src\
├─ tests\
├─ docs\
├─ legacy\powershell\
└─ .git\                         # Git 元数据

发布后的日常入口（发布任务完成后使用）
D:\DevTools\Vela\Vela.exe

用户数据与运行记录
%LocalAppData%\Vela\
~~~

当前仓库已经包含源码、测试、legacy 归档、构建规则和 Git 历史。`implementation-plan.md` 是历史实施记录；当前发布前剩余事项以 [发布准备清单](release-readiness.md) 为准。

## 建议阅读顺序

1. [release-readiness.md](release-readiness.md)
2. [development-environment.md](development-environment.md)
3. [architecture.md](architecture.md)
4. [testing-and-release.md](testing-and-release.md)
5. [agent-handoff.md](agent-handoff.md)
6. [implementation-plan.md](implementation-plan.md)（历史记录）
