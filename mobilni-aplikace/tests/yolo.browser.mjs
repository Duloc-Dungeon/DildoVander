import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-yolo-overdrive');
await mkdir(output, { recursive: true });
const { server, origin } = await createGameFixtureServer();
const browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
const errors = [];
const failures = [];
async function layout(page, label) {
  await page.evaluate(() => document.fonts.ready);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, label);
}
try {
  for (const width of [320, 390, 768, 1280]) {
    const reducedMotion = width === 390 ? 'no-preference' : 'reduce';
    const context = await browser.newContext({ viewport: { width, height: 900 }, reducedMotion });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('requestfailed', request => { if (request.failure()?.errorText !== 'net::ERR_ABORTED') failures.push(request.url()); });
    await page.goto(`${origin}/__game/yolo`);
    const theatre = page.locator('.yolo-theatre');
    await theatre.locator('img').first().waitFor();
    assert.equal(await theatre.locator('img').count(), 2, 'both protected wand images are rendered');
    assert.equal((await page.locator('.yolo-theatre__cast').innerText()).match(/ukázka/g)?.length, 2, 'synthetic images are visibly labelled');
    assert.equal(await theatre.getAttribute('data-result'), null);
    assert.equal(await page.getByText('Tohle bylo tvoje.', { exact: true }).count(), 0, 'screen readers get no premature result');
    await layout(page, `entrance ${width}`);
    await page.screenshot({ path: path.join(output, `yolo-entrance-${width}.png`), fullPage: true });
    await page.getByRole('button', { name: 'Další část', exact: true }).press('Enter');
    await page.getByRole('heading', { name: 'Střet', exact: true }).waitFor();
    assert.equal(await page.locator('.yolo-theatre__story > h2').evaluate(element => element === document.activeElement), true, 'focus follows the reading position');
    const headingStyle = await page.locator('.yolo-theatre__story > h2').evaluate(element => ({ fontSize: parseFloat(getComputedStyle(element).fontSize), outline: getComputedStyle(element).outlineStyle }));
    assert.ok(headingStyle.fontSize >= 28, 'act titles keep their hierarchy over shared game styles');
    assert.equal(headingStyle.outline, 'solid', 'the keyboard reader sees the focused act heading');
    assert.equal(await page.locator('.yolo-theatre__beam').first().evaluate(element => getComputedStyle(element).animationName), reducedMotion === 'reduce' ? 'none' : 'yolo-beam-arrival');
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    assert.equal(await theatre.getAttribute('data-phase'), 'clash', 'poll does not reset the story');
    await page.getByRole('button', { name: 'Další část', exact: true }).press('Enter');
    await page.locator('.yolo-theatre[data-result="loss"]').waitFor();
    await page.getByRole('link', { name: 'Odveta', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Přečíst celý příběh', exact: true }).click();
    assert.equal(await page.locator('.yolo-theatre__beat').count(), 3);
    await layout(page, `whole story ${width}`);
    await page.screenshot({ path: path.join(output, `yolo-verdict-${width}.png`), fullPage: true });

    for (const state of ['generating', 'failed', 'cancelled', 'long', 'task']) {
      await page.goto(`${origin}/__game/yolo-${state}`);
      await theatre.waitFor();
      await layout(page, `${state} ${width}`);
      if (['generating', 'failed', 'cancelled'].includes(state)) {
        assert.equal(await page.locator('.yolo-theatre__beat').count(), 0);
        assert.equal(await page.getByRole('link', { name: 'Odveta', exact: true }).count(), 0);
      }
      if (state === 'failed') await page.getByRole('button', { name: 'Opakovat přenos', exact: true }).waitFor();
      if (state === 'cancelled') assert.equal(await theatre.getByRole('button').count(), 0);
      if (state === 'task') {
        assert.equal(await page.locator('.game-task').count(), 0);
        await page.getByRole('button', { name: 'Ukázat celý výsledek', exact: true }).click();
        await page.locator('.game-task').waitFor();
        assert.equal(await page.getByRole('link', { name: 'Odveta', exact: true }).count(), 0);
      }
      if (state === 'long') {
        await page.evaluate(() => document.documentElement.style.fontSize = '200%');
        await layout(page, `long names, 200% text ${width}`);
      }
    }
    await context.close();
    console.log(`YOLO theatre checked at ${width}px (${reducedMotion}).`);
  }
  assert.deepEqual(errors, [], 'no uncaught browser errors');
  assert.deepEqual(failures, [], 'no failed requests');
  console.log(`YOLO browser checks passed. Synthetic screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
