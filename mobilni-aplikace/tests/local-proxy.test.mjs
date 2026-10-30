import assert from 'node:assert/strict';
import test from 'node:test';
import config, { localWebSocketForwarding } from '../vite.config.mjs';

test('local WebSocket forwarding reports the real HTTP TLS scheme to ASP.NET', () => {
  const handlers = new Map();
  localWebSocketForwarding({ on: (name, callback) => handlers.set(name, callback) });
  const forward = handlers.get('proxyReqWs');
  assert.equal(typeof forward, 'function');
  for (const [encrypted, untrustedScheme, expected] of [
    [true, 'wss', 'https'], [true, 'http', 'https'],
    [false, 'https', 'http'], [undefined, 'wss', 'http'],
  ]) {
    const headers = new Map([['X-Forwarded-Proto', untrustedScheme]]);
    forward({ setHeader: (name, value) => headers.set(name, value) }, {
      socket: { encrypted }, headers: { 'x-forwarded-proto': untrustedScheme },
    });
    assert.equal(headers.get('X-Forwarded-Proto'), expected);
  }
  assert.equal(config.server.proxy['/app/v1'].configure, localWebSocketForwarding);
  assert.equal(config.server.proxy['/app/v1'].ws, true);
});
