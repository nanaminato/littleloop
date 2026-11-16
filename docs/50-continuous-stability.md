# S1 · 持续任务与重启稳定性验证

2026-11-16 用户要求继续长期稳定性验证，重点是持续接任务、补充要求、暂停恢复和重启后的状态一致性，发现问题直接修复。本轮使用已部署的 Release 二进制，在隔离 MySQL 数据库、测试账号和有界文件夹具上执行；现有 5190 服务只做健康检查。

## 可重复的持续验证

`tests/ChatContinuousLive/http_e2e.py` 支持在同一数据库、同一群聊、同一共享项目中连续创建目标。每轮重置夹具，真实 Codex 接收任务，审批边界暂停，拒绝旧审批，暂停期间补充第二个文件的要求，重启后恢复原 thread，执行实际修改及测试并回传完成消息。完成后再重启，逐个比较全部历史目标的完整 API 状态。

验证会遍历聊天历史分页，要求每个任务恰好有一条完成通知。每轮保存结果，异常保存 `failure.json`；日志句柄在重启时关闭，退出时清理测试服务及其子进程。原脚本只检查群里是否出现过“已完成”，现已按任务 ID 检查，防止前一任务的通知掩盖当前任务通知丢失。该修正属于验收脚本，不是产品故障。

配置（在已配置 Codex 登录、可写运行目录和预先创建的全新隔离数据库环境中）：

```powershell
$env:LITTLELOOP_CHAT_TEST_CONNECTION = '<隔离连接字符串，库名必须包含 goal_test>'
$env:LITTLELOOP_CODEX_EXECUTABLE = '<本机 Codex 可执行文件>'
$env:LITTLELOOP_CODEX_HOME = '<独立 Codex home 的绝对路径>'
$env:LITTLELOOP_CHAT_DLL = '<待验证 Release/LittleLoop.Chat.dll 的绝对路径>'
$env:LITTLELOOP_CHAT_TEST_PORT = '5198'
$env:LITTLELOOP_SOAK_ROUNDS = '1000' # 安全上限；也可单独按轮数运行
$env:LITTLELOOP_SOAK_SECONDS = '1800' # 达到时长后在本轮完成时退出
$env:LITTLELOOP_SOAK_FAULTS = '1'
python tests/ChatContinuousLive/http_e2e.py
```

端口须空闲且不能为现有服务的 5190。未设置时默认单轮、5196 端口、不注入故障。轮数先耗尽但尚未达到指定时长会失败，不将短跑标记为长稳通过。每个任务使用真实模型服务和原有宿主审批边界；凭据不写入版本库。

故障注入在真实 Codex 等待审批时强制终止测试进程树，检查重启后进入 `needs-reconciliation`，旧审批返回 409，恢复与补充不能绕过核对，再次重启不改变状态。随后创建新任务，验证等待补充、补充后调度、暂停和取消，最后重启复核取消状态。

## 积压与持久化检查

`WorkerChecks` 增加 40 个目标，先暂停并停止后台执行器，停机期间保存 80 条补充/恢复消息，再启动执行器消费跨批次收件箱。验证每个目标只执行一次，版本均为 4，补充目标和完成状态在再次重启后保持一致。这部分使用受控执行器与真实 MySQL，专门检查批次调度，不冒充真实模型执行。

本轮集成检查共 92 项通过，包含原来的真实 Codex 检查及新增积压检查。构建成功，仍有环境中 NuGet 漏洞源不可访问的 NU1900 警告。

## 验收记录

- 初始 10 轮真实 HTTP 连续验证通过，耗时 374.5 秒：`data/continuous-http/9f3235e2/result.json`。
- 补充 3 轮通知去重与故障恢复验证通过：`data/continuous-http/a96b91b9/result.json`、`faults.json`。另一次独立故障验证见 `data/continuous-http/c4912246/faults.json`。
- 30 分钟持续验证通过：实际 1812.4 秒（30 分 12 秒），47 个真实 Codex 目标、47 个不同线程、94 次轮内重启、1128 次历史任务完整状态比较；所有测试命令回执成功，完成通知逐任务去重检查通过。结束后的七项故障检查也通过。证据：`data/continuous-http/31812753/result.json`、`faults.json`、`audit.json`；服务日志未匹配到未处理异常、SQLite 打开/锁定失败或 ASP.NET `fail:` 记录。
- 最终脚本增加调度状态与目标状态一致性断言、进程树清理后，单独复核完整任务与故障恢复通过：`data/continuous-http/e7ddedbf/`。此前已启动的 30 分钟运行使用原退出清理方式；其持久化结果另经审计，所有目标状态与调度状态一致。
- 积压集成检查日志：`data/continuous-live-checks.log`。主服务连续 25 分钟的 100 次采样全部 HTTP 200，内存 108.7–113.5 MB、句柄 691–711；这是主服务低负载健康观察，不是高负载内存泄漏证明。记录：`data/continuous-deployment/soak-health.jsonl`。
- 最终检查主服务仍为原 PID 31408，HTTP 200，5196/5197/5198 测试端口均已释放：`data/continuous-deployment/soak-final-health.json`。发布二进制哈希与原部署清单一致。

本轮未发现产品状态一致性故障，修改集中于可重复测试、通知判定、测试进程清理及证据记录；没有为测试重启或替换现有主服务。

这些测试覆盖有界任务与高频重启，不代表 24 小时/7 天运行、数据库服务器故障、磁盘耗尽、网络长时间中断或客户端交互验收已经完成。原始证据在 Git 忽略的本机 `data` 目录。
