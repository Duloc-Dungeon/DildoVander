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
    choices: [
      { id: 'investigate', label: 'Pátrat', benefit: 'Doložený výsledek.', cost: 'Jeden tah.', risk: 'Nový nález není zaručen.', plan: { ...plan, intent: 4 } },
      { id: 'guard', label: 'Chránit sebe', benefit: 'Přítomná ochrana.', cost: 'Jeden tah.', risk: 'Nejde o důkaz útoku.', plan: { ...plan, intent: 2, targetId: null } },
      { id: 'delay-A1', label: 'Zdržet stopu', benefit: 'Odloží novou dostupnost.', cost: 'Jeden tah.', risk: 'Může selhat.', plan: { ...plan, intent: 7, method: 'DELAY', resourceId: 'A1', targetId: null } },
    ],
    clues: [{ id: 'A1', chain: '1', family: 'A', candidates: ['p2', 'p3', 'p4'], sourceEvent: `${caseId}:prelude` }], sources: [{ id: 'A1', label: 'Záznam z archivní knihy' }],
    ownOversight: true, ownItems: [], visibleItems: [], notes: ['Zaznamenaná vlastní zkušenost.'], statements: [], ownOutcomes: [], finale: null };
}
const shortId = 'sc_' + 'b'.repeat(32);
const shortPlayers = Array.from({ length: 8 }, (_, index) => ({
  id: `pilot-${index + 1}`, name: `Pilot ${index + 1}`, seat: `S${index + 1}`,
  state: 'Awake', location: index < 2 ? 'VESTIBUL' : 'SALON', mask: null, portraitUrl: null,
}));
function shortView(ownId, phase = 'lobby', chapter = 1) {
  const index = Number(ownId.slice(-1)) - 1;
  return {
    id: shortId, phase, serverNow: 1000, chapter, chapters: 3,
    planningEndsAt: 2000, quietEndsAt: 3000, finaleEndsAt: 9000,
    expectedPlayers: 8, readyPlayers: 0, ownId, ownReady: false, ownScenarioConsent: false,
    canManage: ownId === 'pilot-1', canStart: false, lobbyRevision: 1,
    ownState: 'Awake', ownTeam: index === 1 || index === 4 ? 'Conspirator' : 'Guardian', ownKit: 'Badatel',
    ownObjective: `SOUKROMÝ_CÍL_${ownId}`, teamMembers: [], location: index < 2 ? 'VESTIBUL' : 'SALON',
    locations: { VESTIBUL: 'Vstupní síň', SALON: 'Salon', KOMORA: 'Komora' },
    adjacentLocations: index < 2 ? ['SALON'] : ['VESTIBUL', 'KOMORA'],
    routes: { VESTIBUL: ['SALON'], SALON: ['VESTIBUL', 'KOMORA'], KOMORA: ['SALON'] },
    players: shortPlayers, ownPlan: null,
    choices: [
      { id: 'investigate', label: 'Pátrat', benefit: 'Výsledek podle zkušenosti.', cost: 'Jeden tah.', risk: 'Nový nález není zaručen.', plan: { chapter, expectedActionRevision: 0, confirmed: true, intent: 4 } },
      { id: 'guard', label: 'Chránit sebe', benefit: 'Přítomná ochrana.', cost: 'Jeden tah.', risk: 'Nedokazuje útok.', plan: { chapter, expectedActionRevision: 0, confirmed: true, intent: 2 } },
      { id: 'sabotage-delay', label: 'Zdržet stopu', benefit: 'Zdrží novou dostupnost.', cost: 'Jeden tah.', risk: 'Může selhat.', plan: { chapter, expectedActionRevision: 0, confirmed: true, intent: 7, method: 'DELAY' } },
    ],
    clues: ownId === 'pilot-1' ? [{ id: 'A1', chain: '1', family: 'A', candidates: ['pilot-2'], sourceEvent: 'PRIVATE_EVENT_SOURCE' }] : [],
    sources: chapter === 3 && index < 2
      ? [{ id: 'C1', label: 'Rozpis krbu · část C' }]
      : [{ id: index < 2 ? 'A1' : 'A2', label: `Kniha příchodů · část A · ${index < 2 ? 'síň' : 'salon'}` }],
    sourceSchedule: [
      { family: 'A', label: 'Kniha příchodů', location: 'VESTIBUL', releaseChapter: 1 },
      { family: 'C', label: 'Rozpis krbu', location: 'KOMORA', releaseChapter: 3 },
    ],
    ownOversight: false, ownItems: [], visibleItems: [], notes: [], statements: [], ownOutcomes: [],
    scenario: neighbours, publicNarration: { text: 'Před domovní schůzí se uvolnil pořadník krbu.' },
    finale: null,
  };
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
    await page.keyboard.press('Tab');
    const actionPanel = page.locator('#sealed-panel');
    await actionPanel.focus();
    assert.equal(await actionPanel.evaluate(element => document.activeElement === element), true, 'action panel remains an announcement target');
    assert.equal(await actionPanel.evaluate(element => getComputedStyle(element).outlineStyle), 'none', 'announcing the action panel does not frame the whole section');
    await page.getByRole('button', { name: /Pátrat/ }).click();
    assert.equal(requests.length, 0, 'draft selection is local');
    const extraPlanOptions = page.locator('details').filter({ has: page.getByText('Další možnosti tahu', { exact: true }) });
    await extraPlanOptions.waitFor({ state: 'attached' });
    assert.equal(await extraPlanOptions.evaluate(element => element.open), false, 'optional plan controls start collapsed');
    for (const label of ['Adresát / cíl', 'Místo']) {
      assert.equal(await page.locator('.sealed-draft label').filter({ hasText: label }).first().isVisible(), true, `${label} remains reachable in the core draft`);
    }
    assert.equal(await page.locator('.sealed-draft label').filter({ hasText: 'Předmět / stopa' }).count(), 0, 'investigation follows existing automatic clue selection');
    const confirm = page.getByRole('button', { name: /Výslovně potvrdit plán/ });
    await confirm.waitFor();
    await screenshot('planning-action-draft');
    await extraPlanOptions.locator('summary').click();
    for (const label of ['Zaujmu dosažitelné kryté místo', 'Jak se bránit', 'Podmínka']) {
      assert.equal(await extraPlanOptions.locator('label').filter({ hasText: label }).first().isVisible(), true, `${label} remains reachable in optional plan controls`);
    }
    await screenshot('planning-action-options');
    await extraPlanOptions.locator('summary').click();
    await page.getByRole('button', { name: /Pátrat/ }).focus();
    let confirmationReachedByKeyboard = false;
    for (let index = 0; index < 40 && !confirmationReachedByKeyboard; index++) {
      await page.keyboard.press('Tab');
      confirmationReachedByKeyboard = await confirm.evaluate(element => element === document.activeElement);
    }
    assert.equal(confirmationReachedByKeyboard, true, 'keyboard can reach explicit plan confirmation');
    assert.equal(await confirm.evaluate(element => element.matches(':focus-visible')), true, 'keyboard confirmation focus is visible');
    assert.equal(await confirm.evaluate(element => {
      const style = getComputedStyle(element);
      return style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0;
    }), true, 'plan confirmation retains its rendered keyboard outline');
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

  // Deterministic synthetic API walkthrough for the three-chapter human pilot.
  // Eight isolated browser contexts represent eight separate player sessions;
  // they do not exercise real accounts, a live server, or human decisions.
  const pilot = { created: false, phase: 'lobby', chapter: 1, ready: new Set(), plans: new Map(), outcomes: new Map(),
    positions: new Map(shortPlayers.map(player => [player.id, player.location])), ballots: new Map(), actions: [], actionReceipts: new Set(),
    createRequests: [], dropCreateAck: true, dropFirstActionAck: true, finale: null };
  const sessions = [];
  for (let index = 0; index < shortPlayers.length; index++) {
    const playerId = `pilot-${index + 1}`;
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    const projection = () => {
      const value = shortView(playerId, pilot.phase, pilot.chapter);
      value.readyPlayers = pilot.ready.size;
      value.ownReady = pilot.ready.has(playerId);
      value.ownScenarioConsent = pilot.ready.has(playerId);
      value.canStart = pilot.ready.size === 8;
      value.location = pilot.positions.get(playerId);
      value.sources = pilot.chapter === 3 && value.location === 'KOMORA'
        ? [{ id: 'C1', label: 'Rozpis krbu · část C' }]
        : [{ id: value.location === 'VESTIBUL' ? 'A1' : 'A2', label: `Kniha příchodů · část A · ${value.location === 'VESTIBUL' ? 'síň' : 'salon'}` }];
      value.adjacentLocations = value.location === 'VESTIBUL' ? ['SALON'] : value.location === 'KOMORA' ? ['SALON'] : ['VESTIBUL', 'KOMORA'];
      value.ownPlan = pilot.plans.get(`${playerId}:${pilot.chapter}`) ?? null;
      value.ownOutcomes = pilot.outcomes.get(playerId) ?? [];
      value.ownSubmitted = pilot.ballots.has(playerId);
      if (pilot.phase === 'finale' || pilot.phase === 'finished') value.finale = { ...pilot.finale,
        ownSubmitted: pilot.ballots.has(playerId), ownBallot: pilot.ballots.get(playerId) ?? [] };
      return value;
    };
    await page.route('**/app/v1/sealed-cases**', async route => {
      const request = route.request(); const pathname = new URL(request.url()).pathname;
      if (request.method() === 'GET') {
        if (pathname.endsWith('/scenarios')) return route.fulfill({ json: scenarioCatalog });
        if (pathname.endsWith('/current')) return route.fulfill({ json: { id: pilot.created ? shortId : null } });
        return route.fulfill({ json: projection() });
      }
      const body = request.postDataJSON();
      if (pathname === '/app/v1/sealed-cases') {
        pilot.created = true;
        pilot.createRequests.push(body);
        if (pilot.dropCreateAck) {
          pilot.dropCreateAck = false;
          return route.abort('failed');
        }
        return route.fulfill({ json: { id: shortId } });
      }
      if (pathname.endsWith('/lobby')) {
        pilot.ready.add(playerId);
        return route.fulfill({ json: { accepted: true } });
      }
      if (pathname.endsWith('/start')) {
        pilot.phase = 'planning';
        return route.fulfill({ json: { accepted: true } });
      }
      if (pathname.endsWith('/actions')) {
        const key = `${playerId}:${body.plan.chapter}`;
        const receipt = `${key}:${body.operationId}`;
        if (!pilot.actionReceipts.has(receipt)) {
          pilot.actionReceipts.add(receipt);
          pilot.actions.push({ playerId, plan: body.plan, operationId: body.operationId });
          pilot.plans.set(key, { ...body.plan, id: `action-${playerId}-${body.plan.chapter}`, actorId: playerId, revision: body.plan.expectedActionRevision + 1 });
        }
        if (playerId === 'pilot-1' && body.plan.chapter === 1 && pilot.dropFirstActionAck) {
          pilot.dropFirstActionAck = false;
          return route.abort('failed');
        }
        return route.fulfill({ json: { accepted: true } });
      }
      if (pathname.endsWith('/ballots')) {
        pilot.ballots.set(playerId, body.nominees);
        return route.fulfill({ json: { accepted: true } });
      }
      return route.fulfill({ json: { accepted: true } });
    });
    sessions.push({ context, page, playerId, projection });
  }

  const first = sessions[0].page;
  await first.goto(`${origin}/play/pod-peceti?new=1`);
  await first.getByLabel('Scénář', { exact: true }).selectOption(neighbours.id);
  await first.getByRole('button', { name: 'Založit případ' }).click();
  await first.getByRole('button', { name: 'Ověřit původní založení' }).waitFor();
  await first.getByRole('button', { name: 'Ověřit původní založení' }).click();
  await first.waitForURL(`**/play/pod-peceti/${shortId}`);
  assert.equal(pilot.created, true);
  assert.equal(pilot.createRequests.length, 2);
  assert.deepEqual(pilot.createRequests[0], pilot.createRequests[1], 'a short-game lost response retries the same creation operation');
  assert.equal(pilot.createRequests[0].expectedPlayers, 8);
  assert.equal(pilot.createRequests[0].variant, 'first');
  assert.equal(pilot.createRequests[0].scenarioId, neighbours.id);
  const pendingCreates = await first.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('dv:sealed-create:')));
  assert.deepEqual(pendingCreates, [], 'confirmed create removes its retry record');
  const createLog = `simulated-8-session-${shortId}`;
  await first.getByRole('button', { name: 'Zahájit případ' }).waitFor();
  assert.equal(await first.locator('summary').filter({ hasText: 'Krátká pravidla případu' }).count(), 1, 'the private-intro screen makes the rules available before consent');
  await first.locator('summary').filter({ hasText: 'Krátká pravidla případu' }).click();
  for (const text of ['soukromý cíl', 'výslovném potvrzení', 'Vlastní stopa je doložený důkaz', 'A/B/C', 'Soukromý telefon']) {
    await first.getByText(new RegExp(text, 'i')).first().waitFor();
  }
  await first.screenshot({ path: path.join(output, 'pilot-short-private-intro.png'), fullPage: true });
  await first.locator('label').filter({ hasText: 'Přijímám scénářovou minulost' }).locator('input').check();
  await first.getByRole('button', { name: 'Připojit mou postavu' }).click();
  for (const session of sessions.slice(1)) {
    await session.page.goto(`${origin}/play/pod-peceti/${shortId}`);
    await session.page.locator('label').filter({ hasText: 'Přijímám scénářovou minulost' }).locator('input').check();
    await session.page.getByRole('button', { name: 'Připojit mou postavu' }).click();
  }
  assert.equal(pilot.ready.size, 8, 'eight isolated sessions explicitly accept the private introduction');
  await first.reload();
  await first.getByRole('button', { name: 'Zahájit případ' }).click();
  for (const session of sessions) await session.page.reload();
  for (const session of sessions) {
    await session.page.getByRole('button', { name: 'Moje akce', exact: true }).click();
    assert.deepEqual(await session.page.locator('.sealed-choices strong').allTextContents(), ['Pátrat', 'Chránit sebe', 'Zdržet stopu']);
    await session.page.locator('.sealed-identity summary').click();
    await session.page.getByText(session.projection().ownObjective, { exact: true }).waitFor();
    assert.equal((await session.page.locator('.sealed-content').innerText()).includes('SOUKROMÝ_CÍL_pilot-1') && session.playerId !== 'pilot-1', false,
      'a separate session does not receive another player’s private objective');
  }
  await first.screenshot({ path: path.join(output, 'pilot-short-action-menu.png'), fullPage: true });
  const act = async (session, intentIndex, destination = null, sourceId = null) => {
    const page = session.page;
    await page.getByRole('button', { name: 'Moje akce', exact: true }).click();
    await page.locator('.sealed-choices button').nth(intentIndex).click();
    if (destination) {
      const exploration = page.locator('.sealed-exploration');
      if (!(await exploration.evaluate(element => element.open))) await exploration.locator('summary').click();
      await page.getByRole('button', { name: `Připravit přesun: ${destination === 'SALON' ? 'Salon' : 'Komora'}` }).first().click();
    }
    if (sourceId) await page.locator('.sealed-draft label').filter({ hasText: 'Předmět / stopa' }).locator('select').selectOption(sourceId);
    const before = pilot.actions.length;
    await page.getByRole('button', { name: 'Výslovně potvrdit plán' }).click();
    if (session.playerId === 'pilot-1' && pilot.chapter === 1) {
      await page.getByRole('button', { name: 'Ověřit původní potvrzení' }).waitFor();
      await page.reload();
      await page.getByRole('button', { name: 'Moje akce', exact: true }).click();
      await page.getByRole('button', { name: 'Ověřit původní potvrzení' }).click();
      await page.getByText('Tvůj plán je uložený a čeká na společné vyhodnocení.', { exact: false }).waitFor();
      const attempts = pilot.actions.filter(item => item.playerId === session.playerId && item.plan.chapter === 1);
      assert.equal(attempts.length, 1, 'the lost acknowledgement retry preserves one action effect');
      assert.equal(attempts[0].plan.destination, 'SALON');
    } else {
      assert.equal(pilot.actions.length, before + 1, 'selecting an action requires a separate explicit confirmation');
      await page.getByText('Tvůj plán je uložený a čeká na společné vyhodnocení.', { exact: false }).waitFor();
    }
  };

  await act(sessions[0], 0, 'SALON');
  await act(sessions[1], 1, 'SALON');
  await act(sessions[2], 2, null, 'A2');
  await act(sessions[3], 0);
  await act(sessions[4], 1);
  await act(sessions[5], 2, null, 'A2');
  await act(sessions[6], 0);
  await act(sessions[7], 1);
  assert.equal(pilot.actions.length, 8, 'all eight players confirm one chapter-one action');
  await first.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await first.screenshot({ path: path.join(output, 'pilot-short-chapter-one-mobile.png'), fullPage: true });

  const resolveChapter = chapter => {
    for (const [playerId, plan] of pilot.plans) {
      const [actor, planChapter] = playerId.split(':');
      if (Number(planChapter) !== chapter) continue;
      if (plan.destination) pilot.positions.set(actor, plan.destination);
      const intent = ['Hold', 'Rest', 'Guard', 'Prepare', 'Investigate', 'Follow', 'Steal', 'Sabotage'][plan.intent];
      const outcome = { chapter, actor, target: null, intent, result: 'Tah se vyhodnotil', eventRef: '',
        observation: intent === 'Investigate' ? 'Místo bez nové dostupné stopy.' : null,
        actionObservation: intent === 'Sabotage' ? { actionRef: plan.id, chapter, kind: actor === 'pilot-3' ? 'DELAY_SUCCEEDED' : 'DELAY_NOT_OBSERVED' } : null };
      pilot.outcomes.set(actor, [...(pilot.outcomes.get(actor) ?? []), outcome]);
      pilot.plans.delete(playerId);
    }
    pilot.chapter = chapter + 1;
  };
  resolveChapter(1);
  for (const session of sessions) await session.page.reload();
  await sessions[2].page.getByText('Tvá zaznamenaná zkušenost potvrzuje úspěšné zdržení nové dostupnosti této stopy.', { exact: true }).waitFor();
  await sessions[5].page.getByText('Výsledek zůstává neznámý; neříká nic o cizí ochraně.', { exact: false }).waitFor();
  assert.equal((await sessions[1].page.locator('.sealed-content').innerText()).includes('odrazil útok'), false,
    'the protected player receives no invented observation of an attack');
  await first.getByRole('button', { name: 'Moje akce', exact: true }).click();
  await first.locator('.sealed-exploration summary').waitFor();
  await first.getByRole('button', { name: 'Chránit sebe', exact: false }).click();
  await first.locator('.sealed-exploration summary').click();
  await first.getByRole('button', { name: 'Připravit přesun: Komora' }).click();
  await first.getByRole('button', { name: 'Výslovně potvrdit plán' }).click();
  await first.getByText('Tvůj plán je uložený a čeká na společné vyhodnocení.', { exact: false }).waitFor();
  for (const session of sessions.slice(1)) {
    const index = Number(session.playerId.slice(-1));
    await act(session, index % 3 === 0 ? 2 : index % 2 === 0 ? 1 : 0, index === 2 ? 'KOMORA' : null,
      index % 3 === 0 ? 'A2' : null);
  }
  resolveChapter(2);
  for (const session of sessions) await session.page.reload();
  await first.getByRole('button', { name: 'Moje akce', exact: true }).click();
  await first.locator('.sealed-exploration summary').click();
  await first.getByText('Dorazil jsi včas; zdroj C je teď uvolněný a viditelný.', { exact: true }).waitFor();
  assert.equal(pilot.positions.get('pilot-1'), 'KOMORA');
  assert.equal(first.url().includes(shortId), true, createLog);
  await first.locator('.sealed-choices button').nth(2).click();
  const finalSource = first.locator('.sealed-draft label').filter({ hasText: 'Předmět / stopa' }).locator('select');
  assert.deepEqual(await finalSource.locator('option').allTextContents(), ['Vyber dostupný zdroj', 'Rozpis krbu · část C']);
  await finalSource.selectOption('C1');
  await first.getByText('V poslední kapitole po úspěchu nikdo tuto stopu nově nezíská', { exact: false }).waitFor();
  await first.setViewportSize({ width: 320, height: 844 });
  assert.equal(await first.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'short action form fits a 320px mobile viewport');
  const normalTextSize = Number.parseFloat(await first.locator('.sealed-action-guidance').evaluate(element => getComputedStyle(element).fontSize));
  await first.locator('.sealed-draft').screenshot({ path: path.join(output, 'pilot-short-delay-form-normal.png') });
  await first.screenshot({ path: path.join(output, 'pilot-short-final-delay.png'), fullPage: true });
  await first.evaluate(() => {
    const textSizes = [...document.querySelectorAll('body *')]
      .filter(element => [...element.childNodes].some(node => node.nodeType === Node.TEXT_NODE && node.textContent.trim()))
      .map(element => [element, Number.parseFloat(getComputedStyle(element).fontSize)]);
    for (const [element, fontSize] of textSizes) element.style.setProperty('font-size', `${fontSize * 2}px`, 'important');
  });
  const enlargedTextSize = Number.parseFloat(await first.locator('.sealed-action-guidance').evaluate(element => getComputedStyle(element).fontSize));
  assert.ok(enlargedTextSize >= normalTextSize * 1.9, 'representative action text actually scales to 200%');
  assert.equal(await first.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'short action form remains usable at 200% text');
  await first.getByRole('button', { name: 'Výslovně potvrdit plán' }).scrollIntoViewIfNeeded();
  assert.equal(await first.getByRole('button', { name: 'Výslovně potvrdit plán' }).isVisible(), true, 'plan confirmation remains reachable after scrolling at 200% text');
  await first.locator('.sealed-draft').screenshot({ path: path.join(output, 'pilot-short-delay-form-200-percent.png') });
  await first.screenshot({ path: path.join(output, 'pilot-short-final-delay-200-percent.png'), fullPage: true });
  await first.getByRole('button', { name: 'Výslovně potvrdit plán' }).click();
  await first.getByText('Tvůj plán je uložený a čeká na společné vyhodnocení.', { exact: false }).waitFor();
  const lastDelay = pilot.actions.find(item => item.playerId === 'pilot-1' && item.plan.chapter === 3);
  assert.equal(lastDelay.plan.resourceId, 'C1');
  assert.equal(lastDelay.plan.method, 'DELAY');
  pilot.outcomes.set('pilot-1', [...(pilot.outcomes.get('pilot-1') ?? []), {
    chapter: 3, actor: 'pilot-1', target: null, intent: 'Sabotage', result: 'Tah se vyhodnotil', eventRef: '',
    actionObservation: { actionRef: 'action-pilot-1-3', chapter: 3, kind: 'DELAY_SUCCEEDED' },
  }]);
  pilot.phase = 'finale';
  pilot.finale = { phase: 'ballot', ballotSize: 2, ownSubmitted: false, ownBallot: [], runoffCandidates: [], runoffPlaces: 0,
    selected: null, winner: null, conspirators: null, reconstruction: null };
  for (const session of sessions) await session.page.reload();
  await first.screenshot({ path: path.join(output, 'pilot-short-secret-ballot.png'), fullPage: true });
  for (const session of sessions) {
    const page = session.page;
    const nominees = session.playerId === 'pilot-2' ? ['Pilot 3', 'Pilot 5']
      : session.playerId === 'pilot-5' ? ['Pilot 2', 'Pilot 3'] : ['Pilot 2', 'Pilot 5'];
    for (const nominee of nominees) await page.getByRole('checkbox', { name: nominee, exact: true }).check();
    await page.getByRole('button', { name: 'Potvrdit tajný hlas' }).click();
    assert.equal(pilot.ballots.has(session.playerId), true);
  }
  pilot.phase = 'finished';
  pilot.finale = { phase: 'released', ballotSize: 2, ownSubmitted: true, ownBallot: [], runoffCandidates: [], runoffPlaces: 0,
    selected: ['pilot-2', 'pilot-5'], winner: neighbours.guardianLabel, conspirators: ['pilot-2', 'pilot-5'],
    message: 'Pořadník se do finále dobelhal.',
    reconstruction: pilot.actions.map((item, index) => ({ chapter: item.plan.chapter, actor: item.playerId, target: null,
      intent: ['Hold', 'Rest', 'Guard', 'Prepare', 'Investigate', 'Follow', 'Steal', 'Sabotage'][item.plan.intent],
      result: 'Potvrzený tah', eventRef: `event-${index + 1}` })) };
  await first.reload();
  await first.getByRole('heading', { name: 'Tvá stopa v případu' }).waitFor();
  await first.getByText('Vítězí Sousedé', { exact: false }).waitFor();
  assert.equal(pilot.ballots.size, 8, 'all eight isolated sessions cast a secret ballot before the synthetic reveal');
  for (const session of sessions) await session.context.close();
  console.log('PASS simulated human-pilot browser walk: short-game creation, eight isolated sessions and private objectives, equal action menu, chapter-one lost-ack retry, chapter-two arrival, final DELAY, eight secret ballots and reconstruction. Synthetic fixture only; no live backend or human outcomes.');

  assert.deepEqual(errors, []);
  console.log(`PASS sealed client: 320/390/1280px, scenario catalog and separate duration, scenario finale/exports, explicit draft and retry, journal/conversations/empty/long/ballot/quiet states, responsive end reachability, lost create/AI acknowledgements and reload, privacy and no page errors. ${output}`);
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
