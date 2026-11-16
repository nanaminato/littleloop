#!/usr/bin/env python3
"""Disposable SQLite + real HTTP cookie/auth regression. No provider, credentials or production DB."""
import http.cookiejar, json, os, pathlib, socket, sqlite3, subprocess, tempfile, time, urllib.request, urllib.error, uuid
ROOT = pathlib.Path(__file__).resolve().parents[2]
DOTNET = os.environ.get('DOTNET', 'dotnet')
DLL = ROOT / 'src/LittleLoop.Chat/bin/Debug/net10.0/LittleLoop.Chat.dll'
checks = 0

def check(condition, message):
    global checks
    assert condition, message
    checks += 1

with tempfile.TemporaryDirectory(prefix='littleloop-chat-http-') as directory:
    base = pathlib.Path(directory)
    (base / 'client').mkdir(); (base / 'client/index.html').write_text('test')
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0)); port = probe.getsockname()[1]
    url = f'http://127.0.0.1:{port}'
    env = dict(os.environ, Chat__Url=url, Chat__Invite='synthetic-invite-01234567890', Chat__Keys=str(base/'keys'), Chat__Client=str(base/'client'),
               ChatDatabase__Provider='Sqlite', ConnectionStrings__Chat=f'Data Source={base}/chat.db',
               ChatCommitments__State=str(base/'commitments.json'), ChatCommitments__SyntheticDelayMs='1500')
    process = None
    log = open(base/'server.log', 'w+')
    def start():
        global process
        process = subprocess.Popen([DOTNET, str(DLL)], env=env, cwd=ROOT, stdout=log, stderr=log)
        for _ in range(150):
            if process.poll() is not None:
                log.seek(0); raise AssertionError(log.read())
            try:
                urllib.request.urlopen(url+'/chat', timeout=.2).close(); return
            except Exception: time.sleep(.1)
        raise AssertionError('server did not start')
    def stop():
        if process and process.poll() is None:
            process.terminate()
            try: process.wait(10)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
    def client(): return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    alice, bob, anonymous = client(), client(), client()
    def req(client, path, body=None, expected=200):
        request=urllib.request.Request(url+'/api/chat'+path, data=None if body is None else json.dumps(body).encode(), headers={'Content-Type':'application/json','X-LittleLoop-Chat':'1'})
        try: result=client.open(request, timeout=10)
        except urllib.error.HTTPError as error: result=error
        data=result.read(); check(result.status == expected, f'{path}: {result.status} != {expected}: {data}')
        return json.loads(data) if data and result.headers.get('Content-Type','').startswith('application/json') else None
    try:
        start()
        for name,c in [('alice',alice),('bob',bob)]:
            req(c,'/register',{'name':name,'password':'synthetic-password','invite':env['Chat__Invite']},204)
        stop()
        with sqlite3.connect(base/'chat.db') as db:
            owner=db.execute("select id from chat_accounts where name='alice'").fetchone()[0]
        env.update(ChatCommitments__Enabled='true',ChatCommitments__Operators__0=owner)
        start()
        for name,c in [('alice',alice),('bob',bob)]: req(c,'/login',{'name':name,'password':'synthetic-password'})
        req(anonymous,'/commitments/direct',{},401)
        req(bob,'/commitments/direct',{},403)
        room=req(alice,'/commitments/direct',{})['id']
        req(bob,f'/commitments/rooms/{room}',expected=404)
        def send(text,reply=None,identifier=None):
            return req(alice,f'/rooms/{room}/messages',{'clientId':identifier or str(uuid.uuid4()),'text':text,'replyTo':reply})
        def works(): return req(alice,f'/commitments/rooms/{room}')
        def wait_status(status):
            for _ in range(100):
                tasks=works()
                if tasks and tasks[-1]['status']==status:return tasks
                time.sleep(.05)
            raise AssertionError(f'no {status}: {tasks}')
        send('如果帮我整理文档会怎么样？'); check(works()==[], 'hypothetical must not start work')
        key=str(uuid.uuid4()); first=send('帮我整理模拟文档',identifier=key)
        wait_status('running')
        send('帮我整理模拟文档',identifier=key); check(len(works())==1,'duplicate message must not duplicate task')
        send('暂停',first['sequence']); wait_status('paused')
        time.sleep(.1); send('继续'); wait_status('running')
        send('改为整理另一个模拟文档'); tasks=works(); check(len(tasks)==2,'replacement makes distinct task')
        wait_status('completed'); check(works()[0]['status'] in ('replaced','superseded'),'old work stays replaced')
        send('取消',first['sequence']); check(works()[-1]['status']=='completed','old reply cannot cancel newer work')
        send('帮我检查重启'); wait_status('running'); stop(); start()
        check(works()[-1]['status']=='paused','restart must not silently replay running work')
        stop(); start(); check(works()[-1]['status']=='paused','repeated restart remains paused')
        print(json.dumps({'passed':checks,'scope':'SQLite HTTP auth, ordinary turns, duplicate, pause/resume, replace, stale reply, restart; synthetic only'}))
    except Exception:
        log.flush(); log.seek(0); print(log.read()[-10000:]); raise
    finally: stop(); log.close()
