# Codex App Server 委派闭环

LittleLoop 通过独立 `codex app-server --stdio` 进程进行 JSON-RPC 握手、创建/恢复 thread、发送 turn、接收事件和返回授权决定。`IAgentSessionClient` 是可替换传输接口；目前真实实现是 Codex，其他 agent 尚未接入。

默认任务宿主验证**只读验收矩阵**。配置 `development` 后使用[受限开发工具桥接](21-codex-development.md)，可实际修改授权源码、运行固定测试，并验证最终快照；不支持任意命令或部署。

## DeepSeek Harness ACP 原型

DeepSeek Harness 提供[官方 ACP 接口](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/acp/acp/README.md)，可用 `dsh --profile acp` 通过标准输入输出启动。该接口支持创建和恢复持久会话、提交提示、取消当前提示、接收事件及响应一次性权限请求。

聊天服务通过现有的执行器接口选择 `deepseek`，Codex 仍使用原来的传输。通过群聊的机器人任务表单或 `schedule` API 选择 DeepSeek，随后回复任务卡片补充、暂停或恢复。任务目录中的 `deepseek-session.json` 保存原会话 ID。已暂停的任务可在服务重启后恢复，执行中断且未确认结果的任务沿用待核对状态。

原型默认关闭。设置 `ChatBot__DeepSeek__Project`、`ChatBot__DeepSeek__ReadOnlyLauncher` 和 `ChatBot__DeepSeek__Home`，分别指向隔离项目、ACP 启动程序和独立 Harness 数据目录。使用 `ChatBot__DeepSeek__Arguments__0` 等配置启动参数。桌面版可通过自带 `dsh.cmd` 所使用的 Electron 入口启动，按脚本传递 `--expose-internals`、入口路径、`--profile acp` 和 `--patch`。此方式还需设置 `ChatBot__DeepSeek__ElectronRunAsNode=true`。

测试采用[只读配置覆盖](../tests/ChatContinuousLive/deepseek-readonly.yml)，保留文件搜索工具，禁用文件修改、终端、联网工具和委派入口。宿主设置 `DSH_PERMISSION_MODE=read-only`，不挂载 MCP 工具并拒绝权限请求。更换 Harness 版本时应核对实际加载的工具清单。密钥由独立 Harness 配置的 Models 页面保存。若本地网络需要企业信任根，可通过 `NODE_EXTRA_CA_CERTS` 提供已有 CA 文件。

2026-11-17 使用桌面版 0.2.2-rc.3 自带的真实 `dsh` 完成一轮隔离 HTTP 验收。任务通过聊天调度接收，在执行中取消当前提示并确认暂停，暂停期间补充要求，随后重启聊天服务并续接同一个 ACP 会话。最终报告准确引用了只读文件中的随机标记和补充要求，标记文件内容保持一致。证据位于 `data/continuous-http/01b8fcbe/`。复验使用 `tests/ChatContinuousLive/http_e2e.py` 的 `LITTLELOOP_TEST_DEEPSEEK=1` 模式和独立 `goal_test` 数据库。

### 隔离环境的受控开发

配置 `ChatBot__DeepSeek__TestDevelopmentPolicy` 后，聊天中会出现独立的 `deepseek_test` 执行器，只能操作该策略指定的测试项目。普通 `deepseek` 执行器继续只读。受控执行器在原 ACP 会话里用结构化回复提出工具请求，Harness 本身仍使用只读配置。LittleLoop 将请求交给 Codex 同样的宿主审批和开发执行器。补丁需使用宿主读取回执中的文件哈希，测试只能按明确配置的 ID 启动。每次补丁和测试各有一个审批请求。点击拒绝或在待批期间暂停会使该请求失效。已完成的操作保留真实回执，暂停不回滚此前已批准并执行的操作。

受控开发配置必须使用独立项目，并明确列出可写文件、受保护的测试文件和固定测试命令。测试项目不得与 Codex 项目、普通 DeepSeek 项目或任务状态目录重叠。复验在 `LITTLELOOP_TEST_DEEPSEEK=1` 基础上设置 `LITTLELOOP_TEST_DEEPSEEK_WRITE=1`。

### 按项目选择受控执行器

服务管理员可通过 `ChatBot__DeepSeek__DevelopmentProjects__<项目ID>__Policy` 添加用户指定的项目，值为 `DevelopmentPolicy` JSON 的绝对路径。项目 ID 使用 1–15 位小写字母、数字或下划线，例如 `alpha`。在包含审批人的群聊里安装机器人后，「安排任务」会列出「DeepSeek · alpha」。策略定义绝对根目录、可写文件列表、只读扩展名、带受保护输入的固定测试和工具预算。聊天里输入的路径和要求无法注册项目或增加授权。

各 DeepSeek 写入授权的根目录彼此独立，且不得覆盖 Harness 数据、普通只读工作区或服务任务状态。具名项目可以与 Codex 的完整项目根相同，但 DeepSeek 的授权要单独配置。现有 `deepseek_test` 配置继续可用。暂停任务恢复时会重新核对当前项目授权，发现授权变化则进入待核对，旧任务不会继续按原授权执行。

