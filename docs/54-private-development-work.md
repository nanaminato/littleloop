# 普通私聊中的真实开发任务

本页记录原生聊天直连和历史项目执行器的实现与实测。当前 LittleLoop 主机器人通过统一认知入口处理自然语言和持续目标。[聊天 Bot 的决策与 Harness 协作](57-bot-decision-loop.md)

2026-11-18，用户明确要求把普通私聊消息接入真实开发执行器，补齐任务创建、查询、补充、暂停、恢复和取消，并完成端到端验证。这是本轮工作授权，不表示自动部署或扩大现有工具权限。

## 入口与状态所有权

显式项目开发任务沿用 [S1 聊天目标](48-chat-continuous-work.md)已有的真实 `ChatBot` 路径：消息发送事务 → `chat_goal_inbox` → `ContinuousGoal` → 持久调度 → `LittleLoopChatAdapter` / Codex → 宿主工具与独立测试 → `chat_goal_outbox`。SQL 中的目标是唯一生命周期状态，执行目录保存 thread、审批和工具证据。

源码和历史报告证明这条路径此前已有实现；本轮补齐私聊查询及引用边界并提供独立的真实私聊验收，不新建另一套真实承诺账本。[模拟承诺入口](53-chat-commitments.md)及历史模拟消息保持独立。

配置 `ChatBot__Policy`、`ChatBot__Data`、`ChatBot__Operators`、`ChatBot__Approvers`，登录后调用 `POST /api/chat/bot/direct` 开启真实机器人私聊，之后使用普通消息发送接口或已有聊天输入框：

```text
/task 帮我修复配置解析问题，并通过现有测试。
/status
```

选择原生对话代理后，普通私聊直接发给代理。例如“把采购清单写到清单.txt，下一条我再追加”可以直接交代，并可在同一私聊追问。要在已授权项目中开展开发任务，可发送“开发任务：…”或 `/task …`。后续可使用任务控制命令或回复相应的任务卡来补充、查询、暂停、恢复或取消。项目任务采用自己的工具范围和审批策略。

操作员身份不自动授予审批权。独立审批人用自己的账号登录网页，在会话栏选择「查看待审批操作」。对正在等待确认的私聊任务，审批列表仅显示操作类型、补丁路径和前后内容，或固定测试命令的标识。审批人可批准或拒绝当前请求。私聊历史、任务目标、执行结果和工具回执仍按会话成员权限读取。

## 对话代理与项目审批

2026-11-20，用户要求先提交和推送已有版本，再移除 LittleLoop 直接操作计算机的逻辑，全部交给 Codex 或 DeepSeek Harness。此前包含 Windows 操作的版本保存在提交 `72f59c0`。

用户随后要求按私聊复用外部代理会话，由代理判断何时需要工具，并取消普通私聊对既有项目审批的依赖。用户还提出自动通过所有审批和移除沙箱。当前实现仅赋予下述私聊工作区权限，整机访问与无条件提权未实现。

默认使用已登录的原生 Codex。显式设置 `ChatBot__ConversationHarness=codex` 或 `deepseek` 会选用对应原生会话。要沿用旧文字入口，留空 `ChatBot__ConversationHarness` 并通过 `ChatBot__ConversationModel` 显式指定 Chat Completions 或 Ollama 配置。该文字入口沿用既有项目任务分流规则。`ChatBot__MemorySemantics` 配置用于记忆处理。

开发策略中 `approvalMode` 的默认值是 `auto`。配置的操作员在 LittleLoop 私聊发起开发任务时，宿主允许直接调用既定的文件补丁和固定命令。每次操作继续经过项目范围、角色、预算和回执检查。要逐项确认，将策略文件中的 `approvalMode` 设为 `user`。群聊、仅通过好友开通的账号、具备写入能力的其他机器人沿用逐项审批。恢复已有任务时保留更严格的审批设置。

客户端可调用 `GET /api/chat/bot/private-approvals` 获取待审批操作，使用 `POST /api/chat/bot/private-approvals/{id}/decision` 提交 `nonce` 和 `value`（`accept` 或 `decline`）。重复提交及失效请求返回 409。

### 私聊的运行时上下文

