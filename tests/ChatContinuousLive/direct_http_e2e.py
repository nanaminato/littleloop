"""Real Codex + ordinary private messages, isolated SQLite and bounded fixture files.

Build Chat first. Set LITTLELOOP_CODEX_EXECUTABLE and optionally LITTLELOOP_CODEX_HOME,
CODEX_CA_CERTIFICATE and LITTLELOOP_CHAT_DLL. Pass --memory for the sourced conditional-memory experiment
or --memory-generalization for held-out tasks and both retrieval and outcome metrics.
"""
import argparse
import hashlib
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

from memory_generalization import TRIALS, run as run_memory_generalization

parser = argparse.ArgumentParser(description=__doc__)
mode = parser.add_mutually_exclusive_group()
mode.add_argument('--memory', action='store_true', help='run the private memory and override experiment')
mode.add_argument('--memory-generalization', action='store_true', help='measure held-out private task recall and outcomes')
parser.add_argument('--trial', action='append', choices=[trial.name for trial in TRIALS], help='run selected generalization trials')
options = parser.parse_args()
if options.trial and not options.memory_generalization:
    parser.error('--trial requires --memory-generalization')
REPO = Path(__file__).resolve().parents[2]
DLL = Path(os.environ.get('LITTLELOOP_CHAT_DLL', REPO / 'src/LittleLoop.Chat/bin/Debug/net10.0/LittleLoop.Chat.dll')).resolve()
EXECUTABLE = Path(os.environ['LITTLELOOP_CODEX_EXECUTABLE']).resolve()
assert DLL.is_file() and EXECUTABLE.is_file(), 'Build Chat and configure an installed Codex executable'
ROOT = REPO / 'data' / 'direct-http' / uuid.uuid4().hex[:8]
PROJECT = ROOT / 'project'
PROJECT.mkdir(parents=True)
(ROOT / 'client').mkdir()
(ROOT / 'client/index.html').write_text('Private-message acceptance fixture', encoding='utf-8')
(ROOT / 'tmp').mkdir()
for name, value in [('code.txt', 'broken'), ('notes.txt', 'pending')]:
    (PROJECT / name).write_text(value, encoding='utf-8')
(PROJECT / 'verify.py').write_text(
    "from pathlib import Path\nassert Path('code.txt').read_text().strip() == 'fixed'\n"
    "assert Path('notes.txt').read_text().strip() == 'supplemented'\nprint('PASS independent fixture')\n", encoding='utf-8')
policy = dict(executable=str(EXECUTABLE), project=str(PROJECT), maxTurns=8, timeoutSeconds=300, maxRepairs=1,
    development=dict(root=str(PROJECT), writableFiles=['code.txt', 'notes.txt'], readExtensions=['.txt', '.py'], budget=40,
        commands=[dict(id='verify', executable=sys.executable, arguments=['verify.py'], isTest=True, protectedFiles=['verify.py'])]))
if options.memory or options.memory_generalization:
    # The independently checked convention is deliberately absent from both the workspace and test command.
    policy['development']['writableFiles'] = ['notes.txt']
    (PROJECT / 'verify.py').write_text(
        "from pathlib import Path\nvalue = Path('notes.txt').read_text().splitlines()\n"
        "assert 1 <= len(value) <= 2 and all(x != 'PENDING' and x.isascii() for x in value)\nprint('PASS note updated')\n",
        encoding='utf-8')
(ROOT / 'policy.json').write_text(json.dumps(policy), encoding='utf-8')
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0))
    port = probe.getsockname()[1]
URL = f'http://127.0.0.1:{port}'
# Clear inherited service configuration, including opt-in synthetic hosts and unrelated workers.
env = {k: v for k, v in os.environ.items() if not k.lower().startswith(
    ('chat__', 'chatbot__', 'chatcommitments__', 'chatdatabase__', 'chatfcm__', 'chatpush__',
     'connectionstrings__', 'chatautomation__', 'workbench__'))}
env.update(Chat__Url=URL, Chat__Invite=secrets.token_hex(24), Chat__Keys=str(ROOT / 'keys'),
    Chat__Client=str(ROOT / 'client'), ChatDatabase__Provider='Sqlite',
    ConnectionStrings__Chat=f'Data Source={ROOT / "chat.db"}',
    TEMP=str(ROOT / 'tmp'), TMP=str(ROOT / 'tmp'), TMPDIR=str(ROOT / 'tmp'))
checks = []
process = None
streams = []
started = time.monotonic()


def record(name):
    checks.append(name)
    print('PASS ' + name, flush=True)


