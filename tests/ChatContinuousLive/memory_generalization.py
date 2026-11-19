"""Held-out synthetic private tasks for the direct HTTP fixture."""

from collections import Counter
from dataclasses import dataclass
import json
import re
import secrets
import time


@dataclass(frozen=True)
class Trial:
    name: str
    objective: str
    expected_sources: tuple[str, ...] = ()
    explicit_prefix: str = ''


TRIALS = (
    Trial('same-topic', '整理一份交接清单并生成交接编号。', ('handoff',)),
    Trial('open-chinese-paraphrase', '给新同事汇总要移交的待办事项，生成这份汇总的编号。', ('handoff',)),
    Trial('english-inflection', 'Prepare a new equipment inspection and generate its record number.', ('inspection',)),
    Trial('known-bilingual-alias', 'Prepare the changelog and generate its tracking number.', ('changelog',)),
    Trial('open-cross-language', 'Draft the acceptance certificate and generate its tracking number.', ('acceptance',)),
    Trial('different-object', '整理退货单并生成退货编号，使用 RET- 作为编号前缀。', (), 'RET-'),
    Trial('incidental-restriction', '编写验收单并生成验收编号。本次仅修改 notes.txt。', ('acceptance',)),
    Trial('negative-current-override', '编写验收单。这次不要添加编号前缀。'),
    Trial('affirmative-current-override', '编写验收单，使用 VIP- 作为编号前缀。', (), 'VIP-'),
    Trial('topic-used-as-background', '编写验收单模板使用手册，为这份手册本身生成编号。'),
    Trial('two-relevant-topics', '同时整理交接清单和设备巡检表。按上述顺序分别生成两条编号。', ('handoff', 'inspection_zh')),
    Trial('updated-source', '整理值班日志并生成值班编号。', ('shift_current',)),
    Trial('withdrawn-source', '整理参访记录并生成参访编号。'),
)


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def read_events(path):
    if not path.is_file():
        return []
    # The live process may still be finishing its last write after a timeout.
    return [json.loads(line) for line in path.read_text(encoding='utf-8').splitlines() if line.endswith('}')]


def measure_trial(root, project, trial, number, sources, send, task_for, state, wait, decide):
    notes = project / 'notes.txt'
    notes.write_bytes(b'PENDING\n')
    count = max(1, len(trial.expected_sources))
    output_requirement = (f'Use {number} as the numeric part. Follow any applicable project convention, '
                          'otherwise use the number as-is. Put the result in notes.txt, one number per line, '
                          'then run the host verify test.')
    if count == 2:
        output_requirement += ' Write both results on separate lines.'
    objective = '任务：' + trial.objective + ' ' + output_requirement
    expected = ([sources[name]['marker'] + number for name in trial.expected_sources] or
                [trial.explicit_prefix + number])
    expected_sequences = {sources[name]['sequence'] for name in trial.expected_sources}
    started = time.monotonic()
    message = send(objective)
    task = task_for(message)
    decisions = []

    def advance():
        current = state(task)
        if current['status'] not in ('queued', 'running'):
            return current
        pending = (current.get('execution') or {}).get('approval')
        if pending and pending.get('status') == 'pending' and not any(row['nonce'] == pending['nonce'] for row in decisions):
            args = pending['parameters']['arguments']
            tool = pending['parameters']['tool']
            allowed = (tool == 'project_patch' and args['path'] == 'notes.txt') or (
                tool == 'project_command' and args['command'] == 'verify')
            decision = 'accept' if allowed else 'decline'
            decide(task, pending['nonce'], decision)
            decisions.append(dict(nonce=pending['nonce'], tool=tool, decision=decision))
        return None

    error = None
    try:
        current = wait(advance, seconds=100)
    except TimeoutError as exc:
        error = str(exc)
        send('取消', reply=message['sequence'])
        current = wait(lambda: (value if (value := state(task))['status'] not in ('queued', 'running') else None), seconds=30)

    execution = current.get('execution') or {}
    folder = root / 'bot' / task.replace('-', '')
    requests = read_events(folder / 'requests.jsonl')
    events = read_events(folder / 'events.jsonl')
    prompt = next((call['params']['input'][0]['text'] for call in requests if call.get('method') == 'turn/start'), None)
    actual_sequences = ({int(value) for value in re.findall(r'"sourceSequence"\s*:\s*(\d+)\b', prompt)}
                        if prompt is not None else None)
    contents = notes.read_text(encoding='utf-8').splitlines()
    unexpected_markers = [source['marker'] for source in sources.values()
                          if source['sequence'] not in expected_sequences and any(source['marker'] in line for line in contents)]
    usage = [row['params']['tokenUsage']['total'] for row in events if row.get('method') == 'thread/tokenUsage/updated']
    tools = [row['params'].get('tool', '?') for row in events if row.get('method') == 'item/tool/call']
    model = next((row['params']['thread'] for row in events if row.get('method') == 'thread/started'), {})
    stderr = folder / 'stderr.log'
    capacity_error = error and stderr.is_file() and 'Selected model is at capacity' in stderr.read_text(encoding='utf-8')
    file_pass = contents == expected and (project / 'code.txt').read_text(encoding='utf-8') == 'broken'
    host_pass = any(receipt['exitCode'] == 0 for receipt in execution.get('receipts', []))
    return dict(name=trial.name, objective=objective, task=task, thread=execution.get('threadId'), status=current['status'],
                error=error, infrastructureError='model-capacity' if capacity_error else None,
                model=model.get('model'), modelProvider=model.get('modelProvider'), reasoningEffort=model.get('reasoningEffort'),
                elapsedSeconds=round(time.monotonic() - started, 2),
                expectedSources=sorted(expected_sequences), recalledSources=sorted(actual_sequences) if actual_sequences is not None else None,
                missedSources=sorted(expected_sequences - actual_sequences) if actual_sequences is not None else None,
                wrongSources=sorted(actual_sequences - expected_sequences) if actual_sequences is not None else None,
                actualFile=contents, expectedFile=expected, unexpectedMemoryMarkersWritten=unexpected_markers,
                filePassed=file_pass, hostPassed=host_pass, approvals=decisions, tools=dict(Counter(tools)),
                toolCalls=len(tools), tokenUsage=usage[-1] if usage else None)


