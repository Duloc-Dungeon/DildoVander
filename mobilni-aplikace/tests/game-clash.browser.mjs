// Shared iOS/web game surface, synthetic account only; no live providers or game writes.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-game-clash');
await mkdir(output, { recursive: true });
const { server, origin } = await createGameFixtureServer();
const browser = await chromium.launch();
const errors = [];
async function open(context, state) {
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(`${origin}/__game/${state}`);
  await page.locator('.game-magic-clash').waitFor();
  await page.evaluate(() => document.fonts.ready);
  return page;
}
async function practice(page) {
  await page.goto(`${origin}/play/practice`);
  await page.getByRole('radio', { name: /Úder/ }).check();
  await page.getByRole('button', { name: 'Potvrdit tah' }).click();
  await page.locator('.game-magic-clash__spells').waitFor();
}
async function personalWand(page, kind, side = 0) {
  const image = page.locator('.game-magic-clash__origin').nth(side).locator('img');
  await image.waitFor();
  const source = await readFile(new URL(`./fixtures/generated-wands/${kind}.png`, import.meta.url));
  await image.evaluate(async (image, bytes) => {
    await image.decode();
    if (image.naturalWidth !== 1024 || image.naturalHeight !== 1536) throw new Error('Expected full backend-format 2:3 PNG');
    const original = new Image(); const reference = URL.createObjectURL(new Blob([new Uint8Array(bytes)], { type: 'image/png' }));
    const pixels = picture => { const surface = document.createElement('canvas'); surface.width = 1024; surface.height = 1536; const context = surface.getContext('2d'); context.drawImage(picture, 0, 0); return context.getImageData(0, 0, 1024, 1536).data; };
    try {
      original.src = reference; await original.decode();
      const actual = pixels(image); const expected = pixels(original);
      if (!actual.every((value, index) => value === expected[index])) throw new Error('Rendered art differs from canonical generated PNG pixels');
    } finally { URL.revokeObjectURL(reference); }
  }, [...source]);
  const silhouetteFits = await image.evaluate((image, side) => {
    const sample = document.createElement('canvas'); sample.width = 96; sample.height = 144;
    const context = sample.getContext('2d'); context.drawImage(image, 0, 0, 96, 144);
    const pixels = context.getImageData(0, 0, 96, 144).data;
    if (pixels[3] !== 0) return false;
    const stage = image.closest('.game-magic-clash').getBoundingClientRect();
    const box = image.getBoundingClientRect(); const style = getComputedStyle(image);
    const angle = (side === 0 ? 64 : -64) * Math.PI / 180;
    for (let y = 0; y < 144; y++) for (let x = 0; x < 96; x++) {
      if (pixels[(y * 96 + x) * 4 + 3] < 180) continue;
      const localX = ((x + .5) / 96 - .5) * parseFloat(style.width);
      const localY = ((y + .5) / 144 - .5) * parseFloat(style.height);
      const px = box.x + box.width / 2 + localX * Math.cos(angle) - localY * Math.sin(angle);
      const py = box.y + box.height / 2 + localX * Math.sin(angle) + localY * Math.cos(angle);
      if (px < stage.left - 1 || px > stage.right + 1 || py < stage.top - 1 || py > stage.bottom + 1) return false;
    }
    return true;
  }, side);
  assert(silhouetteFits, 'the complete solid tip and handle remain inside the scene after rotation');
}

try {
  for (const width of [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce' });
    for (const state of ['choice', 'waiting', 'result']) {
      const page = await open(context, state);
      assert.equal(await page.locator('.game-magic-clash').getAttribute('data-motion'), 'still');
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      if (state === 'choice') {
        const confirm = await page.getByRole('button', { name: 'Potvrdit tah' }).boundingBox();
        assert(confirm.y + confirm.height <= 844, `first choice fits the ${width}px phone/desktop viewport`);
        await page.getByRole('radio', { name: /Úder/ }).focus();
        assert.equal(await page.getByRole('radio', { name: /Úder/ }).evaluate(input => input === document.activeElement), true);
      }
      if (state === 'waiting') assert.equal(await page.locator('.game-magic-clash__spells').count(), 0, 'pending choices stay secret');
      await page.screenshot({ path: path.join(output, `${state}-${width}.png`), fullPage: true });
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `200% text: ${state}, ${width}`);
      await page.close();
    }
    const page = await open(context, 'choice');
    await practice(page);
    assert.equal(await page.locator('.game-magic-clash').getAttribute('data-motion'), 'still', 'reduced-motion reveal is immediate');
    await context.close();
  }
  const examples = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
  for (const kind of ['moon', 'thorn']) {
    const page = await open(examples, `wand-${kind}`);
    await personalWand(page, kind);
    await personalWand(page, kind === 'moon' ? 'thorn' : 'moon', 1);
    await page.screenshot({ path: path.join(output, `personal-${kind}-390.png`), fullPage: true });
    await page.close();
  }
  await examples.close();
  const motionContext = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'no-preference' });
  const page = await open(motionContext, 'wand-thorn');
  await personalWand(page, 'thorn');
  await personalWand(page, 'moon', 1);
  await page.getByRole('radio', { name: /Úder/ }).check();
  await page.getByRole('button', { name: 'Potvrdit tah' }).click();
  await page.locator('.game-magic-clash__spells').waitFor();
  await page.waitForFunction(() => {
    const canvas = document.querySelector('.game-magic-clash__particles');
    const pixels = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
    return pixels.some((value, index) => index % 4 === 3 && value > 0);
  });
  assert.equal(await page.locator('.game-magic-clash').getAttribute('data-motion'), 'playing');
  // Capture the collision phase rather than the first barely visible particles.
  await page.waitForTimeout(800);
  await page.screenshot({ path: path.join(output, 'magic-impact-390.png'), fullPage: true });
  await page.waitForFunction(() => document.querySelector('.game-magic-clash')?.dataset.motion === 'still');
  assert.equal(await page.locator('.game-magic-clash__spells img').count(), 2);
  await page.screenshot({ path: path.join(output, 'magic-settled-390.png'), fullPage: true });
  await page.reload();
  await page.locator('.game-magic-clash').waitFor();
  assert.equal(await page.locator('.game-magic-clash').getAttribute('data-motion'), 'still');
  await motionContext.close();

  const fallback = await browser.newContext({ viewport: { width: 390, height: 844 } });
  await fallback.addInitScript(() => { HTMLCanvasElement.prototype.getContext = () => null; });
  const fallbackPage = await open(fallback, 'choice');
  await practice(fallbackPage);
  assert.equal(await fallbackPage.locator('.game-magic-clash').getAttribute('data-motion'), 'still');
  assert.equal(await fallbackPage.locator('.game-magic-clash__spells img').count(), 2);
  await fallback.close();
  assert.deepEqual(errors, []);
  console.log(`PASS: 320/390/1280px, 200% text, keyboard focus, hidden choices, real particle pixels, bounded animation, reduced motion, canvas fallback. ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
