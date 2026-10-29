import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-boss-overdrive');
await mkdir(output, { recursive: true });
const { server, origin } = await createGameFixtureServer();
const browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
const errors = [];
const failedRequests = [];
const bossId = 'a'.repeat(32);
const palette = ['#c8b9fc', '#ffc662', '#f8b5a5'];

async function checkLayout(page, label) {
  await page.evaluate(() => document.fonts.ready);
  const layout = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth,
    overflowing: [...document.querySelectorAll('body *')].filter(element => element.getBoundingClientRect().right > innerWidth + 1)
      .map(element => `${element.tagName}.${element.className}`).slice(0, 8) }));
  assert.ok(layout.scroll <= layout.width, `${label}: horizontal overflow ${JSON.stringify(layout)}`);
}

async function finishReveal(page) {
  await page.locator('.boss-theatre__veil').evaluate(element => Promise.all(element.getAnimations().map(animation => animation.finished)));
}

try {
  for (const width of [320, 390, 1280]) {
    const reducedMotion = width === 390 ? 'no-preference' : 'reduce';
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('requestfailed', request => {
      if (request.failure()?.errorText !== 'net::ERR_ABORTED') failedRequests.push(request.url());
    });
    let patch = {};
    await page.route('**/app/v1/game/boss/attempts/**', async route => {
      const response = await route.fetch();
      if (route.request().method() === 'POST') patch = { ...patch, revision: (patch.revision ?? 3) + 1 };
      await route.fulfill({ response, json: { ...await response.json(), ...patch } });
    });

    await page.goto(`${origin}/__game/boss-setup`);
    await page.getByRole('heading', { name: 'Baron Šarlatán', exact: true }).waitFor();
    await finishReveal(page);
    await checkLayout(page, `intro ${width}`);
    if (reducedMotion === 'reduce') {
      assert.equal(await page.locator('.boss-theatre__veil').evaluate(element => getComputedStyle(element).animationName), 'none');
    }
    if (width !== 320) await page.screenshot({ path: path.join(output, `intro-${width}.png`), fullPage: true });
    await page.getByText('Jak souboj funguje', { exact: true }).click();
    await page.getByText('Nesoutěžní hra · bez fyzické ceny', { exact: true }).waitFor({ state: 'visible' });
    await page.getByRole('button', { name: 'Vyzvat Barona', exact: true }).click();
    await page.waitForURL(`**/play/boss/${bossId}`);
    await page.getByRole('button', { name: 'Potvrdit tah' }).waitFor();

    for (const phase of [1, 2, 3]) {
      patch = { phase, revision: 10 + phase, playerEnergy: 6 - phase, opponentEnergy: 3.5 - phase };
      await page.evaluate(() => window.dispatchEvent(new Event('online')));
      await page.locator(`.game-boss-scene[data-boss-phase="${phase}"]`).waitFor();
      assert.equal(await page.locator('.boss-phase').textContent(), `Fáze ${phase} ze 3`);
      assert.equal(await page.locator('.game-boss-scene').evaluate(element => getComputedStyle(element).getPropertyValue('--boss-light').trim()), palette[phase - 1]);
      assert.equal(await page.getByRole('meter', { name: 'Baronova energie' }).getAttribute('aria-valuenow'), String(3.5 - phase));
      await checkLayout(page, `phase ${phase}, ${width}`);
    }
    await finishReveal(page);
    assert.equal(await page.locator('.game-clash-stage').count(), 0, 'boss stage replaces the regular duel canvas');
    assert.equal(await page.getByRole('button', { name: 'Potvrdit tah' }).isDisabled(), true);
    const strike = page.getByRole('radio', { name: /^Úder / });
    await strike.focus(); await strike.press('Space');
    assert.equal(await strike.isChecked(), true, 'keyboard chooses a spell');
    assert.equal(await page.getByRole('button', { name: 'Potvrdit tah' }).isEnabled(), true);
    assert.notEqual(await strike.evaluate(element => getComputedStyle(element.closest('label')).outlineStyle), 'none', 'keyboard focus is visible');
    if (width !== 320) {
      await page.evaluate(() => window.scrollTo(0, 0));
      await page.screenshot({ path: path.join(output, `battle-${width}.png`), fullPage: true });
    }
    await page.getByRole('button', { name: 'Potvrdit tah' }).click();
    await page.locator('.game-controls .game-helper').filter({ hasText: 'Tvůj tah je uzamčený.' }).waitFor();
    await page.getByRole('button', { name: 'Ukončit pokus bez výsledku', exact: true }).waitFor();

    patch = { phase: 3, revision: 30, exhibition: true, playerEnergy: 2.5, opponentEnergy: .5 };
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.waitForFunction(() => document.querySelector('[role="meter"][aria-label="Tvoje energie"]')?.getAttribute('aria-valuenow') === '2.5');
    assert.equal(await page.getByRole('meter', { name: 'Baronova energie' }).getAttribute('aria-valuemax'), '5');
    assert.equal(await page.getByRole('meter', { name: 'Tvoje energie' }).getAttribute('aria-valuenow'), '2.5');

    for (const result of ['win', 'loss']) {
      patch = { ...patch, revision: result === 'win' ? 40 : 41, status: 'complete', result, completedRounds: 12,
        rounds: [{ index: 12, playerChoice: 'strike', opponentChoice: 'trick', winner: 'player', playerPoints: 1, opponentPoints: 0 }] };
      await page.evaluate(() => window.dispatchEvent(new Event('online')));
      await page.getByRole('heading', { name: result === 'win' ? 'Baron padl. Opona se zvedá.' : 'Tentokrát vyhrál Baron.', exact: true }).waitFor();
      assert.equal(await page.getByRole('button', { name: 'Potvrdit tah' }).count(), 0);
      assert.equal(await page.locator('.game-reveal').count(), 1, 'last round remains readable');
    }
    if (width === 320) {
      await page.evaluate(() => document.documentElement.style.fontSize = '200%');
      await checkLayout(page, 'battle 320, 200% text');
      await page.goto(`${origin}/__game/boss-setup`);
      await page.getByRole('button', { name: 'Vyzvat Barona', exact: true }).waitFor();
      await page.evaluate(() => document.documentElement.style.fontSize = '200%');
      await checkLayout(page, 'intro 320, 200% text');
    }
    await page.goto(`${origin}/__game/choice`);
    await page.locator('.game-clash-stage').waitFor();
    assert.equal(await page.locator('.game-boss-scene').count(), 0, 'ordinary duels keep their own stage');
    await checkLayout(page, `ordinary duel ${width}`);
    await context.close();
    console.log(`Boss states checked at ${width} px (${reducedMotion}).`);
  }
  assert.deepEqual(errors, [], 'no uncaught browser errors');
  assert.deepEqual(failedRequests, [], 'no failed requests');
  console.log(`Boss browser checks passed: 320/390/1280 px; phases 1–3; normal/reduced motion; keyboard; locked turn; exhibition; win/loss; 200% text; regular duel. Screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
