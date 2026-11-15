# 实际文件工具与执行反馈闭环

2026-11-14 用户明确要求进入实际工具执行阶段，真正完成任务并从结果与反馈积累经历。本次实现受许可工作区内的真实文本文件工具，直接读写文件系统；没有模拟环境的结果映射。能力范围是结构化文件任务，不是任意自然语言任务或通用桌面代理。

## 可执行能力

`LittleLoop.Tools` 独立于认知核心，提供工具目录、候选方案插件和 `IAsyncActionExecutor` 实现。

| 工具 ID / 能力许可 | 参数 | 验证后的结果 |
| --- | --- | --- |
| text.read | source | text.read；返回实际 UTF-8 内容 |
| text.create | target、content | file.created、text.written |
| text.copy | source、target | file.created、text.copied；字节保持原样 |
| text.normalize-lines | source、target | file.created、text.normalized；CRLF/CR 转 LF，去除每行末尾空格和制表符 |

工具仅创建新文件，不覆盖既有目标，不删除源文件。空文件可处理，UTF-8 解码严格检查，不静默替换非法字节。所有写操作先写同目录临时文件，再以“不覆盖”方式发布，随后读回比较实际字节。并发写同一目标最多一个成功，其他操作记录拦截或失败。

成功结果来自工具实际检查，不照抄候选的 `Meaning.Outcomes`。回执保存工具版本、工作区根、输入/输出相对路径、字节数与 SHA-256；读取工具还返回实际文本。技术层面的写入成功不保证符合人的意图，后续语义反馈可以将该次经历判断为失败，而不篡改原文件操作回执。

## 从目标到真实操作

`tasks-run-tools` 自动注册 `WorkspaceToolPlugin`。该插件根据当前结构化目标、许可和可用参数，提出目录内已知工具方案；任务可保持 `options: []`。核心仍检查角色、目标、前提、预算和风险，再由实际执行器完成行动。

`Proposal.Tool` 只保存工具 ID 与参数绑定，例如 source → source、target → target；`LearningTask.ToolArguments` 保存本次具体文件名或内容。它与用于经历匹配的语义 `Facts` 分开。召回成功经历后，复用的是“采用何种工具”，路径和内容重新绑定本次参数，不会把上一次的目标文件名带到新任务。缺少所需参数时核心输出 Ask。

执行完成自动形成原有学习系统的经历；实际文件不存在、编码错误等操作失败也会记录。工具操作失败仅在同样语义情境、目标和参数下作为反例，避免一次“缺失文件”阻止读取所有别的文件。带来源的显式评价反馈则仍可影响相同语义情境的策略选择，例如指出“本类文档的尾随空格有意义”，从而让后续任务改选保留格式的复制。此概括依赖提供的情境标签与反馈，不是自动理解任意自然语言教训。

## 许可与执行边界

策略文件指定现有工作区根目录、允许的工具、单文件字节上限和协作式超时。root 相对策略文件所在目录解析；运行状态文件必须位于工具工作区之外。默认单文件上限 64 KiB，可配置到 1 MiB；超时默认 10 秒，最大 60 秒。执行器校验工具真实能力、最低动作成本和风险，不能通过把候选成本写成 0 绕过预算。

工具参数只能使用工作区相对路径，禁止绝对路径、`..`、隐藏路径组件、Windows 设备名与替代数据流；检查已有路径组件的符号链接/目录联接。目标父目录须已存在。许可同时需要当前任务允许该能力和宿主策略允许该工具；不能由候选自行扩大根目录或工具许可。

执行前及发布输出前，执行器重新检查持久状态中的任务版本、当前决策、派发状态和许可。进程内状态读写通过短互斥区串行化，真实文件 I/O 在其外并发执行；跨进程写锁仍保留。工具完成后，原有回执关联、迟到版本保护和反馈机制照常生效。

这是可信本地工作区的操作限制，不是操作系统沙箱。它不能抵御其他进程恶意竞态替换目录，也无法把任务版本检查与外部文件系统提交做成一个跨系统原子事务。政策文件作为本次运行的配置快照；修改它在下次运行生效，运行中可通过取消/更新任务撤销尚未完成的授权。同步插件仍是可信进程内代码，不能用这些限制约束任意第三方 DLL。

取消和超时是协作式的；提交之后取消，文件可能已经存在。此类未知结果保留原调度器的 NeedsReconciliation 状态，不盲目重执行。临时文件清理失败也不伪造无副作用。回执的 ExternalEffect 表示发生过文件写入，含失败前的临时写入；不代表最终目标一定已经创建。

重放只计算原决策并返回记录的执行证据，不再读取或写入原文件。之后人工修改文件不会被重放覆盖，历史摘要只证明当时观察到的内容。

## 无手工候选、无手工经历的完整演示

从仓库根目录执行。使用全新的 `data/real-tools` 状态和空工作区；已有目标不会被覆盖，重复演示请改用新的状态、策略 root 和目录。

```powershell
dotnet build src/LittleLoop.Cli -m:1
dotnet run --no-build --project src/LittleLoop.Cli -- tools-list
New-Item -ItemType Directory -Force data/real-tools/workspace
dotnet run --no-build --project src/LittleLoop.Cli -- learn-init data/real-tools/state.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-enable data/real-tools/state.json examples/real-tools/limits.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-add data/real-tools/state.json examples/real-tools/create-a.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-add data/real-tools/state.json examples/real-tools/create-b.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-run-tools data/real-tools/state.json examples/real-tools/policy.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-add data/real-tools/state.json examples/real-tools/normalize-1.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-run-tools data/real-tools/state.json examples/real-tools/policy.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-add data/real-tools/state.json examples/real-tools/normalize-2.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-run-tools data/real-tools/state.json examples/real-tools/policy.json
dotnet run --no-build --project src/LittleLoop.Cli -- learn-feedback data/real-tools/state.json examples/real-tools/feedback.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-add data/real-tools/state.json examples/real-tools/preserve.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-run-tools data/real-tools/state.json examples/real-tools/policy.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-add data/real-tools/state.json examples/real-tools/read.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-run-tools data/real-tools/state.json examples/real-tools/policy.json
```

顺序为：工具实际创建两个源文件 → 规范化第一个 → 从经历召回做法处理第二个 → 接收“需要保留格式”的反馈 → 改用复制产生 preserved.txt → 实际读回内容。所有任务 `options` 为空，实际结果由工具产生。示例反馈引用此全新演示中固定的决策 ID `task:normalize-1/v1/turn2`；已有历史中须使用实际决策 ID。

`tasks-run-tools` 可追加其他受信任候选插件 DLL；无需另外加载内置文件工具插件。新工具可在独立执行器中实现相同接口，本阶段未开放任意 shell、网络、应用自动化或通用多步规划。

## 验证

```powershell
dotnet build tests/LittleLoop.Checks -m:1
dotnet run --no-build --project tests/LittleLoop.Checks
```

`RealToolChecks` 实际创建、读取并检查磁盘文件，覆盖跨文件经历复用、反馈改变工具选择、输出摘要、真实失败、参数作用域、重放无副作用、目录/符号链接边界、禁止覆盖、容量和许可、成本伪报、候选伪造结果、更新任务后执行拦截、重复执行、并发写同一文件及取消。并发文件工具还验证了状态读取与原子保存的协调。原模拟、角色、学习和调度检查继续保留；这些机制检查不是开放环境自主能力的评测结论。
