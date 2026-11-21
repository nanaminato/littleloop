# Bot 事实核对、决策修复与唤醒

## 职责与可复用接口

Bot 根据用户意图决定目标、下一项工作、求证方式、何时预约以及最终结论。Harness 执行已授权的指令，分别回报过程和结果。Chat 宿主持久化原始消息、目标条件、派发、工具记录、检查和发件箱，提交时复核权限、版本、触发来源和引用范围。完成仍需要 Bot 提交 `Complete`，且所有活动条件都有可受理的证据。

| 接口 | 本轮行为 |
| --- | --- |
| 决策适配器和草稿审计 | 字段拒绝含 JSON 路径、错误码、期望结构和实际值。语义拒绝列出适用条件、合法来源或观察到的状态。Bot 可以改正字段，也可以保持条件开放并选择另一项合法行动。同类错误及被接受的确定性规范化分别记账。 |
| 目标和事实查询 | Bot 可选择查询 `requirements` 或 `events`。查询按会话、用户、目标和版本限定，每页最多五条，一次判断最多两次查询，与草稿修复预算分别计算。较早的派发和明确引用可从账本找回，提交时重新核对。 |
| 证据和判断 | 宿主直接观察的消息、决定和到期事件，与 Harness 文字陈述和工具报告分别标记。失败工具或后续反例保持可见。预期失败只能作为确有此要求的行为或约束证据。Bot 可再委派独立核对，保存派发关联和实际回执，再比较证据。Bot 的已核对标记属于评估，假设与证据不足保持在理由或开放条件中。 |
| 技术恢复 | 认知暂时不可用时，原触发及诊断按退避时间保留并重试。草稿修复预算耗尽后，内部安排一次带诊断的重新判断。仍无法提交就审计为内部阻塞，保留未核对的定向补充，避免无界调用。宿主不为此写 `Wait` 决策，也不占用业务预约。 |
| 业务预约 | 保存时间要求只建立一个条件。Bot 选定定时 `Wait` 后，目标版本、到期时间、检查 ID 和条件 ID 在同一事务提交。重启重新领取已持久化的检查，真实到期才把检查交给 Bot。对于仍未预约的条件，继续派发需要说明为何其他工作在先。 |

[`BotDecision`](../src/LittleLoop.Core/BotDecision.cs) · [草稿适配器](../src/LittleLoop.Tools/ModelBotDecisionEngine.cs) · [事实查询](../src/LittleLoop.Chat/Bot/BotFactStore.cs) · [提交事务](../src/LittleLoop.Chat/Bot/BotActionStore.cs) · [技术恢复记录](../src/LittleLoop.Chat/Bot/BotDecisionRecoveryStore.cs)

## 上轮八次人工等待

以下编号来自 `data/obligations-live/final-candidate/paired` 的 SQLite 账本及原始草稿。“事件”指正式目标的 `chat_bot_events.id`。八次终止均是宿主在草稿失败后提交的等待。轨迹没有显示这八次终止需要新的授权或用户信息。

| 试次 | 实际路径 | 可以自主采取的行动 |
| --- | --- | --- |
| 2 | 首选文件工具失败 #5，DeepSeek 回执 #6。#7 保存三次拒绝：无用户来源的条件编辑、缺 Harness、用失败事实满足结果。0/16 阶段有独立证据。 | 把结果留开放，按已授权的后备路径继续委派。 |
| 3 | #17 的用户补充三次使用 `oldId`。收到执行回执后，#18 又三次使用 `old`。替换字段非法，且回执轮已经失去当前用户来源。3/16。 | 在原用户触发上修复为 `id` 并提交有来源的修订。 |
| 4 | 16/16，定时检查 #31 已观察。#52 连续把通知证据和派发字段混用，最后又用无引用评估交代全程约束。 | 区分发件箱证据和执行证据，查账确认以往执行，再提交完整结论。 |
| 5 | #17 把当前补充的序号提交为 `resolveSourceSequence`，三次被拒。#18 的回执轮又三次使用非法的 `oldId`。3/16。 | 在原用户触发重拟普通 `Revise`，无需另填历史待核对序号。 |
| 6 | #29 是触发业务等待的回执。#30 声称将预约，但提交的是直接派发，预约数为零。#50 又引用普通事件、在预约时同时评估同一时间条件、引用不存在的检查。16/16。 | 到达起算事件时提交绑定条件的定时 `Wait`，到期后再决定。 |
| 7 | #29 至 #30 同样在声明预约时直接派发，预约数为零。#50 三次用普通工具或回执事件申报满足时间。16/16。 | 查询预约和条件状态，取得真正到期记录后再评估。 |
| 9 | 首选读取失败 #5 后，#7 两次用失败探针满足记为结果的条件，末稿又缺 Harness。0/16。 | 保留失败作为反例，继续通过已授权的 Harness 求取实际结果。 |
| 10 | #7 把失败工具当正面行为证据，把 #6 的回执当备用读取证据。末稿发明 `verification` 字段。0/16。 | 用工具事实确认后备读取实际发生，再提交评估。 |

