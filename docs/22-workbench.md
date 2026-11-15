# LittleLoop 开发工作台

第一版为本机单用户工作台。Angular 展示对话、带版本的引用、待审批操作、补丁记录与测试回执。ASP.NET Core 保存对话和后台队列，逐个执行任务。LittleLoop 继续负责角色审议、项目工具边界、实测验收和经验归档。

## 运行

要求 .NET 10、支持 Angular 22 的 Node.js，以及可独立启动并已登录的 Codex App Server。准备一份 [开发策略](21-codex-development.md)，指定项目根目录、绝对 Codex 路径、允许修改的文件、固定测试命令和受保护测试入口。首次使用建议指向隔离工作副本。

在仓库根目录运行：

```powershell
npm --prefix src/LittleLoop.Workbench/Client ci
npm --prefix src/LittleLoop.Workbench/Client run build
$env:Workbench__Policy = (Resolve-Path 'data/workbench-policy.json').Path
$env:Workbench__Client = (Resolve-Path 'src/LittleLoop.Workbench/Client/dist/littleloop-workbench/browser').Path
$env:Workbench__Data = Join-Path (Get-Location) 'data/workbench'
$env:Workbench__Token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --project src/LittleLoop.Workbench
```

打开 http://127.0.0.1:5188，粘贴本次运行的访问令牌。浏览器只在当前标签会话保存令牌。服务限制本机连接。问答默认经本机登录的 Codex 处理选中的源码片段。需要更换问答服务时，可将 `Workbench__Model` 指向一份已授权的 [模型配置](14-model-semantics.md)。选择外部服务意味着所选源码和问题会发送给该服务。

### MySQL

在启动前配置 `ConnectionStrings__Workbench`，指向一个专用、已创建的 MySQL 数据库。服务启动时建立 `littleloop_workbench` 表，连接账户需要建表及读写权限。连接串使用环境变量或 .NET 配置的本地秘密存储。

```text
Server=127.0.0.1;Database=littleloop_workbench;User ID=workbench;Password=<本地密码>
```

每个数据库和数据目录只运行一个工作台实例。对话和任务队列写入 MySQL；运行记录、权限 nonce 和 LittleLoop 经历保留在数据目录。未配置连接串时，使用同一数据目录中的 JSON 持久化，无需数据库。服务重启后继续处理排队任务，执行中断的任务移入待对账状态。

### 使用

1. 选择源码和行号，预览并发送问题。引用保存文件路径、行号、内容和 SHA-256。
2. 从该提问选择「安排开发任务」，输入目标和验收要求。任务运行前验证引用哈希，引用变化时请重新提问。
3. 后台面板逐次显示补丁与固定命令审批。接受还需通过 LittleLoop 核心与策略。关掉网页时，后台继续运行，等待中的审批仍受策略总超时限制。
4. 后台面板展示实际补丁片段和每次测试的退出码、输出。完成状态由 LittleLoop 根据最终快照和必要测试回执判定。

当前只配置一个项目和一段持久化对话，界面以轮询更新。任务补丁直接作用于策略中的目录。浏览器不会提交或推送代码。

## RelaxKonOS 验证

使用真实 RelaxKonOS Core 的隔离副本，在副本恢复已知的应用 ID 校验回归。Angular 构建产物由 ASP.NET Core 成功提供。API 联调经过真实 Codex 源码问答、对话入队、三次限定授权、实际补丁与复测。基线测试失败，修复后 19 项通过。测试由脚本通过同一审批 API 逐次放行。记录位于本机 `data/workbench-relaxkonos/answer.json` 和 `result.json`。LittleLoop 原有 308 项检查通过。

此次联调使用文件持久化。本机 Docker daemon 未运行，MySQL 实库未验证。Angular 完成生产构建，业务流程通过 HTTP API 联调。

### Chrome 和 MySQL 实库复验（2026-11-15）

使用同一 RelaxKonOS Core 回归场景的新隔离副本，运行 MySQL 8.4.13。Chrome 在页面预览第 108–113 行、提交问答、从该问题创建任务，并三次通过界面审批。任务完成后检查补丁、失败基线和 19 项通过的复测输出。重启 ASP.NET Core 后使用新 Chrome 会话重新登录，原对话、任务、补丁和回执仍可查看。MySQL 中 2 条消息、1 个任务，重启前后 JSON 负载 SHA-256 一致。

本机证据在 Git 忽略的 `data/workbench-browser/`，包括 `approval.png`、`completed.png`、`after-restart.png` 和 `restart-evidence.txt`。
