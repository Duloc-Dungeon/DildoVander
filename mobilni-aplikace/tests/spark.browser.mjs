import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-spark');
await mkdir(output, { recursive: true });
const { server, origin, requests } = await createGameFixtureServer();
// Software WebGL is explicit in this headless CI harness, never a production flag.
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader'], ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
const errors = [];
let captures = 0;
const phase = (page, value) => page.locator(`.spark-cabinet[data-phase="${value}"]`).waitFor();
async function ready(page) {
  await page.goto(`${origin}/__game/spark`);
  await page.waitForFunction(() => document.querySelector('.spark-primary')?.disabled === false);
}
async function result(page) { await phase(page, 'result'); await page.getByRole('button', { name: 'Ještě jeden pokus', exact: true }).waitFor(); }
async function capture(page, name) { await page.screenshot({ path: path.join(output, `${name}.png`), fullPage: true }); captures++; }
try {
  for (const [width, height] of [[320, 568], [390, 844], [1280, 900]]) {
    const context = await browser.newContext({ viewport: { width, height }, isMobile: width < 500, hasTouch: width < 500, reducedMotion: 'reduce' });
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    await ready(page);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${width}: no horizontal overflow`);
    assert.equal(await page.locator('.spark-renderer canvas').count(), 1, 'one Pixi canvas');
    assert.equal(await page.locator('.spark-primary').first().evaluate(element => getComputedStyle(element).fontWeight), '800', 'arcade controls retain shared WNS button emphasis');
    assert.equal(await page.locator('.wns-navigation').count(), 0, 'game surface is not covered by navigation');
    await capture(page, `ready-${width}`);
    await page.getByRole('button', { name: 'Hrát o rekord', exact: true }).click(); await phase(page, 'playing');
    const bounds = await page.locator('.spark-cabinet').boundingBox();
    assert.ok(bounds.y + bounds.height <= height + 1, `${width}: whole playfield fits viewport`);
    if (width < 500) await page.touchscreen.tap(bounds.x + bounds.width / 2, bounds.y + bounds.height * .83);
    else await page.locator('.spark-touch').press('Space');
    await capture(page, `playing-${width}`);
    await result(page);
    assert.equal(await page.locator('.spark-delivery').innerText(), 'Výsledek ověřený a zapsaný v žebříčku.');
    assert.ok(requests.filter(request => request.path?.endsWith('/finish')).at(-1).body.taps.length >= 1, 'input reaches verified replay');
    await capture(page, `result-${width}`);
    await page.waitForTimeout(400);
    assert.equal(await page.locator('.spark-cabinet').getAttribute('data-phase'), 'result', 'no automatic restart');
    await page.getByRole('button', { name: 'Ještě jeden pokus', exact: true }).click(); await phase(page, 'playing');
    await page.locator('.spark-touch').press('Escape'); await phase(page, 'paused');
    const pausedScore = await page.locator('.spark-hud strong').innerText(); await page.waitForTimeout(300);
    assert.equal(await page.locator('.spark-hud strong').innerText(), pausedScore, 'pause freezes simulation');
    await capture(page, `paused-${width}`);
    await page.getByRole('button', { name: 'Pokračovat', exact: true }).click(); await result(page);
    await page.getByRole('link', { name: /Žebříček/ }).click();
    await page.getByRole('region', { name: 'Kouzlo bez dozoru: pořadí skupiny' }).waitFor();
    assert.equal(await page.locator('input[value="spark"]').isChecked(), true, 'deep link selects arcade discipline');
    await page.getByRole('list', { name: 'Pořadí v Kouzlu bez dozoru' }).waitFor();
    await capture(page, `ranking-${width}`);
    await context.close();
  }
  const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
  const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
  await ready(page);
  // Failed delivery survives navigation/reload and retries the same replay.
  await page.route('**/arcade/runs/*/finish', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{"code":"unavailable"}' }));
  await page.getByRole('button', { name: 'Hrát o rekord', exact: true }).click(); await result(page);
  assert.match(await page.locator('.spark-delivery').innerText(), /čeká na ověření/);
  const queued = await page.evaluate(() => JSON.parse(localStorage.getItem('wns.spark.pending.v1.one')));
  assert.equal(queued.length, 1);
  await page.reload(); await page.getByRole('button', { name: 'Odeslat výsledky znovu', exact: true }).waitFor();
  await page.unroute('**/arcade/runs/*/finish');
  await page.getByRole('button', { name: 'Odeslat výsledky znovu', exact: true }).click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem('wns.spark.pending.v1.one')).length === 0);
  // Hidden app and an interrupted gesture both stop time without consuming a restart.
  await page.getByRole('button', { name: 'Trénovat hned', exact: true }).click(); await phase(page, 'playing');
  await page.evaluate(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, value: true });
    document.dispatchEvent(new Event('visibilitychange')); delete document.hidden;
  });
  await phase(page, 'paused');
  await page.getByRole('button', { name: 'Pokračovat', exact: true }).click();
  await page.locator('.spark-touch').dispatchEvent('pointercancel', { pointerId: 3, isPrimary: true });
  await phase(page, 'paused');
  await capture(page, 'interrupted-touch');
  await ready(page);
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, '200% text: no horizontal overflow');
  await page.getByRole('button', { name: 'Trénovat hned', exact: true }).scrollIntoViewIfNeeded();
  await capture(page, 'large-text');
  await context.close();
  assert.deepEqual(errors, [], 'no uncaught application errors');
  console.log(`Spark browser acceptance passed: ${captures} screenshots in ${output}; touch, keyboard, pause, retry/reload, ranking, reduced motion and 200% text.`);
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
