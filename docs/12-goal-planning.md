# 多步目标与实际结果驱动的重规划

本增量实现有边界的文本文件目标规划。调用方描述目标、输入来源、可选备用内容、输出约束和预算，不需要列出步骤或工具 ID。自然语言 description 用于说明；可执行含义来自结构化字段，不宣称任意自然语言任务理解。

## 实现

`DocumentGoals` 将目标编译为所需事实：产物已创建、规范化或保留布局、独立读回验证。规划器在工具操作的前置条件、产生事实和移除事实上进行广度优先搜索，最多 16 步、4096 个状态；当前工具每次成本为 1，因此选择最短可行链。宿主工具许可、目标能力范围和剩余目标预算共同限制搜索。

可组合操作包括读取来源、从内容创建输入、规范化、复制、直接创建输出、读取并验证输出。已有来源通常组合为“读取 → 规范化/复制 → 验证”；只有内联内容时可组合为“创建输入 → 规范化 → 验证”或“创建输出 → 验证”。来源优先于备用内容。

`GoalOrchestrator` 每个目标只提交下一条依赖已满足的子任务，由原有角色核心结合偏好、经历、边界和预算审议；核心仍可拒绝执行。不同目标共享一个多任务运行时和记忆，工具执行可并发。规划不绕过执行器的目录与权限检查。

每步真实回执进入既有经历库，后续子任务会检索经历。只有实际成功、工具类型和实际 outcomes 匹配、产物路径一致时才推进事实。最终验证比较独立读取的 SHA-256 与创建回执的 SHA-256；外部修改会阻止目标完成。这里验证的是规定的文本变换和产物一致性，不是任意业务语义正确性。

## 失败、恢复与持久化

- 读取来源失败（缺失或非法 UTF-8）后按提供的候选来源继续；耗尽时若有备用内容，则自动创建新输入并重新搜索工具链。
- 输出已存在时默认阻塞并保留原文件。只有 `allowAlternativeOutput: true` 才自动生成同目录的另存路径；最终路径记录在目标 resources.target。
- 其他失败、角色拒绝、预算不足、重规划次数耗尽或可能已产生副作用时停止自动恢复，不扩大权限或预算。
- 进程中断导致已派发执行无回执时，目标进入 `needsReconciliation`，不重复写入；对账后可继续未完成步骤。
- 目标状态、方案历史、回执关联、子任务入队在同一状态文件中原子保存。重启 `goals-run` 不会重复已完成步骤。每版方案保留初始事实、资源、算子和触发重规划的决策 ID；加载时校验依赖链与步骤历史。

`PrepareNextSteps()` 可供宿主分阶段推进（只更新进度/入队，不调用工具）。常规 CLI 使用 `RunAsync()`；驱动持有目标运行锁，底层工具运行仍由现有 runtime 锁保护。状态冲突拒绝覆盖，调用者可重新加载后重试。

## 运行

以下使用新的 data/goals 目录；已有状态不会被覆盖。

```powershell
dotnet build src/LittleLoop.Cli -m:1 --disable-build-servers
New-Item -ItemType Directory -Force data/goals/workspace
dotnet run --no-build --project src/LittleLoop.Cli -- learn-init data/goals/state.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-enable data/goals/state.json examples/goals/limits.json
dotnet run --no-build --project src/LittleLoop.Cli -- goals-add data/goals/state.json examples/goals/normalize.json
dotnet run --no-build --project src/LittleLoop.Cli -- goals-run data/goals/state.json examples/goals/policy.json
```

示例故意提供缺失来源。系统实际读取失败后，改用备用内容，自动创建输入、规范化并读回；共四次工具调用、两版方案。输出为 `data/goals/workspace/normalized.txt`，状态的 goals 包含完整进度。`learn-show` 查看状态；既有 `learn-replay` 可重放每条子任务决策，不会再次执行工具。

## 验证与范围

`GoalChecks` 检查真实缺失来源重规划、内联内容三步组合、输出冲突与授权另存、预算/工具许可/重试限制、多目标共享核心、产物篡改检测、未知执行对账恢复和完成后重启不重做。运行全部检查：

```powershell
dotnet build tests/LittleLoop.Checks -m:1 --disable-build-servers
dotnet run --no-build --project tests/LittleLoop.Checks
```

当前仍是有限领域的符号规划，操作模型由工程实现提供；没有任意 shell/网络调用、自动发明新工具、长期目标调度、自动补偿或通用业务语义评估。经历影响子任务审议；跨目标自动学习新的规划算子尚未实现。目标完成之后的新人工反馈会影响后续决策，但不会自动重开旧目标。生成的中间输入保留供审计，不自动删除。
