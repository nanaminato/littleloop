from support import *
import uuid
f=json.loads((OUT/'fixtures.json').read_text());view,peer=Api(f['names'][0]),Api(f['names'][1]);report=[]
scenes={s['key']:s for s in f['scenes']}
def record(name):
    capture('action-'+name);report.append(name);(OUT/'actions-result.json').write_text(json.dumps(report,indent=2));print('PASS',name,flush=True)
def poll(check):
    end=time.monotonic()+25
    while time.monotonic()<end:
        value=check()
        if value:return value
        time.sleep(.5)
    raise AssertionError('Backend effect missing')
def messages(room):return view.call('/rooms/'+room+'/messages')
def post(room,text):return peer.call('/rooms/'+room+'/messages','POST',{'clientId':str(uuid.uuid4()),'text':text})
def send(text):
    enter(find(ui(),cls='android.widget.EditText'),text);back();click(text='➤')
def menu(text,action):
    root=wait(text=text);tap([n for n in nodes(root) if n.get('text')==text][-1],True);tap(seek(action))
size(1080,2400,420)
group_title='Native_Empty_'+str(int(time.time()))
launch();wait(desc='New chat');click(desc='New chat');click(text='Group')
root=ui();enter(find(root,cls='android.widget.EditText'),group_title);back();click(text='Create');wait(desc='Chat actions')
room=poll(lambda:next((r for r in view.call('/rooms') if r['title']==group_title),None))['id']
assert len(view.call('/rooms/'+room+'/members'))==1;record('empty-group-created')
click(desc='Chat actions');click(text='Members');root=wait(text='Member username');enter(find(root,cls='android.widget.EditText'),f['names'][1]);back();click(text='Invite member')
poll(lambda:len(view.call('/rooms/'+room+'/members'))==2);record('invite-after-create');back()
send('NATIVE_SENT');sent=poll(lambda:next((m for m in messages(room) if m['text']=='NATIVE_SENT'),None));record('send')
menu('NATIVE_SENT','Edit');root=wait(text='Edit message');enter(find(root,cls='android.widget.EditText'),'NATIVE_EDITED');back();click(text='Save')
poll(lambda:any(m['text']=='NATIVE_EDITED' for m in messages(room)));record('edit')
other=post(room,'NATIVE_PEER');menu('NATIVE_PEER','Reply');send('NATIVE_REPLY')
poll(lambda:any(m['text']=='NATIVE_REPLY' and m.get('replyTo')==other['sequence'] for m in messages(room)));record('reply')
menu('NATIVE_PEER','Pin');poll(lambda:any(r['id']==room and r.get('pinnedMessage')==other['sequence'] for r in view.call('/rooms')));record('pin')
menu('NATIVE_EDITED','Save to Saved messages');click(text='Save to Saved messages')
poll(lambda:any(m['text']=='NATIVE_EDITED' for m in messages(scenes['saved']['id'])));record('save')
menu('NATIVE_EDITED','Forward');tap(seek(f['names'][1]));click(text='Forward')
poll(lambda:any(m['text']=='NATIVE_EDITED' for m in messages(scenes['direct']['id'])));record('forward')
menu('NATIVE_EDITED','Delete');click(text='Delete');poll(lambda:any(m['sequence']==sent['sequence'] and m['deleted'] for m in messages(room)));record('delete')
admin=scenes['group-admin']['id'];target=post(admin,'NATIVE_ADMIN_DELETE');launch(admin);menu('NATIVE_ADMIN_DELETE','Delete');click(text='Delete')
poll(lambda:any(m['sequence']==target['sequence'] and m['deleted'] for m in messages(admin)));record('administrator-delete-peer')
channel=scenes['channel-2']['id'];target=post(channel,'NATIVE_CHANNEL_EDIT');launch(channel);menu('NATIVE_CHANNEL_EDIT','Edit');root=wait(text='Edit message');enter(find(root,cls='android.widget.EditText'),'NATIVE_CHANNEL_CHANGED');back()
peer.call('/channels/'+channel+'/admins/'+f['names'][0],'PUT',{'rights':0},204)
def save_disabled():
    root=ui();node=find(root,text='Save')
    # Text may delegate enabled state to its clickable parent.
    return root if any(n.get('clickable')=='true' and n.get('enabled')=='false' and any(c.get('text')=='Save' for c in n.iter('node')) for n in nodes(root)) else None
poll(save_disabled);record('open-edit-revoked');back()
assert any(m['text']=='NATIVE_CHANNEL_EDIT' for m in messages(channel))
peer.call('/channels/'+channel+'/admins/'+f['names'][0],'PUT',{'rights':2},204)
launch(channel);menu('NATIVE_CHANNEL_EDIT','Edit');root=wait(text='Edit message');enter(find(root,cls='android.widget.EditText'),'NATIVE_CHANNEL_CHANGED');back();click(text='Save')
poll(lambda:any(m['text']=='NATIVE_CHANNEL_CHANGED' for m in messages(channel)));record('channel-edit')
# Offline outbox uses a cached real room and reconnects to the same audit service.
launch(room);adb('reverse','--remove','tcp:5193');launch(room);send('NATIVE_OFFLINE_QUEUED');record('offline-queued')
adb('reverse','tcp:5193','tcp:5193');poll(lambda:any(m['text']=='NATIVE_OFFLINE_QUEUED' for m in messages(room)));record('offline-reconnected')
(OUT/'action-room.json').write_text(json.dumps({'id':room}))
print('Native actions',len(report),flush=True)
