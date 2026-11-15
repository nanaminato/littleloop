# 可替换大模型语义适配器

大模型现在可直接解释自由描述的目标和澄清回答，不必先匹配固定语法。模型返回候选字段、问题、原文证据与不支持的要求；宿主校验后才提交目标，沿用既有规划、角色审议、真实文件执行、回执与反馈经历。

## 接口与边界

- Core 的 `IAsyncGoalInterpreter` 提供可取消的异步理解接口，原有本地 `IGoalInterpreter` 保留。
- `ISemanticModelClient` 隔离模型服务；`HttpSemanticModelClient` 支持 `chat-completions` 和 `ollama` 两种协议。DeepSeek 与 OpenAI Chat Completions 共用前一种适配器，但 endpoint、model、密钥环境变量、token 参数和 reasoning effort 分别配置。没有把 DeepSeek 写死在认知核心。
- `ModelGoalInterpreter` 将完整用户轮次、先前字段、未解决问题和宿主预算发送给模型；入口解释不读取或上传工作区文件、经历库或密钥。用户直接提供的内联文本属于对话，会随请求发送。后续启用模型规划时可发送有界工具观察，范围见文档 15。
- 当前语义仍映射到文本工具支持的目标：原样复制/创建，或统一 LF 换行并去掉行尾空白。自由表达范围扩大，不代表工具能完成翻译、邮件、任意 shell 或其他业务。

模型不能选择执行器、申请权限、制造执行成功或直接调用工具。宿主检查字段白名单、字段数量、枚举、范围、相对路径、输入输出不同、强制验证和禁止覆盖。每个模型字段必须有真实对话的原文证据；路径与内联内容必须在证据中逐字出现。已保存的约束默认保留，变更必须引用当前轮。能由本地语法明确识别的约束还会交叉核对，固定语法在这里用于补充校验，而非限制模型只能理解固定句式。

缺失字段即使模型忘了追问，宿主也会生成问题。旧问题只有在当前回答有相应证据时才解除。不支持的要求必须澄清；结构错误、重复 JSON 字段、未知字段、伪造引文、空响应、截断、意外 tool call 均不生成可执行目标。证据检查能发现凭空引用，但不能数学证明自由语言解释无遗漏或无误解；不确定性仍依赖模型识别和用户澄清。

## 运行

先使用已有方式创建状态、工作区和调度预算（见文档 13）。配置只保存密钥环境变量名，不保存密钥值。示例配置：

| 配置 | 协议与用途 |
| --- | --- |
| `examples/models/deepseek.json` | 已实测的 DeepSeek Chat Completions，模型 `deepseek-flash` |
| `examples/models/openai.template.json` | OpenAI Chat Completions；填入账号可用且支持 JSON mode 的模型名 |
| `examples/models/ollama.template.json` | 本机 Ollama；填入已安装的模型名 |

在运行进程的环境中设置配置指定的 `DEEPSEEK_API_KEY` 或 `OPENAI_API_KEY`，也可写入被 Git 忽略的项目 `.env.local` 以便自动加载（见[执行中的模型规划与本地配置](15-model-planning.md)），然后运行：

```powershell
dotnet build src/LittleLoop.Cli -m:1 --disable-build-servers
dotnet run --no-build --project src/LittleLoop.Cli -- goal-model-chat data/language/state.json examples/language/policy.json examples/models/deepseek.json model-demo
```

可直接描述：

> notes.txt 是同事从 Windows 拷来的，换行很乱，每行结尾还留着空格。帮我统一成 LF 并去掉那些行尾空白，另放到 clean.txt，原件别动，工具调用最多六次。

或分轮说明：

> 给 notes.txt 留个副本就好，排版和空白都别碰。放哪儿我还没想好。

系统先问输出路径，回答 `就用 preserved.txt 吧，别改动原文件。` 后自动规划并执行。

非交互入口只理解和提交目标，工具由后续 `goals-run` 驱动：

```powershell
dotnet run --no-build --project src/LittleLoop.Cli -- goal-model-say data/language/state.json examples/models/deepseek.json another '请保留 notes.txt 的每个空格，给我一个 backup.txt 副本。'
dotnet run --no-build --project src/LittleLoop.Cli -- goals-run data/language/state.json examples/language/policy.json
```

`goal-chat`/`goal-say` 仍使用本地语法。模型模式失败后不会自动降级为语法模式执行，防止漏掉自由描述中的约束。

## 限制、故障与重放

默认单次模型超时 60 秒、输出 4096 tokens、响应最多 128 KiB、序列化对话最多 32768 字符；模型目标预算上限 12。预算未指定时取 `min(12, maxGoalBudget)`，更大目标须由宿主配置允许。API 输出 token 限制和工具预算是两个独立维度。只执行一次请求，不自动重试，不默默截断历史，不自动跟随重定向；远端 endpoint 必须 HTTPS，本机 loopback 可用 HTTP。

服务不可用、401、429、超时、截断等保存为明确失败与追问，没有工具副作用。用户可稍后回复“重试”；调用者取消则不提交半条对话。网络调用期间不占状态写锁，若其他写入改变 revision，拒绝提交迟到解释，重新加载后再处理。原有工具未知结果仍须对账后恢复。

每轮保存服务来源、请求/实际模型名、提示版本、上下文 SHA-256、模型内容、失败代码和宿主预算上限。密钥只进入 Authorization header；不保存 HTTP header/错误响应体，检测到密钥回显时拒绝将其写入审计。凭证配置、完整 HTTP 包和模型隐藏推理不进入对话状态。

```powershell
dotnet run --no-build --project src/LittleLoop.Cli -- goal-model-replay data/language/state.json model-demo
```

重放校验上下文摘要并使用保存的响应重新执行宿主校验，不联网、不执行工具。它验证本版本解释/校验一致性，不是让非确定性模型重新生成答案。

## 已验证与未验证

2026-11-14，DeepSeek 真实服务完成三组 CLI 任务：自由描述规范化（3 次工具）、主动追问后保留格式复制（3 次工具）、缺失来源后使用备用来源（4 次工具，1 次实际失败与重规划）。输出位于本地 `data/model-demo-9a9397d3/workspace`，三段语义历史均离线重放通过。最初沙箱网络不可达的失败记录保留，获准联网重试后完成，没有伪造成功或隐藏失败。

离线 `ModelLanguageChecks` 通过可控 HTTP handler 覆盖两种协议、真实文件执行、反馈否决后续模型目标、重放、假引用、未知/重复字段、必需字段追问、错误状态、截断、字节上限、超时、取消、网络期间 revision 冲突和凭证回显过滤。HTTP handler 测试不能证明任意远程服务或模型都兼容；OpenAI 与 Ollama 本轮未进行真实服务调用。尚无 OpenAI Responses、Anthropic 协议适配器，也未做大规模语义准确率评测或语言模型训练。

接口依据：[DeepSeek Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/)、[DeepSeek JSON Output](https://api-docs.deepseek.com/guides/json_mode/)、[DeepSeek 模型列表](https://api-docs.deepseek.com/quick_start/pricing/)、[OpenAI Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[Ollama Chat](https://docs.ollama.com/api/chat)。配置示例基于本轮查询；具体模型名与可选参数仍由部署方按服务能力配置。