def client():
    return urllib.request.build_opener(urllib.request.ProxyHandler({}),
        urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


owner, outsider, anonymous = client(), client(), client()


def request(who, path, body=None, expected=200):
    req = urllib.request.Request(URL + '/api/chat' + path,
        data=None if body is None else json.dumps(body).encode(),
        headers={'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1'})
    try:
        response = who.open(req, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read()
        assert response.status == expected, (path, response.status, raw.decode()[:1500])
        return json.loads(raw) if raw and response.headers.get('Content-Type', '').startswith('application/json') else None


def wait(fn, seconds=240):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        assert process is not None and process.poll() is None, 'Fixture service exited: ' + str(ROOT)
        value = fn()
        if value:
            return value
        time.sleep(.3)
    raise TimeoutError('Condition not reached; evidence ' + str(ROOT))


def start(label, bot=False):
    global process
    child = dict(env)
    if bot:
        # Explicit test-only grant: the private-room owner is both operator and approver.
        child.update(ChatBot__Policy=str(ROOT / 'policy.json'), ChatBot__Data=str(ROOT / 'bot'),
            ChatBot__Operators='direct_owner', ChatBot__Approvers='direct_owner')
    log = open(ROOT / (label + '.log'), 'wb')
    streams.append(log)
    options = {'creationflags': subprocess.CREATE_NO_WINDOW} if os.name == 'nt' else {'start_new_session': True}
    process = subprocess.Popen([os.environ.get('DOTNET', 'dotnet'), str(DLL)], cwd=REPO, env=child,
        stdout=log, stderr=log, **options)

    def ready():
        try:
            with client().open(URL + '/chat', timeout=1) as response:
                return response.status == 200
        except (urllib.error.URLError, TimeoutError):
            return False
    wait(ready, 30)


def stop():
    global process
    if process is not None and process.poll() is None:
        if os.name == 'nt':
            result = subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True)
            assert result.returncode == 0 or process.poll() is not None, 'Fixture process-tree cleanup failed'
        else:
            import signal
            os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=15)
    process = None


def history():
    messages, cursor = [], 0
    while True:
        page = request(owner, f'/rooms/{room}/messages?after={cursor}')
        if not page:
            return messages
        messages.extend(page)
        cursor = page[-1]['sequence']


def send(text, reply=None, identifier=None):
    return request(owner, f'/rooms/{room}/messages', dict(clientId=identifier or str(uuid.uuid4()), text=text, replyTo=reply))


def task_for(message):
    return wait(lambda: next(((m.get('card') or {}).get('taskId') for m in history()
        if m['sequence'] == message['sequence']), None), 20)


def state(task):
    value = request(owner, f'/bot/work/{task}')
    assert value['goal']['status'] == value['status'], 'Scheduler and goal disagree'
    return value


def approval(task):
    value = state(task)
    assert value['status'] not in ('failed', 'needs-reconciliation', 'disabled', 'refused', 'completed', 'waiting-input'), value
    return (value.get('execution') or {}).get('approval')


def settled(task, status):
    value = state(task)
    return value['status'] == status and (value.get('execution') or {}).get('status') == 'suspended'


def reply_for(message):
    return wait(lambda: next((m for m in history() if m['sender'] == 'littleloop' and m.get('replyTo') == message['sequence']), None), 20)


def progress(task, status):
    query = send('查询进度？')
    reply = reply_for(query)
    assert reply['card']['taskId'] == task and status in reply['text'], reply


def complete_with_approvals(task, allowed_paths):
    approvals = []
    def advance():
        current = state(task)
        if current['status'] == 'completed':
            return current
        assert current['status'] in ('queued', 'running'), (task, current['status'], ROOT)
        pending = (current.get('execution') or {}).get('approval')
        if pending and pending.get('status') == 'pending' and pending['nonce'] not in approvals:
            parameters = pending['parameters']
            tool, args = parameters['tool'], parameters['arguments']
            assert (tool == 'project_patch' and args['path'] in allowed_paths) or (
                tool == 'project_command' and args['command'] == 'verify'), parameters
            request(owner, f'/bot/work/{task}/decision', dict(nonce=pending['nonce'], value='accept'), 204)
            approvals.append(pending['nonce'])
        return None
    return wait(advance, 300), approvals


