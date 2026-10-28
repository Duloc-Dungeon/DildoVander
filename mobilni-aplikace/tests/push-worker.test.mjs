import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const source = readFileSync(new URL('../public/push-sw.js', import.meta.url), 'utf8');
function worker(clients = {}) {
  const handlers = {};
  const shown = [];
  runInNewContext(source, { URL, self: {
    location: { origin: 'https://app.example' }, clients,
    registration: { showNotification: async (...args) => shown.push(args) },
    addEventListener: (name, listener) => { handlers[name] = listener; },
  } });
  return { handlers, shown };
}

test('push displays a notification without caching private pages', async () => {
  const { handlers, shown } = worker();
  let completed;
  handlers.push({ data: { json: () => ({ body: 'Nová výzva do hry.', url: 'https://evil.example', tag: 'challenge' }) }, waitUntil: value => { completed = value; } });
  await completed;
  assert.equal(shown[0][1].body, 'Nová výzva do hry.');
  assert.equal(shown[0][1].data.url, '/me/account');
  assert.equal(handlers.fetch, undefined);
});

test('notification taps focus an existing app window at the safe destination', async () => {
  const navigated = [];
  let focused = false;
  const { handlers } = worker({ matchAll: async () => [{ url: 'https://app.example/me', navigate: async url => { navigated.push(url); return { focus: async () => { focused = true; } }; } }] });
  let completed;
  handlers.notificationclick({ notification: { close() {}, data: { url: '/play' } }, waitUntil: value => { completed = value; } });
  await completed;
  assert.deepEqual(navigated, ['https://app.example/play']);
  assert.equal(focused, true);
});
