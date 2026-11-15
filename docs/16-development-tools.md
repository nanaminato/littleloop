# 开发任务执行闭环

本阶段增加 `dev-run`：自然语言开发目标由可替换模型拆解，每次只提交一个下一步动作。项目检索、读取、精确补丁、固定命令和测试均实际执行；回执进入原有角色审议、经历与反馈系统。模型可以根据失败调整计划、追问或拒绝，核心仍独立决定是否执行。

后续[多文件真实项目开发](17-repository-development.md)补充分页结构浏览、源码范围读取、受保护测试基线和 .NET SDK 临时环境；本页保留最小示例与基础协议说明。

## 可复现实验

需要 .NET 10、PATH 中的 Python 3，以及 `examples/models/deepseek.json` 对应的本地凭证。项目根目录 `.env.local` 自动加载且被 Git 忽略，环境变量优先。也可传入其他兼容模型配置。

```powershell
dotnet build src/LittleLoop.Cli
./examples/development/prepare.ps1
dotnet run --no-build --project src/LittleLoop.Cli -- learn-init data/development-demo/state.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-enable data/development-demo/state.json data/development-demo/limits.json
dotnet run --no-build --project src/LittleLoop.Cli -- dev-run data/development-demo/state.json data/development-demo/policy.json examples/models/deepseek.json fix-add "修复项目中的加法错误，先用现有测试复现问题，再修复并验证正数和负数加法。不得修改测试。"
```

准备脚本拒绝覆盖已有目录；重复实验可传入新的 `-Destination`。模型自己检索、读取、选择工具及构造补丁，用户不填写步骤。运行状态在项目目录外，重启时使用相同 `dev-run` 参数并省略目标文本。需要追问时先运行 `dev-answer <state> <id> <回答>` 再恢复。请使用独立开发状态，或先完成该状态中其他类型的待执行任务。

## 工具与授权

| 工具 | 行为与限制 |
|---|---|
| project.search | 有界文件清单或字面内容检索；忽略隐藏目录、构建产物、链接 |
| project.read | 有界 UTF-8 内容及实际字节 SHA-256 |
| project.patch | 仅宿主指定 writableFiles；必须先读取，哈希匹配，旧文本恰好出现一次 |
| project.command | 仅选择宿主配置的命令 ID；绝对 executable 和固定 arguments，不接受模型生成的 shell 命令 |

策略还规定读取扩展名、文件数量/大小、输出/时间、动作预算、模型次数和失败次数。测试命令的 protectedFiles 不可被补丁工具修改。每步执行仍经过角色目标、边界和经历判断，授权一个工具并不意味着角色必须选择它。策略哈希随目标持久化，不能在恢复时悄悄扩权。

**命令执行不是操作系统沙箱。** 宿主必须信任授权命令及其加载的项目代码；固定命令也可能通过代码访问项目外文件或网络。子进程清空继承环境，仅保留 Windows 系统/安装目录，并设置项目内临时用户目录和 SDK 缓存，避免直接继承模型密钥，但这不是凭证访问隔离。运行不可信项目需要另外配置容器或 OS 隔离。文件工具的路径检查也不提供对恶意并发文件系统替换的内核级隔离。

## 失败、验证与经历

真实退出码、stdout/stderr、文件哈希及执行前后项目快照进入持久化回执。已知非零退出允许模型修复后重测；失败经验按工具、参数及源码快照匹配，避免把旧代码失败永久套用在新代码上。反馈继续使用已有 `learn-feedback`，下一轮规划可见最新反馈。这是情境经历学习，不是训练模型权重。

核心只允许在发生实际代码修改，且所有必需测试在最终同一项目快照通过后完成。后续编辑或命令改变源码会使旧测试证明失效，模型不能直接宣称成功。快照只覆盖策略可读取的项目文件，不覆盖排除的产物、外部依赖或机器环境；测试通过也不等于证明所有业务需求正确。

超时、取消、崩溃或不明确的写入结果进入 NeedsReconciliation，不自动重试副作用。补丁在独占文件句柄下再次校验后写入，进程崩溃仍可能留下部分写入；需要人工检查实际项目再决定如何继续。当前不提供自动回滚、依赖安装、任意 shell、通用 Git 操作或 OS 沙箱。

回归入口：`dotnet run --project tests/LittleLoop.Checks`。开发检查使用真实 Python 测试进程以及确定性模型，覆盖失败修复、再次失败后的调整、最终快照验证、角色拒绝、路径/命令/补丁越权、并发编辑、超时和子进程凭证环境隔离。
