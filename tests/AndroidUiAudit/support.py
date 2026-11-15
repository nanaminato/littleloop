"""Real-device audit helpers. Run from the repository root; no app-state injection."""
import json, os, re, subprocess, time, urllib.request, urllib.error, http.cookiejar
from pathlib import Path
import xml.etree.ElementTree as ET

OUT = Path(os.environ.get('ANDROID_AUDIT_OUT', 'data/android-ui-audit'))
OUT.mkdir(parents=True, exist_ok=True)
BASE = os.environ.get('ANDROID_AUDIT_BASE', 'http://127.0.0.1:5193')
SERIAL = os.environ.get('ANDROID_SERIAL', 'emulator-5558')
ENV = dict(s.split('=', 1) for s in Path('data/chat-phase-a/chat.local.env').read_text(encoding='utf-8-sig').splitlines() if '=' in s)

class Api:
    def __init__(self, name=None):
        self.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        if name: self.call('/login', 'POST', {'name': name, 'password': ENV['CHAT_TEST_PASSWORD']})

    def call(self, path, method='GET', data=None, status=200):
        req = urllib.request.Request(BASE+'/api/chat'+path, data=None if data is None else json.dumps(data).encode(), method=method,
            headers={'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1'})
        try: response = self.client.open(req, timeout=20)
        except urllib.error.HTTPError as e: response = e
        body = response.read().decode()
        assert response.code == status, (method, path, response.code, body[:120])
        try: return json.loads(body)
        except ValueError: return body

def adb(*args):
    p = subprocess.run(['adb', '-s', SERIAL, *map(str,args)], capture_output=True, timeout=45)
    if p.returncode: raise RuntimeError(p.stderr.decode(errors='replace'))
    return p.stdout

def ui():
    for _ in range(4):
        try:
            raw = adb('shell', 'uiautomator dump /sdcard/android-ui-audit.xml >/dev/null && cat /sdcard/android-ui-audit.xml').decode(errors='replace')
        except RuntimeError:
            time.sleep(.4)
            continue
        if '<hierarchy' in raw:
            root = ET.fromstring(raw[raw.index('<?xml'):] if '<?xml' in raw else raw[raw.index('<hierarchy'):])
            return root
        time.sleep(.4)
    raise RuntimeError('No Android accessibility hierarchy')

def nodes(root): return list(root.iter('node'))
def rect(node): return tuple(map(int,re.findall(r'\d+',node.get('bounds',''))))
def labels(root): return [n.get('text') or n.get('content-desc') for n in nodes(root) if n.get('text') or n.get('content-desc')]
def find(root, text=None, desc=None, cls=None):
    return next((n for n in nodes(root) if (text is None or n.get('text')==text) and (desc is None or n.get('content-desc')==desc) and (cls is None or n.get('class')==cls)),None)

def tap(node, long=False):
    if node is None: raise AssertionError('Missing UI control')
    x1,y1,x2,y2=rect(node); assert x2>x1 and y2>y1
    x,y=(x1+x2)//2,(y1+y2)//2
    adb('shell','input',*(['swipe',x,y,x,y,800] if long else ['tap',x,y]))

def click(text=None, desc=None, root=None):
    root=root or ui()
    if text is not None:
        button=next((n for n in nodes(root) if n.get('clickable')=='true' and
                     any(c.get('text')==text for c in n.iter('node'))),None)
        if button is not None:tap(button);return
    tap(find(root,text=text,desc=desc))
def back(): adb('shell','input','keyevent',4)
def enter(node, value):
    tap(node); adb('shell','input','keycombination',113,29)
    if value: adb('shell','input','text',value.replace(' ','%s'))
    else: adb('shell','input','keyevent',67)
def scroll(root=None, up=False):
    root=root or ui()
    area=next((n for n in nodes(root) if n.get('scrollable')=='true'),None)
    if area is None: raise AssertionError('No scrollable control: '+str(labels(root)))
    x1,y1,x2,y2=rect(area);x=(x1+x2)//2;a=y1+(y2-y1)*3//4;b=y1+(y2-y1)//4
    adb('shell','input','swipe',x,b if up else a,x,a if up else b,350)

def seek(text, limit=15):
    for _ in range(limit):
        root=ui(); node=find(root,text=text)
        if node is not None: return node
        scroll(root)
    raise AssertionError('Unreachable '+text+'; '+str(labels(root)))

def wait(text=None, desc=None, timeout=20):
    end=time.monotonic()+timeout
    while time.monotonic()<end:
        root=ui()
        if find(root,text=text,desc=desc) is not None:return root
    raise AssertionError('UI timeout '+str((text,desc,labels(root))))

def capture(name, root=None):
    root=root or ui()
    (OUT/(name+'.xml')).write_bytes(ET.tostring(root,encoding='utf-8'))
    (OUT/(name+'.png')).write_bytes(adb('exec-out','screencap','-p'))
    return root

def launch(room=None):
    adb('shell','am','force-stop','dev.littleloop.chat')
    args=['shell','am','start','-W','-n','dev.littleloop.chat/.MainActivity']
    if room:args+=['--es','room',room]
    adb(*args)
    if room: return wait(desc='Chat actions')
    # Activity launch may return before Compose replaces the launcher window.
    for _ in range(12):
        root = ui()
        if any(n.get('package') == 'dev.littleloop.chat' for n in nodes(root)): return root
    raise AssertionError('Application window did not appear')

def size(width,height,density=320,font='1.0'):
    adb('shell','wm','size',f'{width}x{height}');adb('shell','wm','density',density)
    adb('shell','settings','put','system','font_scale',font)

def login(name, origin=BASE):
    root=launch()
    if find(root,text='Sign in') is None:
        for _ in range(8):
            root=ui()
            if find(root,text='⚙') is not None:break
            back()
        click(text='⚙',root=root);tap(seek('Log out'));root=wait(text='Sign in')
    fields=[n for n in nodes(root) if n.get('class')=='android.widget.EditText']
    enter(fields[0],origin);adb('shell','input','keyevent',61)
    adb('shell','input','text',name);adb('shell','input','keyevent',61)
    adb('shell','input','text',ENV['CHAT_TEST_PASSWORD']);back()
    click(text='Sign in');wait(desc='New chat')
