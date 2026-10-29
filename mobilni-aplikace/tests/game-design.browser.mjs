import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = path.join(tmpdir(), 'wns-game-design');
await mkdir(output, { recursive: true });
const { server, origin } = await createGameFixtureServer();
const browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL || 'msedge' });
const errors = [];
const measurements = [];
const choices = [
  [{ id: 'levitate', title: 'Levitující šálek', benefit: 'Přeletím nerovnou podlahu.', cost: 'Průvan může šálek shodit.' },
    { id: 'tray', title: 'Pochodující podnos', benefit: 'Čaj donese podnos.', cost: 'Na nerovné podlaze může klopýtnout.' },
    { id: 'cup', title: 'Šálek připravený u cíle', benefit: 'Přenesu později jen čaj.', cost: 'Soupeř může šálek posunout.' }],
  [{ id: 'protect', title: 'Zavřít okno', benefit: 'Pojistím svůj plán.', cost: 'Nepřipravím zkratku.' },
    { id: 'sabotage', title: 'Poslat průvan na soupeřův šálek', benefit: 'Naruším soupeřův plán.', cost: 'Vlastní plán nepojistím.' },
    { id: 'shortcut', title: 'Připravit přenos čaje', benefit: 'Připravím rychlé zakončení.', cost: 'Šálek nepojistím.' }],
  [{ id: 'finish', title: 'Dokončit původní plán', benefit: 'Využiju pojistku.', cost: 'Bez pojistky plán selže.' },
    { id: 'adapt', title: 'Přenést šálek v ruce', benefit: 'Obejdu zásah.', cost: 'Doručení je pomalé.' },
    { id: 'risk', title: 'Přenést čaj zkratkou', benefit: 'Obejdu cestu.', cost: 'Bez přípravy čaj skončí mimo šálek.' }]
];

try {
  for (const width of [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce', hasTouch: width < 500 });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    let snapshot;
    const sent = [];
    await page.route('**/app/v1/game/matches/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', async route => {
      if (!snapshot) snapshot = await (await route.fetch()).json();
      await route.fulfill({ json: snapshot });
    });
    await page.route('**/app/v1/game/matches/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/choose', async route => {
      sent.push(route.request().postDataJSON());
      snapshot.revision++;
      snapshot.yolo.scenario.ownChoiceLocked = true;
      await route.fulfill({ json: snapshot });
    });
    await page.goto(`${origin}/__game/choice`);
    await page.locator('.game-controls').waitFor();
    snapshot = { ...snapshot, mode: 'yolo', status: 'preparing', result: undefined, totalRounds: 0, rounds: [],
      yolo: { status: 'preparing', beats: [], verdict: '' } };
    await page.reload();
    await page.getByRole('heading', { name: 'Připravujeme váš scénář.' }).waitFor();
    assert.equal(await page.locator('.yolo-decision form').count(), 0);
    if (width === 390 || width === 1280) await page.screenshot({ path: path.join(output, `yolo-preparing-${width}.png`), fullPage: true });
    snapshot = { ...snapshot, mode: 'yolo', status: 'active', result: undefined, totalRounds: 0, rounds: [],
      yolo: { status: 'choosing', beats: [], verdict: '', scenario: { id: 'ai-scenario-v1', title: 'Čaj pro kontrolora',
        objective: 'Dostat čaj na stůl v nepoškozeném šálku.', situation: 'Kontrolor čeká na čaj. Cesta vede kolem otevřeného okna.',
        act: 1, ownChoiceLocked: false, opponentChoiceLocked: false, options: choices[0], revealed: [] } } };
    await page.reload();
    for (let act = 1; act <= 3; act++) {
      const form = page.locator('.yolo-decision form');
      await form.waitFor();
      const radio = form.getByRole('radio').first();
      await radio.focus(); await radio.press('Space');
      assert.equal(sent.length, act - 1, 'local choice is not submitted');
      await form.getByRole('button', { name: 'Potvrdit plán' }).click();
      await page.getByText('Tvůj plán je uzamčený.', { exact: false }).waitFor();
      await page.reload();
      assert.equal(await page.getByRole('button', { name: 'Potvrdit plán' }).count(), 0, 'locked choice survives reload');
      const chosen = choices[act - 1][0];
      const decision = { ...chosen, act, choice: chosen.id, consequence: act === 3 ? 'Čaj dorazil díky přípravě.' : 'Plán byl uložen a společně odhalen.' };
      snapshot.yolo.scenario.revealed.push({ act, player: decision, opponent: decision });
      snapshot.yolo.scenario.ownChoiceLocked = false;
      snapshot.yolo.scenario.act = Math.min(3, act + 1);
      snapshot.yolo.scenario.options = choices[act] || [];
      snapshot.revision++;
      if (act === 3) { snapshot.status = 'generating'; snapshot.yolo.status = 'generating'; }
      await page.evaluate(() => window.dispatchEvent(new Event('online')));
      if (act < 3) await page.getByText(`Dějství ${act + 1} ze 3 · Oba volíte tajně.`).waitFor();
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      if (act === 1) await page.screenshot({ path: path.join(output, `yolo-choices-${width}.png`), fullPage: true });
    }
    await page.getByText('Souboj se právě píše.').waitFor();
    assert.equal(sent.length, 3);
    snapshot.status = 'complete'; snapshot.result = 'draw'; snapshot.yolo.status = 'complete'; snapshot.yolo.outcome = 'draw';
    snapshot.yolo.verdict = 'Oba doručili čaj díky připravenému šálku: remíza.';
    snapshot.yolo.beats = ['Oba zvolili levitaci.', 'Oba zavřeli okno.', 'Čaj dorazil díky přípravě.'].map(text => ({ text, detailRefs: [] })); snapshot.revision++;
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.getByRole('button', { name: 'Ukázat celý výsledek' }).click();
    await page.getByRole('heading', { name: 'Remíza.', exact: true }).waitFor();
    await page.screenshot({ path: path.join(output, `yolo-result-${width}.png`), fullPage: true });
    if (width === 320) {
      await page.evaluate(() => document.documentElement.style.fontSize = '200%');
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, '200% text');
    }
    const frames = await page.evaluate(() => new Promise(resolve => {
      const times = []; let previous = performance.now(); const start = previous;
      function tick(now) { times.push(now - previous); previous = now; if (now - start < 500) requestAnimationFrame(tick); else resolve(times); }
      requestAnimationFrame(tick);
    }));
    measurements.push({ width, frames: frames.length, maxFrameMs: Math.max(...frames), medianFrameMs: [...frames].sort((a, b) => a - b)[Math.floor(frames.length / 2)] });
    await context.close();
  }
  assert.deepEqual(errors, []);
  await writeFile(path.join(output, 'measurements.json'), JSON.stringify(measurements, null, 2));
  console.log(`YOLO decision browser checks passed: three private confirmations, reload, consequences, async result, keyboard, 320/390/1280px, 200% text. Synthetic loopback only. ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
