"""Read-only native social role/layout regression; safe to run sizes on separate emulators."""
from support import *

f=json.loads((OUT/'social-fixtures.json').read_text())

def friends():
    launch();wait(desc='New chat')
    for _ in range(5):
        root=ui();node=find(root,text='Friends')
        if node is not None and rect(node)[2]>rect(node)[0]:tap(node);return wait(text='Friend username')
        area=next(n for n in nodes(root) if n.get('scrollable')=='true')
        x1,y1,x2,y2=rect(area);adb('shell','input','swipe',x1+(x2-x1)*4//5,(y1+y2)//2,x1+(x2-x1)//5,(y1+y2)//2,350)
    raise AssertionError('Friends entry unreachable')

def menu_for(username):
    seek(username,30)
    root=ui()
    name=find(root,text=username);top=rect(name)[1]
    handle=next(n for n in nodes(root) if n.get('text','').startswith('@'+username))
    menu=next(n for n in nodes(root) if n.get('text')=='⋮' and top<=(rect(n)[1]+rect(n)[3])//2<=rect(handle)[3])
    tap(menu)

for config,w,h,d in [('small',640,1136,320),('phone',1080,2400,420),('tablet',1600,2560,320),('landscape',1280,720,320)]:
    if os.environ.get('AUDIT_SIZE') and config not in os.environ['AUDIT_SIZE'].split(','):continue
    result=[]
    def record(name):
        capture('social-'+config+'-'+name);result.append(name)
        (OUT/('social-matrix-'+config+'.json')).write_text(json.dumps(result,indent=2));print('PASS',config,name,flush=True)
    size(w,h,d,'1.3' if config=='small' else '1.0')
    friends()
    for label,name in [('Accept','incoming'),('Request sent','outgoing'),('Start private chat','accepted')]:
        seek(label,30);record('friend-'+name)
    for role in ['owner','admin','member','muted']:
        room=next(s['id'] for s in f['scenes'] if s['key']=='group-'+role)
        launch(room);click(desc='Chat actions');click(text='Members');wait(text='Members')
        root=ui();assert ('Invite member' in labels(root))==(role=='owner'),labels(root)
        if role in ['owner','admin']:
            menu_for(f['names'][2]);root=ui();options=labels(root)
            if any(n.get('scrollable')=='true' for n in nodes(root)):scroll(root);options+=labels(ui())
            assert 'Remove member' in options and 'Mute member' in options
            for label in ['Promote to admin','Transfer ownership']:assert (label in options)==(role=='owner'),options
            record(role+'-ordinary-menu');back()
            if role=='owner':
                menu_for(f['friend']);root=ui();options=labels(root)
                if any(n.get('scrollable')=='true' for n in nodes(root)):scroll(root);options+=labels(ui())
                assert 'Remove group admin' in options;record('owner-admin-menu');back()
            else:
                seek(f['friend'],30);root=ui()
                # Only the ordinary guest is mutable: self, owner and the second admin are protected.
                name=find(root,text=f['friend'])
                handle=next(n for n in nodes(root) if n.get('text','').startswith('@'+f['friend']))
                assert not any(n.get('text')=='⋮' and rect(name)[1]<=(rect(n)[1]+rect(n)[3])//2<=rect(handle)[3] for n in nodes(root))
                record('admin-protected-targets')
        else:
            seek(f['friend'],30);assert '⋮' not in labels(ui());record(role+'-read-only')
    print('Social matrix',config,len(result),flush=True)
