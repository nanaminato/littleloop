"""Abruptly kill the isolated Chat process tree while an actual native turn is underway."""

import json
import secrets
import sqlite3
import time

from autonomy_endurance import fixture_hashes, rows


def read_native(journal):
    try:
        return json.loads(journal.read_text(encoding='utf-8'))
    except (FileNotFoundError, PermissionError):
        return None


def run(root, project, room, send, task_for, state, wait, kill_fixture, restart_fixture, record):
    markers = {f'crash-read-{ordinal:02}.txt': 'CRASH-' + secrets.token_hex(8) for ordinal in range(1, 21)}
    for name, marker in markers.items():
        (project / name).write_text(marker + '\n', encoding='ascii')
    before_files = fixture_hashes(project)
    evidence = root / 'inflight-crash-trace.json'
    trace = {'case': 'inflight-crash', 'outcome': 'incomplete'}
    work = None
    try:
        source = send(
            '请用已注册的 deepseek 执行一次只读文件核对。请在同一原生回合里按顺序分别用文件工具读取 '
            'crash-read-01.txt 到 crash-read-20.txt（每次只读一个文件），返回每个文件的实际完整值。'
            '工作区是本次临时夹具，不要查询网络、跳过文件或修改任何文件。')
        work = task_for(source, 120)
        trace['goalId'] = work

        def sent_and_processing():
            with sqlite3.connect(f'{(root / "chat.db").as_uri()}?mode=ro', uri=True) as db:
                claim = db.execute('SELECT input,goal FROM chat_bot_work WHERE id=? AND status=\'running\'', (work,)).fetchone()
            if claim:
                dispatch = json.loads(claim[0])['loop']['dispatch']
                assert dispatch['harness'] == 'deepseek', 'Bot did not select the read-only native Harness'
                journal = root / 'bot' / work.replace('-', '') / 'harnesses' / 'deepseek' / (dispatch['id'].replace('-', '') + '.json')
                native = read_native(journal)
                if native and native['Status'] == 'running' and native.get('DispatchSubmitted') is True and any(
                    tool['Status'] == 'completed' and markers['crash-read-01.txt'] in tool['Detail'] for tool in native['Tools']
                ):
                    return dispatch, journal, native, json.loads(claim[1])['version']
            current = state(work)['status']
            assert current not in ('waiting-input', 'needs-reconciliation', 'completed', 'cancelled', 'failed'), (
                current, 'native turn did not remain active after a verified read')
            return None

        dispatch, journal, native, version = wait(sent_and_processing, 180)
        trace.update(dispatchId=dispatch['id'], goalVersion=version, journal=str(journal.relative_to(root)),
            beforeKill=native, killAt=time.time())
        trace['crashedPid'] = kill_fixture()
        stopped = read_native(journal)
        trace['afterKillBeforeRestart'] = stopped
        assert stopped and stopped['Status'] == 'running' and stopped.get('DispatchSubmitted') is True, (
            'no unresolved native turn at the point of simulated crash', stopped)
        record('a real DeepSeek tool result was durable before abrupt process-tree termination')

        trace['restartPid'] = restart_fixture()
        assert trace['restartPid'] != trace['crashedPid']

        def recovered():
            event = next((row for row in rows(root, room) if row['kind'] == 'BotDecision'
                and row['observation'].get('goalId') == work
                and row['detail'].get('triggerDispatchId') == dispatch['id']
                and row['detail'].get('action') == 'Wait'), None)
            current = state(work)
            return event if event and current['status'] == 'needs-reconciliation' else None

        wait(recovered, 60)
        # Observe several additional work-loop ticks before verifying the dispatch fence.
        time.sleep(2)
        events = rows(root, room)
        assert [row['observation'].get('dispatchId') for row in events if row['kind'] == 'BotDecision'
            and row['detail'].get('action') == 'Delegate' and row['observation'].get('goalId') == work] == [dispatch['id']]
        returned = [row['observation'] for row in events if row['kind'] == 'HarnessReceipt'
            and row['observation'].get('dispatchId') == dispatch['id']]
        assert len(returned) == 1 and returned[0]['status'] == 'uncertain'
        assert returned[0].get('dispatchSubmitted') is True and returned[0].get('goalVersion') == version
        recovered_native = read_native(journal)
        assert recovered_native and recovered_native['Status'] == 'uncertain'
        assert recovered_native['SessionId'] == stopped['SessionId'] and recovered_native['Tools'] == stopped['Tools']
        assert len([row for row in events if row['kind'] == 'ToolResult'
            and row['observation'].get('dispatchId') == dispatch['id']]) == len(stopped['Tools'])
        assert not any(row['kind'] == 'BotDecision' and row['detail'].get('action') == 'Complete'
            and row['observation'].get('goalId') == work for row in events)
        with sqlite3.connect(f'{(root / "chat.db").as_uri()}?mode=ro', uri=True) as db:
            assert not db.execute('SELECT 1 FROM chat_bot_inflight WHERE work_id=?', (work,)).fetchone()
        assert fixture_hashes(project) == before_files
        record('real in-flight crash recovered one uncertain receipt without replay or completion')
        trace['outcome'] = 'passed'
        return trace
    finally:
        trace['events'] = rows(root, room)
        evidence.write_text(json.dumps(trace, ensure_ascii=False, indent=2), encoding='utf-8')
