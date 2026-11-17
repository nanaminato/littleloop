"""Opt-in live Codex + HTTP acceptance, using an isolated goal_test database."""
import http.cookiejar
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
import hashlib
import ctypes
from soak_metrics import SoakMetrics, atomic_json

repo = Path(__file__).resolve().parents[2]
connection = os.environ['LITTLELOOP_CHAT_TEST_CONNECTION']
assert re.search(r'(?i)database=[^;]*goal_test', connection), 'Use an isolated goal_test database'
root = repo / 'data' / 'continuous-http' / uuid.uuid4().hex[:8]
project = root / 'project'
project.mkdir(parents=True)
(project / 'code.txt').write_text('broken', encoding='utf-8')
(project / 'notes.txt').write_text('pending', encoding='utf-8')
(project / 'verify.py').write_text("from pathlib import Path\nassert Path('code.txt').read_text().strip() == 'fixed'\nassert Path('notes.txt').read_text().strip() == 'supplemented'\nprint('PASS code and supplemental requirement')\n", encoding='utf-8')
policy = dict(executable=os.environ['LITTLELOOP_CODEX_EXECUTABLE'], project=str(project),
              maxTurns=8, timeoutSeconds=300, maxRepairs=1, development=dict(
                  root=str(project), writableFiles=['code.txt', 'notes.txt'], readExtensions=['.txt', '.py'], budget=40,
                  commands=[dict(id='verify', executable=sys.executable, arguments=['verify.py'], isTest=True, protectedFiles=['verify.py'])]))
(root / 'policy.json').write_text(json.dumps(policy), encoding='utf-8')
port = int(os.environ.get('LITTLELOOP_CHAT_TEST_PORT', '5196'))
assert 1024 <= port <= 65535 and port != 5190
env = {k: v for k, v in os.environ.items() if not k.lower().startswith(('chat__', 'chatbot__', 'chatfcm__', 'connectionstrings__', 'chatautomation__'))}
env.update(ConnectionStrings__Chat=connection, Chat__Invite=secrets.token_hex(24),
           Chat__Keys=str(root / 'keys'), Chat__Client=str(repo / 'src/LittleLoop.Workbench/Client/dist/littleloop-workbench/browser'),
           Chat__Url=f'http://127.0.0.1:{port}')
base = f'http://127.0.0.1:{port}'
with socket.socket() as probe:
    assert probe.connect_ex(('127.0.0.1', port)) != 0, 'Acceptance port already in use'
rounds = int(os.environ.get('LITTLELOOP_SOAK_ROUNDS', '1'))
assert 1 <= rounds <= 10000
duration = float(os.environ.get('LITTLELOOP_SOAK_SECONDS', '0'))
assert 0 <= duration <= 7 * 24 * 60 * 60
restart_every = int(os.environ.get('LITTLELOOP_SOAK_RESTART_EVERY', '1'))
interval = float(os.environ.get('LITTLELOOP_SOAK_INTERVAL_SECONDS', '0'))
assert 1 <= restart_every <= 10000 and 0 <= interval <= 3600
started = time.monotonic()
metrics = SoakMetrics(root)
phase = 'bootstrap'
log_file = root / 'status.json'
service_dll = os.environ.get('LITTLELOOP_CHAT_DLL', str(repo / 'src/LittleLoop.Chat/bin/Debug/net10.0/LittleLoop.Chat.dll'))
atomic_json(root / 'run.json', dict(startedAt=time.strftime('%Y-%m-%dT%H:%M:%S%z'), durationSeconds=duration,
    roundLimit=rounds, restartEvery=restart_every, intervalSeconds=interval,
    binary=service_dll, sha256=hashlib.sha256(Path(service_dll).read_bytes()).hexdigest(),
    assemblyHashes={name: hashlib.sha256(Path(service_dll).with_name(name).read_bytes()).hexdigest()
        for name in ('LittleLoop.Chat.dll', 'LittleLoop.Core.dll', 'LittleLoop.Tools.dll')},
    driverSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
    metricsSha256=hashlib.sha256(Path(__file__).with_name('soak_metrics.py').read_bytes()).hexdigest()))
