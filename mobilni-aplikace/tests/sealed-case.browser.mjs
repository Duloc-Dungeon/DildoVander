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
const originalScenario = {
  id: 'POD_PECETI_POSLEDNI_ZVON_V1', version: '1.0', title: 'Případ posledního zvonu',
  summary: 'Pečeť byla narušena před začátkem vašich tahů.', universe: 'Harry Potter', tone: 'Tajemství a stopy',
  guardianLabel: 'Strážci', conspiratorLabel: 'Spiklenci', echoLabel: 'Ozvěna',
  echoExplanation: 'Jako Ozvěna dál pátráš a hlasuješ.', extremeActionLabel: 'Krajní čin',
  extremeConsequence: 'Tento čin může usmrtit postavu.',
};
// Only the catalog allowlist crosses the synthetic player API. Full narrative
// packs stay in the Node harness, just as the real backend filters its catalog.
const packs = JSON.parse(await readFile(new URL('../../backend/Features/SealedCase/Content/scenario-variants.json', import.meta.url), 'utf8')).variants;
const catalogFields = ['id', 'version', 'title', 'summary', 'universe', 'tone', 'guardianLabel', 'conspiratorLabel', 'echoLabel', 'echoExplanation', 'extremeActionLabel', 'extremeConsequence'];
const scenarioCatalog = { defaultScenarioId: originalScenario.id, scenarios: [originalScenario,
  ...packs.map(pack => Object.fromEntries(catalogFields.map(key => [key, pack[key]])))] };
