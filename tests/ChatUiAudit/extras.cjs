// Run from the repository root. See docs/42-chat-ui-audit.md.
const fs=require('node:fs'),assert=require('node:assert/strict');
const {chromium}=require(process.cwd() + '/data/workbench-browser/node_modules/playwright');
const fixture=JSON.parse(fs.readFileSync('data/chat-full-audit/fixtures.json'));
const pass=fs.readFileSync('data/chat-phase-a/chat.local.env','utf8').match(/^CHAT_TEST_PASSWORD=(.+)$/m)[1].trim();
const base=(process.env.CHAT_AUDIT_BASE || 'http://127.0.0.1:5193'),dir='data/chat-full-audit';
const result={surfaces:[],issues:[],errors:[]};
async function shot(p,name){await p.waitForTimeout(180);const issues=await p.evaluate(()=>[...document.querySelectorAll('.chat-shell,.details-panel,.timeline,.modal-card,.friends-panel,.panel,.drawer,.new-chat form,.channel-directory,.results-inline,.results,.message-form')].filter(e=>e.getBoundingClientRect().width&&(!e.closest('details')||e.closest('details').open)).filter(e=>e.scrollWidth>e.clientWidth+2).map(e=>({class:e.className,w:e.clientWidth,sw:e.scrollWidth})));if(issues.length)result.issues.push({name,issues});result.surfaces.push(name);await p.screenshot({path:dir+'/'+name+'.png'});}
(async()=>{const browser=await chromium.launch({channel:'chrome'});try{
for(const [width,height,lang,theme] of [[1440,900,'zh','light'],[390,844,'en','dark'],[320,568,'zh','light'],[667,375,'en','dark']]){
 const ctx=await browser.newContext({viewport:{width,height},locale:lang==='zh'?'zh-CN':'en-US'});
 await ctx.addInitScript(({lang,theme})=>localStorage.setItem('littleloop-chat-ui',JSON.stringify({language:lang,theme,scale:'large',animations:false})),{lang,theme});
 const p=await ctx.newPage();p.on('pageerror',e=>result.errors.push(e.message));await p.goto(base+'/chat');
 const key=`extra-${width}-${lang}`;await shot(p,key+'-login');
 const login=await ctx.request.post(base+'/api/chat/login',{data:{name:fixture.names[0],password:pass},headers:{'X-LittleLoop-Chat':'1'}});assert.equal(login.status(),200);
 await p.reload();await p.locator('.room-list .room').first().waitFor();
 const text=(zh,en)=>lang==='zh'?zh:en;
 await p.locator('.new-chat summary').click();await p.locator('#kind').selectOption('group');await shot(p,key+'-new-group');await p.keyboard.press('Escape');
 await p.locator('#search').fill('UI_AUDIT');await p.locator('.search-submit').click();await p.locator('.search-result').first().waitFor();await shot(p,key+'-global-search');
 await p.locator('.search-result').first().click();await p.locator('.message').first().waitFor();
 await p.getByLabel(text('会话内搜索','Search in chat'),{exact:true}).click();await p.locator('#room-search').fill('UI_AUDIT');await p.locator('.search-panel button').click();await p.locator('.results-inline button').first().waitFor();await shot(p,key+'-room-search');
 await p.goto(base+'/chat?room='+fixture.saved);await p.locator('.message').first().waitFor();
 for(const [zh,en,suffix] of [['回复','Reply','reply'],['编辑','Edit','edit'],['转发','Forward','forward'],['删除','Delete','delete']]){
  await p.locator('.message-menu summary').first().scrollIntoViewIfNeeded();await p.waitForTimeout(350);await p.locator('.message-menu summary').first().click();await p.locator('.message-menu[open]').getByRole('button',{name:text(zh,en),exact:true}).click();await shot(p,key+'-'+suffix);
  const cancel={reply:['取消回复','Cancel reply'],edit:['取消编辑','Cancel edit'],forward:['取消转发','Cancel forward'],delete:['取消删除','Cancel deletion']}[suffix];
  if(suffix==='reply') await p.locator('.composer-context button').click();else await p.locator('.message-form button').last().click();
 }
 await p.goto(base+'/chat');await p.locator('.room-list .room').first().waitFor();
 const drawer=async()=>{await p.getByLabel(text('主菜单','Main menu'),{exact:true}).click();};
 await drawer();await shot(p,key+'-drawer');await p.locator('.drawer-item').filter({hasText:text('好友','Friends')}).click();await shot(p,key+'-friends');await p.keyboard.press('Escape');
 await drawer();await p.locator('.drawer-item').filter({hasText:text('浏览频道','Browse channels')}).click();await p.locator('.channel-entry').first().waitFor();await shot(p,key+'-directory');
 await drawer();await p.locator('.drawer-item').filter({hasText:text('新建频道','New channel')}).click();await shot(p,key+'-new-channel');await p.keyboard.press('Escape');
 await p.goto(base+'/chat?room='+fixture.group);await p.locator('.message').first().waitFor();await p.locator('.header-menu summary').click();await p.getByRole('button',{name:text('新建话题','New topic'),exact:true}).click();await shot(p,key+'-new-topic');await p.keyboard.press('Escape');
 await p.goto(base+'/chat');await p.locator('.room-list .room').first().waitFor();await drawer();await p.locator('.drawer-item').filter({hasText:text('设置','Settings')}).click();
 await shot(p,key+'-settings');
 const count=await p.locator('chat-settings .row').count();
 for(let i=0;i<count;i++){await p.locator('chat-settings .row').nth(i).click();await shot(p,key+'-settings-'+i);await p.locator('chat-settings header button').first().click();}
 await ctx.close();console.log('extras verified',key);
}
}finally{fs.writeFileSync(dir+'/extras-result.json',JSON.stringify(result,null,2));await browser.close();}assert.deepEqual(result.issues,[]);assert.deepEqual(result.errors,[]);console.log(JSON.stringify(result));})().catch(e=>{console.error(e);process.exitCode=1});