同一用户在同一私聊中使用同一个代理时，后续消息会续接代理保存的会话。新建代理会话时，LittleLoop 会带入本私聊最近六轮已完成的问答作为背景。续接时发送当前消息、本轮引用和当前运行时说明。上一轮约定了代号，下一条就可以直接问“刚才那个代号是什么？”。服务重启后可以续接已保存的代理会话。

要长期保留一条背景或回复偏好，可以在该私聊单独发送一条文字消息“记住上下文：当前在测试环境，回答尽量简短”。说明正文最多 512 个字符，再次设置会替换上一条。单独发送“查看上下文”可查看当前保存的说明，“清除上下文”可清除这条说明。近期问答仍按当前私聊的聊天记录选取。服务将说明关联到原消息，删除或撤回来源消息后，该条说明也会失效。

管理员可设置 `ChatBot:ConversationStyle`（环境变量 `ChatBot__ConversationStyle`）指定普通私聊的默认回复风格，例如“使用简洁中文，并保留技术名称”。有效长度为 1 至 512 个字符。服务在生成回复时读取当前配置。支持热重载的配置提供程序可直接更新，环境变量需要在新服务进程中生效。代理在本轮可参考私聊保存的说明和当前风格，工具授权及执行结果以实际运行环境及回执为准。

### 电脑操作请求

LittleLoop 按所选代理、私聊、发起人和会话版本定位代理记录。每位用户在每个私聊里使用所选代理的独立工作区。编辑或删除消息、保存或清除长期说明会让下一轮新建代理会话，并沿用该私聊原有的工作区文件。

Codex 通过 App Server 创建或恢复 thread，再发送新的 turn。会话运行在专用工作区权限配置下，工具的网络访问关闭，启动时核对实际生效的权限。DeepSeek 通过 ACP 创建或续接 session，并在提供文件操作前验证工作区限制。`ChatBot__DeepSeek__ConversationWorkspaceFiles` 默认为 `true`，允许代理在本私聊工作区根目录读取、写入和编辑小型 `.txt`、`.md` 和 `.csv` 普通文件。设为 `false` 后该会话仅提供对话。普通私聊的文件操作由所选代理的会话权限决定。项目审批列表只处理显式项目开发任务。

在 Windows 上，Codex 提交本轮消息前会调用 `windowsSandbox/readiness`。返回 `ready` 时启用受限工作区工具，返回 `notConfigured` 或 `updateRequired` 时选择原生文字会话，并在回复中提示工作区工具尚未就绪。本机测得的状态为 `notConfigured`。

服务会区分限流、超时、权限不足、会话中断和当前配置不支持的能力。中断时已经发出的操作可能已生效，重启后会提示核对实际结果。

旧版的 `ChatBot__HostOperators`、`ChatBot__HostPairingCandidate` 和本地绑定记录在新版中不再提供宿主操作权限。升级时，仍在排队或等待补充的旧版 Windows 请求会结束，并在原会话说明原因。执行中断的旧操作标记为待核对。历史消息和已完成的结果仍可查阅。

## 暂停、恢复和取消

复用既有协作式工具边界：暂停或取消先持久化目标版本，执行宿主检查后请求中断，收到真实 turn 结束才记录执行状态 `suspended`。目标显示暂停或取消，不等于正在执行的外部操作已经立即终止；已发生的副作用不自动撤销。未知执行进入待核对，不能用“恢复”绕过。

暂停时可以保存补充要求；恢复保留原 thread、剩余预算和既有工具证据。旧审批失效，需重新申请；旧版本完成回执不能完成新版本目标。完成依赖实际补丁和必需测试回执，模型总结不能代替验收。

## 可复现验收

```powershell
dotnet build tests/LittleLoop.ChatChecks
dotnet run --no-build --project tests/LittleLoop.ChatChecks
dotnet run --project tests/LittleLoop.Checks
# 设置已安装的 Codex 可执行路径；可选独立登录目录及本机受信任证书，详见 docs/49。
$env:LITTLELOOP_CODEX_EXECUTABLE = '/absolute/path/to/codex'
python tests/ChatContinuousLive/direct_http_e2e.py
# 使用默认策略复验私聊问答、请求分流和预授权的两个夹具补丁
python tests/ChatContinuousLive/direct_http_e2e.py --conversation
```

