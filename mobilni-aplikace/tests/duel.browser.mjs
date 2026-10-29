import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-duel-delight');
await mkdir(output, { recursive: true });
const { server, origin, requests } = await createGameFixtureServer();
const browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
const errors = [];
const failedRequests = [];

async function checkLayout(page, label) {
  await page.evaluate(() => document.fonts.ready);
  const layout = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth }));
  assert.ok(layout.scroll <= layout.width, `${label}: horizontal overflow ${JSON.stringify(layout)}`);
}

try {
  for (const width of [320, 390, 768, 1280]) {
    const reducedMotion = width === 390 ? 'no-preference' : 'reduce';
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('requestfailed', request => {
      if (request.failure()?.errorText !== 'net::ERR_ABORTED') failedRequests.push(request.url());
    });
    await page.goto(`${origin}/__game/wand-moon`);
    const stage = page.locator('.game-magic-clash--theatre');
    await stage.waitFor();
    await page.locator('.game-magic-clash__origin img').first().waitFor();
    await checkLayout(page, `ready ${width}`);
    assert.equal(await stage.getAttribute('data-winner'), null, 'there is no winner before choices are published');
    if (width === 390 || width === 1280) await page.screenshot({ path: path.join(output, `duel-ready-${width}.png`), fullPage: true });

    for (const [index, winner, title] of [[1, 'opponent', 'Soupeřova výměna.'], [2, 'player', 'Tvoje výměna.'], [3, 'draw', 'Nerozhodná výměna.']]) {
      const previousWinner = await stage.getAttribute('data-winner');
      const strike = page.getByRole('radio', { name: /^Úder / });
      await strike.focus(); await strike.press('Space');
      assert.equal(await stage.getAttribute('data-winner'), previousWinner, 'selecting a private choice does not change the outcome');
      assert.notEqual(await strike.evaluate(element => getComputedStyle(element.closest('label')).outlineStyle), 'none');
      await page.getByRole('button', { name: 'Potvrdit tah', exact: true }).click();
      await page.locator(`.game-magic-clash--theatre[data-winner="${winner}"]`).waitFor();
      await page.getByText(title, { exact: true }).waitFor();
      if (reducedMotion === 'no-preference') {
        await page.locator('.game-magic-clash--theatre[data-motion="playing"]').waitFor();
      }
      await page.locator('.game-magic-clash--theatre[data-motion="still"]').waitFor();
      if (reducedMotion === 'reduce') {
        assert.equal(await page.locator('.game-magic-clash__lights').evaluate(element => getComputedStyle(element).animationName), 'none');
      }
      assert.equal(requests.filter(request => request.path.endsWith('/choose')).at(-1).body.choice, 'strike');
      assert.equal(await page.locator('.game-round-dots .done').count(), index);
      await checkLayout(page, `round ${index}, ${width}`);
      if (index === 2 && (width === 390 || width === 1280)) {
        const lights = await page.locator('.game-magic-clash__light').evaluateAll(elements => elements.map(element => getComputedStyle(element).opacity));
        assert.equal(lights[0], '1'); assert.equal(lights[1], '0.15');
        await page.evaluate(() => window.scrollTo(0, 0));
        await page.screenshot({ path: path.join(output, `duel-reveal-${width}.png`), fullPage: true });
      }
    }
    await page.reload();
    await page.locator('.game-magic-clash--theatre[data-winner="draw"]').waitFor();
    assert.equal(await stage.getAttribute('data-motion'), 'still', 'loading a saved round does not replay the reveal');
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    assert.equal(await stage.getAttribute('data-motion'), 'still', 'polling does not replay the reveal');

    for (const state of ['waiting', 'result', 'adult', 'tactical', 'long']) {
      await page.goto(`${origin}/__game/${state}`);
      await page.locator('.game-duel-scene').waitFor();
      await checkLayout(page, `${state} ${width}`);
      if (state === 'waiting') {
        assert.equal(await stage.getAttribute('data-winner'), null);
        assert.equal(await page.getByRole('radio', { name: /^Úder / }).count(), 0);
        await page.locator('.game-clash-stage--locked').getByText('Tah uzamčený', { exact: false }).waitFor();
      }
      if (state === 'result' || state === 'adult') {
        assert.equal(await page.locator('.game-duel-scene').getAttribute('data-duel-result'), state === 'result' ? 'win' : 'loss');
        await page.getByRole('heading', { name: state === 'result' ? 'Tohle bylo tvoje.' : 'Dnes slaví soupeř.', exact: true }).waitFor();
      }
    }
    if (width === 320) {
      await page.evaluate(() => document.documentElement.style.fontSize = '200%');
      await checkLayout(page, 'long names, 320 px, 200% text');
      await page.getByRole('button', { name: 'Potvrdit tah' }).waitFor();
    }
    await page.goto(`${origin}/__game/boss`);
    await page.locator('.game-boss-scene').waitFor();
    assert.equal(await page.locator('.game-duel-scene').count(), 0, 'boss keeps its own stage');
    await page.goto(`${origin}/__game/teams`);
    await page.locator('.game-content').waitFor();
    assert.equal(await page.locator('.game-duel-scene').count(), 0, 'team lobby keeps its preparation layout');
    await context.close();
    console.log(`PvP checked at ${width} px (${reducedMotion}).`);
  }
  assert.deepEqual(errors, [], 'no uncaught browser errors');
  assert.deepEqual(failedRequests, [], 'no failed requests');
  console.log(`Duel browser checks passed: private choices; live win/loss/draw; keyboard; loaded/polled rounds; waiting; final results; tactical; long names; 200% text; boss/team isolation. Screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
