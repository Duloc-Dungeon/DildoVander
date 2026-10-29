// Two real browser clients, synthetic game/server frames; no live account or AI calls.
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const { server, origin } = await createGameFixtureServer();
const browser = await chromium.launch();
const id = 'a'.repeat(32);
const sockets = new Map();
const choices = [undefined, undefined];
let revision = 2;
const errors = [];
const timings = [];
const contexts = [];
try {
  const pages = [];
  for (const [player, width] of [390, 1280].entries()) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce' });
    contexts.push(context);
    const page = await context.newPage();
    pages.push(page);
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/app/v1/game/live/negotiate?*', route => route.fulfill({ json: {
      negotiateVersion: 1, connectionId: `player-${player}`, connectionToken: `player-${player}`,
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
    } }));
    await page.routeWebSocket('**/app/v1/game/live?*', socket => {
      socket.onMessage(message => {
        if (String(message).includes('"protocol":"json"')) {
          socket.send('{}\x1e');
          sockets.set(player, socket);
        }
      });
    });
    await page.route(`**/app/v1/game/matches/${id}{,/choose}`, async route => {
      if (route.request().method() === 'POST') {
        const command = route.request().postDataJSON();
        assert.equal(command.expectedRevision, revision);
        choices[player] = command.choice;
        revision++;
      }
      // Reuse the established game fixture for every unchanged DTO field.
      const response = await context.request.get(`${origin}/app/v1/game/matches/${id}`, { headers: { Cookie: 'game-fixture=choice' } });
      const match = await response.json();
      const revealed = choices.every(Boolean);
      const winner = player === 0 ? 'player' : 'opponent';
      await route.fulfill({ json: { ...match, revision,
        ownChoiceLocked: !revealed && Boolean(choices[player]),
        opponentChoiceLocked: !revealed && Boolean(choices[1 - player]),
        completedRounds: revealed ? 1 : 0,
        playerScore: revealed && player === 0 ? 1 : 0,
        opponentScore: revealed && player === 1 ? 1 : 0,
        rounds: revealed ? [{ index: 1, playerChoice: choices[player], opponentChoice: choices[1 - player], winner, playerPoints: player === 0 ? 1 : 0, opponentPoints: player === 1 ? 1 : 0 }] : [],
      } });
      if (route.request().method() === 'POST') {
        for (const socket of sockets.values()) socket.send(JSON.stringify({ type: 1, target: 'gameChanged', arguments: [] }) + '\x1e');
      }
    });
    await page.goto(`${origin}/__game/choice`);
    await page.getByRole('radio', { name: /Úder/ }).waitFor();
  }
  await Promise.all(pages.map(page => page.waitForFunction(() => document.querySelector('.game-round-label')?.textContent?.includes('Výměna 1'))));
  assert.equal(sockets.size, 2, 'both SignalR clients connected');
  // Freeze fallback timers; an arriving frame must be sufficient to update UI.
  await Promise.all(pages.map(page => page.clock.install()));
  await pages[0].getByRole('radio', { name: /Úder/ }).check();
  let started = performance.now();
  await pages[0].getByRole('button', { name: 'Potvrdit tah', exact: true }).click();
  await pages[1].getByText('Soupeř je připraven. Jeho tah zůstává tajný.', { exact: true }).waitFor({ timeout: 1500 });
  timings.push(Math.round(performance.now() - started));
  await pages[1].getByRole('radio', { name: /Lest/ }).check();
  started = performance.now();
  await pages[1].getByRole('button', { name: 'Potvrdit tah', exact: true }).click();
  await Promise.all(pages.map(page => page.locator('.game-round-label').filter({ hasText: 'Výměna 2 ze 7' }).waitFor({ timeout: 1500 })));
  timings.push(Math.round(performance.now() - started));
  for (const page of pages) assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ result: 'passed', viewports: [390, 1280], actionToRemoteUiMs: timings, fallbackTimersFrozen: true, fixture: 'synthetic REST and SignalR frames' }));
} finally {
  await Promise.all(contexts.map(context => context.close()));
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