def run(root, project, send, reply_for, task_for, state, wait, decide, restart, names=()):
    sources = {}

    def teach(name, template, stem):
        marker = stem + '-' + secrets.token_hex(4).upper() + '-'
        message = send(template.format(marker))
        reply = reply_for(message)
        assert marker in reply['text'], (name, reply['text'])
        sources[name] = dict(marker=marker, sequence=message['sequence'], teaching=message['text'])

    teach('handoff', '以后处理交接清单时，编号使用 {} 作为前缀。', 'HO')
    teach('inspection', 'When preparing equipment inspections, use {} as the tracking number prefix.', 'EI')
    teach('inspection_zh', '以后处理设备巡检表时，编号使用 {} 作为前缀。', 'CK')
    teach('changelog', '以后处理更新日志时，编号使用 {} 作为前缀。', 'CL')
    teach('acceptance', '以后处理验收单时，编号使用 {} 作为前缀。', 'QA')
    teach('shift_old', '以后处理值班日志时，编号使用 {} 作为前缀。', 'OLD')
    teach('shift_current', '以后处理值班日志时，编号使用 {} 作为前缀。', 'NEW')
    teach('visit_withdrawn', '以后处理参访记录时，编号使用 {} 作为前缀。', 'VIS')
    withdrawal = reply_for(send('撤回经验', reply=sources['visit_withdrawn']['sequence']))
    assert '已撤回' in withdrawal['text'], withdrawal['text']
    write_json(root / 'memory-sources.json', sources)
    restart()

    trials = []
    selected = [(index, trial) for index, trial in enumerate(TRIALS) if not names or trial.name in names]
    for position, (index, trial) in enumerate(selected):
        row = measure_trial(root, project, trial, str(100 + index), sources, send, task_for, state, wait, decide)
        trials.append(row)
        write_json(root / 'memory-generalization-trials.json', trials)
        print(f"{row['name']}: status={row['status']} expected={row['expectedSources']} "
              f"observed={row['recalledSources']} file={row['filePassed']} wrong-write={bool(row['unexpectedMemoryMarkersWritten'])}", flush=True)
        if row['error'] and position + 1 < len(selected):
            # A cancelled task can leave the provider's retry in flight, holding the shared fixture workspace.
            restart()
    observed = [row for row in trials if row['recalledSources'] is not None]
    summary = dict(planned=len(trials), dispatched=len(observed), completed=sum(row['status'] == 'completed' for row in trials),
                   matchedSources=sum(row['expectedSources'] == row['recalledSources'] for row in observed),
                   expectedPositive=sum(bool(row['expectedSources']) for row in observed),
                   expectedNegative=sum(not row['expectedSources'] for row in observed),
                   positiveCompleteRecall=sum(bool(row['expectedSources']) and not row['missedSources'] for row in observed),
                   negativeCorrectAbstention=sum(not row['expectedSources'] and not row['wrongSources'] for row in observed),
                   missedSourceTrials=sum(bool(row['missedSources']) for row in observed),
                   wrongSourceTrials=sum(bool(row['wrongSources']) for row in observed),
                   wrongMemoryFileTrials=sum(bool(row['unexpectedMemoryMarkersWritten']) for row in trials),
                   passingFiles=sum(row['filePassed'] for row in trials), passingHostTests=sum(row['hostPassed'] for row in trials),
                   toolCalls=sum(row['toolCalls'] for row in trials), usageObserved=sum(row['tokenUsage'] is not None for row in trials))
    return dict(mode='memory-generalization', summary=summary, trials=trials)