试次 6、7 的缺口发生在宿主收到任何预约之前。上一轮另外三个已预约目标全部恢复。试次 3、5 的实际补充消息已入库，但有效 `Revise` 未提交。旧试次 1 的草稿也出现 `resolveSourceSequence` 错误，随后自行继续并完成。

## 验收与复现

```powershell
dotnet build tests/LittleLoop.ChatChecks --no-restore -m:1 -p:UseSharedCompilation=false
dotnet run --no-build --project tests/LittleLoop.ChatChecks -- --goal-obligations
dotnet run --no-build --project tests/LittleLoop.ChatChecks -- --decision-loop
dotnet run --no-build --project tests/LittleLoop.ChatChecks -- --harness
dotnet run --no-build --project tests/LittleLoop.ChatChecks
dotnet run --project tests/LittleLoop.Checks
python -m unittest discover -s tests/ChatContinuousLive -p test_goal_obligations_report.py -v
```

条件专项 44/44、决策循环 150/150、完整 SQLite 聊天 359/359、原生协议夹具 14/14、Python 离线计分 7/7，核心回归和项目模型校验通过。到期时遇到认知服务故障的回归检查确认，宿主保留原检查和时间戳，恢复时只结算一次。

真实试验使用 [既有接入配置](57-bot-decision-loop.md) 和上一轮的冻结清单。同一驱动包含严格阶段顺序、同一目标修订、真实定时重启及最终通知的断言。只扩展离线评分，冻结驱动和批次运行器保持其原始指纹。API 成本按同一费率计算已观测用量，供应商实际账单和 Harness 内部用量未纳入。注意技术阻塞与真正需输入的等待分别计数。

```powershell
$baseline = 'data/reliability-live/candidate-b/batch.json'
$previous = 'data/obligations-live/final-candidate/paired/batch.json'
$run = 'data/obligations-live/autonomy-v2-final/paired'
python tests/ChatContinuousLive/autonomy_reliability_batch.py --chat-dll $candidateDll --trials 10 --output $run --paired-with $previous
python tests/ChatContinuousLive/goal_obligations_report.py $baseline "$run/batch.json" --rates-per-million 0.003 0.15 0.60 --output 'data/obligations-live/autonomy-v2-comparison.json'
$env:LITTLELOOP_CHAT_DLL = $candidateDll
python tests/ChatContinuousLive/direct_http_e2e.py --decision-loop inflight-crash
```

最终程序集的在途硬崩溃复测通过。真实 DeepSeek 工具已产出证据、原生日志仍为 `running` 时硬停隔离进程树。重启后保留原派发 ID 和工具证据，只提交一次 `uncertain` 回执，目标等待核对，没有重发或完成。证据在 `data/obligations-live/autonomy-v2-final-crash/inflight/inflight-crash-trace.json`。

## 本轮能力边界

- 工具结果、源消息、目标版本和到期事件可以复查。宿主能拒绝越界和反例，跨任务语义、阶段起算点和独立核对是否充分仍由 Bot 判断。自然语言要求是否拆全也需要通过独立夹具与新任务测量。
- 需要授权、缺少只有用户知道的信息，或原派发已提交但副作用无法核对时，Bot 可以提出具体问题。非用户型外部事件还需要可订阅的来源。当前一个目标同时只持有一个有效预约，纯定时新目标仍需要先建立目标的能力。
- 事实读取是有界分页，使用时才回到既有账本。单个超大目标的完整条件或来源仍可能占满 32 KiB 决策输入。精确引用可以跨派发窗口取回，但单个派发的事件读取仍有 240 条上限。需要使输入预算和账本检索从初次判断开始协作，并测试分页返回的每条证据能否再次精确读取。
- 查询与格式化草稿有各自的预算。错误查询的格式诊断仍可能落入普通草稿校验。授权或版本刚好在查询时变化，也可能漏记已返回的模型用量。实测成本以已观察的账本数据为准。
- 当前尚无相关性索引、跨目标注意力调度、可复用的失败经验和模型替换后主动重启阻塞决策。下一阶段可沿用目标版本、原始触发、到期和执行事件、证据查询及恢复审计，优先打通失败后继续补证和可恢复的目标选择。
