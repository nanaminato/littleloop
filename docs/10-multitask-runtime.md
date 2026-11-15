# 单核心多任务运行时

后续增量：[真实工作区工具](11-workspace-tools.md)已通过同一运行时接入真实文件操作，本文的 `tasks-run` 保留为模拟环境命令。

2026-11-14 用户确认“一个角色、一个认知核心、多任务上下文、多个执行器”的方向，并要求作为持续目标逐步实现。本阶段交付串行认知驱动、持久化多任务状态、并发执行、统一预算和结果版本保护，集成既有执行反馈学习。它不表示整个后续认知路线已完成。

## 状态与共享边界

一个 `LearningSession` 状态文件代表一个角色，保存共享偏好、底线和经历。每个 `WorkItem` 保存独立 `LearningTask`、输入版本、优先级、截止时间、等待轮次、状态及当前决策 ID。更新任务必须提交预期版本；旧版本不能直接覆盖新版本。

任务从 Ready 经一次串行思考变为 WaitingExecution、WaitingFeedback、Deferred 或 Refused。思考事务同时保存完整决策快照与审计事件，不持久化半完成的 Thinking 状态。行动派发后变为 Executing；结果达成目标则 Completed，否则 WaitingFeedback。工具结果不确定则 NeedsReconciliation；取消为 Cancelled。

任务更新后版本递增，重新进入 Ready；这也是收到补充信息后继续 Ask/Reframe/Defer 的入口。不会对失败任务无限自动重试。更新任务是明确的宿主操作，不自动改变角色底线或增加许可。

所有任务共享可检索经历，但每次思考按当前情境、目标和前提筛选，并保存独立快照。先前已经生成的计划保持其当时依据，不因为其他任务的结果到达而被悄悄重写；之后的思考可以使用新经历。

## 注意力与执行

`ThinkNext(now)` 按 `priority + waitingTurns + overdueBonus` 排序。priority 范围 0..100；每等待一个其他任务的认知轮次增加 1；截止时间已过增加 100；平局按进入就绪状态的轮次与任务 ID 决定。由于外加优先级有界，持续到来的新高优先级工作不会永久压住一直就绪的旧任务。该策略是首版工程选择，不是对人的注意力机制的主张。传入的时间与排序依据写入事件记录。

`ThinkNext` 不等待执行槽位：两个工具忙碌时，核心仍可为第三个任务判断并准备行动。`DispatchNext` 按已生成计划的顺序取出行动，限制同时在途的执行数。新高优先级任务不会抢占已经派发的工具，也不会自动撤销已预留的低优先级计划。

`MultiTaskRuntime` 用单个驱动循环处理消息、思考、派发和完成事件；只有 `IAsyncActionExecutor` 在后台并发运行。同步决策插件仍串行调用，必须是可信代码。长时间阻塞的同步插件仍会占住认知核心，首版没有强制终止或隔离它。

运行期间可用 `EnqueueAsync`、`ReviseAsync`、`CancelAsync`、`FeedbackAsync`、`ReadAsync` 向串行消息入口提交操作；`ReconcileAsync` 提交已核实的不确定执行结果，`AddBudgetAsync` 显式补充预算。`RunAsync()` 推进到没有可推进工作、且本进程无活动执行器时返回；WaitingFeedback、Deferred、Uncertain 等状态可以留待处理。`RunAsync(stopWhenIdle: false)` 持续等待新消息，靠取消令牌退出。一个驱动实例只能运行一次，后续继续可创建新实例。每个角色同时只允许一个驱动循环持有文件租约。

## 预算、版本与恢复

配置同时限制执行并发数和角色总动作预算。生成行动时就原子预留成本，下一次思考只能使用剩余角色预算和剩余任务预算的较小值。更新任务输入版本不清零此前已消耗成本；显式提高任务预算和角色总预算是不同操作。

