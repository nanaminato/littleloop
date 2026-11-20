# LittleLoop

**LittleLoop** 是一个以 C# / .NET 实现的、CPU 优先的慢速持续认知系统研究项目。

> 核心问题：能否不依赖大规模端到端训练，仅使用可组合的规则、关联记忆、感知模块、工具与持续学习机制，逐步形成有用的认知与行动能力？

本项目是实验性认知架构，不预设能够复制人类意识、主观体验或通用智能。

## 核心原则

- **允许慢**：优先正确性、可追踪性和持续学习，不追求即时生成。
- **学习而非仅训练**：通过经历、反馈、复现、纠错积累能力；必要时可接入预训练模型作为工具。
- **注意力驱动记忆**：多向关联、关注度、置信度、时间衰减、遗忘与再学习。
- **渐进感知**：从色彩、轮廓、物体与时间连续性，到视觉/语音/触觉融合。
- **认知不等于语言**：内部概念、事件和关系不依赖单一自然语言；研究汉语逻辑表达及跨语言映射。
- **开放可替换**：存储、感知、学习、执行、推理均通过版本化接口隔离。
- **安全与伦理**：工具权限、审计、可撤销操作、人工确认及明确的资源预算。
- **可证伪实验**：每个能力都有输入、输出、基线、指标、失败案例。

## 文档

- [自主认知 Bot 架构差距与验收](docs/58-autonomous-bot.md) — 六个参考项目的工程取舍、本地认知可以承担的判断、跨轮工具证据，以及更换执行器后的恢复边界
- [持续认知主体目标与阶段提案](docs/47-continuous-agent-goals.md) — 长期能力方向、中间层与近期聊天持续工作闭环；区分已确认意图和候选方案
- [S1 聊天目标与持续工作](docs/48-chat-continuous-work.md) — 消息接任务、补充要求、暂停恢复、持久调度和验证边界
- [S1 真实 Codex 验收与部署](docs/49-codex-runtime-deployment.md) — 独立 SQLite/临时目录、真实 HTTP 闭环和现有服务部署记录
- [S1 持续任务稳定性验证](docs/50-continuous-stability.md) — 多轮真实执行、积压调度、通知去重和故障恢复
- [S1 24 小时长稳验证](docs/51-continuous-24h.md) — 持续负载、进程资源、日志增长及实际运行状态
- [S2 聊天多任务协作](docs/55-chat-multitask.md) — 独立工作区并发、同工作区互斥、问答与任务进度
- [S3 私聊长期记忆](docs/56-chat-long-term-memory.md) — 自然表达的偏好、从重复反馈提出建议、按任务召回带来源的项目做法与已验收文件线索

- [初版架构与阶段路线](docs/06-design-baseline.md) — 当前设计基线与第一阶段验收

- [总体概念与研究问题](docs/00-vision.md)
- [认知架构与模块设计](docs/01-architecture.md)
- [记忆、存储与注意力](docs/02-memory.md)
- [感知、语言与知识](docs/03-perception-language.md)
- [学习、推理、驱动与自评估](docs/04-learning-agency.md)
- [调试、开发准则与实验路线](docs/05-development-roadmap.md)

## 初期技术方向

C# / .NET 作为主语言；核心使用无 UI 依赖的类库，提供 CLI 实验宿主与单元测试。最初使用内存实现与本地持久化，未来按测量结果引入索引、图存储或其他后端。感知和外部 AI 服务均为可选适配器。

**基础样例：第一阶段的模拟工单决策闭环已实现。** 使用 .NET 10 SDK 运行：

```powershell
dotnet build src/LittleLoop.Cli
dotnet run --no-build --project src/LittleLoop.Cli -- run examples/phase1/incorrect.json > trace.json
dotnet run --no-build --project src/LittleLoop.Cli -- replay trace.json
```

`run` 先根据观察和角色阈值决策，再由模拟器用情境中的真实路线生成回执。重放重新计算并核对决策，使用原回执，无需再次执行。输出是 JSON。示例目录包含正确、错误、追问和暂缓四种情境。风险估计使用 `1 - 观察置信度`，目前没有经过校准。

## 项目构建模型

