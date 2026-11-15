# 执行中的模型规划与核心裁决

模型现在参与初始任务拆解，以及取得实际回执后的剩余方案调整。它提供候选步骤和解释；核心检查依赖、目标、权限和预算，再由角色审议每一个行动。入口语义理解、计划建议、核心裁决与执行观察分别留档。

## 运行过程

1. 自然语言入口提取不可被规划器擅自修改的目标要求、资源与约束。
2. 模型收到注册操作目录、当前已观察事实、剩余预算、角色边界、最近回执与反馈，提出剩余工具链。
3. 核心只接受目录中的操作 ID，检查前置条件、参数资源、工具许可、成本和最终要求，禁止重复已完成效果。
4. 原有角色审议结合偏好、经历和边界决定下一步；合法模型计划也可能被角色拒绝。
5. 实际执行后更新事实，再请求模型判断剩余步骤。目标完成后停止模型调用；执行结果未知时等待对账，不让模型猜测或重复写入。

模型可从用户已提供的备用来源中选择输入。换源会使 source.readable 失效，必须重新读取；输出创建后禁止换源。目标内容、输出要求、权限和预算不能被模型修改，输出碰撞另存仍由原有明确授权控制。

当前复杂度增加体现在依赖拆解、根据新观察调整剩余步骤、多次失败恢复和执行中澄清。仍只覆盖注册的 UTF-8 文本操作，尚不是任意业务任务规划或自动创造新工具。

## 新信息、失败和追问

模型上下文包含最近至多 8 条本目标回执的状态、失败类型、实际 outcomes、产物 SHA-256、反馈和有界读取文本。maxObservationCharacters 默认 2048，设为 0 可禁止发送文件文本，仍发送状态和元数据。片段截断有标记；目标和约束不静默截断，整个请求超过 maxInputCharacters 时停止并记录原因。

启用模型规划会向配置的服务发送实际工具观察，其中可能包含工作区文本。凭证文件不在工具工作区内，隐藏路径也被工具拒绝。观察与回答均作为数据，不能覆盖宿主协议。

模型可返回 ask，使目标进入 awaitingClarification 并停止后续动作。goal-model-chat 显示问题并接收回答；退出后可用同一对话 ID 继续。回答只澄清原目标内的选择，不扩大权限。模型拒绝、核心拒绝和角色拒绝各有审计原因。

服务失败保留为追问，不自动降级到本地计划执行。每个目标默认最多 8 次规划尝试（包括失败），maxPlanningCalls 上限为 64；工具累计预算和失败重规划次数分别生效。成功观察后的重新审议不增加失败重规划次数，但消耗规划次数。进程中断前未提交的模型请求可能已计费，不保证外部 API exactly-once。

## 配置和命令

examples/models/deepseek.json 已启用 enablePlanning=true、maxPlanningCalls=8、maxObservationCharacters=2048。设 enablePlanning=false 可对新对话仅使用模型解释入口；已经有规划审议历史的目标不能静默切换为本地规划。

```powershell
dotnet run --project src/LittleLoop.Cli -- goal-model-chat data/language/state.json examples/language/policy.json examples/models/deepseek.json task
dotnet run --project src/LittleLoop.Cli -- goals-run-model data/language/state.json examples/language/policy.json examples/models/deepseek.json
dotnet run --project src/LittleLoop.Cli -- goals-answer data/language/state.json 'language:task' '继续使用原目标中允许的备用来源。'
```

goals-answer 只记录澄清并恢复 Ready，随后运行 goals-run-model。goals-run-model 明确选择模型规划，不依赖 enablePlanning 开关。多个目标继续共享角色核心和工具执行额度。

每轮 PlanningReview 保存上下文、实际响应、来源、核心 verdict、原因及触发回执 ID。被拒方案不会进入可执行计划；接受的完整剩余方案进入 PlanRevision。下一子任务与进度原子保存，审议历史只能追加。网络期间不持有状态写锁，revision 冲突会拒绝迟到响应。

## 本地密钥自动加载

项目根目录的 `.env.local` 保存本机凭证：

```text
DEEPSEEK_API_KEY=your-local-key
OPENAI_API_KEY=your-local-key
```

进程环境变量优先，项目 `.env.local` 为后备。CLI 从模型配置文件位置寻找项目根目录；类库也可从当前目录或程序目录向上寻找项目。从其他工作目录启动时传入正确的项目配置路径即可。不执行 shell、不展开变量、不导出其他配置项。

`.env.local` 是本机明文配置，由 `.gitignore` 的 `.env.*` 排除；可追踪的 `.env.example` 只有空值。此前提供的 DeepSeek 密钥已写入本地文件，已用 git check-ignore 和 git ls-files 验证忽略和未追踪。密钥不进入模型上下文、回执或状态审计。

## 验证和范围

PlanningModelChecks 覆盖实际失败后选择另一授权来源、读取新内容后换源并重新读取、未知工具/依赖/不完整目标拒绝、角色拒绝、持久化追问与恢复、规划次数限制、服务失败、禁止静默降级、revision 冲突和凭证读取优先级。

2026-11-14 实际 DeepSeek 运行位于 data/planning-demo-8a275d03：live-planning-v2 完成四次规划审议、四次工具调用，一次真实文件缺失后恢复，产物规范化并读回验证成功。上下文随每次回执更新。首次 live-planning 因模型省略可选 question 字段被拒绝，未执行工具；修正可选字段兼容后以新对话验证，原拒绝记录保留。密钥自动从 .env.local 读取，未手工设置进程环境变量。

可控模型测试验证控制逻辑，不等同于任意任务语义正确率。尚无通用层级子目标学习、补偿事务、任意工具发现或开放领域规划能力。
