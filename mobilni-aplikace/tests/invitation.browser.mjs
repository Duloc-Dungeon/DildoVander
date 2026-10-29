// Offline, repeatable mobile visual/interaction evidence. No provider or production calls.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, mkdir, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
const root = fileURLToPath(new URL('../dist/', import.meta.url));
const output = process.env.APP_SCREENSHOTS ?? fileURLToPath(new URL('../../.codex/artifacts/wand-invitation/', import.meta.url));
await mkdir(output, {recursive:true});
const mime={'.html':'text/html','.js':'text/javascript','.css':'text/css','.ttf':'font/ttf','.png':'image/png','.webp':'image/webp','.svg':'image/svg+xml'};
const server=createServer(async(req,res)=>{
  const url=new URL(req.url,'http://localhost');
  if(/^\/(app\/v1|v1|guest)\//.test(url.pathname)){res.writeHead(404,{'Content-Type':'application/json'}).end('{}');return;}
  let file=path.resolve(root,decodeURIComponent(url.pathname).replace(/^\//,'')||'index.html');
  if(!file.startsWith(path.resolve(root)+path.sep))file=path.join(root,'index.html');
  try{if(!(await stat(file)).isFile())file=path.join(root,'index.html');}catch{file=path.join(root,'index.html');}
  res.writeHead(200,{'Content-Type':mime[path.extname(file)]??'application/octet-stream','Cache-Control':'no-store'}).end(await readFile(file));
});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const origin=`http://127.0.0.1:${server.address().port}`;
const portrait=await readFile(new URL('./fixtures/characters/matylda.webp',import.meta.url));
const account={id:'visual-guest',displayName:'Darth Vader',roles:['Customer'],openAiConsent:true};
let signedIn=true,ready=false,revision=1,long=false,requests=0;
const character=()=>({profileId:'visual-profile',revision,name:long?'Velmi dlouhé jméno postavy '.repeat(4).slice(0,80):'Darth Vader',universe:long?'Velmi rozsáhlé fiktivní univerzum '.repeat(6).slice(0,160):'Star Wars',status:'ready'});
const welcome=()=>long?{joke:'J'.repeat(120),identity:'I'.repeat(28),teaser:'T'.repeat(100)}:{joke:'Tolik temné moci. A dovolenou ti stejně musí podepsat císař.',identity:'Anakin Skywalker, někdejší rytíř Jedi, který přešel na temnou stranu a sloužil císaři.',teaser:'Možná tě čeká nesouhlas, který se nevyřídí rozkazem.'};
const errors=[];
async function fixtures(context){
  await context.route('**/*',async route=>{
    const req=route.request(),url=new URL(req.url());
    if(url.origin!==origin)return route.abort();
    const json=value=>route.fulfill({contentType:'application/json',body:JSON.stringify(value)});
    if(url.pathname==='/app/v1/session')return json({enabled:true,account:signedIn?account:null,csrfToken:'fixture-only',origin});
    if(url.pathname==='/app/v1/customer/access')return json({state:'active',groupId:'fixture-beta',groupName:'Interní beta',joinedAt:Date.now(),accessRevision:1,recruitmentOpen:false,launchAt:Date.now()+86400000*4});
    if(url.pathname==='/app/v1/launch')return json({allowed:false,audience:'production',launchAt:Date.now()+86400000*4,serverNow:Date.now(),teaser:'DO NOT SHOW HOST NOTE',initialLoadingMessage:'DO NOT SHOW INTERNAL TEXT'});
    if(url.pathname==='/app/v1/prelaunch/character')return json({character:character(),artwork:{status:ready?'ready':'running',welcome:welcome()}});
    if(url.pathname==='/app/v1/prelaunch/character/portrait'){requests++;assert.equal(url.searchParams.get('profileId'),'visual-profile');assert.equal(Number(url.searchParams.get('revision')),revision);return route.fulfill({contentType:'image/webp',body:portrait});}
    if(url.pathname.startsWith('/app/v1/'))return json({});
    return route.continue();
  });
}
async function enterReception(page) {
  await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  await page.locator('.portrait-stage').waitFor();
}
async function capture(page,name,details){
  await page.evaluate(()=>document.fonts.ready);
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,name+' overflow');
  const clipped=await page.evaluate(()=>[...document.querySelectorAll('input,select,button,a')].filter(el=>{const r=el.getBoundingClientRect();return r.width>0&&(r.left<-.5||r.right>innerWidth+.5);}).map(el=>el.tagName+':'+el.textContent?.trim().slice(0,40)));
  assert.deepEqual(clipped,[],name+' clipped controls');
  const shortTargets=await page.evaluate(()=>[...document.querySelectorAll('.signin-help a,.signin-options button,.signin-wordmark,.launch-wordmark,.launch-footer a,.launch-account-options summary,.consent-fieldset label,.consent-fieldset summary')].filter(el=>{const r=el.getBoundingClientRect();return r.width>0&&r.height<43.5;}).map(el=>el.textContent?.trim().slice(0,40)));
  assert.deepEqual(shortTargets,[],name+' touch targets below44px');
  await page.screenshot({path:path.join(output,name+'.png'),fullPage:true});
  records.push({file:name+'.png',...details,mode:'fixture',role:signedIn?'Customer':'anonymous'});
}
const records=[];let browser;
try{
  browser=await chromium.launch({...(process.env.PLAYWRIGHT_EXECUTABLE_PATH?{executablePath:process.env.PLAYWRIGHT_EXECUTABLE_PATH}:{}),...(process.env.PLAYWRIGHT_CHANNEL?{channel:process.env.PLAYWRIGHT_CHANNEL}:{})});
  for(const theme of ['lord-voldemort','olivander']){
    for(const [width,height] of [[320,568],[375,812],[390,844],[430,932]]){
      const context=await browser.newContext({viewport:{width,height},deviceScaleFactor:1,isMobile:true,hasTouch:true,reducedMotion:'reduce'});
      await fixtures(context);await context.addInitScript(theme=>localStorage.setItem('dildovander.theme',theme),theme);
      const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
      signedIn=false;
      await page.goto(origin+'/signin#invite=fixture-code-not-live-1234567890');
      await page.getByLabel('Jméno postavy').waitFor({timeout:10000}).catch(async error=>{await page.screenshot({path:path.join(output,'failure-signin.png'),fullPage:true});console.log(page.url(),await page.locator('body').innerText(),errors);throw error;});
      assert.equal(await page.getByRole('checkbox').isChecked(),false);
      assert.equal(await page.getByRole('button',{name:'Mám kód obnovy',exact:true}).count(),0);
      assert.equal(await page.getByText('Kód obnovy',{exact:true}).count(),0);
      const title = page.locator('#sign-in-title');
      await title.focus();
      assert.equal(await title.evaluate(element => getComputedStyle(element).outlineStyle), 'none', 'signup heading has no focus border');
      await capture(page,`signin-invite-${width}x${height}-${theme}-cs`,{route:'/signin',state:'invitation',viewport:`${width}x${height}`,theme,language:'cs'});
      if(width===390){await page.getByLabel('Jméno postavy').focus();await page.setViewportSize({width,height:440});await capture(page,`signin-keyboard-${width}x440-${theme}-cs`,{route:'/signin',state:'focused input; keyboard-sized viewport, not native keyboard',viewport:`${width}x440`,theme,language:'cs'});await page.setViewportSize({width,height});}
      signedIn=true;ready=false;long=false;
      await page.goto(origin+'/launch');await enterReception(page);
      assert.equal(await page.getByText('DO NOT SHOW HOST NOTE').count(),0);
      const frame=await page.locator('.portrait-stage').boundingBox();assert.ok(Math.abs(frame.width/frame.height-.8)<.001);
      assert.equal(await page.locator('.prelaunch-wand').count(),0,'the workshop waits for the portrait');
      await capture(page,`launch-waiting-${width}x${height}-${theme}-cs`,{route:'/launch',state:'saved, image pending',viewport:`${width}x${height}`,theme,language:'cs'});
      ready=true;await page.reload();await enterReception(page);await page.locator('.portrait-stage').scrollIntoViewIfNeeded();await page.locator('[data-curtain="open"]').waitFor();await page.locator('.launch-final').waitFor();
      await page.evaluate(()=>window.scrollTo(0,0));
      await page.getByRole('button',{name:'Vytvořit hůlku',exact:true}).waitFor();
      const workshop=await page.locator('.prelaunch-wand').boundingBox();
      const countdown=await page.locator('.countdown').boundingBox();
      assert.ok(workshop && countdown && countdown.y>=workshop.y+workshop.height,
        'the countdown stays below the newly available workshop without overlapping it');
      await capture(page,`launch-revealed-${width}x${height}-${theme}-cs`,{route:'/launch',state:'revealed card',viewport:`${width}x${height}`,theme,language:'cs'});
      long=true;revision++;await page.reload();await enterReception(page);await page.locator('.portrait-stage').scrollIntoViewIfNeeded();await page.locator('.launch-final').waitFor();
      await page.evaluate(()=>{document.documentElement.style.fontSize='125%';window.scrollTo(0,0);});
      await capture(page,`launch-long-250chars-text125-${width}x${height}-${theme}-cs`,{route:'/launch',state:'80-char name,160-char universe,250-char card,125% text',viewport:`${width}x${height}`,theme,language:'cs'});
      const jokeBox = await page.locator('.launch-joke').evaluate(el => ({ scroll: el.scrollHeight, client: el.clientHeight, lineHeight: getComputedStyle(el).lineHeight, overflow: getComputedStyle(el).overflow }));
      assert.ok(jokeBox.scroll <= jokeBox.client, `Joke overflow at ${width}px: ${JSON.stringify(jokeBox)}`);
      assert.equal(await page.getByRole('combobox',{name:'Jazyk rozhraní'}).count(),0);
      assert.equal(await page.locator('html').getAttribute('lang'),'cs');
      await context.close();
    }
  }
  // A short real-motion recording, plus quick interruption/reload/seen evidence.
  ready=true;long=false;signedIn=true;revision=100;
  const context=await browser.newContext({viewport:{width:390,height:844},isMobile:true,hasTouch:true,reducedMotion:'no-preference',recordVideo:{dir:output,size:{width:390,height:844}}});
  await fixtures(context);const page=await context.newPage();const video=page.video();
  await page.goto(origin+'/launch');await enterReception(page);await page.locator('.portrait-stage').scrollIntoViewIfNeeded();await page.locator('[data-curtain="opening"]').waitFor();
  // Add test-only scroll runway: this compact card otherwise fits too closely to scroll completely off-screen.
  await page.evaluate(()=>{const runway=document.createElement('div');runway.id='test-scroll-runway';runway.style.height='100vh';document.body.append(runway);});
  await page.waitForTimeout(250);
  await page.evaluate(()=>window.scrollTo(0,document.documentElement.scrollHeight));
  await page.locator('[data-curtain="closed"]').waitFor();
  assert.equal(await page.evaluate(()=>Object.keys(localStorage).filter(key=>key.startsWith('wand.portrait-seen')).length),0);
  await page.locator('.portrait-stage').scrollIntoViewIfNeeded();
  await page.evaluate(()=>document.getElementById('test-scroll-runway')?.remove());
  await page.locator('[data-curtain="opening"]').waitFor();
  const opening=await page.locator('.mirror-mist').evaluate(el=>parseFloat(getComputedStyle(el).getPropertyValue('--mirror-opening')));
  assert.ok(opening<25,'quick re-entry restarts the mirror mist near its covered state');
  await page.waitForTimeout(350);await page.reload();await enterReception(page);
  await page.locator('.portrait-stage').scrollIntoViewIfNeeded();await page.locator('[data-curtain="opening"]').waitFor();
  await capture(page,'curtain-replayed-after-reload-390x844-lord-voldemort-cs',{route:'/launch',state:'interrupted reveal replay, same revision/image',viewport:'390x844',theme:'lord-voldemort',language:'cs'});
  await page.locator('[data-curtain="open"]').waitFor();
  await capture(page,'curtain-completed-390x844-lord-voldemort-cs',{route:'/launch',state:'completed opening',viewport:'390x844',theme:'lord-voldemort',language:'cs'});
  const callsBefore= requests;
  await page.reload();await enterReception(page);await page.locator('[data-curtain="open"]').waitFor();
  assert.equal(await page.locator('[data-curtain="opening"]').count(),0,'seen revision opens immediately');
  assert.equal(requests,callsBefore+1,'reload only refetches authorised bytes; no generation request');
  revision++;await page.reload();await enterReception(page);await page.locator('.portrait-stage').scrollIntoViewIfNeeded();await page.locator('[data-curtain="opening"]').waitFor();
  await page.locator('[data-curtain="open"]').waitFor();
  await context.close();await video.saveAs(path.join(output,'curtain-interrupt-return-revision.webm'));
  assert.deepEqual(errors,[]);
  await writeFile(path.join(output,'evidence.json'),JSON.stringify({origin:'loopback fixture only',records,checks:['four smartphone viewports, both themes','reduced motion','250-char unbroken text and125% scale','focused input at keyboard-sized viewport','4:5 frame and stable countdown','real opening interruption, reload replay, seen revision, new revision','authorised bytes refetch only, no generation'],video:'curtain-interrupt-return-revision.webm',requests,consoleErrors:errors},null,2));
  console.log(JSON.stringify({screenshots:records.length,output,video:'curtain-interrupt-return-revision.webm',requests,errors}));
}finally{if(browser)await browser.close();await new Promise(r=>server.close(r));}
