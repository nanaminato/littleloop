// Run from the repository root. See docs/42-chat-ui-audit.md.
// Synthetic responses exercise rendering without running or approving development work.
const fs=require('node:fs'),assert=require('node:assert/strict');const {chromium}=require(process.cwd() + '/data/workbench-browser/node_modules/playwright');
const out=process.env.CHAT_AUDIT_DIR || 'data/chat-full-audit';
const f=JSON.parse(fs.readFileSync(out+'/fixtures.json'));
const pass=process.env.CHAT_AUDIT_PASSWORD || fs.readFileSync('data/chat-phase-a/chat.local.env','utf8').match(/^CHAT_TEST_PASSWORD=(.+)$/m)[1].trim();
const base=process.env.CHAT_AUDIT_BASE || 'http://127.0.0.1:5193';
(async()=>{const browser=await chromium.launch({channel:'chrome'});const report=[];try{
for(const width of [1440,390,320])for(const canApprove of [false,true]){
const ctx=await browser.newContext({viewport:{width,height:844},locale:'zh-CN'});await ctx.addInitScript(()=>localStorage.setItem('littleloop-chat-ui',JSON.stringify({language:'zh',theme:'dark',animations:false})));
await ctx.request.post(base+'/api/chat/login',{data:{name:f.names[0],password:pass},headers:{'X-LittleLoop-Chat':'1'}});
const source={path:'src/'+ 'LongSourcePath/'.repeat(8)+'Example.cs',start:1,end:12,sha256:'a'.repeat(64),text:'public class Example { /* '+ 'source '.repeat(60)+'*/ }'};
const task={kind:'task',taskId:'ui-fixture'},msg={roomId:f.saved,sentAt:new Date().toISOString()};
const p=await ctx.newPage();
await p.route('**/api/chat/rooms/'+f.saved+'/messages*',route=>route.fulfill({json:[
  {...msg,sequence:1,sender:f.names[0],text:'Original request',card:task},
  {...msg,sequence:2,sender:'littleloop',text:'Task reply',replyTo:1,card:task},
  {...msg,sequence:3,sender:'littleloop',text:'Source citation',card:{kind:'citation',source}}
]}));
await p.route('**/api/chat/bot/work/ui-fixture',route=>route.fulfill({json:{id:'ui-fixture',status:'running',provider:'littleloop',canApprove,execution:{approval:{nonce:'fixture-only',method:'file.read',parameters:{path:source.path}},changes:{[source.path]:'+ fixture change'},receipts:[{id:'fixture-test',exitCode:0,standardOutput:'fixture output '.repeat(30),standardError:''}],turns:[{status:'running',report:'fixture report',issues:[]}]}}}));
await p.goto(base+'/chat?room='+f.saved);await p.locator('.task').waitFor();assert.equal(await p.getByRole('button',{name:'允许本次',exact:true}).count(),canApprove?1:0);
assert.equal(await p.locator('[data-sequence="1"] .task').count(),0);
assert.equal(await p.locator('[data-sequence="2"] .task').count(),1);
assert.equal(await p.locator('.task').evaluate(e=>e.open),canApprove);
assert.equal(await p.locator('[data-sequence="3"] chat-bot-card details').count(),1);
for(const d of await p.locator('chat-bot-card details').all())await d.evaluate(e=>e.open=true);
const overflow=await p.locator('.timeline').evaluate(e=>e.scrollWidth>e.clientWidth+2);assert.equal(overflow,false);
await p.screenshot({path:`${out}/bot-${width}-${canApprove?'approver':'reader'}.png`});report.push({width,canApprove,overflow,mode:'synthetic rendering only; no approval sent'});await ctx.close();
}
}finally{fs.writeFileSync(out+'/bot-visual-result.json',JSON.stringify(report,null,2));await browser.close();}console.log('Bot rendering cases:',report.length)})().catch(e=>{console.error(e);process.exitCode=1});
