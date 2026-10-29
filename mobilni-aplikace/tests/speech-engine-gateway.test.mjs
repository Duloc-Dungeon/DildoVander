import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { createSpeechEngineGateway } from '../../devtools/local-try-hub/speech-engine-gateway.mjs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

test('Windows launcher excludes inherited named tunnels and provider credentials in the actual child process', { skip: process.platform !== 'win32' }, () => {
  const script = fileURLToPath(new URL('../../devtools/local-try-hub/Test-LocalTryIsolation.ps1', import.meta.url));
  const result = execFileSync('pwsh', ['-NoProfile', '-File', script], { encoding: 'utf8', timeout: 20000 });
  assert.match(result, /^PASS actual child environment/m);
});

test('public gateway exposes only signed callback upgrades and strips account credentials', async () => {
  const forwarded = [];
  const backend = http.createServer();
  backend.on('upgrade', (request, socket) => {
    forwarded.push({ path: request.url, headers: request.headers });
    socket.end('HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n');
  });
  await new Promise(resolve => backend.listen(0, '127.0.0.1', resolve));
  const gateway = createSpeechEngineGateway({ backendPort: backend.address().port });
  await new Promise(resolve => gateway.server.listen(0, '127.0.0.1', resolve));
  const port = gateway.server.address().port;
  const request = (path, headers = {}) => new Promise((resolve, reject) => {
    const outgoing = http.get({ hostname: '127.0.0.1', port, path, headers }, response => {
      response.resume(); response.on('end', () => resolve(response.statusCode));
    });
    outgoing.on('error', reject);
  });
  try {
    const path = '/app/v1/customer/wand/speech-engine/' + 'a'.repeat(64);
    for (const forbidden of ['/local-try-hub', '/local-try-api/session', '/app/v1/account', path]) {
      assert.equal(await request(forbidden), 404);
    }
    const upgrade = { connection: 'Upgrade', upgrade: 'websocket', 'sec-websocket-key': 'dGhlIHNhbXBsZSBub25jZQ==', 'sec-websocket-version': '13' };
    assert.equal(await request(path, upgrade), 404);
    assert.equal(await request(path + '?bypass=1', { ...upgrade, 'x-elevenlabs-speech-engine-authorization': 'test-proof' }), 404);
    assert.equal(forwarded.length, 0);
    assert.equal(await request(path, {
      ...upgrade, 'x-elevenlabs-speech-engine-authorization': 'test-proof',
      cookie: 'account=must-not-forward', 'x-dv-local-session-secret': 'must-not-forward', authorization: 'Bearer must-not-forward',
    }), 401);
    assert.equal(forwarded.length, 1);
    assert.equal(forwarded[0].path, path);
    assert.equal(forwarded[0].headers['x-elevenlabs-speech-engine-authorization'], 'test-proof');
    for (const name of ['cookie', 'authorization', 'x-dv-local-session-secret']) assert.equal(forwarded[0].headers[name], undefined);
    assert.equal(await request('/healthz'), 200);
  } finally {
    gateway.close();
    await new Promise(resolve => backend.close(resolve));
  }
});

