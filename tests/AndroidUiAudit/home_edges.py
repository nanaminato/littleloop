"""Home filtering, stale parent access and account boundaries on the installed app."""
from support import *

f=json.loads((OUT/'fixtures.json').read_text())
view,peer=Api(f['names'][0]),Api(f['names'][1])
report=[]; stamp=str(int(time.time()))

def record(name):
    capture('home-edge-'+name); report.append(name)
    (OUT/'home-edges-result.json').write_text(json.dumps(report,indent=2))
    print('PASS',name,flush=True)

def home(): launch(); return wait(desc='New chat')
def query(value):
    enter(find(ui(),cls='android.widget.EditText'),value);back()

def row(title):
    # The query field contains the same text; wait for the actual list label.
    end=time.monotonic()+30
    while time.monotonic()<end:
        node=find(ui(),text=title,cls='android.widget.TextView')
        if node is not None:return node
    raise AssertionError('Conversation row missing: '+title)

size(1080,2400,420)
title='HomeArchived_'+stamp
room=view.call('/rooms','POST',{'type':'group','title':title,'members':[]})['id']
home();query(title);row(title);record('list-finds-group')
view.call('/rooms/'+room+'/preference','POST',{'key':'archived','value':True},204)
wait(text='No messages found');record('archived-hidden-during-search')
view.call('/rooms/'+room+'/preference','POST',{'key':'archived','value':False},204)
tap(row(title));wait(desc='Chat actions');record('unarchived-open-from-list')

parent=peer.call('/rooms','POST',{'type':'group','title':'HomeParent_'+stamp,'members':[f['names'][0]]})['id']
launch(parent);click(desc='Chat actions')
tap(next(n for n in nodes(ui()) if n.get('text','').startswith('Topics')))
wait(text='New topic');click(text='New topic');wait(text='Topic name')
query('StaleTopic_'+stamp)
peer.call('/rooms/'+parent+'/members','POST',{'username':f['names'][0],'operation':'remove'},204)
wait(desc='New chat'); assert find(ui(),text='Create topic') is None
assert all(r['title']!='StaleTopic_'+stamp for r in peer.call('/rooms'))
record('removed-parent-closes-topic-dialog')

home();query('PreviousAccountDraft')
login(f['names'][1]);root=wait(desc='New chat')
assert find(root,cls='android.widget.EditText').get('text','')==''
click(desc='New chat');wait(text='Direct chat')
assert find(ui(),cls='android.widget.EditText').get('text','')==''
record('account-switch-clears-home-and-create-drafts')
back();login(f['names'][0])
print('Native home edge cases',len(report),flush=True)
