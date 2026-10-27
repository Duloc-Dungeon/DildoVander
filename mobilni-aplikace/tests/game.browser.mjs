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
  for (const width of [320, 375, 390, 430, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, isMobile: width < 500, hasTouch: width < 500, reducedMotion: 'reduce' });
    await context.addInitScript(theme => localStorage.setItem('dildovander.theme', theme), width < 390 ? 'lord-voldemort' : 'olivander');
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    for (const state of ['lobby', 'challenge', 'incoming', 'choice', 'waiting', 'result', 'early-result', 'adult', 'task-without-wand', 'yolo', 'error', 'empty', 'long','tactical','loadout','boss','boss-setup','captured','rescue','teams','team-choice','team-half','messages','ranking','first-wand','upgrade-wand']) {
      await page.goto(`${origin}/__game/${state}`); await page.locator('.game-content').waitFor();
      await page.locator('h1, [role="alert"]').first().waitFor();
      await page.evaluate(() => document.fonts.ready);
      assert.ok(await page.locator('.game-content').evaluate(element => getComputedStyle(element).fontFamily.includes('Manrope')), `${state}: Manrope font`);
      assert.equal(await page.locator('.scene-shell').evaluate(element => getComputedStyle(element).backgroundColor), 'rgb(8, 9, 9)', `${state}: shared WNS charcoal background`);
      if (['choice', 'incoming', 'adult', 'result', 'challenge'].includes(state)) {
        assert.equal(await page.locator('.game-content .aw-button--primary').first().evaluate(element => getComputedStyle(element).backgroundColor), 'rgb(184, 243, 212)', `${state}: shared mint primary action`);
      }
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${state} ${width}: horizontal overflow`);
      if (state === 'incoming' || state === 'challenge') assert.equal(await page.getByLabel('Chci po prohře krátký úkol').count(), 0);
      if (state === 'early-result') {
        assert.equal(await page.locator('.game-round-dots > .done').count(), 4);
        assert.equal(await page.getByRole('button', { name: 'Potvrdit tah' }).count(), 0);
        await page.getByRole('heading', { name: 'Tohle bylo tvoje.' }).waitFor();
      }
      if (state === 'adult' || state === 'task-without-wand') {
        await page.screenshot({ path: path.join(output, `${state}-pending-${width}.png`), fullPage: true }); captures++;
        const confirm = page.getByRole('button', { name: 'Potvrdit výsledek úkolu' });
        assert.equal(await confirm.isDisabled(), true);
        assert.equal(await page.getByRole('link', { name: 'Odveta', exact: true }).count(), 0);
        const count = requests.length;
        const outcome = width < 390 ? 'done' : 'skip';
        await page.getByRole('radio', { name: outcome === 'done' ? 'Splněno' : 'Nesplněno', exact: true }).check();
        assert.equal(requests.length, count, 'task selection must remain local until confirmed');
        await confirm.click();
        const saved = outcome === 'done' ? 'Uloženo: úkol splněn.' : 'Uloženo: úkol nesplněn.';
        await page.getByText(saved, { exact: true }).waitFor();
        assert.equal(requests.at(-1).body.taskAction, outcome);
        await page.reload();
        await page.getByText(saved, { exact: true }).waitFor();
        assert.equal(await page.getByRole('link', { name: 'Odveta', exact: true }).count(), 1);
      }
      if (state === 'first-wand') { assert.equal(await page.getByRole('button', {name:'Mluvit s Olivanderem'}).count(), 1); assert.equal(await page.getByRole('radio').count(), 0); }
      if (state === 'upgrade-wand') assert.equal(await page.getByRole('heading', {name:'Vylepšit hůlku s Olivanderem'}).count(), 1);
      if (state === 'choice') {
        const count = requests.length;
        await page.getByRole('radio', { name: /Lest/ }).check();
        assert.equal(requests.length, count, 'selection must remain local');
        const confirm = page.getByRole('button', { name: 'Potvrdit tah' });
        await page.keyboard.press('Tab');
        await confirm.focus();
        assert.equal(await confirm.evaluate(button => button.matches(':focus-visible')), true);
        assert.equal(await confirm.evaluate(button => getComputedStyle(button).outlineColor), 'rgb(184, 243, 212)', 'WNS v2 mint confirmation focus remains visible');
        if (width === 390) await page.screenshot({ path: path.join(output, 'confirmation-focus-390.png'), fullPage: true });
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
    await page.getByRole('button', { name: 'Potvrdit tah' }).waitFor();
    for (let round = 0; round < 7 && await page.getByRole('button', { name: 'Potvrdit tah' }).count(); round++) {
      await page.getByRole('radio', { name: /Úder/ }).check();
      await page.getByRole('button', { name: 'Potvrdit tah' }).click();
      await page.waitForFunction(count => document.querySelectorAll('.game-round-dots > .done').length === count, round + 1);
    }
    await page.getByText('Trénink dokončen', { exact: true }).waitFor();
    await page.goto(`${origin}/__game/challenge`); await page.getByRole('heading', { name: 'Koho vyzveš?' }).waitFor();
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `200% text ${width}`);
    await page.screenshot({ path: path.join(output, `large-text-${width}.png`), fullPage: true }); captures++;
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(`Game browser checks passed: ${captures} captures, five widths, shared WNS colors, both persisted themes, reduced motion, 200% text, selection/commit, task confirmation/reload, practice, YOLO reveal. ${output}`);
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
