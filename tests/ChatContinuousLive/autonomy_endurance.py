"""Acceptance oracle for a real, read-only, multi-round LittleLoop goal."""

from collections import Counter
import hashlib
import json
import os
import re
import secrets
import sqlite3
import time


STAGES = tuple(f'B{number:02}' for number in range(1, 9))
MISSING = 'B01-primary.txt'
BACKUP = 'B01-backup.txt'


def rows(root, room):
    # The verifier reads committed evidence. It never updates the service database.
    with sqlite3.connect(f'{(root / "chat.db").as_uri()}?mode=ro', uri=True) as db:
        return [dict(id=event, kind=kind, observation=json.loads(observation),
            detail=json.loads(detail) if detail else {}) for event, kind, observation, detail in db.execute(
                'SELECT id,kind,observation,detail FROM chat_bot_events WHERE room_id=? ORDER BY id', (room,))]


def active_dispatch(root, task):
    with sqlite3.connect(f'{(root / "chat.db").as_uri()}?mode=ro', uri=True) as db:
        row = db.execute("SELECT input,goal FROM chat_bot_work WHERE id=? AND status='running'", (task,)).fetchone()
    if row:
        dispatch = json.loads(row[0])['loop']['dispatch']
        journal = root / 'bot' / task.replace('-', '') / 'harnesses' / dispatch['harness'] / (dispatch['id'].replace('-', '') + '.json')
        try:
            # The native journal sets Running immediately before protocol submission.
            native = json.loads(journal.read_text(encoding='utf-8'))
        except (FileNotFoundError, PermissionError):
            # Windows can briefly deny this read while the native adapter atomically replaces the journal.
            return None
        if native['Status'] == 'running':
            return dict(**dispatch, goalVersion=json.loads(row[1])['version'])
    return None


def fixture_hashes(project):
    return {entry.relative_to(project).as_posix(): hashlib.sha256(entry.read_bytes()).hexdigest()
        for entry in project.rglob('*') if entry.is_file()}


def goal_wait(events, task, current):
    return next((row for row in reversed(events) if row['kind'] == 'BotDecision'
        and row['observation'].get('goalId') == task
        and row['observation'].get('goalVersion') == current['goal']['version']
        and row['detail'].get('action') == 'Wait'), None)


def technical_wait(planned):
    return bool(planned and (planned['detail'].get('cognitionSource') or {}).get('engine') == 'host/decision-retry')


