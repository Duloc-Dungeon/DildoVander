import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-team-practice');
await mkdir(output, { recursive: true });
const { server, origin } = await createGameFixtureServer();
const browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
const errors = [];
const failedRequests = [];
const firstRound = { index: 1, choiceA0: 'strike', choiceB0: 'trick', choiceA1: 'shield', choiceB1: 'trick', teamAPoints: 1, teamBPoints: 1 };
// B choices in second-half DTOs follow the actual paired serialization.
const fifthRound = { index: 5, choiceA0: 'strike', choiceB0: 'strike', choiceA1: 'trick', choiceB1: 'shield', teamAPoints: 1, teamBPoints: 0 };

async function checkLayout(page, label) {
  await page.evaluate(() => document.fonts.ready);
  const size = await page.evaluate(() => ({ viewport: innerWidth, content: document.documentElement.scrollWidth }));
  assert.ok(size.content <= size.viewport, `${label}: horizontal overflow ${JSON.stringify(size)}`);
}
async function refresh(page) { await page.evaluate(() => window.dispatchEvent(new Event('online'))); }
async function checkWand(page, side, kind) {
  const image = page.locator(`.game-magic-clash__origin${side === 'opponent' ? '--opponent' : ':not(.game-magic-clash__origin--opponent)'} img`);
  await image.waitFor();
  const expected = await readFile(new URL(`./fixtures/generated-wands/${kind}.png`, import.meta.url));
  const matches = await image.evaluate(async (img, bytes) => {
    await img.decode();
    const bitmap = await createImageBitmap(new Blob([new Uint8Array(bytes)], { type: 'image/png' }));
    try {
      if (img.naturalWidth !== bitmap.width || img.naturalHeight !== bitmap.height) return false;
      const canvas = document.createElement('canvas'); canvas.width = bitmap.width; canvas.height = bitmap.height;
      const context = canvas.getContext('2d');
      context.drawImage(img, 0, 0); const actual = context.getImageData(0, 0, canvas.width, canvas.height).data;
      context.clearRect(0, 0, canvas.width, canvas.height); context.drawImage(bitmap, 0, 0);
      const wanted = context.getImageData(0, 0, canvas.width, canvas.height).data;
      return actual.every((value, index) => value === wanted[index]);
    } finally { bitmap.close(); }
  }, [...expected]);
  assert.ok(matches, `${side} displays the exact decoded approved ${kind} PNG`);
}

