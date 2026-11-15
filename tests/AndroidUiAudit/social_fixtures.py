"""Extend the ordinary audit fixtures with friend states and protected admin targets."""
from support import *

f=json.loads((OUT/'fixtures.json').read_text())
view,peer,guest=[Api(n) for n in f['names']]
extra='social_friend_'+str(int(time.time()))[-8:]
Api().call('/register','POST',{'name':extra,'password':ENV['CHAT_TEST_PASSWORD'],'invite':ENV['Chat__Invite']},204)
accepted=Api(extra)
accepted.call('/friends','POST',{'username':f['names'][0]},204)
incoming=next(r for r in view.call('/friends') if r['username']==extra)
view.call('/friends/'+incoming['id']+'/respond','POST',{'accepted':True},204)
for s in f['scenes']:
    if s['type']!='group':continue
    owner=view if s['role']=='owner' else peer
    for name in [f['names'][2],extra]:owner.call('/rooms/'+s['id']+'/members','POST',{'username':name,'operation':'invite'},204)
    owner.call('/rooms/'+s['id']+'/members','POST',{'username':extra,'operation':'promote'},204)
f['friend']=extra
(OUT/'social-fixtures.json').write_text(json.dumps(f,indent=2))
print('Social fixtures: incoming, outgoing, accepted; four group roles and protected administrator targets')