def check_memory():
    prefix = 'REL-' + secrets.token_hex(4).upper() + '-'
    source = send('以后处理发布说明时，写版本编号时在数字前紧接前缀 ' + prefix + '。')
    learned = reply_for(source)
    assert prefix in learned['text'], learned['text']
    stop()
    start('memory-restart', True)
    record('natural project convention acknowledged and isolated chat service restarted')
    cases = [
        ('unrelated', '请整理库存清点记录。把 notes.txt 的 PENDING 替换成单行的数字 17。', '17', False),
        ('related', '请撰写版本说明。把 notes.txt 的 PENDING 改成版本编号，数字是 42。请沿用本项目惯例，文件只放最终编号。', prefix + '42', True),
        ('override', '请撰写发布说明。这次不要使用任何前缀，把 notes.txt 的 PENDING 改成数字 43。文件只放这个数字。', '43', False),
    ]
    trials = []
    for name, objective, expected, should_recall in cases:
        (PROJECT / 'notes.txt').write_bytes(b'PENDING\n')
        message = send('任务：' + objective + ' 修改后运行宿主 verify 测试。')
        task = task_for(message)
        current, approvals = complete_with_approvals(task, {'notes.txt'})
        execution = current['execution']
        folder = ROOT / 'bot' / task.replace('-', '')
        calls = [json.loads(line) for line in (folder / 'requests.jsonl').read_text(encoding='utf-8').splitlines()]
        prompt = next(call['params']['input'][0]['text'] for call in calls if call.get('method') == 'turn/start')
        present = prefix in prompt
        provenance = bool(re.search(r'"sourceSequence"\s*:\s*' + str(source['sequence']) + r'[,}]', prompt, re.I))
        content = (PROJECT / 'notes.txt').read_text(encoding='ascii').splitlines()
        trial = dict(case=name, objective=objective, task=task, thread=execution['threadId'], promptHasConvention=present,
            promptHasSource=provenance, expected=expected, actual=content, changes=execution['changes'],
            receipts=execution['receipts'], approvals=approvals, sourceSequence=source['sequence'])
        trials.append(trial)
        (ROOT / 'memory-trials.json').write_text(json.dumps(trials, ensure_ascii=False, indent=2), encoding='utf-8')
        assert (present, provenance) == (should_recall, should_recall), (name, 'outgoing request', folder)
        assert content == [expected], (name, expected, content)
        assert set(trial['changes']) == {'notes.txt'} and any(r['exitCode'] == 0 for r in trial['receipts']), trial
        assert (PROJECT / 'code.txt').read_text() == 'broken'
        record(name + ': actual Codex prompt, independent file value and successful host test')
    assert len({trial['thread'] for trial in trials}) == len(trials)
    return dict(mode='memory', sourceSequence=source['sequence'], trials=trials)


