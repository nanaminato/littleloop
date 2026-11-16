# Chat 工作承诺：普通消息与可中断模拟执行

## 范围与分层

这一增量不是把 LittleLoop 的认知核心替换成命令机器人。`WorkConversationPolicy` 是有限领域的输入解释边界，`WorkCommitmentLedger` 是可重放的纯状态转换；Chat 宿主负责身份、顺序、持久化及执行回执。角色、长期记忆、学习和真实工具权限仍由现有 Core/Tools 路径负责。

首版只验证**已认证的一对一私聊 + 无外部副作用的模拟执行器**。不会将自由聊天直接交给 Codex，不会运行用户文字中的命令，也不会发送模型请求。原有 `/bot/.../schedule`、`work`、`decision` 路径不变。真实开发工具的逐任务中断尚未接入本入口；其既有宿主取消令牌不等于本任务已被物理停止。

## 开启本地试验

先使用 [SQLite 适配说明](47-chat-sqlite.md)运行 Chat、注册测试账号。配置必须明确允许账号的不可变 ID，而不是可改的显示名：

```text
ChatCommitments__Enabled=true
ChatCommitments__State=/absolute/private-data/commitments.json
ChatCommitments__Operators__0=<已注册测试账号 ID>
ChatCommitments__SyntheticDelayMs=1000
```

状态文件应位于源码/静态文件目录之外、仅服务账号可读写。配置不含真实凭据；不要把状态文件提交到 Git。缺省不启用；启用而未配置操作员或状态文件将启动失败。状态文件有进程独占锁，只支持单进程。Chat 消息仍使用所配置的 MySQL/SQLite，承诺账本使用独立原子替换的 JSON，不改生产数据库。

已登录客户端先 `POST /api/chat/commitments/direct`（仍需 `X-LittleLoop-Chat: 1` 防跨站请求头）显式登记试验私聊。返回房间 ID 及 `executor: synthetic`。之后使用现有消息发送接口、网页或 Android 消息输入框即可发送普通文字。`GET /api/chat/commitments/rooms/{room}` 查看当前任务 ID、版本和状态。当前没有增加专用 UI 开关，登记需要 API 调用；未登记的历史消息不会自动成为待办。

有限语法示例：

- `帮我整理一份模拟清单`：接受新任务；已有活动承诺则要求明确替换。
- `暂停` / `取消`：修改唯一当前承诺，也可回复其消息。
- `继续`：仅恢复已暂停任务。
- `修改为整理完整的新清单要求`：修改完整目标、增加版本；不是自动合并任意新约束。
- `改为整理另一个模拟清单`：旧任务标为 replaced，新任务具有独立 ID。
- 引用旧版本、多个候选、`按昨天那个`、部分 `补充` 等不足以确定意图时先澄清。
- 问句、假设、引用、转发和编辑后的消息不作为执行授权。

不声称通用自然语言理解。模拟器完成只证明任务流走通，不证明目标已被真实工具完成。

## 顺序、引用与执行约束

- HTTP 通过现有 cookie/device 身份验证；新入口再限制操作员 ID、登记房间和不可变发送者 ID。消息从数据库回读，不信任客户端提交的任务状态。
- 根据房间 sequence 升序读取，每批最多 50 条；不按 HTTP 到达顺序跳过较早消息。持久 cursor 防止重复发送、重启或历史回放重新授权。
- 原消息和机器人响应锚定 task ID + version。旧消息可作为历史证据，不能取消或批准新版本。唯一活动任务才可作为省略指代的目标。
- 解释窗口最多 64 个任务引用。独立的 `SelectContext` helper 只接受最多 64 条已按账号/房间过滤的候选，再按明确回复/任务引用选择最多 8 条、6,000 字符；它不是自动把整段聊天送进模型，也还不是跨会话语义搜索服务。
- 修订、取消、替换先持久化，再请求执行取消。旧执行仍有 execution ID/version 围栏；收到结束回执前不派发替代任务。迟到回执保留在账本，但不覆盖新目标/状态。
- `CanApprove` 同时检查任务 ID、版本、execution ID 及当前执行状态。新入口不提供真实工具审批；原 ChatBot 审批仍由原实现负责。
- 取消不撤销外部副作用。本阶段执行器只有可取消延时，所以收到其 stopped 回执可证明该模拟执行已结束。此规则不能直接套用到远程工具。
- 重启后未完成模拟工作暂停，不自动重跑。未来真实执行器必须使用未知状态/对账，而不是假定进程退出就撤销了外部操作。

## 限额与故障边界

最多 32 个登记房间，每房间 64 个任务、1,000 个解释事件、2,000 个消息锚点，状态文件上限 4 MiB。达到限额须离线归档，不静默丢弃旧授权记录。模拟延时限制为 10–60,000 ms。对状态修改使用单宿主互斥及原子文件替换。

状态先保存，回复后发送。两者不是跨 SQL/文件事务：进程若恰在保存后崩溃，可能缺少一次机器人确认，但不会重新执行已消费输入。磁盘持久化失败阻止派发；原子替换不是断电 fsync 保证。只建议用于本地/单机试验，文件备份需与 Chat DB 一起管理。

## 验证

```sh
dotnet build src/LittleLoop.Chat/LittleLoop.Chat.csproj
dotnet run --project tests/LittleLoop.CommitmentChecks
dotnet run --project tests/LittleLoop.ChatChecks
DOTNET=dotnet python3 tests/ChatCommitmentHttp/check.py
```

HTTP 检查自行生成临时 SQLite 文件、账号与密码，启动真实 ASP.NET 服务并走 cookie 登录，覆盖隔离、普通消息、重复消息、暂停/恢复、替换、旧回复与重启。不会读取用户聊天或生产数据。测试结果与未验证边界以本次提交记录为准；没有实际 MySQL 服务器或真实模型/远程执行器的测试时不得据此宣称全部路径端到端验证。

### 本次本地结果

.NET 10 Chat 构建 0 警告、0 错误；36 项新增 Core 承诺检查、8 项 Chat 宿主检查、SQLite 存储检查通过。原有 `LittleLoop.Checks` 308 项检查通过。真实 HTTP 用例通过，首次完整运行记录 77 个响应/断言检查（轮询次数会改变总数）。MySQL 实例、真实 Codex 中断、浏览器与 Android UI 交互均未在本次验收；不能将已有聊天 UI 能发送消息视为已完成这两端的界面验收。
