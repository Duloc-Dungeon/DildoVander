import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';
const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-game');
await mkdir(output, { recursive: true });
const { server, origin, requests } = await createGameFixtureServer();
const browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
const errors = [];
let captures = 0;
try {
  for (const width of [320, 375, 390, 430]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    for (const state of ['lobby', 'challenge', 'incoming', 'choice', 'waiting', 'result', 'adult', 'yolo', 'error', 'empty', 'long','tactical','loadout','boss','boss-setup','captured','rescue','teams','team-choice','team-half','messages','ranking']) {
      await page.goto(`${origin}/__game/${state}`); await page.locator('.game-content').waitFor();
      await page.locator('h1, [role="alert"]').first().waitFor();
      await page.evaluate(() => document.fonts.ready);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${state} ${width}: horizontal overflow`);
      if (state === 'incoming') assert.equal(await page.getByLabel('Chci po prohře krátký úkol').isChecked(), false);
      if (state === 'choice') {
        const count = requests.length;
        await page.getByRole('radio', { name: /Lest/ }).check();
        assert.equal(requests.length, count, 'selection must remain local');
        await page.getByRole('button', { name: 'Potvrdit tah' }).click();
        await page.getByText('Tvůj tah je uzamčený.', { exact: false }).first().waitFor();
        assert.equal(requests.at(-1).body.choice, 'trick');
      }
      if (state === 'yolo') { await page.getByRole('button', { name: 'Ukázat celý výsledek' }).click(); await page.getByText('Eliška vítězí díky Děrovačce osudu.', { exact: true }).waitFor(); }
      if (state === 'tactical') assert.equal(await page.getByRole('radio', {name:/Štít/}).isDisabled(), true);
      if (state === 'loadout') { await page.getByRole('radio', {name:/Nátlak/}).check(); await page.getByText('Může ti zbýt jediný typ kouzla.', {exact:false}).waitFor(); }
      if (state === 'captured') assert.equal(await page.getByRole('link', {name:'Vyzvat bosse',exact:true}).count(), 1);
      if (state === 'team-half') { await page.getByRole('button', {name:'Předat 1 jiskru',exact:true}).click(); await page.getByText('Volba uložena.', {exact:false}).waitFor(); }
      await page.screenshot({ path: path.join(output, `${state}-${width}.png`), fullPage: true }); captures++;
    }
    await page.goto(`${origin}/play/practice`);
    for (let round = 0; round < 7; round++) {
      await page.getByRole('radio', { name: /Úder/ }).check();
      await page.getByRole('button', { name: 'Potvrdit tah' }).click();
    }
    await page.getByText('Trénink dokončen', { exact: true }).waitFor();
    await page.goto(`${origin}/__game/challenge`); await page.getByRole('heading', { name: 'Koho vyzveš?' }).waitFor();
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `200% text ${width}`);
    await page.screenshot({ path: path.join(output, `large-text-${width}.png`), fullPage: true }); captures++;
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(`Game browser checks passed: ${captures} captures, four widths, reduced motion, 200% text, selection/commit, practice, YOLO reveal. ${output}`);
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
