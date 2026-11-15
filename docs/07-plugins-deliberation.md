# 插件与开放决策首版

后续增量：[角色审议流程 v2](08-role-deliberation.md) 已实现。输入包含 `cognition` 时采用 v2：不使用插件价值分排序，改用前提检验、角色承诺、经历检索及目标/偏好取舍。本文的评分与示例描述保留为无 `cognition` 输入的 v1 兼容路径。

2026-11-14 用户明确要求实现插件系统，并指出核心应能跳出既定选项，甚至拒绝。这里将该意图落为可扩展机制；具体接口和评分方式属于工程选择，并非用户逐项确认的算法。

## 当前实现

`IDecisionPlugin` 声明 ID、实现版本、契约版本，接收目标、角色版本、风险阈值、事实、原始选项及权限预算，返回带理由、价值和风险估计的建议。`PluginHost` 校验注册、按注册顺序调用、隔离普通异常和无效响应，记录全部响应。每个插件接收独立集合副本。单插件至多返回 64 项，重复 ID 和越界评分会使该次响应失败。

CLI 只加载命令行显式指定的受信任 DLL，发现公开的 `IDecisionPlugin` 实现并调用无参构造器。契约版本必须为 1；重复插件 ID 拒绝加载。依赖从插件输出目录解析，共享核心契约程序集。没有目录自动扫描、热更新或跨进程隔离。进程内插件具有宿主进程权限；集合副本和异常处理不是安全沙箱，也不能终止死循环或限制内存。只加载自己信任的代码。

核心将原始选项与插件新建议合并，不要求建议属于原选项。结果有 Act（行动）、Ask（提出问题）、Reframe（重审前提/提出不同问题）、Defer（暂缓）、Refuse（拒绝目标）。后四者是决策记录，不发送消息或执行工具。Refuse 不是权限拦截：它可以在原行动合法可行时，因目标与角色判断冲突被主动选择；权限不足则使候选不可选，并不会冒充自主拒绝。

首版按 `Value - Risk` 排序，在风险阈值、预算、行动能力许可内选取候选；平分按来源 ID、候选 ID 的 ordinal 顺序决定。核心始终加入零分暂缓项。因此没有有价值的可行建议时可以不行动。插件不能靠高分越过约束。评分仍是插件提供的显式启发式，不是经校准的推理、价值体系或人类自主性。角色目前仅有版本和风险阈值；学习、复杂价值冲突、多轮推演和大模型协作仍待后续实现。

示例插件分别根据记录事实提出不同路线、重审错误前提、拒绝与目标冲突的请求。这些是规则演示，事实和理由保留来源，不宣称已验证其真实性。首版只产出决策，不执行新行动；将来接入执行器必须再次检查实时权限和剩余预算。

## 运行与验证

```powershell
dotnet build src/LittleLoop.Cli -m:1
dotnet build src/LittleLoop.Plugins.Example -m:1
dotnet run --no-build --project src/LittleLoop.Cli -- deliberate examples/deliberation/alternative.json src/LittleLoop.Plugins.Example/bin/Debug/net10.0/LittleLoop.Plugins.Example.dll > trace.json
dotnet run --no-build --project src/LittleLoop.Cli -- replay-deliberation trace.json
dotnet build tests/LittleLoop.Checks -m:1
dotnet run --no-build --project tests/LittleLoop.Checks
```

将输入换为 `refuse.json` 或 `reframe.json` 可观察另外两种结果。省略插件 DLL 则仅评估输入候选和核心暂缓项。重放使用记录的插件输出重新计算核心判断，不重新加载 DLL，不调用插件、不执行行动。它检查记录内部的一致性，不认证日志真实性，也不验证插件本身能再次生成相同响应。

原 `run` / `replay` 与 `ticket-rules/1` 完整保留，阶段一仍是模拟工单闭环；新流程是后续能力的增量，不表示阶段二及以后全部完成。
