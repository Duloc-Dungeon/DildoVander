// Synthetic API + real rendered client: transport retries and mobile layouts.
import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-sealed-case');
await mkdir(output, { recursive: true });
const { server, origin } = await createGameFixtureServer();
const browser = await chromium.launch();
const errors = [];
const caseId = 'sc_' + 'a'.repeat(32);
const players = Array.from({ length: 8 }, (_, i) => ({ id: i ? `p${i + 1}` : 'one', name: `Host ${i + 1}`, seat: `S${i + 1}`, state: i ? 'Stav neznáš' : 'Awake', location: i ? null : 'ARCHIV', mask: null, portraitUrl: null }));
const plan = { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 4 };
function view(phase) {
  return { id: caseId, phase, serverNow: 1000, chapter: 1, chapters: 6, planningEndsAt: 2000, quietEndsAt: 3000, finaleEndsAt: 9000,
    expectedPlayers: 8, readyPlayers: 8, ownId: 'one', ownReady: true, ownScenarioConsent: true, canManage: true, canStart: true,
    lobbyRevision: 9, ownState: 'Awake', ownTeam: 'Guardian', ownKit: 'Archivář', teamMembers: [], location: 'ARCHIV',
    locations: { ARCHIV: 'Archiv', SALON: 'Salon' }, adjacentLocations: ['SALON'], players, ownPlan: null,
    choices: ['Pátrat v archivu', 'Chránit sebe', 'Připravit se'].map((label, i) => ({ id: `${i}`, label, benefit: 'Pouze doložený výsledek.', cost: 'Jeden tah.', risk: 'Úspěch není zaručen.', plan: { ...plan, intent: [4, 2, 3][i], preparedIntent: i === 2 ? 4 : null } })),
    clues: [{ id: 'A1', chain: '1', family: 'A', candidates: ['p2', 'p3', 'p4'], sourceEvent: `${caseId}:prelude` }], sources: [{ id: 'A1', label: 'Stopa A1' }],
    ownOversight: true, ownItems: [], visibleItems: [], notes: ['Zaznamenaná vlastní zkušenost.'], statements: [], ownOutcomes: [], finale: null };
}
try {
  for (const width of [320, 390]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
    const page = await context.newPage();
    page.on('pageerror', e => errors.push(e.message));
    let state = view('planning'); const requests = []; let dropAction = true; let dropCreate = true; let dropAi = true; let rejectAi = false; const aiReceipts = new Set(); let created = false;
    await page.route('**/app/v1/sealed-cases**', async route => {
      const request = route.request(); const pathname = new URL(request.url()).pathname;
      if (request.method() === 'GET') {
        return route.fulfill({ json: pathname.endsWith('/current') ? { id: created ? caseId : null } : state });
      }
      const body = request.postDataJSON(); requests.push({ pathname, body });
      if (pathname.endsWith('/actions')) {
        state.ownPlan = { ...body.plan, id: 'accepted', actorId: 'one', revision: 1 };
        if (dropAction) { dropAction = false; return route.abort('failed'); }
      }
      if (pathname.endsWith('/ai-players')) {
        if (rejectAi) { rejectAi = false; state.lobbyRevision++; return route.fulfill({ status: 409, json: { code: 'revision_conflict' } }); }

        if (!aiReceipts.has(body.operationId)) {
          aiReceipts.add(body.operationId); state.readyPlayers += body.count; state.lobbyRevision++;
          for (let index = 0; index < body.count; index++) state.players.push({ ...players[1], id: `ai_fixture_${state.players.length}`, name: `Svědek AI ${state.players.length}`, isAi: true });
          if (dropAi) { dropAi = false; return route.abort('failed'); }
        }
        return route.fulfill({ json: { accepted: true, replayed: true } });
      }
      if (pathname === '/app/v1/sealed-cases') {
        created = true;
        if (dropCreate) { dropCreate = false; return route.abort('failed'); }
        return route.fulfill({ json: { id: caseId } });
      }
      return route.fulfill({ json: { accepted: true, replayed: true } });
    });
    await page.goto(`${origin}/play/pod-peceti/${caseId}`);
    await page.getByRole('button', { name: 'Moje akce', exact: true }).click();
    await page.getByRole('button', { name: /Pátrat v archivu/ }).click();
    assert.equal(requests.length, 0, 'draft selection is local');
    const confirm = page.getByRole('button', { name: /Výslovně potvrdit plán/ });
    await page.keyboard.press('Tab'); await confirm.focus();
    assert.equal(await confirm.evaluate(element => element.matches(':focus-visible')), true, 'keyboard confirmation focus is visible');
    assert.equal(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches), true);
    await confirm.click();
    await page.getByRole('button', { name: /Ověřit původní potvrzení/ }).waitFor();
    await page.reload();
    await page.getByRole('button', { name: 'Moje akce', exact: true }).click();
    await page.getByRole('button', { name: /Ověřit původní potvrzení/ }).click();
    assert.equal(requests.filter(r => r.pathname.endsWith('/actions')).length, 2);
    assert.deepEqual(requests[0].body, requests[1].body, 'lost ack repeats exact persisted operation');
    await page.getByRole('button', { name: 'Deník', exact: true }).click();
    await page.getByRole('button', { name: 'Doložené stopy', exact: true }).click();
    await page.getByText('Okruh kandidátů:', { exact: false }).waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `overflow ${width}`);
    await page.screenshot({ path: path.join(output, `journal-${width}.png`), fullPage: true });
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `large text overflow ${width}`);
    await page.screenshot({ path: path.join(output, `large-text-${width}.png`), fullPage: true });
    await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
    state = view('quiet'); await page.reload();
    await page.getByText('Telefon teď může stranou.', { exact: false }).waitFor();
    await page.screenshot({ path: path.join(output, `quiet-${width}.png`), fullPage: true });
    state = view('finished');
    state.ownOutcomes = [{ chapter: 1, actor: 'one', target: null, intent: 'Investigate', result: 'accepted', eventRef: '' }];
    state.statements = [{ id: 'private', actor: 'one', body: 'PRIVATE_CHAT_EXPORT_CANARY', recipients: ['p2'], public: false }];
    state.finale = { phase: 'released', ballotSize: 2, ownSubmitted: true, ownBallot: [], runoffCandidates: [], runoffPlaces: 0,
      winner: 'Strážci', selected: ['p2', 'p3'], conspirators: ['p2', 'p3'],
      reconstruction: [{ chapter: 1, actor: 'one', target: null, intent: 'Investigate', result: 'Success', eventRef: `${caseId}:event-1` }] };
    await page.reload(); await page.getByRole('heading', { name: 'Tvá stopa v případu' }).waitFor();
    const downloaded = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Uložit vlastní vzpomínku' }).click();
    const artifact = await downloaded;
    const memory = await readFile(await artifact.path(), 'utf8');
    assert.equal(JSON.parse(memory).medallion.actions.length, 1);
    assert.equal(memory.includes('PRIVATE_CHAT_EXPORT_CANARY'), false, 'private chat never enters exported memory');
    await page.screenshot({ path: path.join(output, `finale-${width}.png`), fullPage: true });
    created = false; state = view('lobby'); await page.goto(`${origin}/play/pod-peceti`);
    await page.getByRole('button', { name: /Založit případ/ }).click();
    await page.getByRole('alert').waitFor();
    await page.getByRole('button', { name: /Založit případ/ }).click();
    const creations = requests.filter(r => r.pathname === '/app/v1/sealed-cases');
    assert.equal(creations.length, 2);
    assert.deepEqual(creations[0].body, creations[1].body, 'lost create ack keeps operation id');
    state = { ...view('lobby'), readyPlayers: 1, players: [players[0]], canManageAi: true, aiAvailable: true };
    await page.reload();
    await page.getByLabel('Počet AI hráčů').fill('1');
    await page.getByRole('button', { name: 'Přidat AI hráče', exact: true }).click();
    await page.getByRole('button', { name: 'Ověřit původní přidání AI' }).waitFor();
    await page.reload();
    await page.getByRole('button', { name: 'Ověřit původní přidání AI' }).click();
    const additions = requests.filter(r => r.pathname.endsWith('/ai-players'));
    assert.equal(additions.length, 2);
    assert.deepEqual(additions[0].body, additions[1].body, 'AI addition retry keeps the exact persisted key/revision/count');
    assert.equal(state.readyPlayers, 2, 'lost acknowledgement cannot duplicate AI seats');
    await page.getByText('Svědek AI 1 · AI', { exact: false }).waitFor();
    await page.screenshot({ path: path.join(output, `owner-ai-lobby-${width}.png`), fullPage: true });
    rejectAi = true;
    await page.getByRole('button', { name: 'Přidat AI hráče', exact: true }).click();
    await page.getByText('Obsazení se mezitím změnilo.', { exact: false }).waitFor();
    await page.getByRole('button', { name: 'Přidat AI hráče', exact: true }).click();
    const conflicts = requests.filter(r => r.pathname.endsWith('/ai-players')).slice(-2);
    assert.notEqual(conflicts[0].body.operationId, conflicts[1].body.operationId, 'definitive conflict permits a new request');
    assert.ok(conflicts[1].body.expectedRevision > conflicts[0].body.expectedRevision);
    await page.getByRole('button', { name: 'Doplnit volná místa AI' }).click();
    assert.equal(state.readyPlayers, 8, 'owner can fill exactly the remaining capacity');
    state.canManageAi = false; await page.reload();
    assert.equal(await page.getByRole('heading', { name: 'AI hráči', exact: true }).count(), 0, 'ordinary host sees no owner control');
    state.canManageAi = true; state.aiAvailable = false; await page.reload();
    await page.getByText('AI pro tento večer není zapnutá.', { exact: false }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Přidat AI hráče', exact: true }).count(), 0);
    state = { ...view('planning'), ownState: 'Operator', ownTeam: null, choices: [], clues: [], notes: [], ownOutcomes: [], canJoin: false };
    await page.reload();
    await page.getByText('Nesoutěžící pozorovatel', { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Moje akce', exact: true }).count(), 0);
    await page.getByRole('button', { name: 'Rozhovory', exact: true }).click();
    assert.equal(await page.getByLabel('Zpráva', { exact: true }).count(), 0, 'observer cannot compose a player message');
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(`PASS sealed client: 320/390px, explicit draft, lost action/create ack and reload, journal, quiet, owner-only AI addition/disabled/reload and no duplicate seats, no page errors. ${output}`);
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