const neighbours = scenarioCatalog.scenarios.find(scenario => scenario.id === 'HP_SPOLECENSTVI_KRBU_V1');
assert.ok(neighbours);
const players = Array.from({ length: 8 }, (_, i) => ({ id: i ? `p${i + 1}` : 'one', name: `Host ${i + 1}`, seat: `S${i + 1}`, state: i ? 'Stav neznáš' : 'Awake', location: i ? null : 'ARCHIV', mask: null, portraitUrl: null }));
const plan = { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 4 };
function view(phase) {
  return { id: caseId, phase, serverNow: 1000, chapter: 1, chapters: 6, planningEndsAt: 2000, quietEndsAt: 3000, finaleEndsAt: 9000,
    expectedPlayers: 8, readyPlayers: 8, ownId: 'one', ownReady: true, ownScenarioConsent: true, canManage: true, canStart: true,
    lobbyRevision: 9, ownState: 'Awake', ownTeam: 'Guardian', ownKit: 'Archivář', teamMembers: [], location: 'ARCHIV',
    locations: { ARCHIV: 'Archiv', SALON: 'Salon' }, adjacentLocations: ['SALON'], players, ownPlan: null,
    choices: ['Pátrat v archivu', 'Chránit sebe', 'Připravit se'].map((label, i) => ({ id: `${i}`, label, benefit: 'Pouze doložený výsledek.', cost: 'Jeden tah.', risk: 'Úspěch není zaručen.', plan: { ...plan, intent: [4, 2, 3][i], preparedIntent: i === 2 ? 4 : null } })),
    clues: [{ id: 'A1', chain: '1', family: 'A', candidates: ['p2', 'p3', 'p4'], sourceEvent: `${caseId}:prelude` }], sources: [{ id: 'A1', label: 'Záznam z archivní knihy' }],
    ownOversight: true, ownItems: [], visibleItems: [], notes: ['Zaznamenaná vlastní zkušenost.'], statements: [], ownOutcomes: [], finale: null };
}
try {
  for (const width of [320, 390, 1280]) {
    const mobile = width <= 390;
    const context = await browser.newContext({ viewport: { width, height: mobile ? 844 : 900 }, isMobile: mobile, hasTouch: mobile, reducedMotion: 'reduce' });
    const page = await context.newPage();
    page.on('pageerror', e => errors.push(e.message));
    let state = view('planning'); const requests = []; let dropAction = true; let dropCreate = true; let dropAi = true; let rejectAi = false; const aiReceipts = new Set(); let created = false;
    await page.route('**/app/v1/sealed-cases**', async route => {
      const request = route.request(); const pathname = new URL(request.url()).pathname;
      if (request.method() === 'GET') {
        if (pathname.endsWith('/scenarios')) return route.fulfill({ json: scenarioCatalog });
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
        state.scenario = scenarioCatalog.scenarios.find(scenario => scenario.id === body.scenarioId);
        if (dropCreate) { dropCreate = false; return route.abort('failed'); }
        return route.fulfill({ json: { id: caseId } });
      }
      return route.fulfill({ json: { accepted: true, replayed: true } });
    });
    await page.goto(`${origin}/play/pod-peceti/${caseId}`);
    const screenshot = name => page.screenshot({ path: path.join(output, `${name}-${width}.png`), fullPage: true });
    const assertNoHorizontalOverflow = async label => assert.equal(
      await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${label} overflow ${width}`);
    await page.getByRole('button', { name: 'Moje akce', exact: true }).click();
    await page.getByRole('button', { name: /Pátrat v archivu/ }).click();
    assert.equal(requests.length, 0, 'draft selection is local');
    const extraPlanOptions = page.locator('details').filter({ has: page.getByText('Další možnosti tahu', { exact: true }) });
    await extraPlanOptions.waitFor({ state: 'attached' });
    assert.equal(await extraPlanOptions.evaluate(element => element.open), false, 'optional plan controls start collapsed');
    for (const label of ['Adresát / cíl', 'Místo', 'Předmět / stopa']) {
      assert.equal(await page.locator('.sealed-draft label').filter({ hasText: label }).first().isVisible(), true, `${label} remains reachable in the core draft`);
    }
    const confirm = page.getByRole('button', { name: /Výslovně potvrdit plán/ });
    await confirm.waitFor();
    await screenshot('planning-action-draft');
    await extraPlanOptions.locator('summary').click();
    for (const label of ['Zaujmu dosažitelné kryté místo', 'Jak se bránit', 'Podmínka']) {
      assert.equal(await extraPlanOptions.locator('label').filter({ hasText: label }).first().isVisible(), true, `${label} remains reachable in optional plan controls`);
    }
    await screenshot('planning-action-options');
    await extraPlanOptions.locator('summary').click();
    await page.getByRole('button', { name: /Pátrat v archivu/ }).focus();
    let confirmationReachedByKeyboard = false;
    for (let index = 0; index < 40 && !confirmationReachedByKeyboard; index++) {
      await page.keyboard.press('Tab');
      confirmationReachedByKeyboard = await confirm.evaluate(element => element === document.activeElement);
    }
    assert.equal(confirmationReachedByKeyboard, true, 'keyboard can reach explicit plan confirmation');
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
    await page.locator('.sealed-evidence-part summary').first().click();
    await page.getByText('Okruh kandidátů:', { exact: false }).waitFor();
    const journalText = await page.locator('.sealed-content').innerText();
    assert.ok(journalText.includes('Záznam z archivní knihy'), 'clue source uses human-readable copy');
    assert.equal(journalText.includes(`${caseId}:prelude`), false, 'clue source event reference stays private');
    await assertNoHorizontalOverflow('journal');
    await screenshot('journal');
    state = { ...view('planning'), clues: [] };
    await page.reload();
    await page.getByRole('button', { name: 'Deník', exact: true }).click();
    await page.getByRole('button', { name: 'Doložené stopy', exact: true }).click();
    await page.getByText('Zatím nemáš novou doloženou stopu.', { exact: false }).waitFor();
    await screenshot('journal-empty');
    state = view('planning');
    state.statements = [
      { id: 'public', actor: 'two', body: 'Veřejná výpověď pro test.', recipients: [], public: true, createdAt: 1 },
      { id: 'private', actor: 'two', body: 'Soukromý vzkaz pro test.', recipients: ['one'], public: false, createdAt: 2 },
    ];
    await page.reload();
    await page.getByRole('button', { name: 'Rozhovory', exact: true }).click();
    await page.getByText('Veřejná výpověď pro test.', { exact: true }).waitFor();
    await page.getByText('Soukromý vzkaz pro test.', { exact: true }).waitFor();
    await screenshot('conversations');
    assert.equal(await page.getByLabel('Zpráva', { exact: true }).count(), 1, 'active players can compose a message');
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    await assertNoHorizontalOverflow('large text');
    await screenshot('large-text');
    await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
    if (mobile) {
      await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
      const reachability = await page.evaluate(() => {
        const content = document.querySelector('.sealed-content');
        const navigation = document.querySelector('.wns-game-navigation');
        if (!content || !navigation || getComputedStyle(navigation).position !== 'fixed') return null;
        const contentBottom = content.getBoundingClientRect().bottom + scrollY;
        const navigationHeight = navigation.getBoundingClientRect().height;
        return { bottomGap: document.documentElement.scrollHeight - contentBottom, navigationHeight };
      });
      assert.ok(reachability, 'mobile game navigation is present when expected');
      assert.ok(reachability.bottomGap >= reachability.navigationHeight - 1, 'end of content clears fixed navigation');
    }
    state = view('quiet'); await page.reload();
    await page.getByText('Telefon teď může stranou.', { exact: false }).waitFor();
    await screenshot('quiet');
    state = { ...view('planning'), players: players.map(player => ({ ...player, name: `${player.name} z Neuvěřitelně dlouhého svědeckého rodu`.repeat(3) })), choices: [] };
    await page.reload();
    await assertNoHorizontalOverflow('long roster');
    await screenshot('long-roster');
    state = { ...view('finished'), finale: { phase: 'ballot', ballotSize: 2, ownSubmitted: false, ownBallot: [], runoffCandidates: [], runoffPlaces: 0, selected: null, winner: null, conspirators: null, reconstruction: null } };
    await page.reload();
    await page.getByText('Vyber 2 ostatní hráče', { exact: false }).waitFor();
    await screenshot('ballot');
    assert.equal(await page.getByLabel('Host 1', { exact: true }).count(), 0, 'player cannot nominate themselves');
    state = view('finished');
    state.scenario = neighbours;
    state.ownOutcomes = [{ chapter: 1, actor: 'one', target: null, intent: 'Investigate', result: 'accepted', eventRef: '' }];
    state.statements = [{ id: 'private', actor: 'one', body: 'PRIVATE_CHAT_EXPORT_CANARY', recipients: ['p2'], public: false }];
    state.privateNarration = { text: 'STALE_PRELUDE_CANARY' };
    state.finale = { phase: 'released', ballotSize: 2, ownSubmitted: true, ownBallot: [], runoffCandidates: [], runoffPlaces: 0,
      winner: neighbours.guardianLabel, selected: ['p2', 'p3'], conspirators: ['p2', 'p3'], message: 'Společný krb zůstal společným.',
      reconstruction: [{ chapter: 1, actor: 'one', target: null, intent: 'Investigate', result: 'Success', eventRef: `${caseId}:event-1` }] };
    await page.reload(); await page.getByRole('heading', { name: 'Tvá stopa v případu' }).waitFor();
    const finaleText = await page.locator('.sealed-content').innerText();
    assert.ok(finaleText.indexOf(`Vítězí ${neighbours.guardianLabel}`) < finaleText.indexOf('Stáhnout veřejnou rekonstrukci'), 'verdict and reveal precede export controls');
    assert.ok(finaleText.includes(neighbours.title), 'released story retains its scenario title');
    assert.equal(finaleText.includes(originalScenario.title), false, 'a different scenario does not become the last bell at finale');
    assert.equal(finaleText.includes(`${caseId}:event-1`), false, 'event references are not shown in the reconstruction');
    assert.equal(finaleText.includes('STALE_PRELUDE_CANARY'), false, 'finished view does not retain a stale private prelude');
    const downloaded = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Uložit vlastní vzpomínku' }).click();
    const artifact = await downloaded;
    const memory = await readFile(await artifact.path(), 'utf8');
    assert.equal(JSON.parse(memory).medallion.actions.length, 1);
    assert.equal(JSON.parse(memory).scenarioId, neighbours.id);
    assert.equal(JSON.parse(memory).title, `Pod pečetí · ${neighbours.title}`);
    assert.equal(memory.includes('PRIVATE_CHAT_EXPORT_CANARY'), false, 'private chat never enters exported memory');
    const publicDownload = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Stáhnout veřejnou rekonstrukci' }).click();
    const publicArtifact = await publicDownload;
    const publicMemory = JSON.parse(await readFile(await publicArtifact.path(), 'utf8'));
    assert.equal(publicMemory.scenarioId, neighbours.id);
    assert.equal('ownNotes' in publicMemory, false, 'public reconstruction excludes personal experiences');
    await screenshot('finale');
    created = true; // /current continues to reference this released case.
    await page.getByRole('button', { name: 'Založit další případ' }).click();
    await page.waitForURL('**/play/pod-peceti?new=1');
    state = view('lobby');
    const scenarioSelect = page.getByLabel('Scénář', { exact: true });
    await scenarioSelect.waitFor();
    assert.equal(await scenarioSelect.locator('option').count(), 3, 'all three public scenarios are selectable');
    await scenarioSelect.selectOption('HP_SMRTIJEDI_BYSTROZOROVE_V1');
    await assertNoHorizontalOverflow('long scenario title');
    await scenarioSelect.selectOption(neighbours.id);
    await page.getByLabel('Délka hry', { exact: false }).selectOption('full');
    await page.getByText(neighbours.summary, { exact: true }).waitFor();
    await assertNoHorizontalOverflow('scenario selection');
    await screenshot('scenario-selection');
    await page.getByRole('button', { name: /Založit případ/ }).click();
    await page.getByRole('alert').waitFor();
    assert.equal(await scenarioSelect.isDisabled(), true, 'pending creation pins the selected scenario');
    await page.getByRole('button', { name: 'Ověřit původní založení' }).click();
    await page.waitForURL(`**/play/pod-peceti/${caseId}`);
    const creations = requests.filter(r => r.pathname === '/app/v1/sealed-cases');
    assert.equal(creations.length, 2);
    assert.equal(creations[0].body.scenarioId, neighbours.id);
    assert.equal(creations[0].body.variant, 'full', 'duration remains independent of story');
    assert.deepEqual(creations[0].body, creations[1].body, 'lost create ack keeps operation id');
    state = { ...view('lobby'), readyPlayers: 1, players: [players[0]], canManageAi: true, aiAvailable: true };
    await page.reload();
    await page.getByText('Doplnit AI hráče', { exact: true }).click();
    await page.getByLabel('Počet AI hráčů').fill('1');
    await page.getByRole('button', { name: 'Přidat AI hráče', exact: true }).click();
    await page.getByRole('button', { name: 'Ověřit původní přidání AI' }).waitFor();
    await page.reload();
    const pendingAiDetails = page.locator('details').filter({ has: page.getByText('Doplnit AI hráče', { exact: true }) });
    assert.equal(await pendingAiDetails.evaluate(element => element.open), false, 'AI controls return to the collapsed default after reload');
    await page.getByText('Doplnit AI hráče', { exact: true }).click();
    await page.getByRole('button', { name: 'Ověřit původní přidání AI' }).click();
    const additions = requests.filter(r => r.pathname.endsWith('/ai-players'));
    assert.equal(additions.length, 2);
    assert.deepEqual(additions[0].body, additions[1].body, 'AI addition retry keeps the exact persisted key/revision/count');
    assert.equal(state.readyPlayers, 2, 'lost acknowledgement cannot duplicate AI seats');
    const aiDetails = page.locator('details').filter({ has: page.getByText('Doplnit AI hráče', { exact: true }) });
    assert.equal(await aiDetails.evaluate(element => element.open), true, 'AI controls stay open while the owner completes the task');
    const rosterSummary = page.locator('summary').filter({ hasText: 'Postavy u stolu' });
    assert.equal(await rosterSummary.count(), 1);
    assert.match(await rosterSummary.innerText(), /2/, 'collapsed roster summary reflects the accepted AI seat');
    const rosterDetails = page.locator('details').filter({ has: rosterSummary });
    assert.equal(await rosterDetails.evaluate(element => element.open), false, 'roster details remain collapsed after membership changes');
    await screenshot('owner-ai-lobby');
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
    await page.getByText('Doplnit AI hráče', { exact: true }).click();
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
  console.log(`PASS sealed client: 320/390/1280px, scenario catalog and separate duration, scenario finale/exports, explicit draft and retry, journal/conversations/empty/long/ballot/quiet states, responsive end reachability, lost create/AI acknowledgements and reload, privacy and no page errors. ${output}`);
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
