"""State recreation and channel controller mutations through the installed APK."""
from support import *
import uuid

fixture = json.loads((OUT/'fixtures.json').read_text())
view, peer = [Api(n) for n in fixture['names'][:2]]
records = []

def record(name):
    capture('refactor-'+name)
    records.append(name)
    (OUT/'refactor-result.json').write_text(json.dumps(records, indent=2))
    print('PASS', name, flush=True)

def poll(check):
    end = time.monotonic()+25
    while time.monotonic()<end:
        value = check()
        if value: return value
        time.sleep(.5)
    raise AssertionError('Backend effect missing')

def field(label):
    seek(label, 40)
    return next(n for n in nodes(ui()) if n.get('class')=='android.widget.EditText'
                and any(c.get('text')==label for c in n.iter('node')))

def toggle(label):
    seek(label, 40)
    root=ui(); text=find(root,text=label); y=rect(text)[1:4:2]
    box=next(n for n in nodes(root) if n.get('checkable')=='true'
             and rect(n)[1]<y[1] and rect(n)[3]>y[0])
    tap(box)

def settings(room):
    launch(room); click(desc='Chat actions'); click(text='Channel settings'); wait(text='Cancel')

size(1080,2400,420)
launch(); wait(desc='New chat')
room=view.call('/rooms','POST',{'type':'group','title':'Refactor state '+str(uuid.uuid4())[:8],'members':[]})['id']
adb('shell','am','start','-W','-n','dev.littleloop.chat/.MainActivity','--es','room',room)
wait(desc='Chat actions'); enter(find(ui(),cls='android.widget.EditText'),'REFACTOR_DRAFT'); back()
# wm changes recreate the Activity (the manifest does not intercept configChanges).
size(1280,720,320); wait(text='REFACTOR_DRAFT'); record('draft-survives-recreation')
size(1080,2400,420); wait(text='REFACTOR_DRAFT')
other=next(s for s in fixture['scenes'] if s['key']=='direct')['id']
adb('shell','am','start','-W','-n','dev.littleloop.chat/.MainActivity','--es','room',other)
wait(desc='Chat actions'); poll(lambda:find(ui(),text='REFACTOR_DRAFT') is None)
assert not any(m['text']=='REFACTOR_DRAFT' for m in view.call('/rooms/'+other+'/messages'))
record('draft-isolated-after-room-switch')

channel=view.call('/channels','POST',{'title':'Refactor channel'})['id']
peer.call('/channels/'+channel+'/subscription','POST',{'subscribed':True},204)
settings(channel); enter(field('Channel name'),'Refactor renamed'); back(); toggle('Show author signatures'); click(text='Save')
poll(lambda:any(r['id']==channel and r['title']=='Refactor renamed' and r['signPosts'] for r in view.call('/rooms')))
record('channel-profile-saved')
enter(field('Subscriber username'),fixture['names'][1]); back(); toggle('Publish posts')
seek('Manage discussion',40); scroll(ui()); click(text='Save')
poll(lambda:any(g['name']==fixture['names'][1] and g['rights']==1 for g in view.call('/channels/'+channel+'/admins')))
record('administrator-granted')
settings(channel); seek('Edit',40); click(text='Edit'); seek('Remove administrator',40); click(text='Remove administrator')
poll(lambda:not any(g['name']==fixture['names'][1] for g in view.call('/channels/'+channel+'/admins')))
record('administrator-revoked')
settings(channel); seek('Remove subscriber',40)
fields=[n for n in nodes(ui()) if n.get('class')=='android.widget.EditText']
enter(fields[-1],fixture['names'][1]); back(); seek('Remove subscriber',40); click(text='Remove subscriber')
poll(lambda:not any(m['name']==fixture['names'][1] for m in view.call('/rooms/'+channel+'/members')))
record('subscriber-removed')
peer.call('/channels/'+channel+'/subscription','POST',{'subscribed':True},204)
seek('Management log',40); click(text='Management log')
for _ in range(40):
    root=ui()
    if any('Refactor renamed' in text for text in labels(root)): break
    scroll(root)
else: raise AssertionError('Profile change missing from management log')
record('management-log-loaded')
settings(channel); seek('Link discussion group',40); click(text='Link discussion group')
title=next(r['title'] for r in view.call('/rooms') if r['id']==room)
seek(title,40); click(text=title)
poll(lambda:view.call('/channels/'+channel+'/discussion')['groupId']==room); record('discussion-linked')
seek('Link discussion group',40); click(text='Link discussion group'); seek('Unlink discussion group',40); click(text='Unlink discussion group')
poll(lambda:view.call('/channels/'+channel+'/discussion')['groupId'] is None); record('discussion-unlinked')
settings(channel); enter(field('New channel owner username'),fixture['names'][1]); back(); seek('Transfer channel',40); click(text='Transfer channel'); wait(text='Confirm')
click(text='Cancel'); assert any(m['name']==fixture['names'][0] and m['owner'] for m in view.call('/rooms/'+channel+'/members'))
record('transfer-cancelled')
click(text='Transfer channel'); wait(text='Confirm'); click(text='Confirm')
poll(lambda:any(m['name']==fixture['names'][1] and m['owner'] for m in peer.call('/rooms/'+channel+'/members')))
record('ownership-transferred')
# Restore synthetic ownership via API so the native deletion flow can be checked.
peer.call('/channels/'+channel+'/ownership','PUT',{'username':fixture['names'][0]},204)
settings(channel); seek('Delete channel',40); click(text='Delete channel'); wait(text='Confirm channel deletion'); click(text='Cancel')
assert any(r['id']==channel for r in view.call('/rooms')); record('deletion-cancelled')
click(text='Delete channel'); wait(text='Confirm channel deletion'); click(text='Confirm channel deletion')
poll(lambda:not any(r['id']==channel for r in view.call('/rooms')))
assert any(r['id']==room for r in view.call('/rooms')); record('channel-deleted-group-retained')
