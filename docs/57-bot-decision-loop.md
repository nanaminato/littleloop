# 聊天 Bot 的决策与 Harness 协作

2026-11-20，用户明确要求 LittleLoop 持有目标和决策权，使用可替换的认知实现理解自然语言、安排工作和评价执行反馈。Codex App Server 与 DeepSeek ACP 提供执行会话。阶段 1 模拟工单仍是独立的实验基线。

## 修正前的执行链

普通私聊与项目任务原先从同一个收件箱进入两条路径。启用原生私聊时，`ChatGoalStore.ProcessGoalMessage` 将缺少显式项目命令的消息直接创建为聊天工作项。另一条路径通过 `GoalMessageRouter` 匹配命令、项目词汇和补充语法。普通工作的最终回复来自预选代理或文字模型。项目工作的最终状态来自开发适配器。数据库、适配器和固定路由共同决定了应该采取什么行动。

原有实现已经能保存用户消息、目标版本、执行租约、通知及私聊记忆。Codex 能建立和恢复 thread，DeepSeek 能建立和加载 ACP session。历史测试覆盖这些协议和旧工作流。[持续目标](48-chat-continuous-work.md) · [历史私聊开发实测](54-private-development-work.md)

## 使用统一入口

在启用 LittleLoop 的聊天中直接描述问题或目标。可以问“你觉得现在这个项目的问题在哪里？”，也可以交代“帮我优化一下记忆模块”。Bot 根据当前消息、同一会话的目标和记忆决定回复、委派或追问。执行期间继续发消息即可补充约束、暂停目标或提出独立问题。需要消除指代歧义时，可以引用任务消息。

Bot 开始工作时先说明计划，将指令交给选择的 Harness，保留当前目标和会话。收到回执后，Bot 可以要求同一会话修复失败的测试，也可以先让 DeepSeek 阅读，再让 Codex 按照分析修改。每次调用都有自己的身份，Harness 的回执、工具结果和 Bot 给用户的消息可分别追溯。

## 部署与认知替换

| 配置项 | 用途 |
| --- | --- |
| `ChatBot__Policy`、`ChatBot__Data`、`ChatBot__Operators` | 指定部署的 Codex 可执行文件和项目、目标会话数据目录、项目操作员账号。数据目录放在项目之外。使用主机器人时，无需配置项目审批人。好友接入的私聊沿用该用户自己的工作区。 |
| `ChatBot__DecisionModel` | 指定认知模型的 JSON 配置路径。可使用项目已有的 Chat Completions 或 Ollama 配置。凭证从配置指定的环境变量读取。旧部署可以沿用 `ChatBot__ConversationModel` 作为认知模型配置。 |
| `ChatBot__DeepSeek__Launcher`、`ChatBot__DeepSeek__Project` | 可选 ACP 进程与工作区。启动器的参数来自 `ChatBot__DeepSeek__Arguments__0` 等项。已有只读部署可沿用 `ReadOnlyLauncher`。Bot 可以在同一个目标中分别调度 Codex 和 DeepSeek。 |
| `ChatBot__MaxHarnessTurns` | 每个新目标可派发的 Harness 回合数，默认 8，范围 1 至 128。多个 Harness 共用此目标的预算。 |
| `ChatBot__AutoFinalReport` | 自动发送最终报告的默认值，初始为 `true`。用户可在会话中说“以后做完不用单独给我总结”，之后也可以改回来。 |

例如在 PowerShell 中设置 `$env:ChatBot__DecisionModel=(Resolve-Path examples/models/deepseek.json).Path`。启动服务前应确保配置中的模型名称和端点可用，以及相应的凭证已设置。认知模型和执行 Harness 分别配置，因此同一模型 API 可以给使用不同 Harness 的目标做决策。[模型配置样例](../examples/models/deepseek.json)

本地认知实现可以实现 `LittleLoop.Core.IBotDecisionEngine`，从 `BotDecisionContext` 读取事件、可用目标和相关记忆，再返回 `BotDecision`。例如，失败测试可来自 `ToolResult`，而 Harness 自己的文字结论来自 `HarnessReceipt`。测试中的替换点是聊天宿主构造时注入的决策引擎。[决策契约](../src/LittleLoop.Core/BotDecision.cs)

## 记忆和升级

可以直接说“今后汇报时给我一句摘要”，之后再说“把刚才的偏好改为分两段”或“忘记那条汇报习惯”。Bot 通过结构化的记忆动作保存、更正或删除同一用户、同一私聊中的记忆。更正和删除通过来源消息序号定位。已有私聊背景、条件经验和经验管理也可进入认知上下文。目标约束随目标保存，便于用户在执行中调整本次工作。

