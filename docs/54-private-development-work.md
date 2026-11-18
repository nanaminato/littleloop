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

`任务` 可先建立等待补充的目标；`新任务：…` 明确建立另一个目标。补充要求追加到原目标并递增版本，不扩大权限或预算。多个活动目标时回复相应任务卡。没有活动目标时，进度查询返回本人在此会话中最近创建的目标，终态查询不会重新执行。回复非任务消息不会转而操作当前活动任务。问候、引文、假设讨论不作为隐式补充或开发请求；仍是有限领域路由，不是通用意图理解。

操作员身份不自动授予审批权。独立审批人用自己的账号登录网页，在会话栏选择「查看待审批操作」。对正在等待确认的私聊任务，审批列表仅显示操作类型、补丁路径和前后内容，或固定测试命令的标识。审批人可批准或拒绝当前请求。私聊历史、任务目标、执行结果和工具回执仍按会话成员权限读取。

客户端可调用 `GET /api/chat/bot/private-approvals` 获取待审批操作，使用 `POST /api/chat/bot/private-approvals/{id}/decision` 提交 `nonce` 和 `value`（`accept` 或 `decline`）。重复提交及失效请求返回 409。

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
```

新脚本默认使用当前 Debug 构建，也可设置 `LITTLELOOP_CHAT_DLL`。它创建独立 SQLite、随机端口、测试账号和仅含两个可写文本文件的夹具，不依赖 MySQL，不读取生产聊天数据。测试配置明确把测试私聊发起人同时列为操作员与审批人，仅批准夹具补丁与固定测试命令。结束时停止测试服务及其子进程，保留 Git 忽略的证据目录。

覆盖普通消息创建与去重、执行中查询、非成员隔离、错误回复不控制任务、真实审批等待、暂停后的停止确认与旧审批拒绝、暂停时补充、重启后原 thread 恢复、两个文件实际修改、宿主测试与独立复核、完成查询、执行中取消、取消后停止确认与过期审批拒绝、重启保留终态及完成通知去重。

## 本轮实跑结果

- 最终真实 Codex 私聊 HTTP 验收通过，约 55 秒。证据：`data/direct-http/34397ec1/result.json`；task `571b4b45-3b69-4c2a-b001-363ba1aad599`，thread `01a1ee57-fba4-7d00-b319-a5de3452a098`。目标 v4 完成，两个实际补丁、宿主测试回执和独立 `verify.py` 复核均通过；第二个真实任务在审批边界取消，收到停止确认，未执行待审批修改。
- `result.json` 保存最终 Chat/Core/Tools 程序集与验收脚本 SHA-256、审批 nonce、实际测试回执及两个任务的最终状态；同目录的独立 SQLite、宿主状态和服务日志保留用于追溯。
- 核心回归 315 项通过：`data/direct-core-checks.log`。SQLite 和 MySQL 各 108 项聊天目标检查通过，两个测试运行均另行通过 SQLite 存储检查和 9 项模拟宿主检查。记录：`data/direct-chat-checks.log`、`data/direct-mysql-checks.log`。

这些结果只证明有界开发任务及所列故障边界。浏览器/Android 交互、生产部署与 24 小时长稳不属于本次实跑，既有长稳记录保持未完成状态。

独立审批增量在现有 5190 服务通过 HTTP 实跑。操作员发送普通私聊消息，独立审批人分别批准夹具补丁和测试，操作员收到完成通知并再次查询到同一任务。操作员访问审批接口返回 403，审批人读取私聊历史和完整任务返回 404，重复提交审批返回 409。实跑记录位于 `data/continuous-deployment/private-independent-live/result.json`。验收结束后恢复正式开发策略，两账号权限配置保持原样。网页构建通过，验收通过 HTTP 验证接口和任务流程。
