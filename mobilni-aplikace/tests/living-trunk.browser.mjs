// Real browser playback with synthetic PCM, no provider calls or user audio.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createWorkshopFixtureServer } from './fixtures/wand-workshop.mjs';
import { installTopPaintProbe, assertCoveredTop, removeTopPaintProbe } from './helpers/reveal-paint.mjs';
const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-living-trunk');
await mkdir(output, { recursive: true });
const fixture = await createWorkshopFixtureServer(0, { reference: true });
const browser = await chromium.launch({ args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] });
const errors = [];
try {
  for (const width of [390, 768, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 900 }, deviceScaleFactor: width === 1280 ? 1.25 : 1, hasTouch: width < 850, permissions: ['microphone'] });
    let socket;
    await context.route('**/workshop/sessions/*/ticket', route => {
      const url = new URL(route.request().url()); url.protocol = 'ws:'; url.pathname = url.pathname.replace('/ticket', '/live'); url.searchParams.set('ticket', 'synthetic');
      return route.fulfill({ json: { url: url.href, expiresAt: new Date(Date.now() + 60000).toISOString() } });
    });
    await context.routeWebSocket('**/workshop/sessions/*/live?*', route => {
      socket = route;
      route.onMessage(message => { if (typeof message === 'string' && JSON.parse(message).type === 'cancel') route.send(JSON.stringify({ type: 'interrupted' })); });
      route.send(JSON.stringify({ type: 'ready', playbackMode: 'stream' }));
    });
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${fixture.origin}/__workshop/upgrade`);
    await page.getByRole('button', { name: 'Promluvit s kufrem', exact: true }).waitFor();
    await page.screenshot({ path: path.join(output, `ready-${width}.png`), fullPage: true });
    await page.getByRole('button', { name: 'Promluvit s kufrem', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.trunk-stage')?.dataset.stage === 'listening');
    const pcm = new Int16Array(24000 * 3);
    for (let i = 0; i < pcm.length; i++) pcm[i] = Math.round(Math.sin(i * 2 * Math.PI * 220 / 24000) * 6000);
    socket.send(Buffer.from(pcm.buffer));
    await page.waitForFunction(() => Number(document.querySelector('.trunk-stage__visual')?.style.getPropertyValue('--trunk-level')) > .2);
    assert.equal(await page.locator('.trunk-stage').getAttribute('data-stage'), 'speaking');
    const stop = page.getByRole('button', { name: 'Stop řeč', exact: true });
    const rect = await stop.boundingBox();
    await page.screenshot({ path: path.join(output, `speaking-${width}.png`), fullPage: true });
    assert(rect && rect.y + rect.height < 800, `Stop above navigation: ${JSON.stringify(rect)}`);
    await page.screenshot({ path: path.join(output, `speaking-${width}.png`), fullPage: true });
    await stop.click();
    await page.waitForFunction(() => Number(document.querySelector('.trunk-stage__visual')?.style.getPropertyValue('--trunk-level')) === 0);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    assert.equal(await page.locator('.trunk-stage__trunk').last().evaluate(img => getComputedStyle(img).transform), 'none');
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    socket.send(JSON.stringify({ type: 'state', session: fixture.finishVoice() }));
    await page.getByRole('heading', { name: 'Hůlka dostává tvar', exact: true }).waitFor();
    assert.equal(await page.locator('[aria-current="step"]').textContent(), 'Výroba');
    await page.screenshot({ path: path.join(output, `forging-${width}.png`), fullPage: true });
    socket.send(JSON.stringify({ type: 'ended' }));
    await page.getByText('Hůlka je uložená a připravená ke hře.', { exact: true }).waitFor();
    await installTopPaintProbe(page);
    await page.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.trunk-stage__wand')?.naturalWidth > 0);
    const field = page.locator('.wand-reveal-field');
    await field.waitFor({ state: 'visible' });
    assert.equal(await field.getAttribute('aria-hidden'), 'true');
    const bounds = await page.locator('.trunk-stage__artwork').boundingBox();
    assert(bounds && bounds.height > bounds.width, 'portrait illustration has a tall stage');
    {
      const viewportLayer = await field.evaluate(canvas => {
        const root = canvas.closest('.wand-reveal-viewport');
        const rect = root.getBoundingClientRect();
        return { left: rect.left, top: rect.top, width: rect.width, height: rect.height,
          viewportWidth: document.documentElement.clientWidth, viewportHeight: innerHeight, inBody: root.parentElement === document.body,
          transform: getComputedStyle(root).transform };
      });
      assert.equal(viewportLayer.inBody, true, 'cinematic stage escapes all card clipping ancestors');
      assert(viewportLayer.left <= 0 && viewportLayer.top <= 0
        && viewportLayer.width + viewportLayer.left >= viewportLayer.viewportWidth
        && viewportLayer.height + viewportLayer.top >= viewportLayer.viewportHeight, 'stage covers the full window with edge overscan');
      assert.equal(viewportLayer.transform, 'none', 'the covering shell never moves');
    }
    await assertCoveredTop(page, path.join(output, `opaque-opening-${width}.png`));
    await removeTopPaintProbe(page);
    if (width === 390) {
      await page.setViewportSize({ width, height: 950 });
      await page.evaluate(() => window.dispatchEvent(new Event('scroll')));
      assert.equal(await field.getAttribute('data-state'), 'active', 'mobile geometry changes preserve the reveal');
    }
    if (width < 850) await page.touchscreen.tap(bounds.x + bounds.width * .3, bounds.y + bounds.height * .4);
    else await page.mouse.move(bounds.x + bounds.width * .3, bounds.y + bounds.height * .4);
    await page.waitForFunction(() => document.querySelector('.wand-reveal-object')?.getAnimations().some(a => a.currentTime > 1300));
    assert.equal(await field.getAttribute('data-state'), 'active');
    await page.screenshot({ path: path.join(output, `reveal-active-${width}.png`) });
    if (width !== 390) {
    const landingHandle = await page.waitForFunction(() => {
      const hero = document.querySelector('.wand-reveal-object');
      if (!hero?.getAnimations().some(animation => animation.currentTime >= 3950)) return false;
      const source = document.querySelector('.trunk-stage__wand');
      const presented = hero.getBoundingClientRect();
      const original = source.getBoundingClientRect();
      return { left: presented.left - original.left, top: presented.top - original.top,
        width: presented.width - original.width, height: presented.height - original.height };
    });
    const landing = await landingHandle.jsonValue();
    assert(Object.values(landing).every(delta => Math.abs(delta) < 4), 'the hero lands on the original artwork without a positional snap');
    }
    await page.waitForFunction(() => document.querySelector('.wand-reveal-field')?.dataset.state === 'settled');
    // Returning to the artwork must never restart the completed reveal.
    await page.mouse.move(0, 0);
    if (width < 850) await page.touchscreen.tap(bounds.x + bounds.width * .3, bounds.y + bounds.height * .4);
    else await page.mouse.move(bounds.x + bounds.width * .3, bounds.y + bounds.height * .4);
    if (width < 850) await page.touchscreen.tap(bounds.x + bounds.width * .3, bounds.y + bounds.height * .4);
    else await page.mouse.click(bounds.x + bounds.width * .3, bounds.y + bounds.height * .4);
    assert.equal(await field.getAttribute('data-state'), 'settled');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.waitForFunction(() => document.querySelector('.wand-reveal-field')?.dataset.state === 'disabled');
    assert.equal(await field.isVisible(), false);
    assert.equal(await page.locator('.trunk-stage__wand').evaluate(img => getComputedStyle(img).opacity), '1');
    await page.screenshot({ path: path.join(output, `reveal-reduced-${width}.png`), fullPage: true });
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    assert.equal(await page.locator('[aria-current="step"]').textContent(), 'Tvoje hůlka');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.screenshot({ path: path.join(output, `result-${width}.png`), fullPage: true });
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ result: 'passed', widths: [390, 768, 1280], realPlaybackMeter: true, stopAboveNavigation: true, reducedMotion: true, finiteReveal: true, noHoverReplay: true, touchAndPointer: true, savedPaidReference: true, liveProviderCalls: 0, output }));
} finally { await browser.close(); await new Promise(resolve => fixture.server.close(resolve)); }