服务重启时，旧版排队的 LittleLoop 目标保留原 ID、目标及约束，进入重新规划。旧版排队的普通聊天工作项回到收件箱。旧命名项目机器人的排队任务会留下停派原因，用户可在主机器人中指定工作区继续。独立的源码审阅账号保留。安装新版本前，需先设置认知模型路径，或在宿主注册本地认知实现。

## 验收记录

SQLite 专项通过 54 项检查，经过真实消息入库、后台服务和通知发件箱。测试决策器控制每次结构化输出，受控 Harness 提供执行反馈。覆盖无前缀讨论、先发计划、失败测试后续接同一会话、执行中修订、暂停后恢复与取消、两个 Harness 接力、服务重启后替换决策器、关闭最终报告，以及 SQL 写入和外部回执之间的崩溃恢复。记忆检查追溯来源、更正与遗忘。本地记忆工具的回执进入认知引擎，由 Bot 发出唯一的用户回复。[聊天工作检查](../tests/LittleLoop.ChatChecks/WorkerChecks.cs) · [恢复检查](../tests/LittleLoop.ChatChecks/BotRecoveryChecks.cs)

```powershell
dotnet run --project tests/LittleLoop.ChatChecks -- --decision-loop
```

真实 HTTP 验收使用独立 SQLite、合成项目及 DeepSeek 认知 API。每一行均从聊天输入发起。下列记录位于运行机的 `data/direct-http`，每次运行都另存程序集哈希、聊天计划和最终结果。[真实 HTTP 驱动](../tests/ChatContinuousLive/direct_http_e2e.py)

| 实测场景 | 结果 | 本机运行目录 |
| --- | --- | --- |
| Codex 原生静态审阅 | Bot 直接回答普通算术问题。独立审阅请求先收到行动说明，随后 Codex 返回正确的边界值，Bot 再次决策并报告。 | `072d43f8` |
| DeepSeek ACP 项目检索 | 只读工具找到用户未提供的随机编号，工具记录和 Harness 回执进入同一目标，Bot 返回正确编号。 | `c769cdfc` |
| DeepSeek → Codex | DeepSeek 首先检索随机编号。Codex 随后根据接收到的编号核对后四位。两份原生回执属于同一个目标，最后的聊天摘要和编号一致。最终版的第二步明确要求基于输入字符串复核。 | `06dd030b`、`e166fa6a` |
| 好友工作区实际写入 | 独立好友账号经普通私聊交办写入。DeepSeek 在该账号的工作区写入 `smoke.txt` 并读取核对，测试进程从磁盘独立比对原文成功。最后一轮未配置项目审批人。 | `3681fce6`、`98340844`、`3b85f1a1` |
| Codex 项目文件复制 | 当前 Windows 环境的 Codex 命令运行服务启动失败，备用 PowerShell 遇到原生权限限制。Bot 转入等待并向用户说明文件尚未读写。此环境的 Codex 文件复制仍缺少成功验收。 | `864b195e` |
| 接力时的模型格式错误 | ACP 检索成功后，认知模型返回的 Harness 字段未匹配已注册 ID。宿主没有派发无效指令，目标进入等待。随后的实现向认知模型明确列出允许的 ID，并在名称唯一时容忍大小写和首尾空格。 | `3b995734` |
| 接力时增加磁盘复核 | 两个原生 Harness 均已收到指令。Codex 尝试在磁盘上独立复核，命令运行服务不可用。Bot 记录了已有编号和验证受阻的原因，目标进入等待。 | `ba6e1ada` |

对照检查从 SQLite 读取目标的 `Complete` 决策并定位其发出的聊天消息。这样可以区分中途进度和最终报告。对于接力任务，还逐项核对两次原生回执中的目标 ID、完整编号，以及 Codex 单独列出的后四位。这些运行记录说明对应试次的结果，持续任务成功率仍需更多样本。

现有配置安装了相应原生 Harness 后，可用 `python tests/ChatContinuousLive/direct_http_e2e.py --decision-loop review` 复核 Codex 审阅。`search`、`handoff`、`private-copy` 分别复核 ACP 检索、协作及私聊写入。驱动从 `LITTLELOOP_CODEX_EXECUTABLE` 读取 Codex 路径，从 `LITTLELOOP_DECISION_MODEL` 读取模型配置路径，默认使用仓库内的 DeepSeek 配置。ACP 场景沿用调用方的 `ChatBot__DeepSeek__*` 配置并将项目路径指向本次合成项目。
