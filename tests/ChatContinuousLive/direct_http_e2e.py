"""Real Codex + ordinary private messages, isolated SQLite and bounded fixture files.

Build Chat first. Set LITTLELOOP_CODEX_EXECUTABLE and optionally LITTLELOOP_CODEX_HOME,
CODEX_CA_CERTIFICATE and LITTLELOOP_CHAT_DLL. Never uses production chat state.
"""
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

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
    approvals = []

    def finish():
        current = state(task)
        if current['status'] == 'completed':
            return current
        pending = approval(task)
        if pending and pending['nonce'] not in approvals:
            parameters = pending['parameters']
            tool, args = parameters['tool'], parameters['arguments']
            assert (tool == 'project_patch' and args['path'] in ('code.txt', 'notes.txt')) or (
                tool == 'project_command' and args['command'] == 'verify'), parameters
            request(owner, f'/bot/work/{task}/decision', dict(nonce=pending['nonce'], value='accept'), 204)
            approvals.append(pending['nonce'])
        return None

    completed = wait(finish, 300)
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

    result = dict(status='passed', checks=checks, elapsedSeconds=time.monotonic() - started,
        room=room, task=task, thread=thread, completed=completed, cancelledTask=cancelled_task, cancelled=cancelled,
        approvals=approvals, verifier=verified.stdout,
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
