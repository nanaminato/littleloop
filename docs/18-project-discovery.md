# 项目路径入口、自动发现与权限申请

现在可以直接输入项目路径与自然语言目标，无需先编写文件白名单、命令 JSON、角色数据或运行时状态：

```powershell
dotnet run --project src/LittleLoop.Cli -- dev-project C:\work\my-project "修复分页边界问题并通过项目测试"
```

默认复用 LittleLoop 的 DeepSeek 配置及被 Git 忽略的本地凭证。可指定其他模型配置与持久会话目录：

```powershell
dotnet run --no-build --project src/LittleLoop.Cli -- dev-project C:\work\my-project "开发目标" examples/models/deepseek.json C:\work\littleloop-sessions\task1
```

会话必须在目标项目之外；默认位于系统临时目录的 `LittleLoop/projects/<id>`，路径会显示出来。需要长期保留时显式指定目录，临时目录可能被系统清理。

## 发现结果不是授权

入口只做本地只读扫描，不加载模型、不执行项目脚本。它生成 `job.json`：项目证据、问题、自动推导的源文件修改范围、固定命令、测试保护范围、项目快照和唯一申请 ID。初次运行显示具体范围供审查。

```powershell
dotnet run --no-build --project src/LittleLoop.Cli -- dev-project-show <会话目录>
dotnet run --no-build --project src/LittleLoop.Cli -- dev-permit <会话目录> <申请ID> approve
dotnet run --no-build --project src/LittleLoop.Cli -- dev-project-resume <会话目录>
```

也可以 `deny`。批准只记录权限，不立即执行。批准前会重新核对发现时的项目快照；变化后需要重新发现，不能批准一份已经过时的范围。首次授权还明确说明源码会提交给所选模型，以及命令可能访问网络或项目外文件。

当前自动适配范围：

| 项目线索 | 推导行为 |
|---|---|
| `.csproj` | 构建命令；IsTestProject/Test SDK 测试入口；tests、`.Checks`、`.Tests` 下的控制台验收入口 |
| `.NET ProjectReference` | 记录引用；超出根目录或动态引用时要求澄清范围 |
| `test_*.py` / `tests.py` | 标准 unittest discovery；必须实际执行至少一个测试，零测试退出码 0 不算通过 |
| `package.json` | 读取脚本声明，提出适配/执行权限问题；本版本不自动执行 npm 脚本 |
| 无测试、缺少 SDK、pytest 线索等 | 输出具体问题，停止；不会把构建成功当作完成 |

发现采用有界、保守的适配器，不声称识别所有工程习惯。测试目录与清单是候选证据，需要审查；自定义参数、复杂 monorepo、未知框架应补充适配器或使用原有显式策略入口。`needs-clarification` 阶段尚未建立执行状态：修正 SDK、选择共同根目录或补齐适配器后重新调用入口，不能用批准绕过未解决的问题。

默认只允许修改已存在的源文件；测试、项目清单、隐藏文件、凭证及构建产物不进入默认可写列表。所有发现的必需测试都要在最终快照通过。现有文件数量、大小、输出、时间和决策预算限制仍适用。

## 运行中的扩权

.NET 初始命令带 `--no-restore`。依赖恢复是独立候选，不在初始可执行清单中。模型遇到缺失依赖时可提出 `request_permission`，必须给出具体候选 ID、理由和风险；宿主显示固定 executable 与 arguments。模型也可申请修改范围内的现有非测试文件，但不能通过此接口获取隐藏文件、项目外路径、受保护验收文件或任意 shell。

申请后状态暂停，不产生工具调用。使用同一个 `dev-permit` 命令处理，然后恢复原会话。普通 `dev-answer` 不能批准权限。批准记录旧/新策略哈希、申请 ID、资源、理由与决定；原目标、预算、规划、执行回执、角色与经历不重置。测试证明在扩权后清空，仍须重新验证。批准过的候选从待申请列表移除，审批记录进入模型上下文；重复申请被纠正，不会重复授权。

权限拒绝会停止任务；未知副作用仍需对账，不能以扩权为由重试。首次授权和扩权由可信宿主 API/CLI 接受，模型只获得提出申请的接口。和原执行器一样，这不是操作系统安全沙箱：项目代码、构建钩子与已授权命令仍可能产生范围外副作用。

模型结构错误保留原响应与校验理由，最多连续纠正三次，并计入原模型调用预算。修复适配器后，可以用 `dev-project-retry <会话目录>` 明确重试被拒绝的规划，再恢复；不会清除历史、增加预算，也不能恢复核心拒绝或未知执行结果。

## 验证记录

`examples/development/prepare-discovery.ps1` 只复制 LittleLoop 真实源码、添加独立验收工程并提供模型配置，**不生成文件/命令策略**。实测输入只有项目路径和自然语言 CLI 开发目标。入口识别出五个 .NET 构建/测试命令及单独的恢复候选。

`data/discovery-demo/session-v3` 中，实际 DeepSeek 在缺失 assets 文件导致失败后提出恢复申请；测试宿主显式批准精确命令，原会话继续执行 restore，依据真实验收失败修正参数校验，最终五项行为检查通过。该会话共八次工具动作、两次已知失败；审批历史、回执、最终快照及经历完整保留。开发得到的 `dev-map <policy.json> [prefix offset]` 已经审查并合入。

之前两次试跑保留在 session/session-v2：暴露了权限原因字段的兼容问题；后续还修正了审批结果未明确进入规划上下文导致重复申请的问题。没有篡改失败记录或重置成功会话的预算。示例复制当前源码，因此功能合入后重复同一目标不再复现历史缺失功能，应选择新的开发目标。

回归包含自动发现、无批准不执行、过期授权、普通回答不能扩权、批准/拒绝、重复申请、测试保护、零测试、失败修复及预算审计。主入口 `dotnet run --project tests/LittleLoop.Checks`；CLI 行为验收沿用 `tests/LittleLoop.RepositoryChecks`。