def measure(events, task, markers, stamp, restart_at, probes, active_dispatch_id=None):
    decisions = [row for row in events if row['kind'] == 'BotDecision']
    goal = [row for row in events if task and row['observation'].get('goalId') == task]
    delegates = {row['observation']['dispatchId']: row for row in goal
        if row['kind'] == 'BotDecision' and row['detail'].get('action') == 'Delegate'}
    receipts = [row for row in goal if row['kind'] == 'HarnessReceipt']
    tools = [row for row in goal if row['kind'] == 'ToolResult']
    tools_by_dispatch = {}
    for row in tools:
        tools_by_dispatch.setdefault(row['observation']['dispatchId'], []).append(row['observation'])
    accepted = {}
    verified_dispatches = set()
    redundant_verified = 0
    for row in receipts:
        obs = row['observation']
        status = obs['status']
        dispatch = obs['dispatchId']
        harness = obs['harness']
        # A report-format revision may interrupt the native turn after its successful read.
        # Only that observed dispatch can count, and only its completed tool evidence.
        after_read_interruption = status == 'interrupted' and harness == 'deepseek' and dispatch == active_dispatch_id
        if status != 'returned' and not after_read_interruption:
            continue
        for batch, marker in markers.items():
            label = re.search(r'\b' + batch + r'\b', obs['text'], re.I) is not None
            if harness == 'deepseek':
                read = any(marker in tool['text'] and tool['status'] == 'completed'
                    for tool in tools_by_dispatch.get(dispatch, []))
                verified = read and (after_read_interruption or (label and marker in obs['text']))
            else:
                instruction = delegates.get(dispatch, {}).get('detail', {}).get('instruction') or ''
                prior_read = accepted.get((batch, 'deepseek'))
                verified = (label and prior_read and prior_read < row['id'] and marker in instruction and
                    re.search(r'(?<![0-9a-f])' + marker[-4:] + r'(?![0-9a-f])', obs['text'], re.I))
            if verified:
                verified_dispatches.add(dispatch)
                stage = (batch, harness)
                if stage in accepted:
                    redundant_verified += 1
                else:
                    accepted[stage] = row['id']
    goal_decisions = [row for row in goal if row['kind'] == 'BotDecision']
    completions = [row for row in goal_decisions if row['detail'].get('action') == 'Complete']
    expected = {(batch, harness) for batch in markers for harness in ('deepseek', 'codex')}
    wrong = [row['id'] for row in completions if not expected.issubset(accepted) or
        any(index > row['id'] for index in accepted.values()) or
        not (row['detail'].get('message') or '').rstrip('。！` ').endswith(stamp) or
        markers['B08'][-4:] not in (row['detail'].get('message') or '')]
    duplicates = sum(count - 1 for count in Counter(row['observation']['dispatchId'] for row in receipts).values()
        if count > 1)
    primary_probes = list(dict.fromkeys(row['observation']['dispatchId'] for row in tools
        if row['observation']['status'] == 'failed' and MISSING in row['observation']['text']))
    redundant_primary_probes = max(0, len(primary_probes) - 1)
    planned_probe = set(primary_probes[:1])
    unproductive = [row['observation']['dispatchId'] for row in receipts
        if row['observation']['dispatchId'] not in verified_dispatches | planned_probe | {active_dispatch_id}]
    session_ids = {harness: {row['detail'].get('sessionId') for row in receipts
        if row['observation']['harness'] == harness and row['id'] < (restart_at or 0)}
        for harness in ('deepseek', 'codex')}
    resumed = {harness for harness, ids in session_ids.items() if any(row['id'] > (restart_at or 0) and
        row['observation']['harness'] == harness and row['observation']['status'] == 'returned' and
        row['detail'].get('sessionId') in ids - {None} for row in receipts)}
    sources = [(row['detail'].get('cognitionSource') or {}) for row in decisions]
    calls = [call for source in sources for call in source.get('modelCalls') or []]
    usage = [call['usage'] for call in calls if call.get('usage') is not None]
    tokens = {}
    for key in ('inputTokens', 'outputTokens', 'totalTokens', 'cachedInputTokens', 'uncachedInputTokens'):
        included = [item[key] for item in usage if item.get(key) is not None]
        tokens[key] = dict(observed=sum(included), coveredCalls=len(included))
    autonomous = {'harnessreceipt', 'timer', 'toolresult'}
    return dict(
        goalDecisionEvents=len(goal_decisions), scenarioDecisionEvents=len(decisions),
        autonomousGoalDecisions=sum(str(row['detail'].get('triggerKind')).lower() in autonomous for row in goal_decisions),
        decisionsByTrigger=Counter(str(row['detail'].get('triggerKind') or 'unknown') for row in decisions),
        decisionsByEngine=Counter(str(source.get('engine') or 'unknown') for source in sources),
        goalCompletion=dict(successful=int(bool(completions and not wrong)), attempted=1),
        incorrectCompletionDecisionIds=wrong, harnessDispatches=len(delegates),
        nativeJournals=dict(submitted=sum(row['observation'].get('dispatchSubmitted') is True for row in receipts),
            notSubmitted=sum(row['observation'].get('dispatchSubmitted') is False for row in receipts),
            unknown=sum(row['observation'].get('dispatchSubmitted') is None for row in receipts)),
        harnessReceiptsByStatus=Counter(row['observation']['status'] for row in receipts),
        nativeReturnedReceipts=sum(row['observation']['status'] == 'returned' for row in receipts),
        harnessDispatchesByProvider=Counter(row['observation']['harness'] for row in delegates.values()),
        toolEvents=len(tools), failedToolEvents=sum(row['observation']['status'] == 'failed' for row in tools),
        duplicateReceiptCount=duplicates, redundantVerifiedStages=redundant_verified,
        redundantPrimaryProbes=redundant_primary_probes,
        invalidRepeats=duplicates + redundant_verified + redundant_primary_probes,
        unexpectedUnproductiveDispatchIds=unproductive,
        invalidRepeatScope='Duplicate settlements, repeated verified stages, and repeat failures of the same primary probe. Unexpected non-progressing dispatches are listed separately.',
        verifiedStages={f'{batch}/{harness}': index for (batch, harness), index in accepted.items()},
        recovery=dict(restarts=int(restart_at is not None), resumedProviders=sorted(resumed),
            sessionRecovery=dict(successful=len(resumed), attempted=2 if restart_at is not None else 0)),
        memoryRecall=dict(correct=sum(probe['correct'] for probe in probes), attempted=len(probes),
            probes=probes),
        cognitionApi=dict(committedCallCount=len(calls), reportedUsageCallCount=len(usage),
            decisionsWithoutCallTelemetry=sum(source.get('modelCalls') is None for source in sources),
            outcomes=Counter(call['outcome'] for call in calls), tokens=tokens, billedAmount=None,
            scope='Committed cognition decisions and their attempts; Harness inference and non-committed calls excluded'))