尚未派发的计划在任务被修改或取消时放弃，并记录 NotExecuted、退回预留。已经派发的成本保守地记为消耗，包含失败、执行拦截或结果未知，不因重启或反馈修正退回。角色总预算只能通过带当前状态 revision 的显式增量修改，不会自动扩张。

计划、预算预留、决策快照与任务状态写入同一原子状态事务；回执与执行槽位更新也是一个事务。已启用调度的状态拒绝使用 `learn-run` / `learn-decide` 绕过统一预算。启用前必须先处理旧 pending 决策；总预算从启用调度这一阶段起计算，已有学习历史仍保留。

每次派发携带逻辑任务 ID、输入版本和唯一决策 ID。执行期间更新任务后，旧回执仍记录到原决策，释放原执行槽位并保留其原情境经历，但不修改当前任务状态。旧反馈同样只能修正旧经历。取消已执行任务不等于撤销外部副作用；首版的任务取消是状态失效处理，不保证单个工具会立即停止。

执行器抛异常、返回错误关联或驱动被中断时，原执行变为结果未知，保留槽位和预算，不能被自动重试。重启会将上次遗留的 Dispatched 标为 Uncertain。确认真实结果后，用 `learn-result` 提交对应回执才能释放槽位。驱动取消令牌传递给执行器；不响应取消的工具可能继续产生副作用，需人工或外部系统核实。

历史决策沿用 `learn-replay`，使用当时的角色、经历及插件响应，不重新执行工具。事件序列记录实际调度和到达顺序；当前没有另行实现“重跑真实并发得到相同墙钟时序”的保证。加载状态会检查任务、执行、预算和审计序列的一致性。

## CLI 示例

```powershell
dotnet build src/LittleLoop.Cli -m:1
dotnet run --no-build --project src/LittleLoop.Cli -- learn-init data/multitask.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-enable data/multitask.json examples/multitask/limits.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-add data/multitask.json examples/multitask/a.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-add data/multitask.json examples/multitask/b.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-add data/multitask.json examples/multitask/c.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-run data/multitask.json examples/multitask/environments.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-add data/multitask.json examples/multitask/recall.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-run data/multitask.json examples/multitask/environments.json
```

首次运行有三个不同耗时的任务，共用两个执行槽位；结果 JSON 中的 `runtime.events` 可查看思考、预留、派发、回执的顺序。第二次加入的 recall 任务不提供候选方案，它使用任务 b 的成功经历完成。四次行动共用四单位总预算。

`tasks-run` 可追加受信任插件 DLL。示例环境按任务 ID 配置延迟和真实结果映射，映射不进入决策输入，仍只是本地模拟工具。

其余命令：`tasks-revise <state> <revision.json>` 接收 task、expectedVersion、可选 priority/dueAt；`tasks-cancel <state> <task-id> <version>` 取消指定版本；`tasks-budget <state> <amount> <state-revision>` 增加预算；`learn-show` 查看状态。分步调试或外部执行可依次调用 `tasks-think`、`tasks-dispatch`，再提交 `learn-result`。手动派发后不要再启动另一个驱动来接管尚未确认的结果。

CLI 的 `tasks-run` 为批次驱动；进程内持续服务请使用消息 API。独占写锁竞争会直接报错，不进行跨进程消息排队。JSON 历史会随任务增长，目前没有数据库索引、压缩或归档，不能据此声称已测得比多核心节省多少内存。

## 验证

```powershell
dotnet build tests/LittleLoop.Checks -m:1
dotnet run --no-build --project tests/LittleLoop.Checks
```

`SchedulingChecks` 使用受控执行闸门验证两个执行同时在途、第三个任务继续思考、运行中添加和修改任务、乱序结果、旧版本回执与反馈、预算预留/释放、单任务累计预算、注意力老化、截止时间、单驱动租约、取消与不确定结果恢复、共享经历和旧快照重放。原决策、插件与学习检查也继续运行。实际外部工具权限隔离、通用多步规划与学习收益评测仍是后续工作。
