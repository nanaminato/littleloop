# 项目指导与外部 agent 边界

`dev-guide <项目路径> <指导目标> <model.json> <output.json>` 是只读指导入口。它读取根目录 AGENTS.md、TODO.md、README.md，以及这些入口引用的本地 Markdown 文档，最多十份、约三万字符，不递归扫描源码。隐藏路径、越界链接和链接文件拒绝读取；每份证据包含哈希和截断标记。输出必须在目标项目之外，已有文件不覆盖。

报告包含下一步任务、验收条件、agent 任务书草案、权限需求和不确定项，同时保存实际输入证据与模型来源。它调用配置的外部模型，因此需要用户授权发送目标项目文档。报告是建议，不是测试、部署或委派已经执行的证明。

2026-11-14 对 `D:/RelaxKon/RelaxKonOS` 的尝试发现：原 `dev-project` 全项目扫描超过 3000 文件上限，未进入执行；只读指导使用项目规范与进度文档绕开不必要的全量源码扫描。首次外部模型请求被自动审批拒绝，用户明确批准本次所述文档发送后才重试。报告存于 `data/relaxkonos-guidance/advice.json`。

## 当前能力与下一步适配

本页描述只读指导阶段。后续已接入 [Codex App Server 委派闭环](20-codex-agent.md)；`dev-guide` 自身仍只生成任务书草案，不创建线程、发送消息或批准权限。当前 Codex 聊天拥有的桌面工具也不会自动授予 LittleLoop。

可采用 Codex App Server 作为后续适配接口：`thread/start` 创建会话，`thread/resume` 恢复，`turn/start` 发任务，`turn/steer` 补充正在执行的任务，并订阅执行事件。命令、文件修改及权限申请通过对应 requestApproval 事件交由宿主处理。官方协议参考：https://developers.openai.com/codex/app-server/ 。当前本机 `codex app-server --help` 可用，但尚未建立连接或验证协议调用。

建议职责：LittleLoop 提出任务、判断结果、积累经历；执行 agent 在明确授权项目和预算内工作；宿主处理授权。只有预先明确授予的范围可以自动批准，越界申请交给用户，不能由模型自行扩大权限。执行 agent 的文字报告仍是待验证证据，需要差异、测试回执和项目验收结果。

当前已实现范围和剩余限制以新文档为准；其他 agent 实现、活动任务接管及未知执行对账仍未完成。不能将固定命令工具直接启动一个无限权限 agent 当成完整能力。
