// Run from the repository root. See docs/42-chat-ui-audit.md.
// Synthetic responses exercise rendering without running or approving development work.
const fs=require('node:fs'),assert=require('node:assert/strict');const {chromium}=require(process.cwd() + '/data/workbench-browser/node_modules/playwright');
const f=JSON.parse(fs.readFileSync('data/chat-full-audit/fixtures.json')),pass=fs.readFileSync('data/chat-phase-a/chat.local.env','utf8').match(/^CHAT_TEST_PASSWORD=(.+)$/m)[1].trim();const base=(process.env.CHAT_AUDIT_BASE || 'http://127.0.0.1:5193');
(async()=>{const browser=await chromium.launch({channel:'chrome'});const report=[];try{
for(const width of [1440,390,320])for(const canApprove of [false,true]){
const ctx=await browser.newContext({viewport:{width,height:844},locale:'zh-CN'});await ctx.addInitScript(()=>localStorage.setItem('littleloop-chat-ui',JSON.stringify({language:'zh',theme:'dark',animations:false})));
await ctx.request.post(base+'/api/chat/login',{data:{name:f.names[0],password:pass},headers:{'X-LittleLoop-Chat':'1'}});
const source={path:'src/'+ 'LongSourcePath/'.repeat(8)+'Example.cs',start:1,end:12,sha256:'a'.repeat(64),text:'public class Example { /* '+ 'source '.repeat(60)+'*/ }'};
const p=await ctx.newPage();await p.route('**/api/chat/rooms/'+f.saved+'/messages*',route=>route.fulfill({json:[{roomId:f.saved,sequence:1,sender:'littleloop',text:'Synthetic UI fixture',sentAt:new Date().toISOString(),card:{kind:'task',taskId:'ui-fixture',source}}]}));
await p.route('**/api/chat/bot/work/ui-fixture',route=>route.fulfill({json:{id:'ui-fixture',status:'running',provider:'littleloop',canApprove,execution:{approval:{nonce:'fixture-only',method:'file.read',parameters:{path:source.path}},changes:{[source.path]:'+ fixture change'},receipts:[{id:'fixture-test',exitCode:0,standardOutput:'fixture output '.repeat(30),standardError:''}],turns:[{status:'running',report:'fixture report',issues:[]}]}}}));
await p.goto(base+'/chat?room='+f.saved);await p.locator('.task').waitFor();assert.equal(await p.getByRole('button',{name:'允许本次',exact:true}).count(),canApprove?1:0);
for(const d of await p.locator('chat-bot-card details').all())await d.evaluate(e=>e.open=true);
const overflow=await p.locator('.timeline').evaluate(e=>e.scrollWidth>e.clientWidth+2);assert.equal(overflow,false);
await p.screenshot({path:`data/chat-full-audit/bot-${width}-${canApprove?'approver':'reader'}.png`});report.push({width,canApprove,overflow,mode:'synthetic rendering only; no approval sent'});await ctx.close();
}
}finally{fs.writeFileSync('data/chat-full-audit/bot-visual-result.json',JSON.stringify(report,null,2));await browser.close();}console.log('Bot rendering cases:',report.length)})().catch(e=>{console.error(e);process.exitCode=1});