工具链已固定为仓库内 Git submodule，首次克隆使用 `--recurse-submodules`；已有工作区运行 `git submodule update --init --recursive`。使用 Python 3.11+ 执行 `python tools/model.py find "SQLite"` 按需查找，或 `python tools/check_model.py` 校验。详见[安装、读取和事务写入说明](.singularity/README.md)。这不是 LittleLoop 运行时依赖。


[Codex 独立开发工具闭环](docs/21-codex-development.md)已接入：通过宿主管理的文件与测试工具完成真实 RelaxKonOS 应用 ID 校验修复，保留核心判断、按源码快照匹配的经验、授权等待与独立测试验收。

已增加[插件与开放决策](docs/07-plugins-deliberation.md)、[角色审议流程](docs/08-role-deliberation.md)和[执行反馈学习闭环](docs/09-execution-learning.md)：结合目标、偏好、证据与持久化经历，检验前提、提出新方案、拒绝冲突目标。执行回执自动形成经历，关联反馈可修正判断，后续任务自动检索。当前是符号推理和情境经历学习；开放领域语言理解与角色性格的自动训练仍未实现。

[单核心多任务运行时](docs/10-multitask-runtime.md)已实现：一个角色共享记忆，核心串行分配注意力和判断，执行器按上限并发；任务有独立上下文和版本，预算统一预留，迟到结果归档但不覆盖新任务。包含可运行的多任务模拟与恢复检查。

[真实工作区工具](docs/11-workspace-tools.md)已接入：实际读取、创建、复制和规范化 UTF-8 文本文件，验证输出字节并记录 SHA-256。工具方案自动发现，经历召回时绑定本次文件参数，反馈可改变后续工具选择；不需要人为填写“实际结果”。支持目录范围、工具许可、资源限制和执行时任务版本检查。

[多步目标规划](docs/12-goal-planning.md)已接入：提供输入、产物要求和预算，系统自动搜索工具链、逐步执行并验证最终产物；实际读取失败可切换备用来源或内容，输出冲突仅在目标允许时另存。方案与步骤持久化，未知执行先对账，不盲目重试。当前范围为结构化文本文件目标。

[自然语言目标与追问](docs/13-language-goals.md)已接入：中文或英文描述文本处理目标，自动提取输入、变换、输出和预算等约束，缺失或冲突时逐轮追问；信息齐全后自动生成目标并组合工具执行，无需填写目标 JSON。对话与解释证据持久化，支持重启继续。当前采用有限领域本地语法解析，未识别的要求会澄清，不声称通用自然语言理解。

[可替换大模型语义适配器](docs/14-model-semantics.md)已接入：支持 DeepSeek/OpenAI Chat Completions 兼容接口和 Ollama 协议，自由描述经模型解释、宿主约束校验和追问后进入真实工具闭环。DeepSeek 已实测完整执行、追问及失败恢复；模型不获得工具权限，错误响应不会触发执行，语义记录可离线重放。本地语法入口继续保留。

[执行中的模型规划](docs/15-model-planning.md)已接入：模型参与初始拆解，每次实际回执后重新审议剩余步骤；可在已授权资源内换源、追问或拒绝，核心独立检查依赖、权限、预算和目标，角色仍可否决行动。规划有持久化次数限制和审计。项目启动可自动读取被 Git 忽略的 `.env.local` 本地凭证。

[奇点项目模型](.singularity/README.md) 描述认知循环、事件契约、设计决策、实施任务与验证计划，供开发过程追溯使用。它不存储运行时认知记忆；模型记录设计与实验计划，首阶段可运行样例见上文。

[开发任务工具闭环](docs/16-development-tools.md)已接入：模型可检索项目、读取代码、提交带哈希的精确补丁、选择宿主授权命令并运行测试；根据真实失败调整计划，最终代码必须通过全部指定测试。保留角色裁决、权限校验、经历积累与未知执行对账。命令授权不等于操作系统沙箱。

[多文件真实项目开发](docs/17-repository-development.md)已验证：分页浏览项目结构和引用、按子树检索、按行读取、跨文件修改并运行实际构建与验收。DeepSeek 已在 LittleLoop 自身代码中完成两文件 CLI 功能，遇到补丁及编译失败后修正；新增只读 `dev-map` 命令，保留核心裁决、测试基线和最终快照校验。

