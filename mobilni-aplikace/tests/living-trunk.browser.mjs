// Real browser playback with synthetic PCM, no provider calls or user audio.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createWorkshopFixtureServer } from './fixtures/wand-workshop.mjs';
const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-living-trunk');
await mkdir(output, { recursive: true });
const fixture = await createWorkshopFixtureServer();
const browser = await chromium.launch({ args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] });
const errors = [];
try {
  for (const width of [390, 768, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 900 }, permissions: ['microphone'] });
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
    const rect = await stop.boundingBox(); assert(rect && rect.y + rect.height < 800, 'Stop above navigation');
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
    await page.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.trunk-stage__wand')?.naturalWidth > 0);
    await page.locator('.trunk-stage__wand').evaluate(async img => Promise.all(img.getAnimations().map(a => a.finished)));
    assert.equal(await page.locator('[aria-current="step"]').textContent(), 'Tvoje hůlka');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.screenshot({ path: path.join(output, `result-${width}.png`), fullPage: true });
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ result: 'passed', widths: [390, 768, 1280], realPlaybackMeter: true, stopAboveNavigation: true, reducedMotion: true, liveProviderCalls: 0, output }));
} finally { await browser.close(); await new Promise(resolve => fixture.server.close(resolve)); }
