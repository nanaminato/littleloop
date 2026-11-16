# S1 · 真实 Codex 验收与部署

2026-11-16，依据用户明确要求修复 SQLite 目录访问、完成真实 Codex 验证并部署现有服务。

## 运行目录修复

`CodexAppServerClient` 为每个宿主会话建立 `codex-runtime`，通过 `sqlite_home`、`log_dir` 配置数据库与日志，并将子进程的 `TEMP`、`TMP`、`TMPDIR` 指向该目录下的 `tmp`。仅更换数据库目录不足以解决本机 SQLite 迁移时临时目录不可写的问题。

可选环境变量 `LITTLELOOP_CODEX_HOME` 必须为绝对路径，只为启动的 Codex 子进程设置 `CODEX_HOME`。现有服务使用 Git 忽略的 `data/codex-service-home`，配置与登录凭据在本机单独准备，没有修改个人 `.codex` 目录权限。子进程不继承聊天数据库、推送或 LittleLoop 管理凭据。

本机另外使用 `CODEX_CA_CERTIFICATE` 指向从系统既有受信任根证书导出的公开 PEM 文件；没有关闭 TLS 校验或修改系统信任库。配置依据：[Codex 配置参考](https://learn.chatgpt.com/docs/config-file/config-reference)、[环境变量参考](https://learn.chatgpt.com/docs/config-file/environment-variables)。其他机器应使用其自己的登录及证书配置，不复制本机凭据。

真实验证还暴露了 Windows 快照原子替换的临时访问冲突。`AtomicSnapshot` 使用允许删除共享的读取句柄，仅对 Windows 错误 5/32/33 的文件替换重试，最多等待 300ms；不重复执行工具操作。检查覆盖短暂占用后成功和持续占用后有界失败。具体外部占用者未得到证明。

## 验收证据

- 核心检查 313 项通过，包括新增的两个文件占用检查。日志：`data/continuous-core-checks.log`。
- MySQL/后台服务及真实 Codex 检查共 51 项通过。日志：`data/continuous-live-checks.log`；真实执行目录：`data/continuous-goals-live/8f3cc659/`。
- `tests/ChatContinuousLive/http_e2e.py` 针对最终 Release 二进制，通过真实 HTTP 消息、后台服务、MySQL 和 Codex 完成九个场景：任务接收与去重、安全暂停、旧审批拒绝、暂停时补充、服务重启、同 thread 恢复、额外文件要求、真实测试回执、聊天完成通知。
- 最终结果：`data/continuous-http/6c4753d2/result.json`；目标版本 4，thread `01a1e441-d09c-7cd1-8af8-8aa8b1ee52dd`。实际将 `code.txt` 从 broken 改为 fixed、`notes.txt` 从 pending 改为 supplemented；宿主测试和独立复核均退出 0。
- Release 发布和 Angular 构建通过。Android 编译沿用上一轮证据；本轮未做浏览器或设备交互、24 小时或 7 天长稳验收。

端到端测试只使用隔离数据库和有界夹具项目。可设置 `LITTLELOOP_CHAT_TEST_CONNECTION`（库名必须包含 `goal_test`）、`LITTLELOOP_CODEX_EXECUTABLE`、`LITTLELOOP_CODEX_HOME`，然后运行 `python tests/ChatContinuousLive/http_e2e.py`；可用 `LITTLELOOP_CHAT_DLL` 指向待部署二进制。数据库需预先创建并授权，5196 端口需空闲。

## 现有服务部署

服务地址：<http://localhost:5190/chat>。发布目录为 `data/continuous-deployment/release`，网页目录为 `data/continuous-deployment/client`。保留原数据库、机器人策略、执行数据、操作员、审批员、FCM 与旧工作台配置；启动脚本 `data/chat-full-audit/restart-main.ps1` 已指向新版本及独立 Codex 运行配置。

部署前备份在 `data/continuous-deployment/backup-20261116-201156`，包含数据库、策略、原启动脚本和机器人状态。原二进制仍在 `data/chat-full-audit/bin-v2`。回退时先停止新进程，再使用备份的启动配置与原二进制；数据库恢复需单独评估部署后新增数据，不能直接覆盖。

部署记录 `data/continuous-deployment/deployment.json` 保存二进制 SHA-256、备份目录与启动结果。部署前后账号 65、会话 309、消息 588、任务 17，数量一致。主服务通过 HTTP 200、原账号登录、机器人能力和会话列表检查；真实工具执行在隔离验收环境完成，没有向现有用户群发送测试任务。所有运行记录、证书和凭据均在 Git 忽略的 `data` 下。
