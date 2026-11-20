# 自主认知 Bot：架构差距与本轮增量

2026-11-20。用户确认的方向是让 LittleLoop 持有目标和决策权，逐步以自身的记忆、知识、推理和学习能力承担认知工作。下面的优先级、实现方式与验收样例是本轮工程判断。

## 起点与优先级

第一阶段的模拟工单支持离线重放。主聊天另有一条已运行的持续工作路径：用户消息或执行回执进入 `IBotDecisionEngine`，宿主检查目标版本、权限和预算，在事务中更新目标、下一动作和通知。原生 Harness 按派发 ID 记账。先前的独立 HTTP 试次已验证 DeepSeek 检索后交给 Codex 复核，也记录过 Windows 命令执行失败。符号审议、经验学习有独立实验，尚未证明聊天 Bot 具备开放领域自主学习能力。[设计基线](06-design-baseline.md) · [主聊天闭环与历史验收](57-bot-decision-loop.md) · [持续主体路线](47-continuous-agent-goals.md)

| 优先级与问题 | 已有实现及差距 | 本轮增量与独立验收 | 对自主认知的意义 |
| --- | --- | --- | --- |
| P0 哪些判断可在本地做？ | 可注入决策引擎，但生产装配对每个事件调用模型。已提交的本地记忆操作结果、执行结果未知时的待核对状态均来自宿主。 | 在同一入口组合可放弃判断的本地策略和现有语义适配器。用模型调用次数、动作和持久记录核对本地覆盖范围。普通语言请求仍由配置的认知实现理解。 | 可以逐项测量一部分职责迁回本地的效果，避免用词表分派意图。 |
| P1 如何判断一轮工作做到了什么？ | `SaveHarnessReceipt` 保存报告与工具事件。当前认知投影缺少派发与工具的结构化标识，一轮可记录 64 个工具结果，而聊天历史只取 32 个事件。 | 在回执和工具观察中保留派发关联，随回执提供有界的失败证据与覆盖信息。用同名工具跨轮出现、早期失败被历史挤出、旧记录缺字段三种情况验收。 | 本地推理与模型能核对相同来源，失败是否影响目标达成留给 Bot 判断。 |
| P2 换一个执行者后能否安全续接？ | 数据库已持久化在途派发，原生适配器有提交前日志和最终回执。重启代码直接读取原生日志，无法解释其他适配器的持久状态。 | 通过 Harness 契约查询某次派发的恢复结果，区分已返回、可证明未发送、执行结果未知。验证重启后已提交的自定义 Harness 调用不会再发，移除适配器后仍能核对既有原生记录。 | 执行器可更换；恢复所得事实与正常回执一样进入 Bot 的下一轮决策。 |

## 六个参考项目的取舍

