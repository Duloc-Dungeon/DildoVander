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
const character = { profileId: 'synthetic-profile', revision: 1, name: 'Sever', universe: 'Půlnoční přístav', status: 'ready' };
const welcome = { joke: 'Tvá pověst dorazila před tebou. Už jsme se jí omluvili.', identity: 'Sever z Půlnočního přístavu.', teaser: 'Večer se připravuje.' };
const loadingJoke = 'Portrét má zachytit tvoji povahu. Plátno požádalo o příplatek za riziko.';
try {
  for (const width of [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce' });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('response', response => { if (response.status() >= 400) failedResponses.push({ status: response.status(), url: response.url() }); });
    await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
    let portraitStatus = 'running';
    await page.route('**/app/v1/prelaunch/character', route => route.fulfill({ json: {
      character, artwork: { status: portraitStatus, loadingMessage: loadingJoke, welcome },
    } }));
    const overflow = () => page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
    await page.goto(`${origin}/__workshop/prelaunch`);
    await page.getByText(loadingJoke, { exact: true }).waitFor();
    assert.equal(await page.locator('[aria-current="step"]').innerText(), 'Portrét');
    assert.equal(await page.getByRole('button', { name: 'Vytvořit hůlku', exact: true }).count(), 0, 'wait for the portrait before offering the workshop');
    assert.equal(await overflow(), false, `${width}: preparation fits`);
    await page.screenshot({ path: path.join(output, `preparing-${width}.png`), fullPage: true }); captures++;
    portraitStatus = 'failed';
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.getByText('Portrét zatím chybí.', { exact: false }).waitFor();
    await page.getByRole('button', { name: 'Vytvořit hůlku', exact: true }).waitFor();
    assert.equal(await page.locator('[aria-current="step"]').innerText(), 'Hůlka');
    await page.getByText(welcome.joke, { exact: true }).waitFor();
    assert.equal(await overflow(), false, `${width}: missing portrait fits`);
    await page.screenshot({ path: path.join(output, `portrait-failed-${width}.png`), fullPage: true }); captures++;
    portraitStatus = 'ready';
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.locator('.portrait-stage').scrollIntoViewIfNeeded();
    await page.locator('.portrait-stage[data-curtain="open"]').waitFor();
    await page.getByRole('button', { name: 'Vytvořit hůlku', exact: true }).waitFor();
    await page.evaluate(() => window.scrollTo(0, 0));
    const prepareButton = page.getByRole('button', { name: 'Vytvořit hůlku', exact: true });
    const prepareBox = await prepareButton.boundingBox();
    assert(prepareBox && prepareBox.y + prepareBox.height <= 844, `${width}: prepare action is in the first viewport`);
    assert.equal(await page.locator('.launch-next-step').count(), 0, 'there is one state-aware wand action');
    await page.getByText(/Večer se otevře/).waitFor();
    assert.equal(requests.filter(request => request.method !== 'GET').length, 0, 'reveal and entry only read state');
    assert.equal(await overflow(), false, `${width}: reveal fits`);
    await page.screenshot({ path: path.join(output, `reveal-${width}.png`), fullPage: true }); captures++;
    await page.getByRole('button', { name: 'Vytvořit hůlku', exact: true }).focus();
    const focus = await prepareButton.evaluate(button => ({
      color: getComputedStyle(button).outlineColor,
      width: getComputedStyle(button).outlineWidth,
      panel: getComputedStyle(button.closest('.prelaunch-wand')).backgroundColor,
      foreground: getComputedStyle(button).color,
      background: getComputedStyle(button).backgroundColor,
      minHeight: getComputedStyle(button).minHeight,
    }));
    assert.equal(focus.color, 'rgb(16, 36, 25)', 'handoff focus uses the dark mint foreground');
    assert.notEqual(focus.color, focus.panel, 'keyboard focus remains visible on the mint handoff');
    assert.equal(focus.width, '3px');
    assert.equal(focus.foreground, 'rgb(184, 243, 212)', 'handoff keeps mint text');
    assert.equal(focus.background, 'rgb(16, 36, 25)', 'handoff keeps its dark action on the mint panel');
    assert.equal(focus.minHeight, '52px', 'handoff retains its local control size');
    await page.keyboard.press('Enter');
    await page.getByRole('heading', { name: 'Kufrova dílna', exact: true }).waitFor();
    assert.equal(await page.locator('.launch-card').isVisible(), false, 'portrait yields to workshop');
    assert.equal(await page.locator('#workshop-title').evaluate(element => element === document.activeElement), true);
    assert.equal(await page.locator('a[href="/play"]').count(), 0, 'prelaunch workshop stays outside the game');
    await page.getByRole('button', { name: 'Raději psát', exact: true }).click();
    assert.equal(await page.getByText('Mikrofon je vypnutý.', { exact: true }).count(), 0, 'text mode does not announce an unused microphone');
    assert.equal(await page.getByLabel('Tvoje sdělení kufru').getAttribute('aria-describedby'), 'trunk-answer-hint');
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
    assert.equal(await page.locator('.trunk-stage').isVisible(), false, 'failed artwork replaces the hidden image with recovery controls');
    assert.equal(await page.getByText('Ilustrace se nezdařila.', { exact: true }).count(), 1, 'artwork failure has one recovery message');
    assert.equal(await page.locator('#workshop-title').evaluate(element => element === document.activeElement), true, 'completion moves focus to the saved wand');
    const artworkFrame = () => page.locator('.trunk-workshop__presentation').evaluate(element => {
      const { width, height } = element.getBoundingClientRect();
      return { width, height };
    });
    const failedArtworkFrame = await artworkFrame();
    await page.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
    await page.locator('.trunk-stage__wand').waitFor();
    await page.waitForFunction(() => document.querySelector('.trunk-stage__wand')?.naturalWidth > 0);
    assert.deepEqual(await artworkFrame(), failedArtworkFrame, `${width}: artwork recovery keeps the same frame`);
    assert.equal(await page.getByRole('button', { name: 'Přehrát verdikt kufru', exact: true }).count(), 0);
    await page.evaluate(() => window.scrollTo(0, 0));
    const returnBox = await page.getByRole('button', { name: 'Počkat na začátek večera', exact: true }).boundingBox();
    assert(returnBox && returnBox.y + returnBox.height <= 844, `${width}: saved result includes its next action in the first viewport`);
    await page.screenshot({ path: path.join(output, `result-${width}.png`), fullPage: true }); captures++;
    assert.equal(await overflow(), false, `${width}: saved wand fits`);
    await page.getByRole('button', { name: 'Počkat na začátek večera', exact: true }).click();
    await page.getByText('Hůlka je uložená. Ollivander Industries™ děkuje za důvěru. Považuje ji za odvážnou.', { exact: true }).waitFor();
    await page.getByRole('heading', { name: 'Vítej v čekárně.', exact: true }).waitFor();
    await page.getByText(welcome.joke, { exact: true }).waitFor();
    assert.equal(await page.locator('[aria-current="step"]').innerText(), 'Čekárna');
    await page.locator('.prelaunch-wand-keepsake').getByText('Tichý přístav', { exact: false }).waitFor();
    assert.equal(new URL(page.url()).pathname, '/launch');
    await page.reload();
    await page.getByRole('button', { name: 'Prohlédnout hůlku', exact: true }).waitFor();
    assert.equal(await page.locator('[aria-current="step"]').innerText(), 'Čekárna', 'server restores the completed preparation after reload');
    assert.equal(requests.filter(request => request.path.endsWith('/sessions') && request.method === 'POST').length, 1, 'reload/resume does not duplicate manufacture');
    await page.screenshot({ path: path.join(output, `waiting-${width}.png`), fullPage: true }); captures++;
    if (width === 390) {
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      assert.equal(await overflow(), false, 'waiting room fits at 200% text');
      await page.screenshot({ path: path.join(output, 'waiting-text-200-percent.png'), fullPage: true }); captures++;
      await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
    }
    await page.getByRole('button', { name: 'Prohlédnout hůlku', exact: true }).click();
    await page.getByRole('heading', { name: 'Tichý přístav', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Počkat na začátek večera', exact: true }).click();
    assert.equal(requests.filter(request => request.path.endsWith('/sessions') && request.method === 'POST').length, 1, 'viewing the saved wand does not manufacture a second one');
    await context.close();
  }
  assert.deepEqual(errors, []);
  assert.deepEqual(failedResponses, []);
  console.log(JSON.stringify({ result: 'passed', widths: [320, 390, 1280], textScale: '200%', reducedMotion: true, captures, output, liveProviderCalls: 0 }));
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
