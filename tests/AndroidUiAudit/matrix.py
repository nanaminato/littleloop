"""Exercise the installed APK, not a WebView or mocked Compose tree."""
from support import *

fixture=json.loads((OUT/'fixtures.json').read_text())
results=[]
for result in OUT.glob('matrix-*.json'):
    results.extend(json.loads(result.read_text()))
configs=[('small',640,1136,320),('phone',1080,2400,420),('tablet',1600,2560,320),('landscape',1280,720,320)]
if os.environ.get('AUDIT_SIZE'): configs=[c for c in configs if c[0] in os.environ['AUDIT_SIZE'].split(',')]

for config,w,h,density in configs:
    size(w,h,density)
    for scene in fixture['scenes']:
        if os.environ.get('AUDIT_CASES') and scene['key'] not in os.environ['AUDIT_CASES'].split(','): continue
        key=config+'-'+scene['key']
        results=[r for r in results if r['case']!=key]
        try:
            marker=('AUDIT_POST_'+scene['role'] if scene['type']=='channel' else
                    'AUDIT_OWN_saved' if scene['type']=='saved' else 'AUDIT_OTHER_'+scene['role'] if scene['type'] in ['group','topic'] else 'AUDIT_OTHER_direct')
            launch(scene['id']); root=wait(text=marker)
            capture(key+'-conversation',root)
            editable=any(n.get('class')=='android.widget.EditText' for n in nodes(root))
            assert editable==scene['canPost'],('composer',editable,scene['canPost'])
            tap(find(root,text=marker),long=True);root=ui(); options=labels(root)
            capture(key+'-message-menu',root)
            if scene['type']=='channel':
                rights=scene['rights']; edit=bool(rights&2);delete=bool(rights&4);pin=bool(rights&8)
            else:
                edit=scene['type']=='saved';delete=edit or scene['type'] in ['group','topic'] and scene['role'] in ['owner','admin'];pin=scene['canPost']
            # A short landscape menu scrolls; collect all entries before checking.
            if any(n.get('scrollable')=='true' for n in nodes(root)):
                scroll(root); options+=labels(ui())
            for label,expected in [('Edit',edit),('Delete',delete),('Pin',pin),('Reply',scene['canPost'])]:
                assert (label in options)==expected,(label,expected,options)
            assert 'Forward' in options and 'Save to Saved messages' in options
            back();click(desc='Chat actions');root=ui();capture(key+'-chat-menu',root)
            menu=labels(root)
            if scene['type']=='group':
                click(text='Members',root=root);root=wait(text='Members');capture(key+'-members',root)
                assert ('Invite member' in labels(root))==(scene['role']=='owner'),labels(root)
            if scene['type']=='channel':
                click(text='Channel settings',root=root);root=wait(text='Cancel');seen=[];last=None
                for i in range(40):
                    seen+=labels(root);capture(key+'-settings-'+str(i),root)
                    sig=ET.tostring(root)
                    if sig==last:break
                    last=sig
                    if not any(n.get('scrollable')=='true' for n in nodes(root)):break
                    scroll(root);root=ui()
                for label,expected in [('Channel name',bool(scene['rights']&16)),('Remove subscriber',bool(scene['rights']&32)),('Transfer channel',scene['rights']==127),('Delete channel',scene['rights']==127)]:
                    assert (label in seen)==expected,(label,expected,seen)
                adb('shell','am','force-stop','dev.littleloop.chat')
                adb('shell','am','start','-W','-n','dev.littleloop.chat/.MainActivity','--es','room',scene['id'],'--ez','comments','true','--el','sequence',scene['post'])
                wait(text='Comments');seek('AUDIT_COMMENT_'+scene['role']);root=ui();capture(key+'-comments',root)
            results.append({'case':key,'passed':True})
            print('PASS',key,flush=True)
        except Exception as e:
            capture(key+'-failure');results.append({'case':key,'passed':False,'error':str(e)})
            print('FAIL',key,str(e),flush=True)
        (OUT/('matrix-'+config+'.json')).write_text(json.dumps([r for r in results if r['case'].startswith(config+'-')],indent=2),encoding='utf-8')
print('RESULT',sum(r['passed'] for r in results),'/',len(results),flush=True)
if any(not r['passed'] for r in results):raise SystemExit(1)