| 参考机制及一手来源 | LittleLoop 采用或保留的边界 |
| --- | --- |
| OpenAI Dots 让用户设置持续目标，在对话之间推进，允许用户暂停、补充方向。系统卡还披露了按时间预算评估持续工作的方式。[产品说明](https://openai.com/index/introducing-dots/) · [系统卡中关于 Dots 的说明](https://deploymentsafety.openai.com/gpt-6-astra/sec%3Aappendix-210198) | 用目标连续性、可暂停和预算作验收维度。公开资料支持这些行为，无法据此推出内部调度算法。保留 LittleLoop 的事件唤醒与目标账本。 |
| OpenClaw 的 Gateway 汇集不同渠道；模型提供方、工具插件和 Harness 可独立注册，`supports` 与 `runAttempt` 表达适用性和单轮执行。[Gateway](https://docs.openclaw.ai/concepts/architecture) · [插件](https://docs.openclaw.ai/plugins/architecture) · [Harness 注册](https://docs.openclaw.ai/plugins/sdk-agent-harness/registration) | P0、P2 沿用 .NET 接口分开认知与执行。新增渠道可复用会话授权和收件箱；现阶段没有创建第二个 Gateway 的需求。 |
| MemGPT 将有限的活跃上下文与外部存储分层；Letta 的 memory blocks 支持更新与只读，archival memory 支持按需召回。[论文](https://arxiv.org/abs/2310.08560) · [Memory blocks](https://docs.letta.com/v1-sdk/memory/memory-blocks) · [Archival memory](https://docs.letta.com/v1-sdk/memory/archival-memory) | 现有 S3 已保存教导来源，支持更正、撤回与权限过滤。P0 直接使用已提交操作的结果。后续可把字符串记忆投影改成带来源的条目，以留出任务比较召回质量与成本，保留宿主写入校验。[现有记忆评测](56-chat-long-term-memory.md) |
| OpenHands 的本地与远程 workspace 封装执行环境，`ConversationState.get_unmatched_actions` 用动作或工具调用标识关联观察和错误。[工作区接口](https://github.com/OpenHands/software-agent-sdk/blob/main/openhands-sdk/openhands/sdk/workspace/__init__.py) · [会话状态源码](https://github.com/OpenHands/software-agent-sdk/blob/main/openhands-sdk/openhands/sdk/conversation/state.py) | P1 采用可关联的事件。工具报告与目标是否完成是两项判断，后者归 LittleLoop；计算机操作仍由已授权的 Harness 执行。 |
| LangGraph 以 `thread_id` 定位检查点，按 super-step 保存状态，用 pending writes 保留同一步已成功任务的写入。[Checkpointer 协议](https://docs.langchain.com/oss/python/langgraph/checkpointers#super-steps) | P2 复用派发日志、目标版本和 SQL 事务。恢复时根据已持久化的事实继续，下一动作依然由认知接口选择。 |
| AutoGen Core 区分定向请求应答和主题发布，`MessageContext` 含 sender、topic_id、message_id 等关联信息。[通信协议](https://microsoft.github.io/autogen/stable/user-guide/core-user-guide/framework/message-and-communication.html) · [MessageContext 源码](https://github.com/microsoft/autogen/blob/main/python/packages/autogen-core/src/autogen_core/_message_context.py) | P1 让交接双方引用同一轮证据，沿用现有 Bot 选择角色和执行者。将来增加审阅角色时可用事件驱动，毋须引入群聊管理器。 |

主动定时复查和跨会话学习保留为后续实验。前者需用“到期只唤醒一次、暂停后不推进”验收，后者需在未见任务上比较收益与退化。本轮优先打通可替换认知、可见反馈及恢复后的继续决策。

## 已交付与验收

| 提交 | 可观察行为 | 验收结果 |
| --- | --- | --- |
| P0 `e4c1dbf` | `LocalBotCognition` 按事件来源和目标状态决定能否本地处理。宿主将决策来源写入 `chat_bot_events.detail.cognitionSource`。旧引擎仍可只实现 `DecideAsync`。 | 两类有充分依据的反馈均触发零次后备模型调用；将同样文字作为用户消息时会交给后备引擎。独立检出运行 SQLite 决策专项，54 项通过。[本地认知](../src/LittleLoop.Core/LocalBotCognition.cs) · [核心对照用例](../tests/LittleLoop.Checks/BotDecisionChecks.cs) |
| P1 `d55128c` | 委派、工具观察和回执共享 `DispatchId`。回执携带工具最终上报状态的计数、首个已记录失败以及首个超额失败样例。原生适配器记录最多 64 个调用；旧记录和未声明覆盖范围的适配器保留未知值。 | 一次委派的 41 个调用中，第一项失败、后 40 项完成，Bot 在 32 条历史以外仍拿到失败详情。下一轮复用同一工具 ID，可按派发区分。独立检出的 SQLite 专项 55 项和原生协议夹具 13 项均通过。[工作循环](../tests/LittleLoop.ChatChecks/WorkerChecks.cs) · [原生边界用例](../tests/LittleLoop.ChatChecks/NativeHarnessChecks.cs) |
| P2 `b0c5506` | 领取派发的事务保存恢复来源。宿主用原派发 ID 查询执行器，已有回执直接结算；适配器明确证明未发送才可续发。已领取的原生派发缺日志、来源不匹配或发送后抛错时，目标转为待核对。移除适配器后仍可读取旧原生日志。 | SQLite 后台恢复用例覆盖独立远端账本、同名替换、已移除适配器、日志丢失和发送后断线。核对了实际发送次数、一次预算扣减、唯一回执及后续目标状态。最终决策专项 81 项通过。[恢复契约](../src/LittleLoop.Chat/Bot/BotHarness.cs) · [真实后台与受控执行器用例](../tests/LittleLoop.ChatChecks/HarnessRecoveryChecks.cs) |

默认服务配置仍需一个语义模型来处理开放式请求。本地接管的是两项可由已知证据支持的职责。工具失败数和截断标记是供下一次判断使用的证据；例如某条命令失败后可能已有另一条命令成功修复。旧事件缺失的派发 ID 与覆盖情况保持未知。

在仓库根目录使用 .NET 10 可复验。核心回归、最终 SQLite 聊天回归（289 项）和原生协议检查（13 项）均通过。专项与完整回归有重叠。

```powershell
dotnet run --project tests/LittleLoop.Checks
dotnet run --project tests/LittleLoop.ChatChecks -- --decision-loop
dotnet run --project tests/LittleLoop.ChatChecks -- --harness
dotnet run --project tests/LittleLoop.ChatChecks
```

### 最终构建的真实 HTTP 试次

三个试次分别创建临时 SQLite、账号和合成工作区，使用同一 Chat 程序集（SHA-256 前 12 位 `08943947b232`）。本机的 `data/direct-http/<试次>/result.json` 保存完整程序集哈希及断言。可用 `python tests/ChatContinuousLive/direct_http_e2e.py --decision-loop <场景>` 复验，模型与原生启动器配置见[部署说明](57-bot-decision-loop.md)。

| 场景 / 试次 | 从用户输入到最终回复的核对 |
| --- | --- |
| `memory-result` / `7b9172ed` | 新账号询问“查看偏好”。`deepseek-flash` 选择查询，`local/memory-result/1` 读取已提交回执；唯一一条聊天回复与查询原文一致。 |
| `review` / `3c314314` | Codex 静态审阅一行随机边界判断，定位错误分类的整数 1456。认知引擎收到同一派发的回执，再完成目标并给出相同答案。 |
| `handoff` / `485beb17` | DeepSeek ACP 在只读项目中找到随机编号，回执含 7 条工具记录。Bot 将该编号交给 Codex，后者从字符串算出末四位 `b11b`。两轮属于同一目标、拥有不同派发 ID；最终聊天答复与两份原生回执一致。 |

本轮未复测 MySQL、生产部署和长时间运行。以上试次验证了指定行为，开放领域的成功率和长期学习收益仍需留出任务与重复测量。

构建模型里的证据索引为 `EVIDENCE-BOT-AUTONOMY-INCREMENTS`。核对这笔六项操作的事务时，固定版 Singularity 的 `apply --dry-run` 输出了整个模型。建议为该命令增加按变更 ID 查看差异、修订号与验证结果的选项。
