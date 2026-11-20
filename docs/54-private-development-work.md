# 普通私聊中的真实开发任务

2026-11-18，用户明确要求把普通私聊消息接入真实开发执行器，补齐任务创建、查询、补充、暂停、恢复和取消，并完成端到端验证。这是本轮工作授权，不表示自动部署或扩大现有工具权限。

## 入口与状态所有权

沿用 [S1 聊天目标](48-chat-continuous-work.md)已有的真实 `ChatBot` 路径：消息发送事务 → `chat_goal_inbox` → `ContinuousGoal` → 持久调度 → `LittleLoopChatAdapter` / Codex → 宿主工具与独立测试 → `chat_goal_outbox`。SQL 中的目标是唯一生命周期状态，执行目录保存 thread、审批和工具证据。

源码和历史报告证明这条路径此前已有实现；本轮补齐私聊查询及引用边界并提供独立的真实私聊验收，不新建另一套真实承诺账本。[模拟承诺入口](53-chat-commitments.md)及历史模拟消息保持独立。

配置 `ChatBot__Policy`、`ChatBot__Data`、`ChatBot__Operators`、`ChatBot__Approvers`，登录后调用 `POST /api/chat/bot/direct` 开启真实机器人私聊，之后使用普通消息发送接口或已有聊天输入框：

```text
帮我修复配置解析问题，并通过现有测试。
查询进度？
暂停
补充：保留原来的错误提示。
恢复
取消
```

`任务` 可先建立等待补充的目标。私聊中的普通问题和日常工作请求会直接收到聊天回复。以“任务：”或“新任务：”开头时也会检查正文。涉及明确项目修改的目标进入现有开发执行器，也可以用“开发任务：…”或 `/task …` 显式选择它。开发任务的补充要求追加到原目标并递增版本。多个活动目标时回复相应任务消息。没有活动目标时，进度查询返回本人在此会话中最近创建的目标，终态查询不会重新执行。回复非任务消息不会转而操作当前活动任务。

操作员身份不自动授予审批权。独立审批人用自己的账号登录网页，在会话栏选择「查看待审批操作」。对正在等待确认的私聊任务，审批列表仅显示操作类型、补丁路径和前后内容，或固定测试命令的标识。审批人可批准或拒绝当前请求。私聊历史、任务目标、执行结果和工具回执仍按会话成员权限读取。

## 直接对话与宿主权限

2026-11-19，用户要求直接用自然语言交代工作，希望 LittleLoop 默认拥有计算机配置权限，并提出在需要时向用户确认或提升管理员权限。当前可用范围包括文字对话、已授权的项目工具和下列 Windows 设置。

私聊中的普通问题使用已有 Codex 登录回答，并可参考同一私聊最近六次已完成的文字问答。宿主向独立的空目录提交固定初始化消息，仅在 `thread/sample` 中提交用户内容。也可将 `ChatBot__ConversationModel` 指向现有 Chat Completions 或 Ollama 的模型配置文件。如果配置了本机 `ChatBot__MemorySemantics` 而未单独指定对话模型，聊天会使用该本机端点。普通文字回复目前可以整理用户提供的内容，实时行情查询和 Excel 附件生成尚未接入。

开发策略中 `approvalMode` 的默认值是 `auto`。配置的操作员在 LittleLoop 私聊发起开发任务时，宿主允许直接调用既定的文件补丁和固定命令。每次操作继续经过项目范围、角色、预算和回执检查。要逐项确认，将策略文件中的 `approvalMode` 设为 `user`。群聊、仅通过好友开通的账号、具备写入能力的其他机器人沿用逐项审批。恢复已有任务时保留更严格的审批设置。

客户端可调用 `GET /api/chat/bot/private-approvals` 获取待审批操作，使用 `POST /api/chat/bot/private-approvals/{id}/decision` 提交 `nonce` 和 `value`（`accept` 或 `decline`）。重复提交及失效请求返回 409。

### Windows 设置与账号

