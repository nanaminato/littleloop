# Codex App Server 委派闭环

LittleLoop 通过独立 `codex app-server --stdio` 进程进行 JSON-RPC 握手、创建/恢复 thread、发送 turn、接收事件和返回授权决定。`IAgentSessionClient` 是可替换传输接口；目前真实实现是 Codex，其他 agent 尚未接入。

默认任务宿主验证**只读验收矩阵**。配置 `development` 后使用[受限开发工具桥接](21-codex-development.md)，可实际修改授权源码、运行固定测试，并验证最终快照；不支持任意命令或部署。

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