print('Evidence: ' + str(root), flush=True)
results = []
deepseek = os.environ.get('LITTLELOOP_TEST_DEEPSEEK') == '1'
controlled = deepseek and os.environ.get('LITTLELOOP_TEST_DEEPSEEK_WRITE') == '1'
alternating = controlled and os.environ.get('LITTLELOOP_TEST_ALTERNATING') == '1'
if controlled:
    (root / 'deepseek-readonly').mkdir()
    codex_project = root / 'codex-project'
    codex_project.mkdir()
    for name in ('code.txt', 'notes.txt', 'verify.py'):
        (codex_project / name).write_bytes((project / name).read_bytes())
    (root / 'policy.json').write_text(json.dumps(dict(policy, project=str(codex_project),
        development=dict(policy['development'], root=str(codex_project)))), encoding='utf-8')
    (project / 'verify.py').write_text("from pathlib import Path\nassert Path('code.txt').read_text() == 'fixed'\nPath('.littleloop-temp/test-ran').write_text('verified')\nprint('DEEPSEEK-VERIFY-PASS')\n", encoding='utf-8')
    atomic_json(root / 'deepseek-grant.json', policy['development'])
    if alternating:
        for fixture in (project, codex_project):
            (fixture / 'verify.py').write_text("from pathlib import Path\nassert Path('code.txt').read_text() == 'fixed'\nwith Path('.littleloop-temp/test-ran').open('a') as f: f.write('verified\\n')\nprint('ALTERNATING-VERIFY-PASS')\n", encoding='utf-8')
password = secrets.token_hex(20)
def client():
    return urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
owner, approver = client(), client()
def request(client, path, body=None, expected=200):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + '/api/chat' + path, data=data,
                                 headers={'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1'})
    try:
        with client.open(req, timeout=10) as response: status, raw = response.status, response.read()
    except urllib.error.HTTPError as e: status, raw = e.code, e.read()
    assert status == expected, (path, status, raw.decode()[:1500])
    return json.loads(raw) if raw else None
process = None
streams = []
def start(label, bot=False):
    global process
    assert process is None or process.poll() is not None, 'Previous fixture service is still running'
    for stream in streams: stream.close()
    streams.clear()
    child_env = dict(env)
    if bot:
        child_env.update(ChatBot__Policy=str(root / 'policy.json'), ChatBot__Data=str(root / 'bot'),
                         ChatBot__Operators='s1_owner', ChatBot__Approvers='s1_approver')
        if deepseek:
            harness = json.loads(Path(os.environ['LITTLELOOP_TEST_DEEPSEEK_CONFIG']).read_text(encoding='utf-8-sig'))
            for key, value in harness.items():
                child_env['ChatBot__DeepSeek__' + key] = value
            child_env['ChatBot__DeepSeek__Project'] = str(project)
            if controlled:
                child_env['ChatBot__DeepSeek__Project'] = str(root / 'deepseek-readonly')
                child_env['ChatBot__DeepSeek__TestDevelopmentPolicy'] = str(root / 'deepseek-grant.json')
    stdout = open(root / (label + '.out'), 'wb'); stderr = open(root / (label + '.err'), 'wb')
    streams.extend([stdout, stderr])
    process = subprocess.Popen(['dotnet', service_dll],
                               cwd=repo, env=child_env, stdout=stdout, stderr=stderr,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    wait(lambda: ready(), 30)
    metrics.sample(process, True)
def ready():
    assert process.poll() is None, 'HTTP service exited; inspect ' + str(root)
    try:
        with client().open(base + '/chat', timeout=2) as r: return r.status == 200
    except (urllib.error.URLError, TimeoutError): return False
def stop():
    global process
    if process and process.poll() is None:
        # Assertions may fail while Codex is still active: don't leave fixture children behind.
        stopped = subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True)
        assert stopped.returncode == 0 or process.poll() is not None, 'Could not stop fixture process tree'
        process.wait(timeout=15)
    process = None
def wait(fn, seconds=240):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        metrics.sample(process)
        value = fn()
        if value: return value
        time.sleep(.3)
    raise TimeoutError('Condition not reached; evidence ' + str(root))