try {
  for (const width of [320, 390, 768, 1280]) {
    const reducedMotion = width === 390 ? 'no-preference' : 'reduce';
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion });
    // Deterministic opponent for the local practice game only; no provider calls.
    await context.addInitScript(() => {
      const original = crypto.getRandomValues.bind(crypto);
      crypto.getRandomValues = array => array instanceof Uint32Array && array.length === 1 ? array.fill(0) : original(array);
    });
    const page = await context.newPage();
    const posts = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => {
      const path = new URL(request.url()).pathname;
      // SignalR's connection handshake is a read subscription, not a game move.
      if (request.method() === 'POST' && path !== '/app/v1/game/live/negotiate') {
        posts.push({ path, body: request.postDataJSON() });
      }
    });
    page.on('requestfailed', request => { if (request.failure()?.errorText !== 'net::ERR_ABORTED') failedRequests.push(request.url()); });
    let patch = { revision: 10, completedRounds: 0, rounds: [], teamScore: 0, opponentScore: 0, ownChoiceLocked: false, allFourLocked: [false, false, false, false] };
    await page.route('**/app/v1/game/teams/**', async route => {
      if (new URL(route.request().url()).pathname.endsWith('/wand-artwork')) return route.continue();
      const response = await route.fetch();
      if (route.request().method() === 'POST') {
        patch = { ...patch, revision: patch.revision + 1,
          ...(route.request().url().endsWith('/choose') ? { ownChoiceLocked: true, allFourLocked: [true, false, false, false] } : { ownTransferLocked: true }) };
      }
      await route.fulfill({ response, json: { ...await response.json(), ...patch } });
    });
    await page.goto(`${origin}/__game/team-choice`);
    await page.locator('.game-team-scene').waitFor();
    await checkWand(page, 'player', 'moon');
    await checkWand(page, 'opponent', 'thorn');
    assert.equal(await page.locator('.team-player').count(), 4);
    assert.equal(await page.locator('.game-clash-stage').getAttribute('data-winner'), null);
    await checkLayout(page, `team ready ${width}`);
    const strike = page.getByRole('radio', { name: /^Úder / });
    await strike.focus(); await strike.press('Space');
    assert.notEqual(await strike.evaluate(element => getComputedStyle(element.closest('label')).outlineStyle), 'none');
    assert.equal(await page.locator('.game-clash-stage').getAttribute('data-winner'), null, 'private choice stays private');
    await page.getByRole('button', { name: 'Potvrdit vlastní tah', exact: true }).click();
    await page.getByText('Tvůj tah je uzamčený. Čekáme na zbývající hráče.', { exact: true }).waitFor();
    await page.getByText('Potvrzené tahy: 1 ze 4.', { exact: true }).waitFor();
    assert.equal(posts.at(-1).body.choice, 'strike');
    assert.equal(await page.locator('.game-clash-stage').getAttribute('data-winner'), null);

    await page.locator('.team-exchange').scrollIntoViewIfNeeded();
    patch = { ...patch, revision: 12, completedRounds: 1, rounds: [firstRound], teamScore: 1, opponentScore: 1,
      ownChoiceLocked: false, allFourLocked: [false, false, false, false] };
    await refresh(page);
    await page.locator('.game-clash-stage[data-winner="player"]').waitFor();
    await page.getByText('Tvůj tým +1 · Soupeři +1', { exact: true }).waitFor();
    if (reducedMotion === 'no-preference') await page.locator('.game-clash-stage[data-motion="playing"]').waitFor();
    await page.locator('.game-clash-stage[data-motion="still"]').waitFor();
    if (reducedMotion === 'reduce') assert.equal(await page.locator('.game-magic-clash__lights').evaluate(element => getComputedStyle(element).animationName), 'none');
    await checkLayout(page, `team reveal ${width}`);
    if (width === 390 || width === 1280) {
      await page.evaluate(() => window.scrollTo(0, 0));
      await page.screenshot({ path: path.join(output, `team-${width}.png`), fullPage: true });
    }
    await page.reload();
    await page.locator('.game-clash-stage[data-winner="player"]').waitFor();
    assert.equal(await page.locator('.game-clash-stage').getAttribute('data-motion'), 'still', 'saved team round does not replay');

    const firstHalf = Array.from({ length: 4 }, (_, index) => ({ ...firstRound, index: index + 1 }));
    patch = { ...patch, revision: 20, status: 'halftime', completedRounds: 4, rounds: firstHalf, teamScore: 4, opponentScore: 4,
      halftimeEndsAt: Math.floor(Date.now() / 1000) + 60, ownTransferLocked: false };
    await refresh(page);
    await page.getByRole('heading', { name: 'Jiskra pro spojence?', exact: true }).waitFor();
    await page.getByRole('timer').waitFor();
    assert.equal(await page.locator('.game-clash-stage').count(), 0, 'halftime keeps focus on the transfer');
    await checkLayout(page, `halftime ${width}`);
    if (width === 390) {
      await page.getByRole('button', { name: 'Předat 1 jiskru', exact: true }).click();
      await page.getByText('Volba uložena. Zásoby potvrdí server po přestávce.', { exact: true }).waitFor();
      assert.equal(posts.at(-1).body.giveSpark, true);
    } else {
      patch = { ...patch, revision: 21, halftimeEndsAt: Math.floor(Date.now() / 1000) - 1 };
      await refresh(page);
      await page.getByText('Přestávka skončila. Čekáme na potvrzení serveru.', { exact: true }).waitFor();
      assert.equal(await page.getByRole('button', { name: 'Předat 1 jiskru', exact: true }).count(), 0);
    }
    patch = { ...patch, revision: 30, status: 'active', completedRounds: 5, rounds: [...firstHalf, fifthRound], teamScore: 5, opponentScore: 4 };
    await refresh(page);
    await page.locator('.game-clash-stage[data-winner="draw"]').waitFor();
    await checkWand(page, 'opponent', 'moon');
    patch = { ...patch, opponentWands: {} };
    await refresh(page);
    await page.locator('.game-magic-clash__origin--opponent img').waitFor({ state: 'hidden' });
    assert.equal(await page.locator('.game-magic-clash__origin--opponent svg').count(), 1, 'withdrawn opponent artwork returns to the neutral outline');
    delete patch.opponentWands;
    await refresh(page);
    await checkWand(page, 'opponent', 'moon');
    await page.getByText('Úder proti Úder.', { exact: true }).waitFor();
    await page.locator('.team-pairing').filter({ hasText: 'Barbora' }).waitFor();
    assert.equal(await page.locator('.game-clash-stage').getAttribute('data-motion'), 'still');
    const secondHalf = Array.from({ length: 4 }, (_, index) => ({ ...fifthRound, index: index + 5 }));
    patch = { ...patch, revision: 40, status: 'complete', completedRounds: 8, rounds: [...firstHalf, ...secondHalf], teamScore: 8, result: 'win', xpAwarded: 12 };
    await refresh(page);
    await page.getByRole('heading', { name: 'Tohle byla souhra.', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Potvrdit vlastní tah', exact: true }).count(), 0);

    const postsBeforePractice = posts.length;
    await page.goto(`${origin}/play/practice`);
    await page.locator('.game-practice-scene .game-magic-clash--theatre').waitFor();
    const practiceWand = page.locator('.game-magic-clash__origin--opponent img');
    await practiceWand.evaluate(async image => { await image.decode(); if (!image.naturalWidth) throw new Error('Practice wand is not decoded'); });
    assert.ok((await practiceWand.getAttribute('src')).includes('wand-violet'), 'practice uses the generic violet wand');
    await page.getByText('Trénink · bez XP', { exact: true }).first().waitFor();
    for (let round = 1; round <= 3; round++) {
      const shield = page.getByRole('radio', { name: /^Štít / });
      await shield.focus(); await shield.press('Space');
      await page.getByRole('button', { name: 'Potvrdit tah', exact: true }).click();
      await page.locator(`.game-clash-stage[data-winner="${round === 2 ? 'draw' : 'player'}"]`).waitFor();
      if (reducedMotion === 'no-preference') await page.locator('.game-clash-stage[data-motion="playing"]').waitFor();
      await page.locator('.game-clash-stage[data-motion="still"]').waitFor();
      await checkLayout(page, `practice round ${round}, ${width}`);
      if (round === 1 && (width === 390 || width === 1280)) {
        await page.evaluate(() => window.scrollTo(0, 0));
        await page.screenshot({ path: path.join(output, `practice-${width}.png`), fullPage: true });
      }
    }
    await page.getByRole('button', { name: 'Znovu otevřít trénink', exact: true }).waitFor();
    assert.equal(await page.locator('.game-practice-scene').getAttribute('data-duel-result'), 'win');
    assert.doesNotMatch(await page.locator('.game-content').textContent(), /\+\d+\s*XP/);
    assert.equal(posts.length, postsBeforePractice,
      `practice does not send game mutations: ${JSON.stringify(posts.slice(postsBeforePractice))}`);
    await page.getByRole('button', { name: 'Znovu otevřít trénink', exact: true }).click();
    assert.deepEqual(await page.locator('.game-score strong').allTextContents(), ['0', '0']);
    assert.equal(await page.locator('.game-clash-stage').getAttribute('data-winner'), null);
    if (width === 320) {
      await page.evaluate(() => document.documentElement.style.fontSize = '200%');
      await checkLayout(page, 'practice 320, 200% text');
      await page.goto(`${origin}/__game/team-choice`);
      await page.locator('.game-team-scene').waitFor();
      await page.evaluate(() => document.documentElement.style.fontSize = '200%');
      await checkLayout(page, 'team 320, 200% text');
    }
    await page.goto(`${origin}/__game/yolo`);
    await page.locator('.game-yolo .game-clash-stage').waitFor();
    await checkWand(page, 'opponent', 'thorn');
    assert.equal(await page.locator('.game-clash-stage').getAttribute('data-winner'), null, 'YOLO artwork does not invent a combat result');
    await checkLayout(page, `YOLO wands ${width}`);
    if (width === 390) await page.screenshot({ path: path.join(output, 'yolo-wands-390.png'), fullPage: true });
    await context.close();
    console.log(`Team and practice checked at ${width} px (${reducedMotion}).`);
  }
  assert.deepEqual(errors, [], 'no uncaught browser errors');
  assert.deepEqual(failedRequests, [], 'no failed requests');
  console.log(`Team/practice passed: four-player readiness, private choices, pair reveal, halftime/transfer/expiry, second-half serialization, final score, practice without XP or mutations, keyboard, reduced motion, 200% text. Screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