在有权使用的 LittleLoop 私聊中单独发送“你能修改哪些系统设置？”，可以查看宿主支持的操作。在聊天服务的环境中设置 `ChatBot__HostOperators`，值为逗号分隔的已有聊天账号名，重启服务后生效。列入其中的账号需要先开通机器人私聊，随后可以直接查看或修改表中的设置。加入这个名单仅授予这些宿主操作。项目工具由 `ChatBot__Operators` 和开发策略授权，项目审批由 `ChatBot__Approvers` 授权。

原有角色也可继续使用宿主设置。项目操作员可以读取设置，同时具备操作员与审批人身份且策略为 `auto` 的账号可以直接修改。未指定或留空 `ChatBot__HostOperators` 时仍适用这条规则。专门列入 `ChatBot__HostOperators` 的账号可在项目逐项审批模式下修改宿主设置。

也可以设置 `ChatBot__HostPairingCandidate` 为一个已有聊天账号名，重启服务后，由该账号在已开通的 LittleLoop 私聊发送“绑定这台电脑”。登录这台 Windows 电脑的用户会看到账号名和授权范围，选择“是”后绑定才生效。每台宿主只保存一个绑定账号的 ID。重启后会核对当前候选账号与该 ID，移除候选配置并重启即可停用这项绑定授权。拒绝、超时或找不到可以确认的交互桌面时，账号仍保持原有权限。

| 设置 | 查询举例 | 修改举例 |
| --- | --- | --- |
| 时区 | 这台电脑的时区是什么？ | 把本机时区改成北京时间 |
| 活动电源计划 | 这台电脑的电源设置是什么？ | 把本机插电时的屏幕关闭时间设为15分钟 |
| 电池供电时的睡眠超时 | 本机使用电池时多久会睡眠？ | 把本机使用电池时的睡眠时间设为半小时 |
| 隐藏文件 | 文件资源管理器里是否显示隐藏文件？ | 在文件资源管理器中显示隐藏文件 |
| 文件扩展名 | 这台电脑是否显示文件扩展名？ | 在文件资源管理器中显示文件扩展名 |
| 应用主题偏好 | 当前 Windows 应用主题是什么？ | 把这台电脑的应用主题改成深色 |

例如，发送“把本机睡眠时间设为15分钟”，LittleLoop 会问是插电时还是使用电池时。接着回复“插电时”，它就会调整插电时的睡眠时间。也可以先问“本机使用电池时多久会睡眠？”，再回复“改成15分钟”，它会沿用刚才问到的电池供电设置。