新脚本默认使用当前 Debug 构建，也可设置 `LITTLELOOP_CHAT_DLL`。它创建独立 SQLite、随机端口、测试账号和仅含两个可写文本文件的夹具，不依赖 MySQL，不读取生产聊天数据。测试配置明确把测试私聊发起人同时列为操作员与审批人，仅批准夹具补丁与固定测试命令。结束时停止测试服务及其子进程，保留 Git 忽略的证据目录。

覆盖普通消息创建与去重、执行中查询、非成员隔离、错误回复不控制任务、真实审批等待、暂停后的停止确认与旧审批拒绝、暂停时补充、重启后原 thread 恢复、两个文件实际修改、宿主测试与独立复核、完成查询、执行中取消、取消后停止确认与过期审批拒绝、重启保留终态及完成通知去重。

## 代理委派验证

本机已安装的 Codex App Server 连同隔离的模拟模型服务完成了两个真实的协议回合。重启代理进程后复用了同一 thread ID，模型服务在第二回合收到第一轮的随机标记，期间无需重发聊天记录。使用已配置的托管模型和禁用工具的权限配置时，真实原生文字回合也返回了正确的算术结果。当前 Windows 环境的沙箱运行服务不可用，开启文件工具的托管会话在回复前被拒绝。工作区内的 Codex 原生命令和文件写入尚未得到实机验证。此次为本地验证，记录见 `data/private-native-codex-build/native-loopback-observations.log` 和 `data/private-native-codex-build/native-text-observations.log`。

原生私聊在本地通过了 SQLite 聊天目标套件的 257 项检查，记录见 `data/native-conversation-checks-final.log`。真实 DeepSeek Harness 在本次私聊工作区创建了 `memo.txt`，并返回成功的写入回执。随后通过原生读取与编辑工具更新了同一文件，复核内容为 `EDITED`。以相同工作 ID 再次处理时返回既有结果。另一个进程续接同一 ACP 会话后，在没有重发之前问答的情况下答出了上次记住的随机值。针对相邻工作区的测试未读出随机标记，目标文件内容保持完整。原生结果和工作区记录见 `data/harness-private-probe/61643ad439c443a3bec6f01e230800a5`。另一项检查验证了工作区限制无法确认时会在发送私聊提示前停止，协议记录见 `data/harness-private-probe/da827559af5d43fd9073cdcd75d9b352/methods.txt`。

本地 SQLite 和独立 MySQL 各通过 249 项聊天目标检查。保存和查看说明由聊天服务直接响应。后台服务重启并更换对话代理后，同一用户在原私聊的说明和问答历史仍可用。运行中修改默认风格会影响后续回复，清除说明后仍可引用近期问答。测试还核对了用户与会话之间的隔离。本次运行时上下文在本地完成验证，记录见 `data/runtime-context-checks.log` 和 `data/runtime-context-mysql-checks.log`。

2026-11-20，SQLite 聊天目标套件的 245 项检查全部通过。升级夹具覆盖了旧 Windows 工作的终止与待核对状态。后台服务检查确认，配置为 DeepSeek 的私聊可在原会话收到回复，追问时会携带同一用户引用的上一轮内容。完整输出见 `data/delegated-conversation-checks.log`。

另用已安装的真实 DeepSeek Harness 和只读 ACP 配置验证了携带上文的私聊请求。编译后的适配器收到一段包含样例水果名的模拟上文，在追问中正确返回了该名称。运行结束后隔离工作区仍为空。结果在控制台核验，复验驱动位于 `data/harness-conversation-checks/InspectExternalHarness.cs`，只读配置位于 `tests/ChatContinuousLive/deepseek-readonly.yml`。

同日，5190 服务已切换到代理委派版本，普通私聊选用 Codex，部署环境已移除旧的本机操作员和配对配置。已登录账号的算术提问和依赖上一轮的追问分别得到正确答案 43 和 45。两次时区查询都作为普通聊天转交代理，分别遇到上游 HTTP 429 和服务不可用，私聊返回了相应提示。升级前后的旧本机操作记录均为 6 条，待执行的旧操作和活动任务均为零。实机核验未修改系统设置。部署校验记录包含程序集 SHA-256 和每次请求的状态。[本机部署校验记录](../data/continuous-deployment/external-agent-deployment-20261120.json)