def check_direct():
    greeting = send('你好')
    assert reply_for(greeting).get('card') is None
    discussion = send('如果修改数据库会怎样')
    assert reply_for(discussion).get('card') is None
    record('greeting and hypothetical discussion do not create work')

    text = '帮我修复 code.txt：将 broken 改为 fixed，然后运行 verify 测试。只使用宿主工具。'
    identifier = str(uuid.uuid4())
    message = send(text, identifier=identifier)
    assert send(text, identifier=identifier)['sequence'] == message['sequence']
    task = task_for(message)
    request(outsider, f'/bot/work/{task}', expected=404)
    pending = wait(lambda: approval(task))
    thread = state(task)['execution']['threadId']
    assert (PROJECT / 'code.txt').read_text() == 'broken'
    record('ordinary message creates one real Codex task; reads work and mutations await approval')
    progress(task, '正在工作')
    active_discussion = send('如果修改数据库会怎样')
    assert reply_for(active_discussion).get('card') is None and state(task)['goal']['version'] == 1
    unrelated = send('取消', active_discussion['sequence'])
    assert reply_for(unrelated).get('card') is None and state(task)['status'] == 'running'
    irrelevant = send('取消', greeting['sequence'])
    assert reply_for(irrelevant).get('card') is None and state(task)['status'] == 'running'
    record('progress while executing; reply to unrelated message cannot cancel active task')

    send('暂停')
    wait(lambda: settled(task, 'paused'), 40)
    request(owner, f'/bot/work/{task}/decision', dict(nonce=pending['nonce'], value='accept'), 409)
    send('补充：还需要把 notes.txt 改为 supplemented；两个文件和 verify 测试均通过才算完成。')
    wait(lambda: state(task)['goal']['version'] == 3, 20)
    assert state(task)['status'] == 'paused' and (PROJECT / 'code.txt').read_text() == 'broken'
    progress(task, '暂停')
    stop()
    start('paused-restart', True)
    assert settled(task, 'paused')
    record('confirmed tool-boundary pause, stale approval rejected, supplement persists paused across restart')

    send('恢复')
    completed, approvals = complete_with_approvals(task, {'code.txt', 'notes.txt'})
    assert completed['goal']['version'] == 4 and completed['execution']['threadId'] == thread
    assert set(completed['execution']['changes']) == {'code.txt', 'notes.txt'}
    assert any(r['exitCode'] == 0 for r in completed['execution']['receipts'])
    verified = subprocess.run([sys.executable, 'verify.py'], cwd=PROJECT, capture_output=True, text=True, timeout=15)
    assert verified.returncode == 0, verified.stderr
    wait(lambda: any(m['sender'] == 'littleloop' and (m.get('card') or {}).get('taskId') == task
        and '已完成' in m['text'] for m in history()), 20)
    progress(task, '已完成')
    record('same-thread resume, both real patches, host test receipts, independent verification and completion query')

    for name, value in [('code.txt', 'broken'), ('notes.txt', 'pending')]:
        (PROJECT / name).write_text(value, encoding='utf-8')
    cancelled_message = send('帮我修改 code.txt 为 fixed，并运行 verify 测试。只使用宿主工具。')
    cancelled_task = task_for(cancelled_message)
    pending = wait(lambda: approval(cancelled_task))
    send('取消')
    wait(lambda: settled(cancelled_task, 'cancelled'), 40)
    request(owner, f'/bot/work/{cancelled_task}/decision', dict(nonce=pending['nonce'], value='accept'), 409)
    progress(cancelled_task, '已取消')
    cancelled = state(cancelled_task)
    stop()
    start('cancelled-restart', True)
    assert state(cancelled_task) == cancelled and state(task) == completed
    assert (PROJECT / 'code.txt').read_text() == 'broken' and (PROJECT / 'notes.txt').read_text() == 'pending'
    assert len([m for m in history() if m['sender'] == 'littleloop' and
        (m.get('card') or {}).get('taskId') == task and '已完成' in m['text'] and
        m.get('replyTo') == message['sequence']]) == 1
    record('cancel running task, confirm real turn stopped, invalidate approval; restart preserves terminal tasks and one completion')

    return dict(task=task, thread=thread, completed=completed, cancelledTask=cancelled_task, cancelled=cancelled,
        approvals=approvals, verifier=verified.stdout)


print('Evidence: ' + str(ROOT), flush=True)
try:
    start('bootstrap')
    password = secrets.token_hex(20)
    for name in ('direct_owner', 'direct_other'):
        request(owner, '/register', dict(name=name, password=password, invite=env['Chat__Invite']), 204)
    stop()
    start('private-live', True)
    for who, name in ((owner, 'direct_owner'), (outsider, 'direct_other')):
        request(who, '/login', dict(name=name, password=password))
    request(anonymous, '/bot/direct', {}, 401)
    request(outsider, '/bot/direct', {}, 403)
    room = request(owner, '/bot/direct', {})['id']
    assert request(owner, '/bot/direct', {})['id'] == room
    assert request(owner, f'/bot/rooms/{room}')['type'] == 'direct'
    request(outsider, f'/rooms/{room}/messages', expected=404)
    record('authenticated private Codex room, idempotent enrollment and operator isolation')
    if options.memory_generalization:
        def decide(task, nonce, decision):
            request(owner, f'/bot/work/{task}/decision', dict(nonce=nonce, value=decision), 204)

        def restart():
            stop()
            start('memory-generalization-restart', True)

        details = run_memory_generalization(ROOT, PROJECT, send, reply_for, task_for, state, wait, decide, restart,
                                            names=options.trial)
        details['scenarioSha256'] = hashlib.sha256(Path(__file__).with_name('memory_generalization.py').read_bytes()).hexdigest()
    else:
        details = check_memory() if options.memory else check_direct()
    result = dict(**details, status='measured' if options.memory_generalization else 'passed',
        checks=checks, elapsedSeconds=time.monotonic() - started, room=room,
        assemblyHashes={name: hashlib.sha256(DLL.with_name(name).read_bytes()).hexdigest()
            for name in ('LittleLoop.Chat.dll', 'LittleLoop.Core.dll', 'LittleLoop.Tools.dll')},
        driverSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())
    (ROOT / 'result.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
except BaseException as error:
    (ROOT / 'failure.json').write_text(json.dumps(dict(status='failed', error=str(error), checks=checks),
        ensure_ascii=False, indent=2), encoding='utf-8')
    raise
finally:
    stop()
    for stream in streams:
        stream.close()