[项目自动发现与权限申请](docs/18-project-discovery.md)已接入：`dev-project <项目路径> <自然语言目标>` 自动发现 .NET/Python 测试和源文件范围，生成可审查的执行权限；依赖恢复等扩权由模型说明原因并暂停申请，宿主明确批准后在同一会话继续，保留预算、核心判断和经历。未知框架与范围冲突先澄清，不盲目执行项目脚本。

[只读项目指导与 agent 边界](docs/19-agent-guidance.md)：`dev-guide` 从有界项目文档生成下一步任务、验收条件和 agent 任务书草案；任务书生成不代表已经发送或执行。

[Codex App Server 委派](docs/20-codex-agent.md)已接入：创建/恢复会话、发送任务、接收实际结果、按宿主检查自动补充任务，记录核心判断和经历。授权支持明确清单内的自主判断、等待与用户处理；RelaxKonOS 的只读验收矩阵已真实跑通。受限代码开发见上文，其他 agent 实现仍待扩展。

[开发工作台](docs/22-workbench.md)提供 Angular 对话界面、ASP.NET Core 后台队列、源码引用、逐次授权和测试回执，可配置 MySQL。

[多人聊天](docs/24-chat-phase-a.md)及[日常聊天功能](docs/25-chat-phase-b.md)提供独立账号、私聊群聊、附件、搜索、收藏和多设备同步。

[聊天机器人](docs/26-chat-phase-c.md)支持 LittleLoop 私聊和群聊、源码引用、开发任务、逐次审批、执行结果与历史工作台导入。

[Chat SQLite 适配](docs/52-chat-sqlite.md)提供不依赖 MySQL 服务的本地单实例选项，MySQL 仍为默认。[普通消息工作承诺](docs/53-chat-commitments.md)以显式启用的私聊模拟执行器验证接受、修订、暂停、取消、替换和迟到回执边界；尚未连接真实开发工具中断。

LittleLoop 主机器人通过同一决策入口处理讨论、工作目标和执行中的补充。需要工作时，Bot 先发出计划，再选择 Codex 或 DeepSeek Harness。每轮回执回到 Bot，由它决定继续、等待还是完成。服务可以替换认知 API、保存各目标的 Harness 会话，并按用户偏好发送最终报告。[配置与验收](docs/57-bot-decision-loop.md)

[聊天平台扩展](docs/27-chat-phase-d.md)增加源码审阅机器人、群话题和频道订阅广播。

[Telegram 式聊天界面](docs/29-telegram-ui-implementation.md)实现桌面和手机布局、十类设置入口及多设备同步。

[聊天设置补齐](docs/30-telegram-settings.md)说明用户名、屏蔽、双语和后台推送。

[原生 Android 聊天客户端](docs/31-android-chat.md)使用 Kotlin 和 Jetpack Compose，复用同一套账号、会话、已读与设置接口。

[Android 同步架构](docs/32-android-chat-sync.md)说明本地历史、持久发送队列和引用回复，以及 Telegram 开源客户端提供的设计参考。

[Android 图片与文件](docs/33-android-attachments.md)说明系统文件选择器、上传进度、断网续发和附件权限。

[Android 消息搜索](docs/34-android-message-search.md)覆盖全局搜索、会话内搜索、历史定位和离线缓存。

[Android LittleLoop 机器人](docs/35-android-bot-cards.md)支持源码问答、从消息安排开发任务、审批和查看执行证据。

[Android FCM 云推送](docs/36-android-fcm.md)说明 Firebase 配置、会话与待审批通知，以及如何验收后台送达。

[Android 消息长按菜单](docs/37-android-message-actions.md)支持编辑、删除、转发、消息置顶和个人收藏。

[Android 群话题和频道](docs/38-android-topics-channels.md)复用同一套历史、未读、订阅和广播通知。

[Android 会话与频道管理重构](docs/44-android-conversation-refactor.md)说明组件、业务状态和共享权限的边界及回归验证。

[好友申请和群成员管理](docs/39-chat-friends-and-group-members.md)覆盖网页版和 Android，群成员变更会同步到话题。

[Android 好友与群成员管理重构](docs/45-android-social-refactor.md)说明组件与状态分离、共享权限及原生回归结果。

[Android 会话列表与新建会话重构](docs/46-android-home-refactor.md)说明首页与频道目录的状态分离、创建权限和模拟器回归。
