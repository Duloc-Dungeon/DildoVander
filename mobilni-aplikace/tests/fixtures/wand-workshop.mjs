// Loopback browser fixture only. No provider, microphone or production account is called.
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const dist = path.resolve(fileURLToPath(new URL('../../dist/', import.meta.url)));
const base = '/app/v1/customer/wand/workshop';
const id = 'b'.repeat(32);
const locator = '12345678-1234-4234-8234-123456789abc';
const fixtureArtwork = fileURLToPath(new URL('../../src/assets/wns-v2/art/wand-amber.png', import.meta.url));


const wand = { wandId: 'synthetic-browser-wand', name: 'Tichý přístav', material: 'Jasan', core: 'Vlákno mlhy', temperament: 'Trpělivá', lore: 'Pamatuje si cestu domů.', designDescription: 'Štíhlá stříbrná hůlka.', affinity: 'Mlha', weakness: 'Spěch', combatArchetype: 'balanced', combatRulesVersion: 'wand-archetypes-v1', spells: ['strike', 'trick', 'shield'].map((action, index) => ({ action, name: ['Tichý úder', 'Mlhavý obrat', 'Noční clona'][index], description: 'Fiktivní testovací kouzlo.' })) };
export async function createWorkshopFixtureServer(port = 0) {
  let session = null;
  let state = 'ready';
  let exchange = 0;
  let nightAllowed = true;
  const requests = [];
  const complete = () => ({ ...session, status: 'complete', question: null, wand, verdict: 'Tichý přístav je hotový. Tvoje cesta do mlhy má konečně kvalifikovaný doprovod.', artwork: { locator, assetId: null, status: 'failed', canRetry: true, version: 1, synthetic: true } });
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    const json = (value, status = 200) => { response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); response.end(JSON.stringify(value)); };
    if (url.pathname === '/__workshop/open-night') { nightAllowed = true; return json({}); }
    if (url.pathname.startsWith('/__workshop/')) {
      state = url.pathname.split('/').at(-1); session = null; requests.length = 0; exchange = 0;
      nightAllowed = state !== 'prelaunch';
      const workshopPath = state === 'upgrade' ? '/play/wand?upgrade=1' : '/play/wand';
      response.writeHead(302, { Location: nightAllowed ? workshopPath : '/launch' }); response.end(); return;
    }
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'synthetic', displayName: 'Test', roles: ['Customer'], openAiConsent: true }, csrfToken: 'synthetic' });
    if (url.pathname === '/app/v1/customer/access') return json({ state: 'active', groupId: 'synthetic', accessRevision: 1 });
    if (url.pathname === '/app/v1/launch') return json({ allowed: nightAllowed, audience: 'beta', launchAt: Date.now() + 86400000, serverNow: Date.now() });
    if (url.pathname === '/app/v1/prelaunch/character') return json({ character: { profileId: 'synthetic-profile', revision: 1, name: 'Sever', universe: 'Půlnoční přístav', status: 'ready' }, artwork: { status: 'ready', welcome: { joke: 'Tvá pověst dorazila před tebou. Už jsme se jí omluvili.', identity: 'Sever z Půlnočního přístavu.', teaser: 'Večer se připravuje.' } } });
    if (url.pathname === '/app/v1/prelaunch/character/portrait') {
      response.writeHead(200, { 'Content-Type': 'image/png' });
      response.end(await readFile(new URL('../../src/assets/wns-v2/art/wand-violet.png', import.meta.url))); return;
    }
    if (url.pathname === base && session?.status === 'generating') session = complete();
    if (url.pathname === base) {
      requests.push({ path: url.pathname, method: request.method, upgrade: url.searchParams.get('upgrade') });
      return json({ eligible: !['captured', 'preparing'].includes(state), reason: state === 'captured' ? 'wand_workshop_boss_captured' : state === 'preparing' ? 'wand_character_preparing' : null, profile: { profileId: 'synthetic-profile', revision: 1, name: 'Sever', world: 'Půlnoční přístav', version: 'Fiktivní testovací postava', description: 'Mluví s havrany.' }, tier: state === 'upgrade' ? session?.status === 'complete' ? 2 : 1 : session?.status === 'complete' ? 1 : 0, session });
    }
    if (url.pathname === `${base}/sessions/${id}/artwork/assets/${locator}`) {
      response.writeHead(200, { 'Content-Type': 'image/png' }); response.end(await readFile(fixtureArtwork)); return;
    }
    if (url.pathname.startsWith(`${base}/sessions`)) {
      let body = '';
      for await (const chunk of request) body += chunk;
      requests.push({ path: url.pathname, method: request.method, body: body ? JSON.parse(body) : null });
      if (url.pathname === `${base}/sessions` && request.method === 'POST') {
        session ??= { id, profileId: 'synthetic-profile', profileRevision: 1, tier: JSON.parse(body).upgrade ? 2 : 1, status: 'asking', question: null, steps: [], messages: [], wand: null, artwork: null };
      } else if (request.method === 'GET' && session?.status === 'generating') session = complete();
      else if (url.pathname.endsWith('/artwork/retry')) session.artwork = { locator, assetId: locator, status: 'ready', canRetry: false, version: 1, synthetic: true };
      else if (url.pathname.endsWith('/finish')) session.status = 'generating';
      else if (url.pathname.endsWith('/messages') && request.method === 'POST') {
        const message = JSON.parse(body); exchange++;
        session.messages.push({ messageId: message.messageId, revision: 1, role: 'user', text: message.text, sourceMode: 'text' });
        session.messages.push({ messageId: `assistant-${exchange}`, revision: 1, role: 'assistant', text: `Kufr si pamatuje tvé sdělení. Co má hůlka zachránit, když plán selže?`, sourceMode: 'text' });
      }
      return json({ session });
    }
    if (url.pathname === `/app/v1/archive/owned/${locator}/assets/${locator}`) {
      // Use a committed wand illustration so the browser check verifies an
      // actual image in the wand slot, without generating paid artwork.
      response.writeHead(200, { 'Content-Type': 'image/png' }); response.end(await readFile(fixtureArtwork)); return;
    }
    if (/^\/(app\/v1|v1|guest)\//.test(url.pathname)) return json({});
    let file = path.resolve(dist, decodeURIComponent(url.pathname).replace(/^\//, '') || 'index.html');
    if (!file.startsWith(dist + path.sep)) return json({}, 404);
    try { await stat(file); } catch { file = path.join(dist, 'index.html'); }
    const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.webp': 'image/webp', '.png': 'image/png', '.woff2': 'font/woff2' };
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream' }); response.end(await readFile(file));
  });
  await new Promise(resolve => server.listen(port, '127.0.0.1', resolve));
  return {
    server, origin: `http://127.0.0.1:${server.address().port}`, requests,
    voiceTurn(text) {
      if (!session || session.status !== 'asking') throw new Error('No workshop conversation is open.');
      exchange++;
      session.messages.push({ messageId: `voice-user-${exchange}`, revision: 1, role: 'user', text, sourceMode: 'voice' });
      session.messages.push({ messageId: `voice-assistant-${exchange}`, revision: 1, role: 'assistant', text: 'Zachovám klid a přidám důraz.', sourceMode: 'voice' });
      return structuredClone(session);
    },
    finishVoice() {
      if (!session || session.tier !== 2) throw new Error('No wand upgrade is open.');
      session.status = 'generating';
      return structuredClone(session);
    },
  };
}
if (process.argv.includes('--serve')) {
  const fixture = await createWorkshopFixtureServer(4805);
  console.log(`Synthetic workshop preview: ${fixture.origin}/__workshop/ready`);
}
