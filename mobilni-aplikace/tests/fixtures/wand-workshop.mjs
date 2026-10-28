// Loopback browser fixture only. No provider, microphone or production account is called.
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const dist = path.resolve(fileURLToPath(new URL('../../dist/', import.meta.url)));
const base = '/app/v1/customer/wand/workshop';
const id = 'b'.repeat(32);
const locator = '12345678-1234-4234-8234-123456789abc';
const texts = ['Už ses dokázal ztratit i s vlastní mapou. Co uděláš, když tě havran varuje před cestou, kterou chceš pokračovat?', 'Co má hůlka udělat jako první, když tvoje ověření selže?', 'Jakou drobnou protivnost hůlce odpustíš, když tě dostane z maléru?'];
const question = number => ({ questionId: String(number).repeat(32), questionRevision: 1, number, question: texts[number - 1], basedOnFactIds: [] });
const wand = { wandId: 'synthetic-browser-wand', name: 'Tichý přístav', material: 'Jasan', core: 'Vlákno mlhy', temperament: 'Trpělivá', lore: 'Pamatuje si cestu domů.', designDescription: 'Štíhlá stříbrná hůlka.', affinity: 'Mlha', weakness: 'Spěch', combatArchetype: 'balanced', combatRulesVersion: 'wand-archetypes-v1', spells: ['strike', 'trick', 'shield'].map((action, index) => ({ action, name: ['Tichý úder', 'Mlhavý obrat', 'Noční clona'][index], description: 'Fiktivní testovací kouzlo.' })) };
export async function createWorkshopFixtureServer(port = 0) {
  let session = null;
  let state = 'ready';
  let pendingQuestion = 1;
  const requests = [];
  const complete = () => ({ ...session, status: 'complete', question: null, wand, verdict: 'Tichý přístav je hotový. Tvoje cesta do mlhy má konečně kvalifikovaný doprovod.', artwork: { locator, assetId: null, status: 'failed', canRetry: true, version: 1, synthetic: true } });
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    const json = (value, status = 200) => { response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); response.end(JSON.stringify(value)); };
    if (url.pathname.startsWith('/__workshop/')) {
      state = url.pathname.split('/').at(-1); session = null; requests.length = 0; pendingQuestion = 1;
      response.writeHead(302, { Location: '/play/wand' }); response.end(); return;
    }
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'synthetic', displayName: 'Test', roles: ['Customer'], openAiConsent: true }, csrfToken: 'synthetic' });
    if (url.pathname === '/app/v1/customer/access') return json({ state: 'active', groupId: 'synthetic', accessRevision: 1 });
    if (url.pathname === '/app/v1/launch') return json({ allowed: true });
    if (url.pathname === base) return json({ eligible: state !== 'captured', reason: state === 'captured' ? 'wand_workshop_boss_captured' : null, profile: { profileId: 'synthetic-profile', revision: 1, name: 'Sever', world: 'Půlnoční přístav', version: 'Fiktivní testovací postava', description: 'Mluví s havrany.' }, tier: 0, session });
    if (url.pathname.startsWith(`${base}/sessions`)) {
      let body = '';
      for await (const chunk of request) body += chunk;
      requests.push({ path: url.pathname, method: request.method, body: body ? JSON.parse(body) : null });
      if (url.pathname === `${base}/sessions` && request.method === 'POST') {
        session ??= { id, profileId: 'synthetic-profile', profileRevision: 1, tier: 1, status: 'authoring', question: null, steps: [], wand: null, artwork: null };
      } else if (request.method === 'GET' && session?.status === 'generating') session = complete();
      else if (request.method === 'GET' && session?.status === 'authoring') { session.status = 'asking'; session.question = question(pendingQuestion); }
      else if (url.pathname.endsWith('/artwork/retry')) session.artwork = { locator, assetId: locator, status: 'ready', canRetry: false, version: 1, synthetic: true };
      else if (request.method === 'PUT') { const step = session.steps.find(item => url.pathname.endsWith(item.answerId)); step.answer = JSON.parse(body).answer; step.answerRevision++; }
      else if (request.method === 'POST') {
        const answer = JSON.parse(body); const active = session.question;
        session.steps.push({ ...active, answerId: String(active.number + 3).repeat(32), answerRevision: 1, answer: answer.answer ?? null, skipped: url.pathname.endsWith('/skip'), sourceMode: answer.sourceMode ?? 'text' });
        session.question = null;
        pendingQuestion = active.number + 1;
        session.status = active.number === 3 ? 'generating' : 'authoring';
      }
      return json({ session });
    }
    if (url.pathname === `/app/v1/archive/owned/${locator}/assets/${locator}`) {
      response.writeHead(200, { 'Content-Type': 'image/svg+xml' }); response.end('<svg xmlns="http://www.w3.org/2000/svg" width="500" height="500"><rect width="500" height="500" fill="#181b19"/><text x="30" y="240" fill="#b8f3d4">Syntetická testovací ilustrace</text></svg>'); return;
    }
    if (/^\/(app\/v1|v1|guest)\//.test(url.pathname)) return json({});
    let file = path.resolve(dist, decodeURIComponent(url.pathname).replace(/^\//, '') || 'index.html');
    if (!file.startsWith(dist + path.sep)) return json({}, 404);
    try { await stat(file); } catch { file = path.join(dist, 'index.html'); }
    const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.webp': 'image/webp', '.png': 'image/png', '.woff2': 'font/woff2' };
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream' }); response.end(await readFile(file));
  });
  await new Promise(resolve => server.listen(port, '127.0.0.1', resolve));
  return { server, origin: `http://127.0.0.1:${server.address().port}`, requests };
}
if (process.argv.includes('--serve')) {
  const fixture = await createWorkshopFixtureServer(4805);
  console.log(`Synthetic workshop preview: ${fixture.origin}/__workshop/ready`);
}
