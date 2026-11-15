// Run from the repository root. See docs/42-chat-ui-audit.md.
const fs = require('node:fs');
const assert = require('node:assert/strict');
const { chromium } = require(process.cwd() + '/data/workbench-browser/node_modules/playwright');
const env = Object.fromEntries(fs.readFileSync('data/chat-phase-a/chat.local.env','utf8').split(/\r?\n/).filter(s=>s.includes('=')).map(s=>{const i=s.indexOf('=');return [s.slice(0,i),s.slice(i+1)];}));
const base=(process.env.CHAT_AUDIT_BASE || 'http://127.0.0.1:5193'), dir='data/chat-full-audit';
const results={api:[],surfaces:[],issues:[],errors:[]};
async function api(ctx,path,method='GET',data,expected=200) {
 const r=await ctx.request.fetch(base+'/api/chat'+path,{method,data,headers:{'X-LittleLoop-Chat':'1'}});
 assert.equal(r.status(),expected,method+' '+path+' '+await r.text());
 results.api.push({path,method,status:r.status()});
 const text=await r.text();try{return JSON.parse(text)}catch{return text}
}
async function inspect(page,name,shot=true) {
 await page.waitForTimeout(160);
 const problems=await page.evaluate(()=>{
  const selectors=['.chat-shell','main','.conversation','.timeline','.details-panel','chat-channel-settings section','.new-chat form','.results','.results-inline','.channel-directory','chat-settings','.drawer','.friends-panel','.message-form','chat-comments section','.menu-sheet'];
  return [...document.querySelectorAll(selectors.join(','))].filter(e=>e.getBoundingClientRect().width && e.getBoundingClientRect().height && getComputedStyle(e).visibility!=='hidden' && !e.closest('details:not([open])') )
   .filter(e=>e.scrollWidth>e.clientWidth+2).map(e=>({element:e.tagName+'.'+e.className,width:e.clientWidth,scroll:e.scrollWidth}));
 });
 results.surfaces.push(name); if(problems.length) results.issues.push({name,problems});
 if(shot) await page.screenshot({path:dir+'/'+name+'.png'});
}
(async()=>{
 const browser=await chromium.launch({channel:'chrome'});
 try {
  const stamp=Date.now().toString().slice(-8), names=['owner','admin','member','muted','outside'].map(s=>'qa_'+s+'_'+stamp), contexts=[];
  for(const name of names){
   const ctx=await browser.newContext({viewport:{width:1440,height:900},locale:'zh-CN'});
   await api(ctx,'/register','POST',{name,password:env.CHAT_TEST_PASSWORD,invite:env.Chat__Invite},204);
   await api(ctx,'/login','POST',{name,password:env.CHAT_TEST_PASSWORD},200);
   await ctx.addInitScript(()=>localStorage.setItem('littleloop-chat-ui',JSON.stringify({language:'zh',theme:'light',animations:false})));
   contexts.push(ctx);
  }
  const [owner,admin,member,muted,outside]=contexts;
  const page=await owner.newPage();page.on('pageerror',e=>results.errors.push(e.message));
  await page.goto(base+'/chat'); await page.locator('.room-list').waitFor();
  await page.getByLabel('新建会话',{exact:true}).click();
  await page.locator('#kind').selectOption('group');await page.locator('#title').fill('审查群 '+stamp);
  await inspect(page,'create-group-optional');
  const created=page.waitForResponse(r=>r.url()===base+'/api/chat/rooms'&&r.request().method()==='POST');
  await page.getByRole('button',{name:'创建会话',exact:true}).click();
  const response=await created;assert.equal(response.status(),200);const group=(await response.json()).id;
  let members=await api(owner,`/rooms/${group}/members`);assert.equal(members.length,1);assert.ok(members[0].owner);
  await page.getByLabel('打开会话详情').click();await page.getByPlaceholder('成员用户名',{exact:true}).fill(names[1]);
  await page.getByRole('button',{name:'邀请成员',exact:true}).click();
  await page.locator('.member-copy small').filter({hasText:names[1]}).waitFor();
  for(const name of names.slice(2,4)) await api(owner,`/rooms/${group}/members`,'POST',{username:name,operation:'invite'},204);
  await api(owner,`/rooms/${group}/members`,'POST',{username:names[1],operation:'promote'},204);
  await api(owner,`/rooms/${group}/members`,'POST',{username:names[3],operation:'silence'},204);
  await api(member,`/rooms/${group}/members`,'POST',{username:names[4],operation:'invite'},403);
  await api(owner,'/rooms','POST',{type:'direct',title:'',members:[]},400);
  await api(owner,'/rooms','POST',{type:'group',title:' ',members:[]},400);
  const direct=(await api(owner,'/rooms','POST',{type:'direct',members:[names[2]],title:''})).id;
  const saved=(await api(owner,'/saved','POST',{})).id;
  const topic=(await api(owner,`/rooms/${group}/topics`,'POST',{title:'审查话题 '+stamp})).id;
  const channel=(await api(owner,'/channels','POST',{title:'频道长名称'+ '完整界面审查'.repeat(10)})).id;
  for(const ctx of [admin,member,muted]) await api(ctx,`/channels/${channel}/subscription`,'POST',{subscribed:true},204);
  await api(owner,`/channels/${channel}/admins/${names[1]}`,'PUT',{rights:1},204);
  await api(owner,`/channels/${channel}/discussion`,'PUT',{groupId:group},204);
  const post=async(ctx,room,text,extra={})=>api(ctx,`/rooms/${room}/messages`,'POST',{clientId:crypto.randomUUID(),text,...extra});
  for(const room of [direct,saved,topic,group]) await post(owner,room,'UI_AUDIT '+ '长文本LongUnbrokenText'.repeat(22));
  const message=await post(owner,channel,'UI_AUDIT 频道帖子');
  const discussion=await api(owner,`/channels/${channel}/posts/${message.sequence}/comments`);
  await post(member,group,'评论'+ '长评论LongComment'.repeat(25),{replyTo:discussion.rootSequence});
  await api(muted,`/rooms/${group}/messages`,'POST',{clientId:crypto.randomUUID(),text:'denied'},403);
  await api(member,`/rooms/${channel}/messages`,'POST',{clientId:crypto.randomUUID(),text:'denied'},403);
  await api(outside,`/rooms/${group}/messages`,'GET',undefined,404);
  await api(admin,`/channels/${channel}/audit`,'GET',undefined,403);
  fs.writeFileSync(dir+'/fixtures.json',JSON.stringify({names,group,direct,saved,topic,channel,post:message.sequence},null,2));
  const scenes=[['owner',owner,group],['group-admin',admin,group],['member',member,group],['muted',muted,group],['topic-owner',owner,topic],['topic-member',member,topic],['topic-muted',muted,topic],['channel-owner',owner,channel],['channel-publisher',admin,channel],['subscriber',member,channel],['direct',owner,direct],['saved',owner,saved]];
  await page.close();
  for(const [role,ctx,room] of scenes){
   const p=await ctx.newPage();p.on('pageerror',e=>results.errors.push(e.message));
   for(const [width,height] of [[1440,900],[1024,768],[390,844],[320,568]]) {
    await p.setViewportSize({width,height});await p.goto(base+'/chat?room='+room);await p.locator('.message').first().waitFor();
    await inspect(p,`${role}-${width}-conversation`);
    const canPost=!['muted','topic-muted','subscriber'].includes(role);
    assert.equal(await p.locator('.composer').count(),canPost?1:0,role+' composer');
    await p.locator('.message-menu summary').first().click();await inspect(p,`${role}-${width}-message-menu`);
    await p.locator('.message-menu summary').first().click();
    if(room===channel) {await p.getByRole('button',{name:'查看评论',exact:true}).first().click();await p.getByRole('region',{name:'帖子评论'}).waitFor();await p.waitForTimeout(200);await inspect(p,`${role}-${width}-comments`);}
    await p.getByLabel('打开会话详情').click();await p.locator('.member-copy').first().waitFor();
    await inspect(p,`${role}-${width}-details`);
    if(room===channel){
     assert.equal(await p.getByRole('button',{name:'管理记录',exact:true}).count(),role==='channel-owner'?1:0);
     if(role==='channel-owner') {
      await p.getByRole('button',{name:'管理记录',exact:true}).click();await inspect(p,`${role}-${width}-audit`);
      await p.getByRole('button',{name:'删除频道',exact:true}).click();await inspect(p,`${role}-${width}-delete-confirm`);
      await p.getByRole('button',{name:'取消',exact:true}).click();
     }
    }
   }
   await p.close();console.log('verified',role);
  }
 } finally {fs.writeFileSync(dir+'/result.json',JSON.stringify(results,null,2));await browser.close();}
 assert.deepEqual(results.issues, []); assert.deepEqual(results.errors, []);
 console.log(JSON.stringify({surfaces:results.surfaces.length,issues:results.issues,errors:results.errors}));
})().catch(e=>{console.error(e);process.exitCode=1});