## 历史实跑记录

提交 `72f59c0` 中的旧版直接 Windows 执行路径于 2026-11-20 完成专项检查，通过了 27 项时区、34 项电源、25 项桌面和 21 项账号绑定检查。三项桌面偏好的宿主查询与独立读取的 Windows 设置逐项一致，分别核对了隐藏文件、文件扩展名和应用主题。写入与本地授权窗口使用测试替身。输出见 `data/desktop-settings-qa/focused-checks.log` 和 `data/desktop-settings-qa/read-checks.json`。

同日完成了 SQLite 的 250 项聊天目标回归和核心套件的 357 项检查。聊天回归覆盖了私聊账号的系统设置权限、当前设置的短句追问，以及未指明供电方式时发起澄清。完整日志为 `data/host-settings-checks.log` 和 `data/host-core-checks.log`。

同日，5190 服务部署了上述直接执行版本。使用已登录账号在 LittleLoop 私聊中完成六次查询。能力清单列出了已接入的设置，时区返回 `China Standard Time`，与 Windows 一致。插电时的闲置关屏与睡眠超时均为“永不”，使用电池时闲置 3 分钟关屏、10 分钟睡眠。资源管理器显示隐藏文件和文件扩展名，应用主题为浅色。本次线上核验全程只读，实际系统修改、UAC 确认和真人账号绑定仍待验证。部署记录及程序集哈希见 `data/continuous-deployment/host-chat-deployment-20261120.json`。

部署后另行核对了候选账号有一间可用的机器人私聊，当时尚未绑定该账号。六次查询均已结束，待处理工作数为零。记录见 `data/continuous-deployment/host-chat-postflight-20261120.json`。

2026-11-19 的隔离 HTTP 验收使用真实 Codex 和 SQLite。同一段算术对话首轮得到 52，接着追问得到 43。金价表格请求收到了文字回复，测试核对了两份项目文件均未改动。时区回复与 Windows 独立读取的值一致。另一个明确的开发请求直接修改了两个夹具文件，通过宿主固定测试及独立复核。显式逐项审批模式也完成了暂停、重启、原会话恢复和取消验收。两次实跑分别记录在 `data/direct-http/c46c2995/result.json` 与 `data/direct-http/7b3b09e9/result.json`。时区写入与系统授权分支通过测试替身覆盖，实机只做了时区读取。

- 最终真实 Codex 私聊 HTTP 验收通过，约 55 秒。证据：`data/direct-http/34397ec1/result.json`；task `571b4b45-3b69-4c2a-b001-363ba1aad599`，thread `01a1ee57-fba4-7d00-b319-a5de3452a098`。目标 v4 完成，两个实际补丁、宿主测试回执和独立 `verify.py` 复核均通过；第二个真实任务在审批边界取消，收到停止确认，未执行待审批修改。
- `result.json` 保存最终 Chat/Core/Tools 程序集与验收脚本 SHA-256、审批 nonce、实际测试回执及两个任务的最终状态；同目录的独立 SQLite、宿主状态和服务日志保留用于追溯。
- 核心回归 315 项通过：`data/direct-core-checks.log`。SQLite 和 MySQL 各 108 项聊天目标检查通过，两个测试运行均另行通过 SQLite 存储检查和 9 项模拟宿主检查。记录：`data/direct-chat-checks.log`、`data/direct-mysql-checks.log`。

这些结果只证明有界开发任务及所列故障边界。浏览器/Android 交互、生产部署与 24 小时长稳不属于本次实跑，既有长稳记录保持未完成状态。

独立审批增量在现有 5190 服务通过 HTTP 实跑。操作员发送普通私聊消息，独立审批人分别批准夹具补丁和测试，操作员收到完成通知并再次查询到同一任务。操作员访问审批接口返回 403，审批人读取私聊历史和完整任务返回 404，重复提交审批返回 409。实跑记录位于 `data/continuous-deployment/private-independent-live/result.json`。验收结束后恢复正式开发策略，两账号权限配置保持原样。网页构建通过，验收通过 HTTP 验证接口和任务流程。
