// Fictional loopback session with the saved paid portrait reference; no provider calls.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';
import { createWorkshopFixtureServer } from './fixtures/wand-workshop.mjs';
import { installTopPaintProbe, assertCoveredTop, removeTopPaintProbe } from './helpers/reveal-paint.mjs';

const { server, origin, requests } = await createWorkshopFixtureServer(0, { reference: true });
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
  for (const width of process.env.WNS_REVEAL_PC_ONLY ? [1280] : [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, deviceScaleFactor: width === 1280 ? 1.25 : 1, reducedMotion: 'reduce' });
    const page = await context.newPage();
    if (width === 1280) await page.clock.install();
    // Headless Chromium starts with notifications denied. Exercise the eligible
    // waiting-room state without requesting any real browser permission.
    await page.addInitScript(() => {
      window.notificationPermissionRequests = 0;
      Object.defineProperty(window, 'Notification', { configurable: true, value: {
        permission: 'default', requestPermission: async () => { window.notificationPermissionRequests++; return 'denied'; },
      } });
    });
    page.on('pageerror', error => errors.push(error.message));
    page.on('response', response => { if (response.status() >= 400) failedResponses.push({ status: response.status(), url: response.url() }); });
    await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
    await page.route('**/app/v1/push', route => route.fulfill({ json: { web: true, android: false, ios: false, publicKey: 'offline-fixture', enabled: false } }));
    let portraitStatus = 'running';
    let researchFailed = width === 390;
    const failedProfile = { profileId: character.profileId, revision: character.revision, name: character.name,
      world: character.universe, status: 'failed' };
    let characterRetry;
    await page.route('**/app/v1/customer/wand/workshop', route => researchFailed
      ? route.fulfill({ json: { eligible: false, reason: 'wand_character_preparation_failed',
        profile: failedProfile, tier: 0, session: null } }) : route.continue());
    await page.route('**/app/v1/prelaunch/character/retry', route => {
      characterRetry = route.request().postDataJSON();
      researchFailed = false;
      return route.fulfill({ json: {} });
    });
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
    if (width === 1280) {
      await page.emulateMedia({ reducedMotion: 'no-preference' });
      await page.clock.pauseAt(new Date(await page.evaluate(() => Date.now()) + 50));
      await installTopPaintProbe(page);
    }
    portraitStatus = 'ready';
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.locator('.portrait-stage').scrollIntoViewIfNeeded();
    if (width === 1280) {
      const curtain = page.locator('.viewport-curtain');
      await curtain.waitFor({ state: 'attached' });
      const coverage = await curtain.evaluate(layer => {
        const rect = layer.getBoundingClientRect();
        const wings = [...layer.children].map(wing => wing.getBoundingClientRect());
        return { left: rect.left, top: rect.top, width: rect.width, height: rect.height,
          viewportWidth: innerWidth, viewportHeight: innerHeight, inBody: layer.parentElement === document.body,
          leftEdge: wings[0].left, rightEdge: wings[1].right, seamCovered: wings[0].right >= wings[1].left };
      });
      assert.equal(coverage.inBody, true);
      assert.deepEqual([coverage.left, coverage.top, coverage.width, coverage.height],
        [0, 0, coverage.viewportWidth, coverage.viewportHeight], 'portrait curtain covers the complete PC window');
      assert(coverage.leftEdge <= -1 && coverage.rightEdge >= coverage.viewportWidth + 1, 'wings overscan fractional-pixel edges');
      assert.equal(coverage.seamCovered, true, 'closed wings have no gap');
      // Both committed closed frames, before the second RAF starts opening.
      for (const [delta, frame] of [[0, 0], [16, 16]]) {
        if (delta) await page.clock.runFor(delta);
        await writeFile(path.join(output, `curtain-frame-${frame}.json`), JSON.stringify(await page.evaluate(() => {
          const layer = document.querySelector('.viewport-curtain');
          return { portraitPhase: document.querySelector('.portrait-stage')?.getAttribute('data-curtain'),
            exists: !!layer, opening: layer?.className, scrollY, time: performance.now(),
            wings: layer ? [...layer.children].map(wing => ({ rect: wing.getBoundingClientRect().toJSON(),
              transform: getComputedStyle(wing).transform,
              animations: wing.getAnimations().map(animation => ({ time: animation.currentTime,
                state: animation.playState, timing: animation.effect?.getComputedTiming() })) })) : [] };
        }), null, 2));
        await assertCoveredTop(page, path.join(output, `curtain-opaque-frame-${frame}-1280.png`)); captures++;
      }
      await removeTopPaintProbe(page);
      await page.clock.runFor(1500);
      await curtain.waitFor({ state: 'detached' });
      await page.clock.resume();
      await page.emulateMedia({ reducedMotion: 'reduce' });
    }
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
    if (width === 390) {
      await page.getByRole('button', { name: 'Zopakovat přípravu postavy', exact: true }).click();
      await page.getByRole('button', { name: 'Raději psát', exact: true }).waitFor();
      await page.waitForFunction(() => document.activeElement?.id === 'workshop-title');
      assert.deepEqual(characterRetry, { expectedProfileId: failedProfile.profileId, expectedRevision: failedProfile.revision });
      assert.equal(requests.filter(request => request.path.endsWith('/sessions') && request.method === 'POST').length, 0,
        'character recovery does not silently start manufacturing');
    }
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
    const headerReturn = await page.getByRole('button', { name: 'Zpět k odpočtu', exact: true }).boundingBox();
    assert(headerReturn && headerReturn.y + headerReturn.height <= 844, `${width}: header return stays in the first viewport`);
    const returnAction = page.getByRole('button', { name: 'Počkat na začátek večera', exact: true });
    await returnAction.focus();
    const returnBox = await returnAction.boundingBox();
    assert(returnBox && returnBox.y >= 0 && returnBox.y + returnBox.height <= 844, `${width}: next action is reachable with keyboard focus beside the larger portrait scene`);
    await page.screenshot({ path: path.join(output, `result-${width}.png`), fullPage: true }); captures++;
    assert.equal(await overflow(), false, `${width}: saved wand fits`);
    await page.getByRole('button', { name: 'Počkat na začátek večera', exact: true }).click();
    await page.getByText('Hůlka je uložená. Ollivander Industries™ děkuje za důvěru. Považuje ji za odvážnou.', { exact: true }).waitFor();
    await page.getByRole('heading', { name: 'Vítej v čekárně.', exact: true }).waitFor();
    await page.locator('.push-offer').getByRole('button', { name: 'Zapnout oznámení', exact: true }).waitFor();
    assert.equal(await page.evaluate(() => window.notificationPermissionRequests), 0, 'waiting-room entry offers opt-in without requesting permission automatically');
    await page.getByText(welcome.joke, { exact: true }).waitFor();
    assert.equal(await page.locator('[aria-current="step"]').innerText(), 'Čekárna');
    await page.locator('.prelaunch-wand-keepsake').getByText('Referenční hůlka', { exact: false }).waitFor();
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
    if (width === 390) await page.emulateMedia({ reducedMotion: 'no-preference' });
    await page.getByRole('button', { name: 'Prohlédnout hůlku', exact: true }).click();
    await page.getByRole('heading', { name: 'Referenční hůlka', exact: true }).waitFor();
    if (width === 390) {
      const field = page.locator('.wand-reveal-field');
      await page.waitForFunction(() => document.querySelector('.wand-reveal-field')?.dataset.state === 'active');
      await page.waitForFunction(() => document.querySelector('.wand-reveal-object')?.getAnimations().some(a => a.currentTime > 1300));
      await page.screenshot({ path: path.join(output, 'waiting-reveal-active-390.png'), fullPage: true }); captures++;
      await page.waitForFunction(() => document.querySelector('.wand-reveal-field')?.dataset.state === 'settled');
      await page.locator('.trunk-stage__wand').click();
      assert.equal(await field.getAttribute('data-state'), 'settled', 'waiting-room reveal never replays on returning pointer');
      await page.emulateMedia({ reducedMotion: 'reduce' });
    }
    await page.getByRole('button', { name: 'Počkat na začátek večera', exact: true }).click();
    assert.equal(requests.filter(request => request.path.endsWith('/sessions') && request.method === 'POST').length, 1, 'viewing the saved wand does not manufacture a second one');
    await page.unrouteAll({ behavior: 'wait' });
    await context.close();
  }
  // Real document time: record every native frame of the curtain, without JS clock mocking.
  const nativeContext = await browser.newContext({ viewport: { width: 1280, height: 844 }, deviceScaleFactor: 1.25 });
  const nativePage = await nativeContext.newPage();
  nativePage.on('pageerror', error => errors.push(error.message));
  await nativePage.goto(`${origin}/__workshop/prelaunch`);
  await nativePage.locator('.portrait-stage').scrollIntoViewIfNeeded();
  await nativePage.locator('.viewport-curtain').waitFor({ state: 'attached' });
  const nativePlayback = await nativePage.evaluate(() => new Promise(resolve => {
    const start = performance.now();
    let frames = 0;
    let movingFrames = 0;
    let runningFrames = 0;
    let verticalGaps = 0;
    function sample() {
      const layer = document.querySelector('.viewport-curtain');
      if (!layer || performance.now() - start > 2500) {
        resolve({ frames, movingFrames, runningFrames, verticalGaps, removed: !layer }); return;
      }
      frames++;
      const box = layer.getBoundingClientRect();
      if (box.top > 0 || box.bottom < innerHeight || getComputedStyle(layer).transform !== 'none') movingFrames++;
      for (const wing of layer.children) {
        const bounds = wing.getBoundingClientRect();
        if (bounds.top > 0 || bounds.bottom < innerHeight) verticalGaps++;
        for (const animation of wing.getAnimations()) {
          const timing = animation.effect?.getComputedTiming();
          if (animation.playState === 'running' && animation.currentTime > 0 && animation.currentTime < timing.activeDuration) runningFrames++;
        }
      }
      requestAnimationFrame(sample);
    }
    requestAnimationFrame(sample);
  }));
  assert(nativePlayback.frames >= 10 && nativePlayback.runningFrames > 0, 'native Motion playback runs through observable intermediate frames');
  assert.equal(nativePlayback.movingFrames, 0, 'the fullscreen shell never moves vertically or transforms');
  assert.equal(nativePlayback.verticalGaps, 0, 'every native frame keeps both wings overscanned above and below the window');
  assert.equal(nativePlayback.removed, true, 'the native curtain is removed after its bounded playback');
  await writeFile(path.join(output, 'native-curtain-playback.json'), JSON.stringify(nativePlayback, null, 2));
  await nativeContext.close();
  assert.deepEqual(errors, []);
  assert.deepEqual(failedResponses, []);
  console.log(JSON.stringify({ result: 'passed', widths: [320, 390, 1280], textScale: '200%', reducedMotion: true, captures, output, liveProviderCalls: 0 }));
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
