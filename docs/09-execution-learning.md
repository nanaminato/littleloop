# 执行结果与反馈驱动的经历学习

后续增量：[单核心多任务运行时](10-multitask-runtime.md) 将此学习宿主扩展为一个角色处理多个任务；启用调度后的新工作须经过任务队列和统一预算。未启用调度的原 `learn-*` 用法保持兼容。

2026-11-14 用户明确要求从实际执行结果和反馈自动积累经历、修正判断并影响后续决策，减少手工提供角色与经历数据。本增量实现持久化运行时学习闭环；它不是 `.singularity/model.json` 中的构建模型，也不是完整的阶段三训练系统。

## 一次运行

`LearningSession` 从本地状态文件读取持久角色及历史，按照新任务的事实条件检索经历，组装带版本的 `DeliberationInput`，再调用现有插件宿主和角色审议器。任务提供目标、当前事实、可选行动、前提、证据、预算和许可，不包含角色或经历字段。角色只初始化一次；省略配置则采用默认角色（风险上限 0.5、无额外偏好或底线）。成功策略已被记住时，任务甚至可以不提供候选行动。

宿主先保存 pending 决策与完整输入快照，然后调用 `IActionExecutor`。执行器返回 `ActionResult`，包含决策 ID、候选 ID 和来源、执行状态、实际观察到的结果、执行来源、说明及外部副作用标记。只有对应行动的回执可被接受。内置 `OutcomeSimulation` 根据独立环境结果映射生成回执，不把隐藏结果交给决策器，也不把候选声明的预期效果当成执行成功。

`Completed` 只表示执行完成。是否达成任务，还要对照实际结果与本次必需目标，并至少满足一个目标。Completed 但目标未实现，以及 Failed，都自动形成失败经历。Blocked、NotExecuted 和没有回执的 pending 决策不会被学成成功或策略失败。模拟器会再次检查许可、预算、风险；外部执行器必须检查执行时的实时权限，回执入口不承担实际外部授权。

## 自动学习与纠正

每次新决策从历史派生经历，无需维护第二份手工经验文件。经历记录原事实条件、采用的策略、成败、来源及预测与观察差异。成功经历的可召回效果使用实际观察结果，不把未发生的预测效果写成既成事实；失败经历作为当前审议器的反例，阻止在同样条件下直接重复策略。候选提供方仍需对其当前行动模型负责，系统没有普遍证明外部候选模型是否真实。

检索要求原事实条件全部匹配；依赖的前提必须在新任务中具有相同定义。失败还要求目标结果及必需标记相同，避免把“未达成目标 A”误泛化成“对任何任务都无用”。最多取最近 64 条匹配经历参与一次决策；完整原始历史仍保存。成功经历可以在目标相符的新任务中重新提出行动，实际执行仍受本次权限和预算约束。

`ResultFeedback` 对已经执行的决策给出带来源、理由和实际结果的修正。首次反馈的 `supersedes` 为 null，后续修正必须引用该决策最新反馈的 ID；相同 ID 和内容重试不重复写入，旧版本不能覆盖新版本。最新反馈决定该次经历当前如何解读，但原回执和所有反馈均保留。成功反馈必须包含达成原目标的实际结果。反馈源是受信任的调用方声明，首版没有身份认证或独立事实核验。

这实现的是可纠正的情境经历学习：一次失败可以立即影响下一次相同情境判断，不自动改写角色的偏好、价值底线、风险上限或许可。设计基线要求的性格/策略参数训练仍需独立评测；本次不把单次成败直接提升为永久角色训练结论。现有推理器对失败反例仍保守处理；矛盾经历不会通过多数投票自动消解，误判可用关联反馈纠正，暂未加入主动重试探索、遗忘、概率校准或跨情境归纳。

## 可运行示例

在仓库根目录执行，每个运行命令都是独立进程：

```powershell
dotnet build src/LittleLoop.Cli -m:1
dotnet run --no-build --project src/LittleLoop.Cli -- learn-init data/learner.json
dotnet run --no-build --project src/LittleLoop.Cli -- learn-run data/learner.json examples/learning/ticket-1.json
dotnet run --no-build --project src/LittleLoop.Cli -- learn-run data/learner.json examples/learning/ticket-2.json
dotnet run --no-build --project src/LittleLoop.Cli -- learn-run data/learner.json examples/learning/ticket-3.json
dotnet run --no-build --project src/LittleLoop.Cli -- learn-feedback data/learner.json examples/learning/feedback.json
dotnet run --no-build --project src/LittleLoop.Cli -- learn-decide data/learner.json examples/learning/after-feedback.json
dotnet run --no-build --project src/LittleLoop.Cli -- learn-replay data/learner.json ticket-1
```

预期：ticket-1 选择直接处理，环境回报未解决；ticket-2 自动避开失败策略，选择专员复核并成功；ticket-3 的 options 为空，仍能召回复核方案。反馈确认 ticket-1 实际已解决后，ticket-4 可重新考虑直接处理。示例无手工经历或角色文件。ticket-4 使用 `learn-decide`，仅保存待执行决策。

`learn-init` 可追加一次性的 `PersistentRole` JSON 配置。已有状态拒绝覆盖；同一任务 ID 拒绝再次启动执行，重复演示请使用新的状态文件。`learn-show <state>` 查看历史。`learn-run` 和 `learn-decide` 可追加受信任插件 DLL 路径，插件照常读取组装好的角色/经历快照。

实际工具可实现 `IActionExecutor` 并调用 `LearningSession.Run`；也可先调用 `learn-decide <state> <task>`，由外部执行器执行，再通过 `learn-result <state> <result.json>` 提交匹配回执。本文示例为模拟环境；后续[真实工作区工具](11-workspace-tools.md)已经通过异步多任务宿主接入真实文件操作，联网与桌面工具仍未接入。

## 持久化与验证边界

状态文件包含格式版本、单调递增修订、角色、决策快照、执行回执和反馈链。每次操作使用独占锁，先写同目录临时文件并 flush，再替换状态文件，防止正常并发写丢失更新。锁冲突直接报错，不自动排队。加载时校验契约及决策重放一致性，不将损坏历史静默重置。它是本地 JSON 存储，不是大型数据库或防篡改日志。

执行器异常或进程中断留下 pending 决策，必须核实外部结果后提交回执，不能自动重复执行；本地日志无法证明外部副作用恰好发生一次。`learn-replay` 用当时保存的角色和经历快照及原插件输出重新计算，返回记录的回执和反馈，不读取最新经历替换旧输入，不重执行工具。

检查命令：

```powershell
dotnet build tests/LittleLoop.Checks -m:1
dotnet run --no-build --project tests/LittleLoop.Checks
```

覆盖空经历启动、执行完成但目标失败、跨宿主实例记忆、新方案召回、预测差异、反馈纠正及历史保留、重复提交、过期反馈、错误关联、无执行不学习、权限限制、异常 pending 与恢复、写锁、旧快照重放，以及原 v1/v2 行为。当前检查证明机制和示例行为，不证明在未见任务上的统计学习收益。
