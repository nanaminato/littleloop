"""Synthetic bot rendering only; all other HTTP traffic uses the audit backend.
No bot task or approval is ever submitted. Not evidence of real bot execution.
"""
from support import OUT
import http.server, http.client, json, urllib.parse
fixture=json.loads((OUT/'fixtures.json').read_text())
room=next(s['id'] for s in fixture['scenes'] if s['type']=='saved')
source={'path':'src/'+('LongPath/'*12)+'Example.cs','start':1,'end':12,'sha256':'a'*64,'text':'public class Example { /* '+ 'source '*60+' */ }'}
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def do_GET(self):self.handle_request()
    def do_POST(self):self.handle_request()
    def do_PUT(self):self.handle_request()
    def do_DELETE(self):self.handle_request()
    def handle_request(self):
        try:
            mode=json.loads((OUT/'bot-mode.json').read_text())
            result=None
            if self.path.startswith('/api/chat/rooms/'+room+'/messages'):
                result=[{'sequence':1,'clientId':'native-bot-fixture','sender':'littleloop','text':'Synthetic native bot fixture', 'deleted':False,'sentAt':'2026-11-15T12:00:00Z','card':{'kind':mode.get('kind','task'),'taskId':'native-ui-fixture','source':source}}]
                if int(urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query).get('after',['0'])[0])>=1:result=[]
            elif self.path=='/api/chat/bot/capabilities':result={'enabled':True,'canOperate':True}
            elif self.path.endswith('/sources'):result=['README.md',source['path']]
            elif self.path.startswith('/api/chat/bot/rooms/') and self.command=='GET':result={'installed':True,'providers':['littleloop','codex']}
            elif self.path=='/api/chat/bot/work/native-ui-fixture':
                result={'status':'running','provider':'littleloop','canApprove':mode['canApprove'],'execution':{'approval':{'nonce':'fixture-only','method':'file.read','parameters':{'path':source['path']}},'changes':{source['path']:'+ fixture change'},'receipts':[{'id':'fixture-test','exitCode':0,'standardOutput':'fixture output '*30,'standardError':''}]}}
            elif self.path.startswith('/api/chat/bot/') and self.command!='GET':
                self.send_error(405,'Rendering fixture prohibits executing bot actions');return
            if result is not None:
                payload=json.dumps(result).encode();self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',len(payload));self.end_headers();self.wfile.write(payload);return
            body=self.rfile.read(int(self.headers.get('Content-Length',0)))
            connection=http.client.HTTPConnection('127.0.0.1',5193,timeout=20)
            headers={k:v for k,v in self.headers.items() if k.lower() not in ['host','connection','transfer-encoding']}
            connection.request(self.command,self.path,body,headers);response=connection.getresponse();payload=response.read()
            if self.path=='/api/chat/rooms' and response.status==200:
                rooms=json.loads(payload)
                for item in rooms:
                    if item['id']==room:
                        item.update(latest=1,read=1,unread=0,revision=1)
                        if mode.get('legacy'):item.update(type='legacy',title='Historical workbench fixture')
                payload=json.dumps(rooms).encode()
            self.send_response(response.status)
            for k,v in response.getheaders():
                if k.lower() not in ['transfer-encoding','connection','content-length']:self.send_header(k,v)
            self.send_header('Content-Length',len(payload));self.end_headers();self.wfile.write(payload);connection.close()
        except (BrokenPipeError,ConnectionResetError):pass
http.server.ThreadingHTTPServer(('127.0.0.1',5194),Handler).serve_forever()
