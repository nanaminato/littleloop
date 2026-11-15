"""Native error handling preserves the editable input and allows a successful retry."""
from support import *

f=json.loads((OUT/'social-fixtures.json').read_text());records=[]
def record(name):
    capture('social-error-'+name);records.append(name)
    (OUT/'social-errors-result.json').write_text(json.dumps(records));print('PASS',name,flush=True)
def cleared():
    end=time.monotonic()+25
    while time.monotonic()<end:
        field=find(ui(),cls='android.widget.EditText')
        if field is not None and not field.get('text'):return
    raise AssertionError('Successful retry did not clear submitted input')

size(1080,2400,420);launch();wait(desc='New chat');click(text='Friends');wait(text='Friend username')
enter(find(ui(),cls='android.widget.EditText'),f['names'][0]);back();click(text='Add friend')
wait(text='Request failed (404).');assert find(ui(),cls='android.widget.EditText').get('text')==f['names'][0];record('friend-request-failed-input-retained')
enter(find(ui(),cls='android.widget.EditText'),f['friend']);back();click(text='Add friend');cleared();record('friend-request-retry')

room=next(s['id'] for s in f['scenes'] if s['key']=='group-owner')
launch(room);click(desc='Chat actions');click(text='Members');wait(text='Member username')
enter(find(ui(),cls='android.widget.EditText'),f['names'][0]);back();click(text='Invite member')
wait(text='Check your input.');assert find(ui(),cls='android.widget.EditText').get('text')==f['names'][0];record('invite-failed-input-retained')
enter(find(ui(),cls='android.widget.EditText'),f['names'][1]);back();click(text='Invite member');cleared();record('invite-retry')
