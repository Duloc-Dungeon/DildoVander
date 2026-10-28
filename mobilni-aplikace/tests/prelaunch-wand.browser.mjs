// Synthetic loopback regression for the real reveal -> workshop -> waiting UI.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';
import { createWorkshopFixtureServer } from './fixtures/wand-workshop.mjs';

const { server, origin, requests } = await createWorkshopFixtureServer();
const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'dildovander-prelaunch-wand');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const errors = [];
const failedResponses = [];
let captures = 0;
try {
  for (const width of [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce' });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('response', response => { if (response.status() >= 400) failedResponses.push({ status: response.status(), url: response.url() }); });
    await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
    const overflow = () => page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
    await page.goto(`${origin}/__workshop/prelaunch`);
    await page.locator('.portrait-stage[data-curtain="open"]').waitFor();
    await page.getByRole('button', { name: 'Vytvořit hůlku', exact: true }).waitFor();
    assert.equal(requests.filter(request => request.method !== 'GET').length, 0, 'reveal and entry only read state');
    assert.equal(await overflow(), false, `${width}: reveal fits`);
    await page.screenshot({ path: path.join(output, `reveal-${width}.png`), fullPage: true }); captures++;
    await page.getByRole('button', { name: 'Vytvořit hůlku', exact: true }).focus();
    await page.keyboard.press('Enter');
    await page.getByRole('heading', { name: 'Kufrova dílna', exact: true }).waitFor();
    assert.equal(await page.locator('.launch-card').isVisible(), false, 'portrait yields to workshop');
    assert.equal(await page.locator('#workshop-title').evaluate(element => element === document.activeElement), true);
    assert.equal(await page.locator('a[href="/play"]').count(), 0, 'prelaunch workshop stays outside the game');
    await page.getByRole('button', { name: 'Raději psát', exact: true }).click();
    await page.getByLabel('Tvoje sdělení kufru').fill('Hůlka má zachránit cestu přes mlhu.');
    await page.getByRole('button', { name: 'Odeslat', exact: true }).click();
    await page.getByRole('heading', { name: /Kufr si pamatuje/ }).waitFor();
    assert.equal(await page.locator('a[href="/play"]').count(), 0, 'an unfinished workshop also stays outside the game');
    assert.equal(await overflow(), false, `${width}: workshop fits`);
    if (width === 390) {
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      assert.equal(await overflow(), false, '200% text fits');
      await page.screenshot({ path: path.join(output, 'text-200-percent.png'), fullPage: true }); captures++;
      await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
    }
    await page.screenshot({ path: path.join(output, `workshop-${width}.png`), fullPage: true }); captures++;
    await page.getByRole('button', { name: 'Zpět k odpočtu', exact: true }).click();
    await page.getByRole('button', { name: 'Pokračovat v tvorbě hůlky', exact: true }).waitFor();
    await page.reload();
    await page.getByRole('button', { name: 'Pokračovat v tvorbě hůlky', exact: true }).click();
    await page.getByRole('button', { name: 'Raději psát', exact: true }).click();
    await page.getByRole('button', { name: 'To stačí, vyrob hůlku', exact: true }).click();
    await page.getByRole('button', { name: 'Počkat na začátek večera', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
    await page.locator('.trunk-stage__wand').waitFor();
    await page.waitForFunction(() => document.querySelector('.trunk-stage__wand')?.naturalWidth > 0);
    assert.equal(await overflow(), false, `${width}: saved wand fits`);
    await page.getByRole('button', { name: 'Počkat na začátek večera', exact: true }).click();
    await page.getByText('Hůlka je uložená. Ollivander Industries™ děkuje za důvěru. Považuje ji za odvážnou.', { exact: true }).waitFor();
    assert.equal(new URL(page.url()).pathname, '/launch');
    await page.reload();
    await page.getByRole('button', { name: 'Prohlédnout hůlku', exact: true }).waitFor();
    assert.equal(requests.filter(request => request.path.endsWith('/sessions') && request.method === 'POST').length, 1, 'reload/resume does not duplicate manufacture');
    await page.screenshot({ path: path.join(output, `waiting-${width}.png`), fullPage: true }); captures++;
    await context.close();
  }
  assert.deepEqual(errors, []);
  assert.deepEqual(failedResponses, []);
  console.log(JSON.stringify({ result: 'passed', widths: [320, 390, 1280], textScale: '200%', reducedMotion: true, captures, output, liveProviderCalls: 0 }));
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