模型工具申请格式有误时，宿主最多要求修正两次，未通过校验的回复不会发起审批或执行。修正仍受该轮的总步数和超时限制。

复验设置 `LITTLELOOP_TEST_DEEPSEEK=1`、`LITTLELOOP_TEST_DEEPSEEK_WRITE=1` 和 `LITTLELOOP_TEST_DEEPSEEK_PROJECTS=1`，使用独立数据库运行 `http_e2e.py`。

真实 dsh 复验 `data/continuous-http/9523f268/` 通过。`alpha` 和 `beta` 分别在审批后修改自己的文件并运行固定测试，执行 `beta` 时 `alpha` 的文件和测试标记均未变化。未配置的项目执行器被拒绝，普通 DeepSeek 成功执行只读搜索。暂停 `beta` 后收紧写入策略并重启，旧审批失效，恢复请求进入待核对，文件未改动。现有服务未加载具名项目配置。

RelaxKonOS 的共享项目验收 `data/continuous-http/9427859b/` 通过。复制现有 Codex 项目后，仅重定位策略根目录，在同一隔离副本分别派给 Codex 和 `deepseek_project_relaxkonos`。每个执行器获批后仅在 `ApplicationDescriptor.cs` 写入一处验收注释，`appid-tests` 单独获批后才运行，均退出 0 并报告 19 项通过。现有策略说明文字写的是 20 项，验收以实际测试程序输出为准。源项目哈希未变，未配置项目被拒绝，普通 DeepSeek 仍可只读搜索。现有服务未启用 RelaxKonOS 的 DeepSeek 写入。

真实 dsh 隔离验收 `data/continuous-http/86f90de2/` 通过。拒绝补丁时原文件保持 `broken`，拒绝测试时没有执行标记或命令回执。另一任务在补丁审批前保持原文件，批准后变为 `fixed`。暂停待批测试后，旧审批返回 HTTP 409，测试标记仍不存在。恢复后收到新审批，批准后测试产生标记和退出码 0 回执。

预检发现，原共享宿主把用户主动暂停计入委派失败，可能阻止同一目标恢复。修复后这种中断不进入执行成败学习，已执行工具的回执仍独立保存。DeepSeek 恢复时会收到明确提示，说明上次未获回执的请求已失效。真实 Codex HTTP 回归 `data/continuous-http/82ee3c02/` 通过。

### 同一任务内协作

设置 `ChatBot__DeepSeek__ReviewCodexChanges=true` 后，在包含审批人的群聊里可选择「Codex + DeepSeek」。该入口使用当前 Codex 项目策略。Codex 先按原有流程分别申请补丁和测试审批。只有宿主验证当前源码快照与实际成功测试回执一致后，才交给普通只读 DeepSeek 审阅补丁片段和测试结果。审阅不开放开发工具。

LittleLoop 将修改文件、测试退出码和输出、DeepSeek 审阅意见保存到同一任务结果。开始审阅前会保存已通过测试的源码快照，审阅阶段暂停后可从该记录继续。源码或授权变化时转为待核对。已完成修改后收到新的修改要求时，任务会提示另建任务。

RelaxKonOS 隔离验收 `data/continuous-http/6dfcbc9e/` 通过。同一任务中，Codex 分别获批追加一条验收注释并运行 `appid-tests`，实际退出码为 0、19 项通过。审批前确认审阅会话尚未启动，测试结束后真实 `dsh` 在独立只读会话中审阅，最终任务输出同时包含变更文件、实际回执和审阅意见。除获批注释外，隔离项目的源码哈希不变，原项目未修改。当前线上服务未加载协作配置。

### 审阅阶段的暂停与重启恢复

当前发布版在审阅中强制停止时会把整个任务标记为待核对，隔离复现记录在 `data/continuous-http/e178b966/failure.json`。修复后，服务重启会核对原任务要求、项目策略、源码快照、Codex 完成状态及工具回执。全部一致时，运行中的任务重新进入只读审阅队列，用户主动暂停的任务保持暂停。审阅结果已落盘时可以直接生成汇总。没有匹配记录时仍按原有审批或待核对规则处理。

修复版隔离复验 `data/continuous-http/a3a15112/relaxkonos-collaboration-recovery-result.json` 覆盖审阅中暂停后重启和审阅中直接强制重启。两次都续接原 DeepSeek 会话，各自保留一条补丁回执和一条成功测试回执，工具日志哈希在恢复前后相同。任务完成后再次重启，最终汇总不变，聊天各有一条完成通知。现有服务未部署本修复。

## 调用方式

策略文件示例（路径替换成本机实际值）：

```json
{
  "executable": "C:/path/to/codex.exe",
  "project": "D:/RelaxKon/RelaxKonOS",
  "approvalMode": "auto",
  "outsideRule": "user",
  "commands": [],
  "maxTurns": 4,
  "timeoutSeconds": 900,
  "maxRepairs": 1,
  "attachDocumentation": true
}
```

