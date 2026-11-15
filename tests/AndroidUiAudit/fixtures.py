from support import *
import uuid
stamp=str(int(time.time()))[-7:]
names=['android_audit_'+role+'_'+stamp for role in ['view','peer','guest']]
anon=Api()
for name in names: anon.call('/register','POST',{'name':name,'password':ENV['CHAT_TEST_PASSWORD'],'invite':ENV['Chat__Invite']},204)
view,peer,guest=[Api(n) for n in names]
def post(api,room,text,**extra):return api.call('/rooms/'+room+'/messages','POST',{'clientId':str(uuid.uuid4()),'text':text,**extra})
scenes=[]
for role in ['owner','admin','member','muted']:
    creator=view if role=='owner' else peer
    group=creator.call('/rooms','POST',{'type':'group','title':'Audit '+role+' group','members':[names[1] if role=='owner' else names[0]]})['id']
    if role in ['admin','muted']:peer.call('/rooms/'+group+'/members','POST',{'username':names[0],'operation':'promote' if role=='admin' else 'silence'},204)
    post(peer,group,'AUDIT_OTHER_'+role)
    scenes.append({'key':'group-'+role,'id':group,'type':'group','role':role,'canPost':role!='muted'})
    topic=creator.call('/rooms/'+group+'/topics','POST',{'title':'Audit '+role+' topic'})['id']
    post(peer,topic,'AUDIT_OTHER_'+role)
    scenes.append({'key':'topic-'+role,'id':topic,'type':'topic','role':role,'canPost':role!='muted'})
discussion=peer.call('/rooms','POST',{'type':'group','title':'Audit discussion','members':[names[0]]})['id']
for right in [127,1,2,4,8,16,32,64,0]:
    creator=view if right==127 else peer
    channel=creator.call('/channels','POST',{'title':'Audit channel '+str(right)+' '+('LongName'*8)})['id']
    for api in [view,peer]:
        if api is not creator:api.call('/channels/'+channel+'/subscription','POST',{'subscribed':True},204)
    if right not in [0,127]:peer.call('/channels/'+channel+'/admins/'+names[0],'PUT',{'rights':right},204)
    # Both publishers can manage the discussion by using a group owned by the channel creator.
    linked=creator.call('/rooms','POST',{'type':'group','title':'Discussion '+str(right),'members':[names[0] if creator is peer else names[1]]})['id']
    creator.call('/channels/'+channel+'/discussion','PUT',{'groupId':linked},204)
    message=post(creator,channel,'AUDIT_POST_'+str(right))
    thread=creator.call('/channels/'+channel+'/posts/'+str(message['sequence'])+'/comments')
    post(peer,linked,'AUDIT_COMMENT_'+str(right),replyTo=thread['rootSequence'])
    scenes.append({'key':'channel-'+str(right),'id':channel,'type':'channel','role':str(right),'rights':right,'post':message['sequence'],'canPost':bool(right&1)})
direct=view.call('/rooms','POST',{'type':'direct','members':[names[1]],'title':''})['id']
saved=view.call('/saved','POST',{})['id']
post(peer,direct,'AUDIT_OTHER_direct');post(view,direct,'AUDIT_OWN_direct');post(view,saved,'AUDIT_OWN_saved')
scenes.extend([{'key':'direct','id':direct,'type':'direct','role':'member','canPost':True},{'key':'saved','id':saved,'type':'saved','role':'owner','canPost':True}])
view.call('/friends','POST',{'username':names[1]},204)
guest.call('/friends','POST',{'username':names[0]},204)
fixture={'names':names,'scenes':scenes,'discussion':discussion}
(OUT/'fixtures.json').write_text(json.dumps(fixture,indent=2),encoding='utf-8')
print('Created native audit fixtures:',len(scenes),'conversation/role cases')
