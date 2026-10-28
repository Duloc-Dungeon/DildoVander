// Compiled app, loopback fixture API, no AI or remote writes.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = path.join(tmpdir(), 'dildovander-character-labels');
await mkdir(output, { recursive: true });
const { server, origin } = await createGameFixtureServer();
const browser = await chromium.launch(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {});
const errors = [];
try {
  for (const width of [390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce' });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    let ownLabel = 'Petr';
    await page.route('**/app/v1/session', async route => {
      const response = await route.fetch();
      const value = await response.json();
      value.account.displayName = 'Mozkomor';
      await route.fulfill({ response, json: value });
    });
    await page.route('**/app/v1/customer/wand', async route => {
      const response = await route.fetch();
      await route.fulfill({ response, json: { ...await response.json(), characterDisplayName: ownLabel ? `Mozkomor · ${ownLabel}` : null } });
    });
    await page.route('**/app/v1/game/matches/*', async route => {
      const response = await route.fetch();
      const value = await response.json();
      value.opponent.displayName = 'Mozkomor · Jana';
      await route.fulfill({ response, json: value });
    });
    await page.goto(`${origin}/__game/choice`);
    await page.getByText('Mozkomor · Petr', { exact: true }).waitFor();
    await page.locator('.game-score--duel').getByText('Mozkomor · Jana', { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.screenshot({ path: path.join(output, `duel-labels-${width}.png`), fullPage: true });
    ownLabel = '';
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.locator('.game-score--duel').getByText('Mozkomor', { exact: true }).waitFor();
    assert.equal(await page.getByText('Mozkomor · Petr', { exact: true }).count(), 0);
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ passed: true, viewports: ['390x844', '1280x844'], checks: ['duplicate names', 'direct match', 'live label clear', 'no horizontal overflow'], screenshots: output }));
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
