import { createServer } from 'vite';
import { fileURLToPath } from 'node:url';

// Synthetic local browser fixture. It has no live account, backend, provider,
// generation, or device integration; all API responses stay in this process.
const port = Number(process.env.PORT ?? 5192);
const readyId = 'e1111111-1111-4111-8111-111111111111';
const sealedId = 'e2222222-2222-4222-8222-222222222222';
const conversationId = 'e3333333-3333-4333-8333-333333333333';
const at = 1793275200;
const accountId = 'a'.repeat(32);
const groupId = 'f1111111-1111-4111-8111-111111111111';
const badge = { id: 'badge-first-win', title: 'První vítězství', scope: 'duel', evidence: 'Rozhodující tah v posledním kole.', eventId: readyId, acquiredAt: at };
const event = {
  id: readyId, sourceKind: 'yolo', sourceId: '1'.repeat(32), groupId, occurredAt: at,
  title: 'Když se plášť rozhodl spolupracovat', summary: 'Mira změnila taktiku po prvním střetu a otočila výsledek.',
  participants: [
    { accountId, displayName: 'Mira', role: 'hráčka', characterName: 'Mira z Východní věže', wandName: 'Měsíční účetní', visualReferenceVersion: 'fixture-v1' },
    { accountId: 'b'.repeat(32), displayName: 'Eliška', role: 'soupeřka', characterName: 'Eliška z Přístavu', wandName: 'Trnová námitka', visualReferenceVersion: 'fixture-v1' },
  ],
  result: 'win', winnerId: accountId, story: [
    'První střet skončil nerozhodně; Mira si všimla, že soupeřka chrání levou stranu.',
    'Změnila volbu a odvedla pozornost k plášti, který si myslel, že je hlavní postava.',
    'Poslední tah rozhodl zápas. Plášť si připsal asistenci, kterou nikdo nezpochybnil.',
  ],
  decisions: [{ accountId, choice: 'Přestat útočit na chráněnou stranu', consequence: 'Soupeřka odkryla pravý bok a Mira získala rozhodující převahu.', evidence: 'Změna nastala po prvním kole.' }],
  awards: [badge], illustrationState: 'ready', revealed: true, shareText: 'Památka na zápas, kde plášť málem dostal vlastní medaili.', revision: 4,
  decisiveChoice: 'Mira změnila taktiku místo toho, aby opakovala neúspěšný tah.', shareAllowed: true,
};
const locked = { id: sealedId, sourceKind: 'duel', sourceId: '2'.repeat(32), occurredAt: at - 3600, title: 'Zapečetěná událost', summary: 'Otevři ji, až budeš připravená.', illustrationState: 'not_requested', revealed: false,
  // Deliberately malicious fixture-only fields prove the UI uses its locked allowlist.
  result: 'leaked-winner', winnerId: accountId, story: ['LEAKED STORY'], awards: [badge] };
const privateConversation = { ...event, id: conversationId, sourceKind: 'conversation', sourceId: '3'.repeat(32), title: 'Odpověď z dílny', summary: 'Soukromá replika uložená na výslovné přání.', story: ['Tohle je soukromá vzpomínka.'], decisions: [], awards: [], illustrationState: 'none', shareText: undefined, shareAllowed: false };
const pixels = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j6k0AAAAASUVORK5CYII=', 'base64');
let wasRevealed = false;

function json(res, value, status = 200) {
  res.statusCode = status;
  res.setHeader('Content-Type', 'application/json; charset=utf-8');
  res.setHeader('Cache-Control', 'no-store');
  res.end(JSON.stringify(value));
}
function image(res) {
  res.statusCode = 200;
  res.setHeader('Content-Type', 'image/png');
  res.setHeader('Cache-Control', 'no-store');
  res.end(pixels);
}
function fixtureApi(req, res, next) {
  const url = new URL(req.url ?? '/', 'http://127.0.0.1');
  if (!url.pathname.startsWith('/app/v1/')) return next();
  if (req.method === 'GET' && url.pathname === '/app/v1/session') return json(res, { enabled: true, account: { id: accountId, displayName: 'Mira · místní ukázka', roles: ['Customer'], openAiConsent: true }, csrfToken: 'fixture-csrf' });
  if (req.method === 'GET' && url.pathname === '/app/v1/launch') return json(res, { allowed: true });
  if (req.method === 'GET' && url.pathname === '/app/v1/customer/access') return json(res, { state: 'active', accessRevision: 1 });
  if (req.method === 'GET' && url.pathname === '/app/v1/chronicle') return json(res, { items: [event, locked, privateConversation], nextCursor: null, badges: [badge] });
  if (req.method === 'GET' && url.pathname === `/app/v1/chronicle/${readyId}`) return json(res, event);
  if (req.method === 'GET' && url.pathname === `/app/v1/chronicle/${conversationId}`) return json(res, privateConversation);
  if (req.method === 'GET' && url.pathname === `/app/v1/chronicle/${sealedId}`) return json(res, { ...locked, result: 'must remain hidden', story: ['hidden'], awards: [badge] });
  if (req.method === 'POST' && url.pathname === `/app/v1/chronicle/${sealedId}/reveal`) { wasRevealed = true; return json(res, { revealed: true, item: { ...event, id: sealedId, sourceId: locked.sourceId, title: 'Rozhodující tah pod tlakem', awards: [] } }); }
  if (req.method === 'GET' && /^\/app\/v1\/chronicle\/[0-9a-f-]{36}\/(?:image|share)$/.test(url.pathname)) return image(res);
  if (req.method === 'GET' && /^\/app\/v1\/chronicle\/[0-9a-f-]{36}\/badges\/[A-Za-z0-9._:-]{1,100}\/image$/.test(url.pathname)) return image(res);
  if (req.method === 'POST' && url.pathname.endsWith('/illustration')) return json(res, { accepted: true });
  if (req.method === 'GET' && url.pathname === `/app/v1/chronicle/source/conversation/${'3'.repeat(32)}`) return json(res, privateConversation);
  return json(res, { code: 'fixture_route_not_available', detail: `Synthetic fixture has no response for ${req.method} ${url.pathname}.` }, 404);
}

const server = await createServer({
  configFile: fileURLToPath(new URL('../vite.config.mjs', import.meta.url)),
  server: { host: '127.0.0.1', port, strictPort: true },
  plugins: [{ name: 'chronicle-local-fixture', configureServer(vite) { return () => vite.middlewares.use(fixtureApi); } }],
});
await server.listen();
console.log(`Synthetic chronicle preview (loopback only; no live account or generation): http://127.0.0.1:${port}/me/keepsakes`);
console.log(`Fixture states: revealed illustration, sealed event${wasRevealed ? ' (opened in this browser session)' : ''}, private conversation without share.`);
for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, async () => { await server.close(); process.exit(0); });
