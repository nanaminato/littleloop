from support import *
f=json.loads((OUT/'fixtures.json').read_text());room=next(s['id'] for s in f['scenes'] if s['type']=='saved')
report=json.loads((OUT/'bot-result.json').read_text()) if os.environ.get('AUDIT_RESUME') and (OUT/'bot-result.json').exists() else []
adb('reverse','tcp:5194','tcp:5194')
(OUT/'bot-mode.json').write_text(json.dumps({'kind':'task','canApprove':False}))
size(1080,2400,420);login(f['names'][0],origin='http://127.0.0.1:5194');capture('account-switch-home')
for config,w,h,d in [('small',640,1136,320),('phone',1080,2400,420),('tablet',1600,2560,320),('landscape',1280,720,320)]:
    size(w,h,d)
    for kind,title,approve in [('question','Source Q&A',False),('citation','Source citation',False),('task','Development task',False),('task','Development task',True)]:
        key='bot-'+config+'-'+kind+('-approver' if approve else '-reader')
        if any(r['case']==key for r in report):continue
        (OUT/'bot-mode.json').write_text(json.dumps({'kind':kind,'canApprove':approve}))
        launch(room);wait(text='Synthetic native bot fixture');tap(seek(title));wait(text='Close')
        # Details are scrollable at all sizes; inspect both approval presence and evidence controls.
        seen=[];last=None
        for i in range(15):
            root=ui();seen+=labels(root)
            if i==0:capture(key,root)
            raw=ET.tostring(root)
            if raw==last:break
            last=raw
            if not any(n.get('scrollable')=='true' for n in nodes(root)):break
            scroll(root)
        assert ('Allow once' in seen)==approve,(key,seen)
        assert any('fixture-test' in s for s in seen),(key,seen)
        if kind=='task' and not approve:
            back();tap(seek(title));wait(text='Close')
            for prefix in ['Source citation ·','Code patch ·','Test receipt ·']:
                for _ in range(30):
                    root=ui();node=next((n for n in nodes(root) if n.get('text','').startswith(prefix)),None)
                    if node is not None:break
                    scroll(root)
                assert node is not None,prefix
                tap(node);capture(key+'-expanded-'+prefix.split()[0])
        report.append({'case':key,'passed':True,'mode':'synthetic native rendering; no bot execution or approval'})
        (OUT/'bot-result.json').write_text(json.dumps(report,indent=2));print('PASS',key,flush=True)
    launch(room)
    for _ in range(10):
        click(desc='Chat actions');root=ui()
        if find(root,text='Ask about source code') is not None:break
        back()
    click(text='Ask about source code');wait(text='Select source file');capture('bot-'+config+'-request')
    click(text='Select source file');wait(text='Filter files');capture('bot-'+config+'-source-picker')
print('Synthetic native bot cases',len(report),flush=True)
