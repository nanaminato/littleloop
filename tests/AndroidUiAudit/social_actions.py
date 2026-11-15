"""Real friendship and member mutations, plus live permission and recreation checks."""
from support import *
import uuid

f=json.loads((OUT/'social-fixtures.json').read_text())
view,peer,guest=[Api(n) for n in f['names']]
records=json.loads((OUT/'social-actions-result.json').read_text()) if os.environ.get('SOCIAL_RESUME_JOINS') else []

def record(name):
    capture('social-action-'+name);records.append(name)
    (OUT/'social-actions-result.json').write_text(json.dumps(records,indent=2));print('PASS',name,flush=True)

def poll(check):
    end=time.monotonic()+30
    while time.monotonic()<end:
        value=check()
        if value:return value
        time.sleep(.5)
    raise AssertionError('Expected state did not arrive')

def account(prefix):
    name='social_'+prefix+'_'+uuid.uuid4().hex[:8]
    Api().call('/register','POST',{'name':name,'password':ENV['CHAT_TEST_PASSWORD'],'invite':ENV['Chat__Invite']},204)
    return name,Api(name)

def row_button(username,label):
    seek(username,40)
    for _ in range(10):
        root=ui()
        name=find(root,text=username)
        candidates=[n for n in nodes(root) if n.get('text')==label and name is not None
                    and (rect(n)[1]+rect(n)[3])//2>=rect(name)[1]]
        if candidates:
            tap(min(candidates,key=lambda n:rect(n)[1]));return
        scroll(root)
    raise AssertionError('Missing row action '+username+' '+label)

def friends():
    launch();wait(desc='New chat');click(text='Friends');wait(text='Friend username')

def members(room):
    launch();wait(text='Connected')
    adb('shell','am','start','-n','dev.littleloop.chat/.MainActivity','--es','room',room)
    wait(desc='Chat actions');click(desc='Chat actions');wait(text='Members');click(text='Members');wait(text='Members')

def choose(username,label):
    row_button(username,'⋮');seek(label);click(text=label);wait(text='Confirm')

def change(api,room,name,operation):api.call('/rooms/'+room+'/members','POST',{'username':name,'operation':operation},204)
def listing(api,room):return api.call('/rooms/'+room+'/members')
def member(api,room,name):return next((m for m in listing(api,room) if m['name']==name),None)

def finish_join_actions(adminroom, channel, applicant, applicant_api):
    if 'join-approved' not in records: members(adminroom)
    if 'join-declined' not in records:
        wait(text='Join requests');click(text='Join requests');wait(text='Decline');click(text='Decline')
        poll(lambda:applicant not in view.call('/rooms/'+adminroom+'/discussion-requests'))
        assert member(view,adminroom,applicant) is None;record('join-declined')
    if 'join-approved' not in records:
        if applicant not in view.call('/rooms/'+adminroom+'/discussion-requests'):
            applicant_api.call('/channels/'+channel+'/discussion/join','POST',{},204)
        wait(text='Join requests');click(text='Join requests');wait(text='Approve');click(text='Approve')
        poll(lambda:member(view,adminroom,applicant));record('join-approved')
    empty,empty_api=account('empty');friends();enter(find(ui(),cls='android.widget.EditText'),'ACCOUNT_LOCAL_DRAFT');back()
    login(empty);friends();wait(text='No friends or requests yet');assert find(ui(),text='ACCOUNT_LOCAL_DRAFT') is None;record('account-switch-isolates-friends')
    login(f['names'][0]);print('Social actions',len(records),flush=True)

if os.environ.get('SOCIAL_RESUME_JOINS'):
    # Resume a device/ADB interruption using the saved synthetic fixture IDs.
    saved=json.loads((OUT/'social-action-context.json').read_text())
    finish_join_actions(saved['group'],saved['channel'],saved['applicant'],Api(saved['applicant']))
    raise SystemExit(0)

size(1080,2400,420)
recipient,recipient_api=account('recipient');incoming,incoming_api=account('incoming');declining,declining_api=account('decline')
friends();enter(find(ui(),cls='android.widget.EditText'),'UNSENT_FRIEND_DRAFT');back()
size(1280,720,320);wait(text='UNSENT_FRIEND_DRAFT');record('friend-draft-recreated');size(1080,2400,420)
enter(find(ui(),cls='android.widget.EditText'),recipient);back();click(text='Add friend')
poll(lambda:any(r['username']==recipient and r['state']=='outgoing' for r in view.call('/friends')));record('friend-request-sent')
incoming_api.call('/friends','POST',{'username':f['names'][0]},204)
row_button(incoming,'Accept');poll(lambda:any(r['username']==incoming and r['state']=='friend' for r in view.call('/friends')));record('friend-accepted')
row_button(incoming,'Start private chat');wait(desc='Chat actions')
direct=poll(lambda:next((r for r in view.call('/rooms') if r['type']=='direct' and r['title']==incoming),None))['id'];record('friend-opens-direct')
friends();row_button(incoming,'Start private chat');wait(desc='Chat actions')
assert len([r for r in view.call('/rooms') if r['type']=='direct' and r['title']==incoming])==1;record('friend-reuses-direct')
declining_api.call('/friends','POST',{'username':f['names'][0]},204)
friends();row_button(declining,'Decline');poll(lambda:not any(r['username']==declining for r in view.call('/friends')));record('friend-declined')

invited,invited_api=account('invitee')
room=view.call('/rooms','POST',{'type':'group','title':'Social owner actions','members':[f['names'][1],f['names'][2]]})['id']
topic=view.call('/rooms/'+room+'/topics','POST',{'title':'Social action topic'})['id']
members(room);enter(find(ui(),cls='android.widget.EditText'),invited);back()
size(1280,720,320);wait(text=invited);record('invite-draft-recreated');size(1080,2400,420)
click(text='Invite member');poll(lambda:member(view,room,invited));assert any(r['id']==topic for r in invited_api.call('/rooms'));record('invite-propagates-to-topic')
choose(f['names'][2],'Promote to admin');size(1280,720,320);wait(text='Confirm');record('confirmation-recreated');click(text='Cancel');size(1080,2400,420)
assert not member(view,room,f['names'][2])['administrator'];record('promotion-cancelled')
for label,operation,key,value in [('Promote to admin','promote','administrator',True),('Remove group admin','demote','administrator',False),('Mute member','silence','silenced',True),('Unmute member','unsilence','silenced',False)]:
    choose(f['names'][2],label);click(text='Confirm');poll(lambda:member(view,room,f['names'][2])[key]==value);record(operation)
choose(invited,'Remove member');click(text='Cancel');assert member(view,room,invited);record('removal-cancelled')
message=invited_api.call('/rooms/'+room+'/messages','POST',{'clientId':str(uuid.uuid4()),'text':'SOCIAL_RETAINED_MESSAGE'})
choose(invited,'Remove member');click(text='Confirm');poll(lambda:member(view,room,invited) is None)
assert not any(r['id'] in [room,topic] for r in invited_api.call('/rooms'))
assert any(m['sequence']==message['sequence'] and m['text']=='SOCIAL_RETAINED_MESSAGE' for m in view.call('/rooms/'+room+'/messages'));record('remove-revokes-topic-retains-history')
choose(f['names'][2],'Transfer ownership');click(text='Cancel');assert member(view,room,f['names'][0])['owner'];record('transfer-cancelled')
choose(f['names'][2],'Transfer ownership');click(text='Confirm');poll(lambda:member(guest,room,f['names'][2])['owner'])
poll(lambda:find(ui(),text='Invite member') is None);assert member(guest,room,f['names'][0]);record('transfer-revokes-owner-controls')

adminroom=peer.call('/rooms','POST',{'type':'group','title':'Social live permissions','members':[f['names'][0],f['names'][2]]})['id']
change(peer,adminroom,f['names'][0],'promote');members(adminroom)
choose(f['names'][2],'Remove member');change(peer,adminroom,f['names'][2],'promote')
poll(lambda:find(ui(),text='Confirm') is None);assert member(peer,adminroom,f['names'][2]);record('pending-target-promotion-invalidated')
change(peer,adminroom,f['names'][2],'demote');members(adminroom);choose(f['names'][2],'Remove member')
change(peer,adminroom,f['names'][0],'demote');poll(lambda:find(ui(),text='Confirm') is None)
assert member(peer,adminroom,f['names'][2]);record('pending-actor-demotion-invalidated')
change(peer,adminroom,f['names'][0],'promote');members(adminroom);choose(f['names'][2],'Mute member');click(text='Confirm')
poll(lambda:member(peer,adminroom,f['names'][2])['silenced']);record('administrator-mutes-member')
choose(f['names'][2],'Unmute member');click(text='Confirm');poll(lambda:not member(peer,adminroom,f['names'][2])['silenced']);record('administrator-unmutes-member')
choose(f['names'][2],'Remove member');change(peer,adminroom,f['names'][2],'remove')
poll(lambda:find(ui(),text='Confirm') is None);record('pending-target-removal-invalidated')

# An approval list is visible only to its current owner, including while a dialog is open.
applicant,applicant_api=account('applicant')
channel=peer.call('/channels','POST',{'title':'Social join requests'})['id']
peer.call('/channels/'+channel+'/discussion','PUT',{'groupId':adminroom},204)
applicant_api.call('/channels/'+channel+'/subscription','POST',{'subscribed':True},204)
applicant_api.call('/channels/'+channel+'/discussion/join','POST',{},204)
(OUT/'social-action-context.json').write_text(json.dumps({'group':adminroom,'channel':channel,'applicant':applicant}))
change(peer,adminroom,f['names'][0],'transfer');members(adminroom);seek('Join requests');click(text='Join requests');wait(text='Approve')
change(view,adminroom,f['names'][1],'transfer');poll(lambda:find(ui(),text='Approve') is None);record('pending-join-owner-change-invalidated')
change(peer,adminroom,f['names'][0],'transfer')
finish_join_actions(adminroom,channel,applicant,applicant_api)