def run(root, project, room, send, reply_for, task_for, state, wait, history, restart, record):
    seed = os.environ.get('LITTLELOOP_ENDURANCE_TRIAL_SEED')

    def fixture_value(label, size):
        if seed is None:
            return secrets.token_hex(size)
        return hashlib.sha256(f'{seed}/{label}'.encode()).hexdigest()[:2 * size]

    markers = {batch: 'LL-' + fixture_value(batch, 8) for batch in STAGES}
    for batch, marker in markers.items():
        name = BACKUP if batch == 'B01' else f'{batch}.txt'
        (project / name).write_text(marker + '\n', encoding='ascii')
    initial_files = fixture_hashes(project)
    memo = 'MEM-' + fixture_value('memo', 8)
    stamp = 'RPT-' + fixture_value('stamp', 6)
    task = restart_at = active_dispatch_id = None
    probes = []
    metadata = dict(workload='eight read-only audit batches', expectedMarkers=markers, finalNoticeStamp=stamp, outcome='incomplete')
    try:
        source = send(f'请记住一个长期约定，以后有人问我保存的北窗清点口令时，准确答复 {memo}。只保存口令，不启动任务。')
        remembered = wait(lambda: next((row['detail'] for row in rows(root, room) if row['kind'] == 'BotDecision'
            and row['observation'].get('sequence') == source['sequence']), None), 120)
        assert remembered.get('action') == 'Remember' and memo in (remembered.get('instruction') or ''), remembered
        message = send(
            '请替我完成一次只读的批次编号验收，作为一个持续目标逐轮推进。共有 B01 到 B08 八批，必须顺序处理。'
            '每批先让 deepseek 用文件工具读取一个实际编号，再让 codex 在独立的一轮只凭该完整字符串核对最后四个字符。'
            '这两个执行器的名称分别是 deepseek 和 codex，每次只分派给当前负责这一步的一位。'
            'codex 不需要再读取、运行命令或改动任何文件；只报告标签和末四位，保留前导零。已有一份正确的独立复核就进入下一批。'
            'B01 首轮请 deepseek 仅调用内容搜索工具 grep：在文件范围 B01-primary.txt 内搜 LL-，直接发工具请求；不要先用'
            'glob 检查文件是否存在，不要找其他文件。报告该次工具结果并停止本轮。若首选读取失败且未取到编号，'
            '请 Bot 在新一轮指挥 DeepSeek 改读 B01-backup.txt。其余批次直接读取对应同名 .txt，例如 B02.txt。'
            'deepseek 报告只包含批次标签、实际完整编号或读取错误。'
            'B04 两份回执齐后，请主动预约 30 秒后检查并继续 B05；这次只约一次，到点后继续，无需我再发消息。'
            '只在 B08 的 codex 回执齐备且八批都经两位执行者按顺序复核后完成。完成时请告诉我 B08 复核的末四位。')
        task = task_for(message, 120)
        metadata['task'] = task

        def running_second_batch():
            running = active_dispatch(root, task)
            if running and running['harness'] == 'deepseek':
                mentioned_files = re.findall(r'\bB0[1-8](?:-primary|-backup)?\.txt\b', running['instruction'], re.I)
                if mentioned_files and mentioned_files[0].lower() == 'b02.txt':
                    # A B01 instruction can mention B02 in an exclusion. Require B01's real
                    # Codex proof and the current native dispatch's first concrete file target.
                    groups = measure(rows(root, room), task, markers, stamp, None, [])
                    if 'B01/codex' in groups['verifiedStages']:
                        return running
            current = state(task)
            assert current['status'] not in ('paused', 'completed', 'failed', 'cancelled',
                'needs-reconciliation'), (current['status'], current.get('error'), 'stopped before the running revision')
            if current['status'] == 'waiting-input':
                assert current.get('wakeAtUnixMilliseconds') and technical_wait(goal_wait(rows(root, room), task, current)), (
                    current['status'], current.get('error'), 'stopped before the running revision')
            return None

        active = wait(running_second_batch, 480)
        active_dispatch_id = active['id']
        supplement = send(
            f'补充这项正在执行的验收。最终通知的末尾请加上验收流水号 {stamp}，其他批次、双回执要求及计时要求照旧。', message['sequence'])
        metadata['revisionDuringNativeDispatch'] = dict(dispatchId=active['id'], runningVersion=active['goalVersion'],
            userSequence=supplement['sequence'])
        wait(lambda: any(row['kind'] == 'BotDecision' and row['detail'].get('action') == 'Revise'
            and row['observation'].get('sequence') == supplement['sequence']
            and row['observation'].get('goalId') == task and stamp in (row['detail'].get('instruction') or '')
            for row in rows(root, room)), 120)
        record('the running native dispatch was observed before submitting a revision tied to its goal')

        def scheduled():
            current = state(task)
            assert current['status'] not in ('completed', 'failed', 'cancelled', 'needs-reconciliation'), (
                current['status'], current.get('error'), 'before restart')
            if current['status'] == 'waiting-input':
                assert current.get('wakeAtUnixMilliseconds'), (
                    current.get('error'), 'a human-input wait stopped the autonomous run')
                events = rows(root, room)
                event = goal_wait(events, task, current)
                if event and not technical_wait(event):
                    last_review = measure(events, task, markers, stamp, None, [], active_dispatch_id)['verifiedStages'].get('B04/codex')
                    assert last_review and last_review < event['id'], 'business timer was scheduled before the verified B04 review'
                    return event
            return None

        timer = wait(scheduled, 600)
        restart_at = timer['id']
        before = state(task)
        assert stamp in ' '.join(before['goal']['requirements']) and before.get('wakeAtUnixMilliseconds'), before
        restarted_at = time.time()
        assert restarted_at * 1000 < before['wakeAtUnixMilliseconds'], 'the acceptance driver did not stop the service before the timer was due'
        restart()
        assert state(task)['goal']['version'] >= before['goal']['version']
        metadata['restart'] = dict(waitDecision=restart_at, unixTime=restarted_at,
            delaySeconds=timer['detail']['wakeAfterSeconds'], dueUnixMilliseconds=before['wakeAtUnixMilliseconds'])

        def completed():
            current = state(task)
            assert current['status'] not in ('failed', 'cancelled', 'needs-reconciliation'), current
            assert current['status'] != 'waiting-input' or current.get('wakeAtUnixMilliseconds'), current
            return current if current['status'] == 'completed' else None

        wait(completed, 720)
        events = rows(root, room)
        completion, = (row for row in events if row['kind'] == 'BotDecision'
            and row['observation'].get('goalId') == task and row['detail'].get('action') == 'Complete')
        final_text = completion['detail'].get('message')
        assert final_text and final_text.rstrip('。！` ').endswith(stamp) and markers['B08'][-4:] in final_text, completion
        notices = wait(lambda: [row for row in history() if row['sender'] == 'littleloop' and row['text'] == final_text
            and (row.get('card') or {}).get('taskId') == task], 60)
        assert len(notices) == 1, notices
        metadata['finalNotice'] = notices[0]
        timers = [row for row in events if row['kind'] == 'Timer' and row['observation'].get('goalId') == task
            and row['observation'].get('status') != 'decision-retry']
        assert len(timers) == 1 and timers[0]['id'] > restart_at and timers[0]['observation'].get(
            'wakeAtUnixMilliseconds') == before['wakeAtUnixMilliseconds'] and timers[0]['observation'].get(
            'goalVersion') == before['goal']['version'], 'the persisted business timer did not enter the Bot once after restart'
        failed = {row['observation']['dispatchId'] for row in events if row['kind'] == 'ToolResult'
            and row['observation'].get('goalId') == task and row['observation'].get('status') == 'failed'
            and (MISSING in row['observation']['text'] or re.search(
                r'missing|not found|does not exist|文件不存在|未找到|ENOENT', row['observation']['text'], re.I))}
        assert failed, 'no real native failed read of the primary source was recorded'
        failure_receipt = min(row['id'] for row in events if row['kind'] == 'HarnessReceipt'
            and row['observation']['dispatchId'] in failed)
        assert any(row['kind'] == 'BotDecision' and row['id'] > failure_receipt and
            row['detail'].get('action') == 'Delegate' and BACKUP in (row['detail'].get('instruction') or '')
            and row['observation'].get('goalId') == task for row in events), 'the Bot did not instruct a separate fallback after the failed receipt'
        record('a real native tool failure led the Bot to select a separate fallback, then its timer resumed after restart')

        # The teaching message must be outside the normal 32-event window before the unquoted probes.
        assert len(events) > 32 and all(memo not in row['observation']['text'] for row in events[-32:])
        for question, expected in [('之前保存的北窗清点口令是什么？请只回复该口令。', memo),
            ('曾保存南窗清点口令吗？若无记录，请只回复“未设置”。', '未设置')]:
            answer = reply_for(send(question), 120)['text'].strip()
            probes.append(dict(question=question, expected=expected, answer=answer,
                correct=answer.strip('。！「」“”` ') == expected))
        result = measure(rows(root, room), task, markers, stamp, restart_at, probes, active_dispatch_id)
        stages = result['verifiedStages']
        ordered = [stages[f'{batch}/{harness}'] for batch in STAGES for harness in ('deepseek', 'codex')]
        assert all(first < second for first, second in zip(ordered, ordered[1:])), result
        assert stages['B04/codex'] < restart_at < timers[0]['id'] < stages['B05/deepseek'], result
        assert 20 <= result['goalDecisionEvents'] <= 50 and result['scenarioDecisionEvents'] <= 50, result
        # A second settlement for the same dispatch is a durability defect. A fresh but redundant
        # decision is a workload quality observation; the independent goal oracle still decides completion.
        assert result['goalCompletion']['successful'] == 1 and result['duplicateReceiptCount'] == 0, result
        assert result['recovery']['resumedProviders'] == ['codex', 'deepseek'], result
        assert result['memoryRecall']['correct'] == len(probes), result
        assert fixture_hashes(project) == initial_files, 'the read-only workload changed the fixture files'
        record('eight independent native handoffs, one completion notice, same native sessions after restart, and two memory probes')
        metadata['outcome'] = 'passed'
    finally:
        events = rows(root, room)
        report = dict(case='endurance', **metadata,
            metrics=measure(events, task, markers, stamp, restart_at, probes, active_dispatch_id))
        (root / 'endurance-trace.json').write_text(json.dumps(dict(**report, events=events), ensure_ascii=False, indent=2), encoding='utf-8')
    return report
