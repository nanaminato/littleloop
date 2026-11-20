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
| LangGraph 以 `thread_id` 定位检查点，按 super-step 保存状态，用 pending writes 保留同一步已成功任务的写入。[持久化协议](https://docs.langchain.com/oss/python/langgraph/persistence) | P2 复用派发日志、目标版本和 SQL 事务。恢复时根据已持久化的事实继续，下一动作依然由认知接口选择。 |
| AutoGen Core 区分定向请求应答和主题发布，`MessageContext` 含 sender、topic_id、message_id 等关联信息。[通信协议](https://microsoft.github.io/autogen/stable/user-guide/core-user-guide/framework/message-and-communication.html) · [MessageContext 源码](https://github.com/microsoft/autogen/blob/main/python/packages/autogen-core/src/autogen_core/_message_context.py) | P1 让交接双方引用同一轮证据，沿用现有 Bot 选择角色和执行者。将来增加审阅角色时可用事件驱动，毋须引入群聊管理器。 |

主动定时复查和跨会话学习保留为后续实验。前者需用“到期只唤醒一次、暂停后不推进”验收，后者需在未见任务上比较收益与退化。本轮优先打通可替换认知、可见反馈及恢复后的继续决策。