def log(text):
    print(text, flush=True)
    atomic_json(log_file, dict(status='running', phase=text, completedRounds=len(results),
        elapsedSeconds=time.monotonic()-started, pid=process.pid if process else None))

def observe_until(deadline):
    while time.monotonic() < deadline:
        assert ready(), 'Service unavailable between tasks'
        metrics.sample(process)
        atomic_json(log_file, dict(status='running', phase='between-tasks', completedRounds=len(results),
            elapsedSeconds=time.monotonic()-started, pid=process.pid))
        time.sleep(min(5, max(0, deadline-time.monotonic())))

try:
    assert ctypes.windll.kernel32.SetThreadExecutionState(0x80000001), 'Could not hold system awake for soak'
    start('bootstrap')
    for name in ['s1_owner', 's1_approver']:
        request(owner, '/register', dict(name=name, password=password, invite=env['Chat__Invite']), 204)
    stop(); start('before-pause', True)
    for c, name in [(owner, 's1_owner'), (approver, 's1_approver')]: request(c, '/login', dict(name=name, password=password))
    room = request(owner, '/rooms', dict(type='group', title='S1 live acceptance', members=['s1_approver']))['id']
    request(owner, f'/bot/rooms/{room}/install', dict(enabled=True), 204)
    def send(text, reply=None, identifier=None):
        return request(owner, f'/rooms/{room}/messages', dict(clientId=identifier or str(uuid.uuid4()), text=text, replyTo=reply))
    if alternating:
        from alternating_restarts import verify_alternating
        results.extend(verify_alternating(project, codex_project, root, room, owner, approver, request, send,
                                          start, stop, wait, log))
        atomic_json(root / 'alternating-result.json', results)
    if controlled and not alternating:
        assert not duration and os.environ.get('LITTLELOOP_SOAK_FAULTS') != '1'
        anchor = send('DeepSeek controlled tools fixture')
        marker = project / '.littleloop-temp' / 'test-ran'
        code = project / 'code.txt'
        def schedule():
            return request(owner, f'/bot/rooms/{room}/schedule', dict(clientId=str(uuid.uuid4()),
                sequence=anchor['sequence'], provider='deepseek_test', goal='先用 project_read 读取 code.txt，若为 broken 则用 project_patch 精确改为 fixed。文件正确后申请 project_command 运行 verify。仅使用宿主工具。'), 202)
        def state():
            value = request(approver, f'/bot/work/{task}')
            assert value['provider'] == 'deepseek_test' and value['status'] not in ('failed', 'needs-reconciliation', 'disabled', 'refused'), value
            return value
        def approval(tool):
            def pending():
                current = state()
                assert current['status'] not in ('completed','waiting-input'), current
                a = (current.get('execution') or {}).get('approval')
                if a:
                    assert a['parameters']['tool'] == tool, a
                    return a
            return wait(pending)
        def decide(a, value, expected=204):
            request(approver, f'/bot/work/{task}/decision', dict(nonce=a['nonce'], value=value), expected)
        message = schedule()
        task = message['card']['taskId']
        a = approval('project_patch')
        assert code.read_text() == 'broken' and not marker.exists()
        decide(a, 'decline')
        wait(lambda: state()['status'] == 'waiting-input')
        assert code.read_text() == 'broken' and not marker.exists()
        declined = task
        log('PASS DeepSeek patch rejection has no effects')
        code.write_text('fixed', encoding='utf-8')
        message = schedule()
        task = message['card']['taskId']
        a = approval('project_command')
        assert a['parameters']['arguments'] == {'command': 'verify'} and not marker.exists()
        decide(a, 'decline')
        wait(lambda: state()['status'] == 'waiting-input')
        assert not marker.exists() and not state()['execution']['receipts']
        declined_test = task
        log('PASS DeepSeek test rejection does not start the test')
        code.write_text('broken', encoding='utf-8')
        message = schedule()
        task = message['card']['taskId']
        a = approval('project_patch')
        assert a['parameters']['arguments']['path'] == 'code.txt' and code.read_text() == 'broken' and not marker.exists()
        decide(a, 'accept')
        a = approval('project_command')
        assert a['parameters']['arguments'] == {'command': 'verify'} and code.read_text() == 'fixed' and not marker.exists()
        send('暂停', message['sequence'])
        wait(lambda: state()['status'] == 'paused' and state()['execution']['status'] == 'suspended')
        decide(a, 'accept', 409)
        assert not marker.exists()
        log('PASS DeepSeek pause invalidates pending test approval without running the test')
        send('恢复', message['sequence'])
        a = approval('project_command')
        assert a['parameters']['arguments'] == {'command': 'verify'} and not marker.exists()
        decide(a, 'accept')
        result = wait(lambda: (value if (value := state())['status'] == 'completed' else None))
        assert marker.read_text() == 'verified' and code.read_text() == 'fixed'
        assert any(r['id'] == 'verify' and r['exitCode'] == 0 and 'DEEPSEEK-VERIFY-PASS' in r['standardOutput'] for r in result['execution']['receipts'])
        results.append(dict(task=task, rejectedPatchTask=declined, rejectedTestTask=declined_test, result=result))
        atomic_json(root / 'deepseek-write-result.json', results[0])
        log('PASS DeepSeek executes only approved patch and fixed test, with actual receipts')
    if deepseek and not controlled:
        assert not duration and os.environ.get('LITTLELOOP_SOAK_FAULTS') != '1'
        assert 'deepseek' in request(owner, '/bot/capabilities')['providers']
        marker = 'fixture-' + uuid.uuid4().hex
        (project / 'code.txt').write_text(marker, encoding='utf-8')
        anchor = send('ACP read-only fixture')
        message = request(owner, f'/bot/rooms/{room}/schedule', dict(clientId=str(uuid.uuid4()),
            sequence=anchor['sequence'], provider='deepseek',
            goal='请用只读搜索查找 code.txt 中以 fixture- 开头的完整标记，并在输出中原样引用该标记。'), 202)
        task = message['card']['taskId']
        folder = root / 'bot' / task.replace('-', '')
        receipt_path = folder / 'deepseek-execution.json'
        def receipt():
            return json.loads(receipt_path.read_text(encoding='utf-8')) if receipt_path.exists() else {}
        def state():
            value = request(owner, f'/bot/work/{task}')
            assert value['provider'] == 'deepseek' and value['goal']['status'] == value['status']
            assert value['status'] not in ('failed', 'needs-reconciliation', 'disabled'), value
            return value
        wait(lambda: receipt().get('stage') == 'prompting', 30)
        send('暂停', message['sequence'])
        wait(lambda: state()['status'] == 'paused' and receipt().get('stage') == 'suspended', 40)
        session = receipt()['sessionId']
        send('补充：请在结果中另附 ACP-SUPPLEMENT-OK。', message['sequence'])
        wait(lambda: state()['goal']['version'] == 3 and state()['status'] == 'paused', 15)
        stop(); start('deepseek-paused-restart', True)
        assert state()['status'] == 'paused' and receipt()['sessionId'] == session
        send('恢复', message['sequence'])
        result = wait(lambda: (value if (value := state())['status'] == 'completed' else None))
        assert result['goal']['version'] == 4 and receipt() == dict(sessionId=session, stage='settled')
        assert marker in result['output'] and 'ACP-SUPPLEMENT-OK' in result['output'], result
        assert (project / 'code.txt').read_text(encoding='utf-8') == marker
        results.append(dict(task=task, sessionId=session, result=result))
        atomic_json(root / 'deepseek-result.json', results[0])
        log('PASS DeepSeek intake, cancellation, supplement and same-session resume after service restart')
    for iteration in range(1, 1 if deepseek else rounds + 1):
        round_started = time.monotonic()
        restart_round = iteration % restart_every == 0 or (duration and time.monotonic()-started >= duration)
        (project / 'code.txt').write_text('broken', encoding='utf-8')
        (project / 'notes.txt').write_text('pending', encoding='utf-8')
        original_id = str(uuid.uuid4())
        original_text = '@littleloop 任务：将 code.txt 中 broken 改为 fixed，然后运行 verify 测试。只使用宿主工具。'
        message = send(original_text, identifier=original_id)
        assert send(original_text, identifier=original_id)['sequence'] == message['sequence']
        def find_task():
            return next((m['card']['taskId'] for m in request(owner, f'/rooms/{room}/messages')
                         if m['sequence'] == message['sequence'] and m.get('card')), None)
        task = wait(find_task, 20)
        def state():
            current = request(approver, f'/bot/work/{task}')
            assert current['goal']['status'] == current['status'], 'Scheduler status disagrees with durable goal'
            return current
        def approval(): return (state().get('execution') or {}).get('approval')
        pending = wait(approval)
        log('PASS HTTP message accepted and real Codex requested bounded tool approval')
        send('暂停', message['sequence'])
        folder = root / 'bot' / task.replace('-', '')
        def paused():
            return state()['status'] == 'paused' and json.loads((folder / 'agent.json').read_text(encoding='utf-8'))['status'] == 'suspended'
        wait(paused, 30)
        thread = state()['execution']['threadId']
        request(approver, f'/bot/work/{task}/decision', dict(nonce=pending['nonce'], value='accept'), 409)
        assert (project / 'code.txt').read_text() == 'broken'
        assert (project / 'notes.txt').read_text() == 'pending'
        log('PASS pause acknowledged; stale approval rejected; no pending mutation executed')
        send('补充：还需要将 notes.txt 的内容改为 supplemented；两个文件都正确后才算完成。', message['sequence'])
        wait(lambda: state()['goal']['version'] == 3, 15)
        assert state()['status'] == 'paused'
        if restart_round:
            stop(); start(f'round-{iteration}-paused-restart', True)
        assert state()['status'] == 'paused'
        send('恢复', message['sequence'])
        approvals = []
        def finish():
            current = state()
            if current['status'] == 'completed': return current
            assert current['status'] not in ['needs-reconciliation', 'failed', 'waiting-input', 'disabled', 'refused'], current
            pending = (current.get('execution') or {}).get('approval')
            if pending and pending['nonce'] not in approvals:
                parameters = pending['parameters']
                tool = parameters['tool']; args = parameters['arguments']
                assert (tool == 'project_patch' and args['path'] in ['code.txt', 'notes.txt']) or (tool == 'project_command' and args['command'] == 'verify'), parameters
                request(approver, f'/bot/work/{task}/decision', dict(nonce=pending['nonce'], value='accept'), 204)
                approvals.append(pending['nonce'])
            return None
        result = wait(finish)
        assert result['goal']['version'] == 4 and result['execution']['threadId'] == thread
        assert (project / 'notes.txt').read_text().strip() == 'supplemented'
        verified = subprocess.run([sys.executable, 'verify.py'], cwd=project, capture_output=True, text=True, timeout=15)
        assert verified.returncode == 0, verified.stderr
        assert any(r['exitCode'] == 0 for r in result['execution']['receipts'])
        wait(lambda: any(m['sender'] == 'littleloop' and (m.get('card') or {}).get('taskId') == task and '已完成' in m['text'] for m in request(owner, f'/rooms/{room}/messages')), 15)
        evidence = dict(room=room, task=task, thread=thread, version=4, approvalCount=len(approvals), restarted=bool(restart_round),
                        result=result, verifier=verified.stdout, checks=['HTTP intake and dedupe', 'real safe pause', 'stale approval rejection',
                        'supplement while paused', 'same-thread resume', 'additional file requirement', 'host receipts', 'chat completion'] +
                        (['service restart'] if restart_round else []))
        (root / f'result-{iteration}.json').write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding='utf-8')
        log('PASS same-thread resume, supplemental file change, actual tests and chat completion')
        log('Evidence: ' + str(root))
        results.append(evidence)
        if restart_round:
            stop(); start(f'round-{iteration}-completed-restart', True)
        for previous in results:
            restored = request(approver, f"/bot/work/{previous['task']}")
            assert restored == previous['result'], 'Completed state changed after restart or subsequent task'
        history = []
        cursor = 0
        while True:
            page = request(owner, f'/rooms/{room}/messages?after={cursor}')
            if not page: break
            history.extend(page)
            cursor = page[-1]['sequence']
        for previous in results:
            completions = [m for m in history if m['sender'] == 'littleloop'
                and (m.get('card') or {}).get('taskId') == previous['task'] and '已完成' in m['text']]
            assert len(completions) == 1, 'Completion notice missing or duplicated after restart'
        (root / 'result.json').write_text(json.dumps(dict(rounds=len(results), elapsedSeconds=time.monotonic()-started,
            completed=results), ensure_ascii=False, indent=2), encoding='utf-8')
        log(f'PASS round {iteration}/{rounds}: all {len(results)} historical states unchanged; restarted={bool(restart_round)}')
        if duration and time.monotonic() - started >= duration: break
        observe_until(round_started + interval)

    assert time.monotonic() - started >= duration, 'Round limit reached before requested soak duration'

    if os.environ.get('LITTLELOOP_SOAK_FAULTS') == '1':
        (project / 'code.txt').write_text('broken', encoding='utf-8')
        (project / 'notes.txt').write_text('pending', encoding='utf-8')
        message = send(original_text)
        task = wait(find_task, 20)
        pending = wait(approval)
        # Kill only the fixture service and its child processes, including the waiting Codex.
        killed = subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True)
        assert killed.returncode == 0, 'Could not terminate fixture process tree'
        process.wait(timeout=15)
        # Model the crash window after an answer was persisted and before its effects are known.
        atomic_json(root / 'bot' / task.replace('-', '') / ('answer-' + pending['nonce'] + '.json'),
                    dict(nonce=pending['nonce'], decision='accept'))
        start('crash-recovery', True)
        wait(lambda: state()['status'] == 'needs-reconciliation', 20)
        before = state()
        send('恢复', message['sequence'])
        send('补充：尝试恢复未知执行', message['sequence'])
        time.sleep(2)
        assert state() == before, 'Unknown execution was resumed or revised'
        request(approver, f'/bot/work/{task}/decision', dict(nonce=pending['nonce'], value='accept'), 409)
        assert (project / 'code.txt').read_text() == 'broken'
        # Restart again: recovery must be idempotent and a blocked task must not starve new work.
        stop(); start('crash-recovery-repeat', True)
        assert state() == before
        blocked = dict(task=task, state=before)
        message = send('@littleloop 任务')
        task = wait(find_task, 20)
        wait(lambda: state()['status'] == 'waiting-input', 20)
        waiting = state()
        stop(); start('waiting-input-restart', True)
        assert state() == waiting and not state().get('execution')
        send('补充：将 code.txt 改成 fixed 并运行 verify', message['sequence'])
        wait(approval)
        send('暂停', message['sequence'])
        folder = root / 'bot' / task.replace('-', '')
        wait(paused, 30)
        send('取消', message['sequence'])
        wait(lambda: state()['status'] == 'cancelled', 15)
        cancelled = state()
        stop(); start('cancelled-restart', True)
        assert state() == cancelled
        (root / 'faults.json').write_text(json.dumps(dict(blocked=blocked, cancelled=cancelled,
            checks=['running crash blocks replay', 'stale approval rejected after crash', 'repeated recovery is idempotent',
                    'new task after blocked task', 'waiting-input survives restart', 'clarification dispatches', 'cancelled survives restart']),
            ensure_ascii=False, indent=2), encoding='utf-8')
        log('PASS crash recovery, blocked replay, subsequent intake, waiting-input and cancellation restart')

    metrics.sample(process, True)
except BaseException as error:
    (root / 'failure.json').write_text(json.dumps(dict(error=str(error), completedRounds=len(results),
        elapsedSeconds=time.monotonic()-started), ensure_ascii=False, indent=2), encoding='utf-8')
    log('FAIL evidence: ' + str(root))
    atomic_json(log_file, dict(status='failed', error=str(error), completedRounds=len(results), elapsedSeconds=time.monotonic()-started))
    raise
finally:
    try:
        stop()
        for stream in streams: stream.close()
    except BaseException as error:
        atomic_json(log_file, dict(status='failed', phase='cleanup', error=str(error), completedRounds=len(results)))
        raise
    finally:
        ctypes.windll.kernel32.SetThreadExecutionState(0x80000000)

atomic_json(log_file, dict(status='passed', completedRounds=len(results), elapsedSeconds=time.monotonic()-started, samples=metrics.samples))
