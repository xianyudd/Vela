# Vela 文档索引

Vela 是一个面向 Win11 的单入口终端界面工具。它盘点 WSL 发行版与其 VHDX 状态，并在用户完成明确确认后，由提升权限 worker 执行 VHDX 压缩工作流。

## 文档体系一览

全部 18 份 markdown 按可信度分三层。`[现]` 是当前事实来源，断言均可回溯到代码或配置；`[证]` 是时点验收证据；`[史]` 是历史决策记录，不描述现状。行数便于判断篇幅，不随小改动更新。

~~~text
Vela/
├─ README.md                        380  [现] 产品说明、键位表、结构与门禁总览
├─ AGENTS.md                        125  [现] agent 开工必读；覆盖率门槛等硬约束
├─ CONTRIBUTING.md                   46  [现] TDD 顺序、TUI 验收清单、PR 约定
├─ SECURITY.md                       29  [现] 报告入口、脱敏要求、安全边界
│
├─ docs/
│  ├─ README.md                     123  [现] 本文；文档索引与阅读顺序
│  ├─ architecture.md               393  [现] 分层、数据流、worker 协议、日志
│  ├─ testing-and-release.md        266  [现] 测试矩阵、覆盖率 gate；第 8 节是
│  │                                     每次发布逐条执行的交付检查表模板
│  ├─ development-environment.md    206  [现] VS 2022 / SDK / 目录与写入边界
│  ├─ agent-handoff.md               81  [现] 实施边界与强制架构约束
│  ├─ release-readiness.md           62  [现] 发布待办清单；v1.0.0 已发布，
│  │                                     仅剩物理压缩成功态截图待补
│  ├─ implementation-plan.md        662  [史] 首版实施清单；61 个空复选框非待办
│  │
│  ├─ testing/
│  │  ├─ menu-01-preflight.tdd.md    37  [证] 菜单 01 预检 TDD 证据
│  │  └─ tui-readonly.tdd.md         20  [证] TUI 只读迁移 TDD 与 tmux 验收
│  │
│  ├─ superpowers/
│  │  ├─ plans/
│  │  │  └─ 2026-08-13-model-first-tui-refactor.md
│  │  │                            1328  [史] 已完成；145 个空复选框非待办
│  │  └─ specs/
│  │     └─ 2026-08-13-architecture-remediation-design.md
│  │                                 563  [史] 架构整改设计；「当前」指 08-13
│  │
│  ├─ assets/tui/                        19 张截图 + 1 个只读 Demo GIF
│  └─ releases/
│     ├─ v1.0.0/
│     │  ├─ RELEASE_NOTES.md         34  [证] 正式版发布说明
│     │  └─ SHA256SUMS.txt               对应 tag 发布物，非本地构建产物
│     └─ v0.1.0-preview.1/
│        ├─ RELEASE_NOTES.md         24  [证] preview 发布说明
│        └─ SHA256SUMS.txt               对应 tag 发布物，非本地构建产物
│
└─ legacy/powershell/README.md      113  [史] 旧 PowerShell 工具行为对照
~~~

三层的实际差别在于「读到不一致时该改哪边」：`[现]` 与代码不符是文档 bug，要改文档；`[证]` 与代码不符通常正常，它记录的是当时；`[史]` 与代码不符也正常，但要留着。

复选框在本仓库有三种含义，不能一律当待办：`release-readiness.md` 的是真待办；`testing-and-release.md` 第 8 节的是每次发布重新走一遍的模板；两份 `[史]` 计划里合计 206 个空框是执行时没回勾的残留，两份都已标注**不要执行**。

上面的树覆盖全部 17 份，下面三张表只索引 `docs/` 下的文档。根级 4 份和 `legacy/powershell/README.md` 只出现在树里。

### 当前手册

| 文档 | 用途 |
| --- | --- |
| [development-environment.md](development-environment.md) | 已验证的 Win11 / Visual Studio / .NET 环境、依赖版本、VS 启动调试与目录约定。 |
| [architecture.md](architecture.md) | 产品范围、TUI、分层、数据流、UAC worker、日志与原生命令设计。 |
| [testing-and-release.md](testing-and-release.md) | 自动化测试、覆盖率 gate、人工验收、单文件发布与交付清单。 |
| [release-readiness.md](release-readiness.md) | 公开仓库、真实截图、CI 和 Release 的交付清单；v1.0.0 已全部完成。 |
| [agent-handoff.md](agent-handoff.md) | 实施边界、写入范围与强制架构约束。 |

### 验收证据

按时点记录，不随代码自动更新；其中的测试计数与覆盖率数字属于当次运行快照。

| 文档 | 用途 |
| --- | --- |
| [testing/menu-01-preflight.tdd.md](testing/menu-01-preflight.tdd.md) | 菜单 01 实例选择与只读预检的 TDD 证据。 |
| [testing/tui-readonly.tdd.md](testing/tui-readonly.tdd.md) | TUI 只读迁移的 TDD 证据与 tmux 验收记录。 |

### 历史记录

保留决策依据，不作为当前事实来源。

| 文档 | 用途 |
| --- | --- |
| [implementation-plan.md](implementation-plan.md) | 首版逐步实施清单；以 .NET 9 与四项目结构为基线，均已演进。**不要执行**，未勾选的步骤不是待办。 |
| [superpowers/plans/2026-08-13-model-first-tui-refactor.md](superpowers/plans/2026-08-13-model-first-tui-refactor.md) | model-first TUI 重构计划，`Vela.Application` 分层的决策记录。已完成，**不要执行**。 |
| [superpowers/specs/2026-08-13-architecture-remediation-design.md](superpowers/specs/2026-08-13-architecture-remediation-design.md) | 架构整改设计说明。 |

### 素材与发布物

- `assets/tui/` — 9 组真实 Release TUI 素材，共 19 张截图与 1 个只读 Demo GIF。每组含 `-focus` / `-live` / 无后缀等变体；README 每组只引用一个，其余作为替换备选保留。
- `releases/v0.1.0-preview.1/` — 该版本的 `RELEASE_NOTES.md` 与 `SHA256SUMS.txt`。

## 当前基线与目标路径

~~~text
当前源码基线
D:\Jason\Documents\Workspace\vs2022\repo\Vela
├─ Vela.sln
├─ src\                          # Vela.Core / Vela.Application / Vela.Windows / Vela.Tui
├─ tests\                        # Vela.Tests
├─ docs\
├─ scripts\                      # 安装、覆盖率 gate、只读 TUI 验收
├─ legacy\powershell\            # 旧 PowerShell 工具行为对照
├─ artifacts\                    # 构建、测试与发布输出，Git 忽略
├─ .github\workflows\            # Windows CI
└─ .git\                         # Git 元数据

发布后的日常入口
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

需要追溯某次改动的验证过程时，再读 `testing/` 下的 TDD 证据和 `superpowers/` 下的计划；两者都是时点记录，不描述当前状态。`implementation-plan.md` 同理，作为历史实施记录保留。