```powershell
dotnet run --project src/LittleLoop.Cli -- agent-run policy.json data/agent-session task.txt
# 进程退出后恢复同一 thread，提交下一轮任务：
dotnet run --no-build --project src/LittleLoop.Cli -- agent-send data/agent-session followup.txt
dotnet run --no-build --project src/LittleLoop.Cli -- agent-show data/agent-session
```

宿主目录必须位于目标项目之外。`agent.json` 保存策略、thread/turn ID、任务、报告和检查结果；`learning.json` 保存角色决策、实际结果及反馈；`events.jsonl`、`requests.jsonl` 保存协议记录，stderr 有独立上限。配置通过 `role` 可提供角色偏好/边界，省略使用默认角色。Codex 使用本机自身的登录和模型配置，不使用 DeepSeek 密钥；本接口不读取或复制登录凭证。

恢复仅针对该宿主已经记录且已结束的会话。发送中断、断线、超时或响应丢失标记 `needs-reconciliation`，不自动重发未知副作用。当前没有自动接管其他桌面聊天、处理中途断线对账、活动 turn steering 或跨机器恢复的 CLI；等待授权时宿主进程需保持运行，仍受总超时约束。

## 授权由策略与核心共同决定

`approvalMode` 支持：

- `auto`：请求中的完整命令字符串和 cwd 必须匹配 `commands` 中的 `{ "command": "...", "cwd": "..." }`。网络附加请求、其他权限及 stdin 审批不借用命令授权。匹配后还经过角色核心审议。
- `wait`：保存请求并保持等待，不自动回答。
- `user`：保存请求并显示处理入口，由用户处理。
- `decline`：拒绝请求。

`outsideRule` 决定自动策略遇到未覆盖请求时等待、转交用户还是拒绝。默认不预授权任何额外命令。文件修改与权限 profile 不按命令清单自动批准，只能按用户指引处理。用户接受也经过核心边界判断。

待处理请求写入 `approval.json`，包含 server request ID、thread/turn 归属、完整参数和随机 nonce：

```powershell
dotnet run --no-build --project src/LittleLoop.Cli -- agent-decide data/agent-session <nonce> accept
# 或 decline
```

决定仅对应当前请求，拒绝旧 nonce、重复回答和已过期请求。不会生成 acceptForSession 或永久 execpolicy 修改。权限 profile 接受时仅回传请求中的权限，scope 为 turn；App Server 自身仍执行其限制。未知协议请求保守停止，不猜测批准载荷。

自动清单是用户给出的预授权边界，不是模型自行扩权。执行命令的语义仍由用户承担授权判断；完整字符串匹配不能代替 OS 沙箱。初始会话使用 read-only sandbox 和 on-request 审批，矩阵任务禁止部署、安装、重启、卸载、修改项目及访问凭证。

## 判断、调整与经验

每轮委派先经过 `LearningSession.Begin`，核心可拒绝发送任务。收到 `turn/completed` 后记录真实完成状态；agent 文字报告不会自动成为设备测试成功。宿主独立检查六个场景覆盖、前置条件、预期证据、项目内证据路径存在性，以及只能为 not-run/blocked 的设备状态。提供文档快照时还检查其哈希没有变化。

缺项产生负反馈，并在剩余轮数预算内自动发一轮具体修正任务；验收通过产生正反馈。报告内容、原始事件和判定均保留。此处验证的是矩阵结构和文档依据，不证明 agent 的每条语义推断正确，更不等于执行了设备操作。

`attachDocumentation` 默认开启：LittleLoop 只读采集至多十份项目入口及引用 Markdown 文件、约三万字符，保存路径/哈希/截断标记后附给 agent。这样在 agent 的文件工具不可用时仍有明确来源；缺失资料必须作为问题保留，不能凭空补齐。日志和文档快照包含项目资料，应按本地敏感工作数据保管，示例目录被 Git 忽略。

## RelaxKonOS 实跑

2026-11-14，`data/codex-relaxkonos/session` 创建真实 thread `01a1da9f-f689-7193-a27f-a90dd0ff5773`，共完成三轮：

1. Codex 独立启动后，文件命令遇到桌面运行时 provisioning service 不可用；返回矩阵缺少证据路径，LittleLoop 给出负反馈。
2. LittleLoop 自动发出补充任务，得到六行 blocked 矩阵及引用路径；宿主核对路径存在，但报告仍明确没有读取完整正文。
3. 重新启动 LittleLoop，调用 thread/resume 恢复同一 thread，发送实际文档快照复核。Codex 返回 desktop/android × install/update/uninstall 六行矩阵和需要补充的环境信息。设备验收仍全部 blocked。

最终产物：`data/codex-relaxkonos/matrix.md`、`matrix.json`。没有部署、修改 RelaxKonOS 或读取连接凭证。此次实跑没有触发 server approval 请求；自动批准、核心拒绝、等待/用户决定、过期请求及未知执行保护由协议模拟检查验证，不将其描述为实机授权成功。

接口依据：本机 `codex-cli 0.169.0-alpha.11.1` 生成的 JSON Schema，以及 [Codex App Server 官方文档](https://developers.openai.com/codex/app-server/)。后续版本需要核对协议差异，不能假设实验性字段永久稳定。
