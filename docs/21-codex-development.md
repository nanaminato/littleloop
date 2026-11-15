# Codex 独立开发工具闭环

桌面捆绑 Codex 0.169.0-alpha.11.1 独立运行时，原生文件命令报 `app runtime provisioning service is unavailable; refusing helper fallback`。本阶段通过 App Server 的实验性 `dynamicTools` / `item/tool/call` 协议接入 LittleLoop 已有开发执行器，解决独立委派的文件读写与测试需求。没有修复 Codex 私有 provisioning 服务，也没有关闭原生沙箱。协议字段依据本机 `generate-json-schema --experimental`，升级需重验。

## 使用与边界

`agent-run` / `agent-send` 保持不变。在 AgentHostPolicy 增加 `development`（DevelopmentPolicy）：`root` 必须与 `project` 相同；明确给出 `writableFiles`、固定 `commands`、`readExtensions`、预算及输出/超时上限。命令必须使用绝对可执行路径与固定参数，测试必须声明不可修改的 `protectedFiles`。配置示例及实跑任务在 Git 忽略的 `data/relaxkonos-development/policy.json`、`task.txt`。

宿主注册 `project_inspect`、`project_search_page`、`project_read_range`、`project_search`、`project_read`、`project_patch`、`project_command`。参数均为字符串；命令只接受预授权 ID。补丁只改已有授权文件，需要本会话读到的 SHA-256，执行前再次核对。隐藏路径、越界路径、链接和未授权扩展名仍受 ProjectFiles 限制。

每次工具请求经过持久化单核心调度、角色审议与现有 DevelopmentExecutor；核心仍可拒绝。真实回执保存到 `tools-learning.json`，完成/失败自动产生反馈，被阻止或未执行的操作保留回执但不伪造执行反馈。决策上下文包含源码快照：旧代码的失败经验不会直接阻止修复后的重测。调用 ID 的单独日志避免污染经验匹配，同一调用的重放返回已保存结果，不再次执行；未知执行停下等待对账。

`approvalMode=auto` 允许核心审议既定开发工具授权；`wait` / `user` 在补丁和命令执行前写入 `approval.json`，通过既有 `agent-decide` 回答。`decline` 拒绝这些操作。读取仍允许。用户接受请求也不能突破 DevelopmentPolicy 的文件/命令边界；缺少范围时 agent 必须解释需要什么和原因，由用户提供新授权配置再开启会话，当前没有自动扩大动态工具授权的功能。原生 App Server 授权请求继续使用原有策略。

原生 thread 仍为 read-only。开发副作用来自宿主受限工具；命令清单是应用层授权，**不是 OS 沙箱**。固定命令及其构建输入必须可信；开发进程不继承模型密钥，保留已有清理环境、输出上限、超时和未知副作用保护。不要把任意模型 shell 文本加入命令清单。

## 独立验收

Agent 的总结不是成功证据。宿主要求实际成功补丁、源码相对基线发生变化、受保护测试未变，以及所有必需测试的最后一次成功回执都对应当前源码快照。缺少测试或测试失败会形成具体反馈，预算内再次发送任务；编译错误、断线、超时等未知副作用不会盲目重放。

## RelaxKonOS 实跑证据（2026-11-14）

任务是修复 `ApplicationDescriptorValidator.IsValidAppId` 接受末尾换行的问题。复制真实项目完整 Core 源码到隔离目录，并增加引用真实 Core 的独立回归测试。测试由宿主准备且对 agent 只读；Codex 仅获准修改 `ApplicationDescriptor.cs`。

最终会话 `01a1dab6-45ed-7a71-bc00-c96bab08902f` 在一轮中完成：读取 → `appid-tests` 真实失败（接受 `com.example\n`）→ 检索 → 将正则 `$` 改为 `\z` → 测试退出 0，19 项通过 → 宿主最终快照验收。学习状态保存成功与失败经历。前期会话另外暴露并修复了 blocked 回执不能当作执行反馈、经验缺少源码快照两个宿主问题；这些失败会话保留，没有伪报成功。

核对真实目标原始哈希后，将修复与 `Tests/Framework/RelaxKonOS.AppId.Tests` 两个测试文件回写到 `D:/RelaxKon/RelaxKonOS`，在真实目录再次运行 `dotnet run --project Tests/Framework/RelaxKonOS.AppId.Tests --disable-build-servers`，19 项通过。未部署，也未执行全项目或设备验收。

实跑记录在 `data/relaxkonos-development/final/{agent.json,learning.json,tools-learning.json,events.jsonl,requests.jsonl,development-baseline.json,tool-calls/}`；交付哈希在 `delivery.json`。这些是被忽略的本机证据，不随 Git 传播。LittleLoop 308 项检查通过，新增覆盖实际补丁、失败后重测、最终快照验收、重复调用、跨会话拒绝、保护测试、等待授权及经验积累。实跑使用既定授权，人工等待/拒绝分支由协议测试验证。
