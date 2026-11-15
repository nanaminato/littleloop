from support import *
import hashlib
size(1080,2400,420)
name='NativeAudit-'+str(int(time.time()))+'.txt';download='Downloaded-'+name
source=OUT/name;source.write_bytes(b'Native Android attachment audit\r\nsecond line\r\n')
adb('push',source,'/sdcard/Download/'+name)
room=json.loads((OUT/'action-room.json').read_text())['id'];launch(room);click(text='＋');wait(desc='Show roots')
click(desc='Show roots');click(text='Downloads');wait(text=name);click(text=name);wait(desc='Chat actions');click(text='➤')
wait(text=name);click(text=name);wait(text='Download');capture('attachment-preview');click(text='Download')
root=wait(text='SAVE');enter(find(root,cls='android.widget.EditText'),download);back();click(text='SAVE')
wait(text='Attachment saved');capture('attachment-downloaded')
saved=adb('exec-out','cat','/sdcard/Download/'+download);assert saved==source.read_bytes()
(OUT/'attachment-result.json').write_text(json.dumps({'nativeUpload':True,'nativePreview':True,'nativeDownload':True,'byteEqual':True,'sha256':hashlib.sha256(saved).hexdigest()}))
print('Native attachment upload, preview and download byte comparison passed')
