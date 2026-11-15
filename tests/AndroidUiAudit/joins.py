from support import *
f=json.loads((OUT/'fixtures.json').read_text());view,peer,guest=[Api(n) for n in f['names']];scenes={s['key']:s for s in f['scenes']};records=[]
def record(name):capture('joins-'+name);records.append(name);(OUT/'joins-result.json').write_text(json.dumps(records));print('PASS',name,flush=True)
channel=scenes['channel-0']['id'];group=peer.call('/channels/'+channel+'/discussion')['groupId']
peer.call('/rooms/'+group+'/members','POST',{'username':f['names'][0],'operation':'remove'},204)
try:
    size(1080,2400,420);launch(channel);click(desc='Chat actions');click(text='Channel settings')
    tap(seek('Request to join discussion group'));wait(text='Join request submitted');record('nonmember-request')
    assert f['names'][0] in peer.call('/rooms/'+group+'/discussion-requests')
finally:
    operation='approve_join' if f['names'][0] in peer.call('/rooms/'+group+'/discussion-requests') else 'invite'
    peer.call('/rooms/'+group+'/members','POST',{'username':f['names'][0],'operation':operation},204)
channel=scenes['channel-127']['id'];group=view.call('/channels/'+channel+'/discussion')['groupId']
guest.call('/channels/'+channel+'/subscription','POST',{'subscribed':True},204)
guest.call('/channels/'+channel+'/discussion/join','POST',{},204)
size(1280,720,320);launch(group);click(desc='Chat actions');click(text='Members');root=wait(text='Join requests');record('landscape-join-entry')
click(text='Join requests');root=wait(text='Approve');record('review-dialog');click(text='Approve')
for _ in range(10):
    members=view.call('/rooms/'+group+'/members')
    if any(m['name']==f['names'][2] for m in members):break
    time.sleep(.5)
assert any(m['name']==f['names'][2] for m in members);record('approved')
