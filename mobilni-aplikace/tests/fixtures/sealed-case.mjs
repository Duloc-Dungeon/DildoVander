// Synthetic, loopback-only evidence for the rendered dossier. No live accounts,
// provider calls, secret scenario packs or production backend are involved.
import { createServer, request as proxyRequest } from 'node:http';
import { createGameFixtureServer } from './game.mjs';

export const sealedCaseId = `sc_${'d'.repeat(32)}`;
export function dossierFixture(complete = false) {
  const names = ['Mira', 'Hermiona', 'Lucius', 'Minerva', 'Albus', 'Draco', 'Luna', 'Severus'];
  const players = names.map((name, index) => ({ id: index ? `p${index + 1}` : 'one', name, seat: `S${index + 1}`,
    state: index ? 'Stav neznáš' : 'Awake', location: index ? null : 'VESTIBUL', mask: null, portraitUrl: null }));
  const clue = (chain, family, candidates, label) => ({ id: `${chain}-${family}`, chain, family, candidates,
    label, description: 'Jde o část dokladu o události před začátkem hry.', sourceEvent: 'synthetic-private-event' });
  return {
    id: sealedCaseId, phase: 'planning', serverNow: 1000, chapter: complete ? 3 : 2, chapters: 3,
    planningEndsAt: 2000, quietEndsAt: 3000, finaleEndsAt: 9000, expectedPlayers: 8, readyPlayers: 8,
    ownId: 'one', ownReady: true, ownScenarioConsent: true, canManage: false, canStart: false, lobbyRevision: null,
    ownState: 'Awake', ownTeam: 'Guardian', ownKit: 'Badatel', teamMembers: [], location: 'VESTIBUL',
    locations: { VESTIBUL: 'Vstupní síň', SALON: 'Salon', ARCHIV: 'Archiv', KOMORA: 'Zapečetěná komora' },
    adjacentLocations: ['SALON'], routes: { VESTIBUL: ['SALON'], SALON: ['VESTIBUL', 'ARCHIV', 'KOMORA'], ARCHIV: ['SALON'], KOMORA: ['SALON'] },
    sourceSchedule: [
      { family: 'A', label: 'Zápis z knihy návštěv', location: 'ARCHIV', releaseChapter: 1 },
      { family: 'B', label: 'Otisk narušené pečeti', location: 'SALON', releaseChapter: 2 },
      { family: 'C', label: 'Svědectví z komory', location: 'KOMORA', releaseChapter: 3 },
    ], players, ownPlan: null,
    choices: [
      { id: 'investigate', label: 'Prozkoumat místo', benefit: 'Nejvýše jedna dostupná stopa.', cost: 'Jeden hlavní tah',
        risk: 'Pátrání nemusí přinést nový poznatek.', plan: { chapter: complete ? 3 : 2, expectedActionRevision: 0, confirmed: true, intent: 4 } },
      { id: 'guard', label: 'Chránit sebe', benefit: 'Ochrana při skutečném střetu.', cost: 'Jeden hlavní tah',
        risk: 'Nebudeš současně pátrat.', plan: { chapter: complete ? 3 : 2, expectedActionRevision: 0, confirmed: true, intent: 2 } },
    ],
    clues: [clue('R01', 'A', ['p2', 'p3', 'p4'], 'Zápis z knihy návštěv'), clue('R01', 'B', ['p2', 'p5'], 'Otisk narušené pečeti'),
      ...(complete ? [clue('R01', 'C', ['p2', 'p6'], 'Svědectví z komory')] : []), clue('R02', 'A', ['p3', 'p6'], 'Druhý zápis z knihy')],
    sources: [], ownOversight: false, ownItems: [], visibleItems: [], notes: ['Znovu jsi prozkoumal vlastní známou stopu.'],
    statements: [{ id: 'claim', actor: 'p3', body: 'Stopa R02-B · řetězec R02 · část B.\nOkruh kandidátů: Draco, Severus.', recipients: [], public: true, createdAt: 800 }],
    ownOutcomes: [{ chapter: 1, actor: 'one', target: null, intent: 'Investigate', result: 'Tah se vyhodnotil', eventRef: '', observation: 'Znovu jsi narazil na již známou stopu.' }],
    publicNarration: { text: 'Každý si pamatuje, kdo byl v salonu. Zápis v knize návštěv má na věc vlastní názor.' },
    privateNarration: { text: 'Vlastní stopy už máš ve spisu. Na zbývající část bude potřeba dojít včas.' }, finale: null,
  };
}

export async function createSealedCaseFixtureServer() {
  const base = await createGameFixtureServer();
  let play = dossierFixture();
  const operations = new Set();
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    if (!url.pathname.startsWith('/app/v1/sealed-cases')) {
      if (url.pathname.startsWith('/play/pod-peceti/') && url.searchParams.get('dossier') === 'complete') play = dossierFixture(true);
      const upstream = proxyRequest(new URL(request.url, base.origin), { method: request.method, headers: request.headers }, incoming => {
        response.writeHead(incoming.statusCode, incoming.headers); incoming.pipe(response);
      });
      upstream.on('error', () => { response.writeHead(502); response.end(); }); request.pipe(upstream); return;
    }
    response.setHeader('Content-Type', 'application/json');
    response.setHeader('Cache-Control', 'no-store');
    const json = value => response.end(JSON.stringify(value));
    if (request.method === 'GET') {
      return json(url.pathname.endsWith('/current') ? { id: sealedCaseId } : play);
    }
    let raw = ''; for await (const chunk of request) raw += chunk;
    const body = JSON.parse(raw);
    if (!operations.has(body.operationId)) {
      operations.add(body.operationId);
      if (url.pathname.endsWith('/actions')) play.ownPlan = { ...body.plan, revision: 1, id: 'synthetic-plan', actorId: 'one' };
      if (url.pathname.endsWith('/statements')) play.statements.push({ ...body, id: body.operationId, actor: 'one', createdAt: 1000 });
    }
    json({ accepted: true });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return { origin: `http://127.0.0.1:${server.address().port}`, close: () => Promise.all([
    new Promise(resolve => server.close(resolve)), new Promise(resolve => base.server.close(resolve)),
  ]) };
}
