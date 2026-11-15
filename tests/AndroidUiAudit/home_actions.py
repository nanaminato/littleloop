"""Native home/create regression; synthetic fixtures and API assertions only."""
from support import *

f = json.loads((OUT/'fixtures.json').read_text())
view, peer = Api(f['names'][0]), Api(f['names'][1])
scenes = {s['key']: s for s in f['scenes']}
resume_offline = bool(os.environ.get('HOME_RESUME_OFFLINE'))
resume = bool(os.environ.get('HOME_RESUME_SUBSCRIPTION')) or resume_offline
report = json.loads((OUT/'home-actions-result.json').read_text()) if resume else []
context = json.loads((OUT/'home-actions-context.json').read_text()) if resume else {}
stamp = context.get('stamp', str(int(time.time())))

def record(name):
    capture('home-action-'+name)
    if name not in report: report.append(name)
    (OUT/'home-actions-result.json').write_text(json.dumps(report, indent=2))
    print('PASS', name, flush=True)

def poll(check):
    end = time.monotonic()+90
    while time.monotonic()<end:
        result = check()
        if result: return result
        time.sleep(.5)
    raise AssertionError('Expected state did not arrive')

def home():
    launch(); return wait(desc='New chat')

def chip(text):
    for _ in range(4):
        root=ui(); node=find(root,text=text)
        if node is not None and rect(node)[2]>rect(node)[0]: tap(node); return
        area=next(n for n in nodes(root) if n.get('scrollable')=='true')
        x1,y1,x2,y2=rect(area)
        adb('shell','input','swipe',x1+(x2-x1)*4//5,(y1+y2)//2,x1+(x2-x1)//5,(y1+y2)//2,350)
    raise AssertionError('Home action missing: '+text)

def enabled(text):
    buttons=[n for n in nodes(ui()) if n.get('clickable')=='true' and
        any(c.get('text')==text for c in n.iter('node'))]
    assert buttons, 'Button missing: '+text
    return any(n.get('enabled')=='true' for n in buttons)

def input_text(value, index=0):
    enter([n for n in nodes(ui()) if n.get('class')=='android.widget.EditText'][index],value)
    back()

def created(title):
    wait(desc='Chat actions')
    return poll(lambda:next((r for r in view.call('/rooms') if r['title']==title),None))

if not resume:
    size(1080,2400,420)
    home(); input_text('Audit owner topic'); wait(text='Audit owner topic')
    record('search-finds-topic')
    adb('shell','wm','user-rotation','lock',1)
    wait(text='Audit owner topic'); assert find(ui(),cls='android.widget.EditText').get('text')=='Audit owner topic'
    record('search-restored-on-rotation')
    adb('shell','wm','user-rotation','lock',0)
    input_text('NoSuchConversation_'+stamp); wait(text='No messages found'); record('search-empty')

    home(); click(desc='New chat'); assert not enabled('Create')
    input_text(f['names'][1]); click(text='Create'); wait(desc='Chat actions')
    assert sum(r['id']==scenes['direct']['id'] for r in view.call('/rooms'))==1
    record('direct-reuses-existing-room')

    home(); click(desc='New chat'); click(text='Group'); assert not enabled('Create')
    title='HomeEmpty_'+stamp; input_text(title)
    click(text='Direct chat'); assert not enabled('Create'); click(text='Group')
    assert find(ui(),cls='android.widget.EditText').get('text')==title
    record('switch-type-keeps-draft')
    adb('shell','wm','user-rotation','lock',1); wait(text=title)
    record('creation-draft-restored-on-rotation')
    adb('shell','wm','user-rotation','lock',0); click(text='Create')
    room=created(title)['id']; assert len(view.call('/rooms/'+room+'/members'))==1
    record('empty-group-created')
    click(desc='Chat actions'); click(text='Members'); wait(text='Member username')
    input_text(f['names'][1]); click(text='Invite member')
    poll(lambda:len(view.call('/rooms/'+room+'/members'))==2); record('invite-after-empty-group')

    home(); click(desc='New chat'); click(text='Group')
    title='HomeMembers_'+stamp; input_text(title); input_text(f['names'][1]+','+f['names'][2],1)
    click(text='Create'); room=created(title)['id']
    assert len(view.call('/rooms/'+room+'/members'))==3; record('group-created-with-members')

    home(); click(desc='New chat'); input_text('missing_'+stamp); click(text='Create')
    poll(lambda:enabled('Create'))
    assert find(ui(),cls='android.widget.EditText').get('text')=='missing_'+stamp
    assert find(ui(),text='Direct chat') is not None
    record('failed-create-keeps-draft')
    input_text(f['names'][1]); click(text='Create'); wait(desc='Chat actions'); record('failed-create-retry')

    # Posting restrictions must not accidentally become topic-creation restrictions.
    for role in ['owner','admin','member','muted']:
        parent=scenes['group-'+role]['id']; launch(parent); click(desc='Chat actions')
        tap(next(n for n in nodes(ui()) if n.get('text','').startswith('Topics')))
        wait(text='New topic'); click(text='New topic'); wait(text='Topic name'); assert not enabled('Create topic')
        title='HomeTopic_'+role+'_'+stamp; input_text(title); click(text='Create topic')
        topic=created(title); assert topic['parentId']==parent
        record('topic-created-'+role)

    home(); chip('Channels'); wait(text='Search channels'); click(text='New channel'); wait(text='Channel name')
    assert not enabled('Create channel'); title='AAHomeChannel_'+stamp; input_text(title); click(text='Create channel')
    channel=created(title)['id']; record('channel-created')
    home(); chip('Channels'); wait(text='Search channels'); input_text(title); wait(text='Publisher')
    assert find(ui(),text='Unsubscribe') is None; record('publisher-no-unsubscribe')

    # A unique early-sorting name remains inside the server's existing 200-channel directory limit.
    title='AAHomeSubscribe_'+stamp
    channel=peer.call('/channels','POST',{'title':title})['id']
    (OUT/'home-actions-context.json').write_text(json.dumps({'stamp':stamp,'title':title,'channel':channel}))
    home(); chip('Channels'); wait(text='Search channels'); input_text(title); wait(text='Subscribe')
    click(text='Subscribe'); wait(text='Unsubscribe'); assert any(r['id']==channel for r in view.call('/rooms'))
    record('channel-subscribe')
elif not resume_offline:
    title, channel = context['title'], context['channel']
    size(1080,2400,420)
    home(); chip('Channels'); wait(text='Search channels'); input_text(title); wait(text='Open channel')

if not resume_offline:
    click(text='Open channel'); wait(desc='Chat actions'); record('subscribed-channel-open')
    home(); chip('Channels'); wait(text='Search channels'); input_text(title); wait(text='Unsubscribe'); click(text='Unsubscribe')
    wait(text='Subscribe'); assert all(r['id']!=channel for r in view.call('/rooms')); record('channel-unsubscribe')

home(); click(desc='New chat'); click(text='Group'); input_text('OfflineDraft_'+stamp)
forwards=[line.split()[1:] for line in adb('reverse','--list').decode().splitlines() if line.strip()]
try:
    adb('reverse','--remove','tcp:5193')
    # Removing a listener alone leaves established SignalR sockets alive.
    adb('reconnect'); adb('wait-for-device')
    poll(lambda:not enabled('Create')); record('offline-create-disabled')
finally:
    for remote, local in forwards: adb('reverse',remote,local)
poll(lambda:enabled('Create'))
assert find(ui(),cls='android.widget.EditText').get('text')=='OfflineDraft_'+stamp
record('reconnected-draft-retained'); back()
print('Native home actions',len(report),flush=True)
