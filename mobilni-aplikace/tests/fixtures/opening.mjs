// Loopback-only synthetic backend shared by the opening preview and visual checks.
// This module is never imported by the application bundle.
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const dist = path.resolve(fileURLToPath(new URL('../../dist/', import.meta.url)));
const portrait = new URL('./characters/matylda.webp', import.meta.url);
export const openingStates = ['A', 'B', 'C', 'D', 'E', 'F', 'fixed-universe', 'enrollment-error', 'consent', 'portrait-error', 'offline', 'paused', 'revoked'];
const account = { id: 'midnight-fixture', displayName: 'Mira Nocturne', roles: ['Customer'] };
const profileId = '8c66a540-12c4-4ebc-af30-f6ad8133f601';
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.webp': 'image/webp', '.png': 'image/png', '.ttf': 'font/ttf', '.svg': 'image/svg+xml' };

export async function createOpeningFixtureServer(port = 0) {
  await stat(path.join(dist, 'index.html'));
  const launchAt = Date.now() + (2 * 3600 + 14 * 60 + 8) * 1000;
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://localhost');
    const selected = /(?:^|; )opening-fixture=([^;]+)/.exec(request.headers.cookie ?? '')?.[1] ?? 'A';
    const state = openingStates.includes(selected) ? selected : 'A';
    const json = (value, status = 200) => {
      response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      response.end(JSON.stringify(value));
    };
    if (url.pathname === '/__opening') {
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end(`<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Midnight Table · synthetic preview</title><style>body{margin:0;background:#102c29;color:#f4ecd9;font:16px system-ui}nav{padding:12px;display:flex;flex-wrap:wrap;gap:8px}a{padding:10px;color:#f4ecd9;border:1px solid #d6a45f}p{margin:12px}iframe{display:block;width:390px;max-width:100%;height:844px;margin:auto;border:1px solid #d6a45f}</style><p>Development fixture · synthetic data · no live authentication or generation</p><nav>${openingStates.map(key => `<a href="/__fixture/${key}" target="screen">${key}</a>`).join('')}</nav><iframe title="Opening fixture" name="screen" src="/__fixture/A"></iframe></html>`);
      return;
    }
    if (url.pathname.startsWith('/__fixture/')) {
      const key = url.pathname.split('/').at(-1);
      if (!openingStates.includes(key)) return json({}, 404);
      const destination = key === 'A' ? '/' : ['fixed-universe', 'enrollment-error'].includes(key) ? '/signin' : key === 'B' ? '/signin?return=/launch' : key === 'C' ? '/signin#invite=synthetic-not-live-invitation-1234567890' : '/launch';
      response.writeHead(302, { 'Set-Cookie': `opening-fixture=${key}; Path=/; SameSite=Strict`, Location: destination });
      response.end();
      return;
    }
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: ['A', 'B', 'C', 'fixed-universe', 'enrollment-error'].includes(state) ? null : account, csrfToken: 'synthetic-not-live', origin: `http://${request.headers.host}` });
    if (url.pathname === '/app/v1/customer/access') return json({ state: state === 'revoked' ? 'revoked' : 'active', groupId: 'synthetic', accessRevision: 1, fixedUniverse: 'Za zrcadlem', launchAt });
    if (url.pathname === '/app/v1/enrollment/default') return state === 'enrollment-error' ? json({}, 503) : json({ available: true, fixedUniverse: 'Za zrcadlem' });
    if (url.pathname === '/app/v1/launch') {
      if (state === 'offline') return json({ code: 'unavailable' }, 503);
      return json({ allowed: state === 'F', audience: state === 'revoked' ? 'revoked' : state === 'paused' ? 'paused' : 'production', launchAt: state === 'paused' ? Date.now() - 1000 : launchAt, serverNow: Date.now() });
    }
    if (url.pathname === '/app/v1/prelaunch/character/portrait') {
      if (state === 'portrait-error') return json({}, 503);
      response.writeHead(200, { 'Content-Type': 'image/webp', 'Cache-Control': 'no-store' });
      response.end(await readFile(portrait));
      return;
    }
    if (url.pathname === '/app/v1/prelaunch/character') {
      if (state === 'consent') return json({ character: null, artwork: { status: 'not_requested' } });
      return json({ character: { profileId, revision: 1, name: 'Mira Nocturne', universe: 'Za zrcadlem', status: 'ready' }, artwork: {
        status: state === 'D' ? 'running' : state === 'portrait-error' ? 'failed' : 'ready',
        loadingMessage: 'Opona má trému. Slyšela, koho má představit.',
        welcome: { joke: 'Tvoje ego už dorazilo. Zabralo dvě židle.', identity: 'Mira Nocturne. Sběratelka cizích tajemství.', teaser: 'Dnes možná přijde řada na tvoje.' },
      } });
    }
    if (/^\/(app\/v1|v1|guest)\//.test(url.pathname)) return json({});
    let file = path.resolve(dist, decodeURIComponent(url.pathname).replace(/^\//, '') || 'index.html');
    if (!file.startsWith(dist + path.sep) && file !== path.join(dist, 'index.html')) return json({}, 404);
    try { await stat(file); } catch { file = path.join(dist, 'index.html'); }
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    response.end(await readFile(file));
  });
  await new Promise(resolve => server.listen(port, '127.0.0.1', resolve));
  return { server, origin: `http://127.0.0.1:${server.address().port}` };
}
