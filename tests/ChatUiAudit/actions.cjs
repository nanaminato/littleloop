// Run from the repository root. See docs/42-chat-ui-audit.md.
const fs=require('node:fs'),assert=require('node:assert/strict');const {chromium}=require(process.cwd() + '/data/workbench-browser/node_modules/playwright');
const f=JSON.parse(fs.readFileSync('data/chat-full-audit/fixtures.json'));const pass=fs.readFileSync('data/chat-phase-a/chat.local.env','utf8').match(/^CHAT_TEST_PASSWORD=(.+)$/m)[1].trim();const base=(process.env.CHAT_AUDIT_BASE || 'http://127.0.0.1:5193'),dir='data/chat-full-audit';let checks=[];const tag=Date.now();
(async()=>{const browser=await chromium.launch({channel:'chrome'});try{
async function context(name){const c=await browser.newContext({viewport:{width:390,height:844},locale:'zh-CN'});await c.addInitScript(()=>localStorage.setItem('littleloop-chat-ui',JSON.stringify({language:'zh',theme:'light',animations:false})));await call(c,'/login','POST',{name,password:pass});return c;}
async function call(c,path,method='GET',data,status=200){const r=await c.request.fetch(base+'/api/chat'+path,{method,data,headers:{'X-LittleLoop-Chat':'1'}});assert.equal(r.status(),status,path);checks.push(method+' '+path+' '+status);const t=await r.text();try{return JSON.parse(t)}catch{return t}}
const owner=await context(f.names[0]),admin=await context(f.names[1]),member=await context(f.names[2]);
const p=await admin.newPage();
for(const right of [1,2,4,8,16,32,64,0]){
 await call(owner,`/channels/${f.channel}/admins/${f.names[1]}`,'PUT',{rights:right},204);
 await p.goto(base+'/chat?room='+f.channel);await p.locator('.message').first().waitFor();assert.equal(await p.locator('.composer').count(),right===1?1:0);
 await p.locator('.message-menu summary').first().click();const menu=p.locator('.message-menu[open]');
 for(const [bit,name] of [[2,'编辑'],[4,'删除'],[8,'置顶消息']])assert.equal(await menu.getByRole('button',{name,exact:true}).count(),right===bit?1:0,`right ${right} ${name}`);
 await p.keyboard.press('Escape');await p.getByLabel('打开会话详情').click();await p.locator('.member').first().waitFor();
 assert.equal(await p.getByLabel('频道名称',{exact:true}).count(),right===16?1:0);
 assert.equal(await p.getByLabel('订阅者用户名',{exact:true}).count(),right===32?1:0);
 assert.equal(await p.locator('select[name=group]').count(),right===64?1:0);
 if(right===64) await p.waitForFunction(id=>document.querySelector('select[name=group]')?.value===id, f.group);
 assert.equal(await p.getByRole('button',{name:'转让频道',exact:true}).count(),0);
 await p.screenshot({path:dir+`/right-${right}.png`});
}
// Group moderation is available in the ordinary timeline, but mirrored roots stay immutable.
const incoming=await call(member,`/rooms/${f.group}/messages`,'POST',{text:'Moderator UI verification',clientId:crypto.randomUUID()});
await p.goto(base+'/chat?room='+f.group);await p.locator(`[data-sequence="${incoming.sequence}"]`).waitFor();const row=p.locator(`[data-sequence="${incoming.sequence}"]`);
await row.locator('summary').click();assert.equal(await row.getByRole('button',{name:'编辑',exact:true}).count(),0);await row.getByRole('button',{name:'删除',exact:true}).click();await row.getByRole('button',{name:'确认删除',exact:true}).click();await row.locator('.deleted-text').waitFor();checks.push('group admin deletes another member message via UI');
const own=await owner.newPage();await own.goto(base+'/chat?room='+f.saved);await own.locator('#message').waitFor();await own.locator('#message').fill(('UI action send '+tag));await own.getByRole('button',{name:'发送',exact:true}).click();await own.locator('.text').filter({hasText:('UI action send '+tag)}).waitFor();
let item=own.locator('.message').filter({hasText:('UI action send '+tag)});await item.locator('summary').click();await item.getByRole('button',{name:'编辑',exact:true}).click();await own.locator('#edit-text').fill(('UI action edited '+tag));await own.locator('.message-form').getByRole('button',{name:'保存修改',exact:true}).click();await own.locator('.text').filter({hasText:('UI action edited '+tag)}).waitFor();
item=own.locator('.message').filter({hasText:('UI action edited '+tag)});await item.locator('summary').click();await item.getByRole('button',{name:'转发',exact:true}).click();await own.locator('#forward-room').selectOption(f.direct);await Promise.all([own.waitForResponse(r=>r.url().endsWith('/forward') && r.request().method()==='POST'), own.getByRole('button',{name:'确认转发',exact:true}).click()]);assert.ok((await call(owner,`/rooms/${f.direct}/messages`)).some(m=>m.text===('UI action edited '+tag)));checks.push('send edit forward persisted');
await own.goto(base+'/chat?room='+f.saved);await own.locator('#attachment').setInputFiles({name:'附件文件名'+ 'long'.repeat(20)+'.txt',mimeType:'text/plain',buffer:Buffer.from('attachment audit')});await own.locator('.composer-context').waitFor();await own.getByRole('button',{name:'发送',exact:true}).click();await own.locator('.file-link').last().waitFor();await own.screenshot({path:dir+'/attachment-sent.png'});const fileUrl=await own.locator('.file-link').last().getAttribute('href');const file=await owner.request.get(base+fileUrl);assert.equal(await file.text(),'attachment audit');checks.push('attachment upload send download exact bytes');
await own.goto(base+'/chat?room='+f.channel);await own.getByRole('button',{name:'查看评论',exact:true}).first().click();await own.getByLabel('发表评论',{exact:true}).fill(('UI_COMMENT_AUDIT_'+tag));await own.locator('chat-comments').getByRole('button',{name:'发送',exact:true}).click();await own.locator('chat-comments article').filter({hasText:('UI_COMMENT_AUDIT_'+tag)}).waitFor();await own.screenshot({path:dir+'/comment-sent.png'});checks.push('comment UI send');
// Verify empty-state filters despite unrelated rooms/messages being present.
const empty=(await call(owner,'/rooms','POST',{type:'group',title:'Empty state audit',members:[]})).id;await own.goto(base+'/chat?room='+empty);await own.getByLabel('打开会话详情').click();await own.getByText('暂无话题',{exact:true}).waitFor();await own.getByText('暂无共享媒体',{exact:true}).waitFor();checks.push('empty group topics and media');
await call(owner,`/channels/${f.channel}/admins/${f.names[1]}`,'PUT',{rights:1},204);
console.log(JSON.stringify({checks:checks.length}));
}finally{fs.writeFileSync(dir+'/actions-result.json',JSON.stringify(checks,null,2));await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
