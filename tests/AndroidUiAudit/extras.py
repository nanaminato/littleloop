from support import *
fixture=json.loads((OUT/'fixtures.json').read_text())
records=json.loads((OUT/'extras-result.json').read_text()) if (OUT/'extras-result.json').exists() else []
def shot(name,root=None):
    root=capture(name,root);records.append(name) if name not in records else None
    (OUT/'extras-result.json').write_text(json.dumps(records,indent=2));return root
def home():launch();return wait(desc='New chat')
def chip(text):
    for _ in range(4):
        root=ui();node=find(root,text=text)
        if node is not None and rect(node)[2]>rect(node)[0]:tap(node);return
        area=next(n for n in nodes(root) if n.get('scrollable')=='true')
        x1,y1,x2,y2=rect(area);adb('shell','input','swipe',x1+(x2-x1)*4//5,(y1+y2)//2,x1+(x2-x1)//5,(y1+y2)//2,350)
    raise AssertionError('Hidden home action '+text)
for config,w,h,d in [('small',640,1136,320),('phone',1080,2400,420),('tablet',1600,2560,320),('landscape',1280,720,320)]:
    if os.environ.get('AUDIT_SIZE') and config not in os.environ['AUDIT_SIZE'].split(','): continue
    size(w,h,d,'1.3' if config=='small' else '1.0')
    if not os.environ.get('AUDIT_ONLY_SETTINGS'):
        home();shot(config+'-home');chip('Friends');wait(text='Friends');shot(config+'-friends')
        back();chip('Channels');wait(text='Search channels');shot(config+'-directory')
        click(text='New channel');wait(text='Channel name');shot(config+'-create-channel');back()
        home();click(desc='New chat');wait(text='Direct chat');shot(config+'-create-direct')
        click(text='Group');shot(config+'-create-group-top');tap(seek('Group name'))
        adb('shell','input','text','AuditEmpty_'+config);back()
        # The members field and optional explanation must remain reachable at low height.
        seek('Members (optional)');root=ui();assert 'Members (optional)' in labels(root);shot(config+'-create-group-members',root);back()
        scene=next(s for s in fixture['scenes'] if s['key']=='group-owner');launch(scene['id']);click(desc='Chat actions')
        root=ui();tap(next(n for n in nodes(root) if n.get('text','').startswith('Topics')));wait(text='New topic');shot(config+'-topics')
        click(text='New topic');wait(text='Topic name');shot(config+'-create-topic');back()
        launch(scene['id']);click(desc='Search in chat');root=ui();shot(config+'-search-empty')
        enter(find(root,cls='android.widget.EditText'),'AUDIT_OTHER');back();wait(text='AUDIT_OTHER_owner');shot(config+'-search-results')
    home();click(text='⚙');wait(text='Settings');shot(config+'-settings-profile')
    for label in ['NOTIFICATIONS','APPEARANCE','LANGUAGE','Log out']:
        seek(label);shot(config+'-settings-'+label.replace(' ','-'))
    print('PASS extras',config,flush=True)
print('Captured native extra states',len(records),flush=True)
