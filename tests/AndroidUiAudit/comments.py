from support import *
import uuid
f=json.loads((OUT/'fixtures.json').read_text());view,peer=Api(f['names'][0]),Api(f['names'][1]);scenes={s['key']:s for s in f['scenes']};records=[]
def comments(key):
    scene=scenes[key];adb('shell','am','force-stop','dev.littleloop.chat')
    adb('shell','am','start','-W','-n','dev.littleloop.chat/.MainActivity','--es','room',scene['id'],'--ez','comments','true','--el','sequence',scene['post'])
    return wait(text='Comments')
def record(name):
    capture('comments-'+name);records.append(name);(OUT/'comments-result.json').write_text(json.dumps(records));print('PASS',name,flush=True)
def thread(key):
    s=scenes[key];return view.call('/channels/'+s['id']+'/posts/'+str(s['post'])+'/comments')
size(1080,2400,420);comments('channel-127');seek('Message');root=ui();enter(find(root,cls='android.widget.EditText'),'NATIVE_COMMENT_SENT');back();click(text='Send')
wait(text='NATIVE_COMMENT_SENT');assert any(m['text']=='NATIVE_COMMENT_SENT' for m in thread('channel-127')['messages']);record('send')
tap(seek('Follow discussion'));wait(text='Unfollow discussion');assert thread('channel-127')['following'];record('follow')
tap(seek('Close comments'));wait(text='Comments are closed');assert thread('channel-127')['closed'];assert find(ui(),cls='android.widget.EditText') is None;record('closed')
tap(seek('Open comments'));wait(text='Message');assert not thread('channel-127')['closed'];record('reopened')
key='channel-1';group=thread(key)['groupId']
peer.call('/rooms/'+group+'/members','POST',{'username':f['names'][0],'operation':'silence'},204)
try:
    comments(key);wait(text='You are muted in the discussion group.');assert find(ui(),cls='android.widget.EditText') is None;record('muted-discussion')
finally:peer.call('/rooms/'+group+'/members','POST',{'username':f['names'][0],'operation':'unsilence'},204)
comments(key);wait(text='Message');record('unmuted-discussion')
print('Native comment states',len(records),flush=True)
