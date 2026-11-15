# 自然语言目标与持久化追问

自然语言入口已接入现有目标规划、角色审议、真实工具和经历学习。用户不再填写 DocumentGoal JSON，也不需要指定步骤或工具：在对话中描述输入、处理要求、输出和约束，系统理解后创建结构化目标，规划器自动组合工具完成。

本文描述本地、确定性的中文/英文文本领域语法解释器 `document-language/1`，无需联网或模型密钥。这是有限领域自然语言解析。后续已增加[可替换大模型语义适配器](14-model-semantics.md)，自由描述可使用 `goal-model-chat`，本文的本地模式继续保留。无法解释的片段会触发追问，不会假装完成不支持的要求。

## 交互

完整描述示例：

> 请把 notes.txt 规范化，保存为 clean.txt，不要覆盖已有文件，最多使用六次工具，重试 2 次。

系统提取输入文件、规范化要求、输出位置、禁止覆盖、预算与重规划限制；随后自动形成“读取 → 规范化 → 独立读回验证”。支持中文数字（零至九十九的常见写法）、ASCII 文件名不加空格的中文描述，以及英文 `Please normalize source.txt and save as result.txt, at most 5 tool calls.`。中文文件名、带空格的路径请加引号，如 `“项目 笔记.txt”`。

信息不足时逐轮追问：

```text
用户：帮我整理一下文件
系统：要处理哪个文件？也可以回答：内容是“要保存的文本”。
用户：notes.txt
系统：希望规范化，还是保留格式原样复制？
用户：保留格式，不要规范化
系统：结果保存到哪个新文件？
用户：preserved.txt
系统：已理解目标，自动规划并执行。
```

“整理”不会被擅自解释为修改内容；“不要规范化”映射为保留格式。规范化同时将换行统一为 LF 并去掉行尾空白。若只请求其中一种变换，会先说明当前工具的组合效果并追问，避免扩大修改范围。互相冲突的格式、预算或路径要求必须明确解决；回答其他问题不能消除冲突。

来源可写成 `源文件是 a.txt，备用文件是 b.txt`，备用文本写成 `内容是“备用文本”`。工具失败后的恢复沿用已提供的资源。`如果文件存在，可以另存` 只授权输出碰撞时换路径；默认不覆盖、不另存。任意其他条件、时间限制、翻译、删除、发送邮件等未知要求会要求重新描述完整目标。笼统回答“好的”不会被当作丢弃未知约束的同意。

引号中的内容始终是数据，包括命令式文字；不会把文件内容或文件名作为新的对话指令。工具权限只来自宿主策略，用户文本不能扩大它。目标提交前不执行任何工具；目标提交后仍受角色边界、预算、工具白名单和工作区约束。

## CLI

使用新的状态目录（已存在的状态不会被覆盖）：

```powershell
dotnet build src/LittleLoop.Cli -m:1 --disable-build-servers
New-Item -ItemType Directory -Force data/language/workspace
dotnet run --no-build --project src/LittleLoop.Cli -- learn-init data/language/state.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-enable data/language/state.json examples/goals/limits.json
dotnet run --no-build --project src/LittleLoop.Cli -- goal-chat data/language/state.json examples/language/policy.json demo
```

`goal-chat` 接收自然语言、显示追问；信息齐全后自动运行工具。`/exit` 或输入结束会保留进度，同一个对话 ID 可在重启后继续。也可以用提供的纯文本对话跑完整示例，无需准备输入文件：

```powershell
Get-Content examples/language/conversation.txt | dotnet run --no-build --project src/LittleLoop.Cli -- goal-chat data/language/state.json examples/language/policy.json example
```

外部 UI/脚本可逐次提交话语（只理解和入队，不立即运行工具）：

```powershell
dotnet run --no-build --project src/LittleLoop.Cli -- goal-say data/language/state.json another '规范化 notes.txt，保存为 another.txt'
dotnet run --no-build --project src/LittleLoop.Cli -- goals-run data/language/state.json examples/language/policy.json
```

宿主策略和运行时预算仍由部署方配置；用户无需编写结构化目标。未指定时单目标默认预算 12 次调用、最多重规划 3 次，始终独立读回验证。单条消息最多 8192 字符，每段对话最多 64 轮。

## 状态与验证

Core 定义 `IGoalInterpreter`、理解快照、问题和原文证据契约；Tools 提供当前领域解释器及到文档目标的编译。每轮保存原话、回答的问题、提取字段、证据来源轮次、解释器版本和剩余问题。最终对话与目标创建在同一个 expected revision 事务中提交；不会因中断只保存一半，也不能用相同对话重复创建目标。`learn-show` 可检查完整解释记录，既有 `learn-replay` 可重放子任务决策。

`LanguageChecks` 覆盖中英文和否定表达、多轮持久化、来源与预算提取、解释重放、未知附加要求、引用内容、冲突保留、部分变换澄清、版本冲突、重复提交，以及真实文件执行和失败后备用内容恢复。

```powershell
dotnet build tests/LittleLoop.Checks -m:1 --disable-build-servers
dotnet run --no-build --project tests/LittleLoop.Checks
```

本地模式不支持任意口语、跨对话指代、从目录猜测“那份文件”、自由业务规划或提交后通过对话改写正在执行的目标。无法理解的请求需要完整重述；已提交目标的修订使用已有任务机制或新对话。大模型调用见文档 14，语言规则自动学习仍未实现。执行经历仍由原有核心积累，不将语法解析结果当作实际执行成功。
