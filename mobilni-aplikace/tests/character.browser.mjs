// Isolated consumer regression: all account/media/AI responses are synthetic.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, stat, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-character-browser');
await mkdir(output, {recursive:true});
const media = await readFile(new URL('../src/assets/brand/game/boss.webp', import.meta.url));
const mime = {'.html':'text/html','.js':'text/javascript','.css':'text/css','.woff2':'font/woff2','.webp':'image/webp','.png':'image/png','.svg':'image/svg+xml'};
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    let file = path.resolve(dist, '.' + url.pathname);
    if (!file.startsWith(dist)) { response.writeHead(403).end(); return; }
    try { if (!(await stat(file)).isFile()) file=path.join(dist,'index.html'); } catch { file=path.join(dist,'index.html'); }
    response.writeHead(200, {'Content-Type':mime[path.extname(file)] ?? 'application/octet-stream'}).end(await readFile(file));
  } catch { response.writeHead(500).end(); }
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? {executablePath:process.env.PLAYWRIGHT_EXECUTABLE_PATH} : {})});
const errors = [];
let captures=0;
try {
  for (const width of [320,390,1280]) {
    const context = await browser.newContext({viewport:{width,height:900},reducedMotion:'reduce'});
    const page=await context.newPage(); page.setDefaultTimeout(10000);
    page.on('pageerror',e=>errors.push(e.message));
    let consent=true, status='failed', retryCount=0;
    const requests=[];
    const joke='Tolik temné moci. A dovolenou ti stejně musí podepsat císař.';
    const profile={profileId:'fixture-profile',revision:1,userInput:{name:'Darth Vader',world:'Star Wars'},status:'ready'};
    await context.route('**/*',async route=>{
      const req=route.request(),url=new URL(req.url());
      if(url.origin!==origin)return route.abort();
      const json=(value,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(value)});
      if(!url.pathname.startsWith('/app/'))return route.continue();
      requests.push({path:url.pathname,method:req.method()});
      if(url.pathname==='/app/v1/session')return json({enabled:true,account:{id:'fixture-owner',displayName:'Host',roles:['Customer'],openAiConsent:consent},csrfToken:'fixture'});
      if(url.pathname==='/app/v1/account/ai-consent'){assert.equal(req.postDataJSON().openAiConsent,true);consent=true;return route.fulfill({status:204});}
      if(url.pathname==='/app/v1/launch')return json({allowed:true});
      if(url.pathname==='/app/v1/customer/access')return json({state:'active',accessRevision:1,groupId:'fixture-group'});
      if(url.pathname==='/app/v1/customer/character')return json({profile});
      if(url.pathname==='/app/v1/prelaunch/character')return json({character:{profileId:profile.profileId,revision:1,name:'Darth Vader',universe:'Star Wars',status:'ready'},artwork:status==='missing'?null:{status,welcome:{joke,identity:'Rytíř, jehož kariéra nabrala temný obrat.',teaser:''}}});
      if(url.pathname==='/app/v1/prelaunch/character/retry'){
        assert.equal(req.method(),'POST');assert.deepEqual(req.postDataJSON(),{expectedProfileId:'fixture-profile',expectedRevision:1});retryCount++;status='ready';return json({});
      }
      if(url.pathname==='/app/v1/prelaunch/character/portrait')return route.fulfill({contentType:'image/webp',body:media});
      if(url.pathname==='/app/v1/customer/wand')return json({hasCharacter:true,wand:null,tier:0});
      if(url.pathname==='/app/v1/game')return json({player:{id:'fixture-owner',displayName:'Host',xp:0,matchesPlayed:0,tacticalUnlocked:false,yoloUnlocked:false},opponents:[],incoming:[],active:[],history:[]});
      if(url.pathname==='/app/v1/customer/sessions')return json({items:[]});
      if(url.pathname==='/app/v1/customer/completion-updates')return json({items:[]});
      return json({});
    });
    const fits=async label=>{
      await page.evaluate(()=>document.fonts.ready);
      assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false,label+' overflow');
      await page.screenshot({path:path.join(output,`${label}-${width}.png`),fullPage:true});captures++;
    };
    await page.goto(origin+'/me/character');
    await page.getByText(joke,{exact:true}).waitFor();
    assert.equal(await page.getByRole('button',{name:'Vytvořit novou postavu',exact:true}).count(),0);
    assert.equal(await page.getByText('Upravit a upřesnit postavu',{exact:true}).count(),0);
    assert.equal(await page.locator('body').innerText().then(text=>text.includes('OpenAI')),false);
    assert.equal(await page.getByRole('checkbox').count(),0,'registration consent is not asked again');
    await fits('failed-portrait-with-joke');
    await page.locator('.private-character-avatar').getByRole('button',{name:'Zkusit znovu',exact:true}).click();
    await page.locator('.private-character-avatar > img[src^="blob:"]').waitFor();
    assert.equal(retryCount,1);
    assert.equal(requests.filter(r=>r.method==='PUT').length,0,'retry never edits or replaces character');
    await fits('recovered-portrait');
    status='missing'; await page.reload();
    await page.getByRole('button',{name:'Připravit portrét',exact:true}).click();
    await page.locator('.private-character-avatar > img[src^="blob:"]').waitFor();
    assert.equal(retryCount,2);
    await page.goto(origin+'/play');
    await page.getByRole('heading',{name:'Nejdřív vlastní hůlku.',exact:true}).waitFor();
    await page.getByRole('link',{name:'Vstoupit do kufrovy dílny',exact:true}).waitFor();
    assert.equal(await page.getByRole('button',{name:'Potvrdit tah',exact:true}).count(),0);
    await fits('first-wand-required');
    consent=false; await page.reload();
    await page.getByRole('heading',{name:'Dokonči nastavení účtu',exact:true}).waitFor();
    assert.equal(await page.getByRole('button',{name:'Potvrdit a pokračovat',exact:true}).isDisabled(),true);
    await page.getByRole('checkbox').check();
    await page.getByRole('button',{name:'Potvrdit a pokračovat',exact:true}).click();
    await page.getByRole('heading',{name:'Nejdřív vlastní hůlku.',exact:true}).waitFor();
    assert.equal(await page.locator('body').innerText().then(text=>text.includes('OpenAI')),false);
    if(width===390){await page.evaluate(()=>document.documentElement.style.fontSize='200%');await fits('first-wand-text200');}
    await context.close();
  }
  assert.deepEqual(errors,[]);
  console.log(`Character browser checks passed: ${captures} captures, 320/390/1280px, portrait generation retry, stable joke, single-character controls, first-wand gate and legacy consent.`);
} finally {await browser.close();await new Promise(resolve=>server.close(resolve));}