时区和电源超时修改后会重新读取系统值。调整电源超时时须指明插电或电池供电，也可指定“永不”。Windows 要求管理员权限时，交互桌面会出现本次系统工具的授权窗口。聊天回复区分拒绝、取消、无法显示授权窗口和结果待核对。服务重启后会保留中断的变更记录。[Windows 时区工具](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/tzutil)、[Windows 电源工具](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options)、[Windows 系统授权](https://learn.microsoft.com/en-us/windows/win32/shell/launch)

文件显示与应用主题作用于运行宿主的 Windows 桌面账号。LittleLoop 检查该账号是否与当前交互会话的登录账号一致，并在结果中标出实际账号。后台服务会话或身份不匹配时会说明原因。资源管理器的两个显示选项分别读回核对，已打开的文件夹可以按 F5 刷新。深浅色是遵循 Windows 设置的应用所使用的偏好，已打开的应用可能需要重新打开。[Windows Shell 设置](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetsetsettings)、[Windows 应用颜色设置](https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-common)

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
# 使用默认策略复验两轮文字问答、表格请求分流、时区查询和预授权的两个夹具补丁
python tests/ChatContinuousLive/direct_http_e2e.py --conversation
```

新脚本默认使用当前 Debug 构建，也可设置 `LITTLELOOP_CHAT_DLL`。它创建独立 SQLite、随机端口、测试账号和仅含两个可写文本文件的夹具，不依赖 MySQL，不读取生产聊天数据。测试配置明确把测试私聊发起人同时列为操作员与审批人，仅批准夹具补丁与固定测试命令。结束时停止测试服务及其子进程，保留 Git 忽略的证据目录。

覆盖普通消息创建与去重、执行中查询、非成员隔离、错误回复不控制任务、真实审批等待、暂停后的停止确认与旧审批拒绝、暂停时补充、重启后原 thread 恢复、两个文件实际修改、宿主测试与独立复核、完成查询、执行中取消、取消后停止确认与过期审批拒绝、重启保留终态及完成通知去重。

## 本轮实跑结果

2026-11-20 的 Windows 专项检查通过了 27 项时区、34 项电源、25 项桌面和 21 项账号绑定检查。三项桌面偏好的宿主查询与独立读取的 Windows 设置逐项一致，分别核对了隐藏文件、文件扩展名和应用主题。写入与本地授权窗口使用测试替身。输出见 `data/desktop-settings-qa/focused-checks.log` 和 `data/desktop-settings-qa/read-checks.json`。

同日完成了 SQLite 的 250 项聊天目标回归和核心套件的 357 项检查。聊天回归覆盖了私聊账号的系统设置权限、当前设置的短句追问，以及未指明供电方式时发起澄清。完整日志为 `data/host-settings-checks.log` 和 `data/host-core-checks.log`。

同日，现有 5190 服务已部署新版。使用已登录账号在 LittleLoop 私聊中完成六次查询。能力清单列出了已接入的设置，时区返回 `China Standard Time`，与 Windows 一致。插电时的闲置关屏与睡眠超时均为“永不”，使用电池时闲置 3 分钟关屏、10 分钟睡眠。资源管理器显示隐藏文件和文件扩展名，应用主题为浅色。本次线上核验全程只读，实际系统修改、UAC 确认和真人账号绑定仍待验证。部署记录及程序集哈希见 `data/continuous-deployment/host-chat-deployment-20261120.json`。

部署后另行核对了候选账号有一间可用的机器人私聊，服务仍在等待该账号主动绑定。六次查询均已结束，待处理工作数为零。记录见 `data/continuous-deployment/host-chat-postflight-20261120.json`。

2026-11-19 的隔离 HTTP 验收使用真实 Codex 和 SQLite。同一段算术对话首轮得到 52，接着追问得到 43。金价表格请求收到了文字回复，测试核对了两份项目文件均未改动。时区回复与 Windows 独立读取的值一致。另一个明确的开发请求直接修改了两个夹具文件，通过宿主固定测试及独立复核。显式逐项审批模式也完成了暂停、重启、原会话恢复和取消验收。两次实跑分别记录在 `data/direct-http/c46c2995/result.json` 与 `data/direct-http/7b3b09e9/result.json`。时区写入与系统授权分支通过测试替身覆盖，实机只做了时区读取。

- 最终真实 Codex 私聊 HTTP 验收通过，约 55 秒。证据：`data/direct-http/34397ec1/result.json`；task `571b4b45-3b69-4c2a-b001-363ba1aad599`，thread `01a1ee57-fba4-7d00-b319-a5de3452a098`。目标 v4 完成，两个实际补丁、宿主测试回执和独立 `verify.py` 复核均通过；第二个真实任务在审批边界取消，收到停止确认，未执行待审批修改。
- `result.json` 保存最终 Chat/Core/Tools 程序集与验收脚本 SHA-256、审批 nonce、实际测试回执及两个任务的最终状态；同目录的独立 SQLite、宿主状态和服务日志保留用于追溯。
- 核心回归 315 项通过：`data/direct-core-checks.log`。SQLite 和 MySQL 各 108 项聊天目标检查通过，两个测试运行均另行通过 SQLite 存储检查和 9 项模拟宿主检查。记录：`data/direct-chat-checks.log`、`data/direct-mysql-checks.log`。

这些结果只证明有界开发任务及所列故障边界。浏览器/Android 交互、生产部署与 24 小时长稳不属于本次实跑，既有长稳记录保持未完成状态。

独立审批增量在现有 5190 服务通过 HTTP 实跑。操作员发送普通私聊消息，独立审批人分别批准夹具补丁和测试，操作员收到完成通知并再次查询到同一任务。操作员访问审批接口返回 403，审批人读取私聊历史和完整任务返回 404，重复提交审批返回 409。实跑记录位于 `data/continuous-deployment/private-independent-live/result.json`。验收结束后恢复正式开发策略，两账号权限配置保持原样。网页构建通过，验收通过 HTTP 验证接口和任务流程。
