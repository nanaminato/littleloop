from support import *
f=json.loads((OUT/'fixtures.json').read_text());room=next(s['id'] for s in f['scenes'] if s['type']=='saved');results=[]
(OUT/'bot-mode.json').write_text(json.dumps({'kind':'task','canApprove':False,'legacy':True}))
for config,w,h,d in [('small',640,1136,320),('phone',1080,2400,420),('tablet',1600,2560,320),('landscape',1280,720,320)]:
    size(w,h,d);launch(room);wait(text='Historical workbench fixture');wait(text='Synthetic native bot fixture');capture('legacy-'+config)
    click(text='Development task');wait(text='Close');capture('legacy-'+config+'-details')
    results.append({'size':config,'mode':'synthetic legacy room rendering; no historical import or task execution'})
(OUT/'legacy-result.json').write_text(json.dumps(results,indent=2));print('Legacy native render cases',len(results))
