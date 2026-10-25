import assert from 'node:assert/strict';
import test from 'node:test';
import { backendFetch, correlationHeaders, createTraceparent } from '../src/application/requestCorrelation.mjs';

const format = /^00-[0-9a-f]{32}-[0-9a-f]{16}-00$/;

test('generates fresh W3C context with no user state or sampling instruction', () => {
  const parents = Array.from({ length: 10 }, () => createTraceparent());
  for (const parent of parents) assert.match(parent, format);
  assert.equal(new Set(parents).size, parents.length);
});

test('skips correlation when secure randomness fails or returns forbidden zero IDs', () => {
  assert.equal(createTraceparent({}), undefined);
  assert.equal(createTraceparent({ getRandomValues() { throw new Error('unavailable'); } }), undefined);
  assert.equal(createTraceparent({ getRandomValues: bytes => bytes }), undefined);
  assert.equal(createTraceparent({ getRandomValues(bytes) { bytes.fill(1, 0, 16); return bytes; } }), undefined);
});

test('replaces caller context and retains existing request IDs and auth headers', () => {
  const input = new Headers({ traceparent: 'old', tracestate: 'private-state', baggage: 'private-content', 'X-Request-Id': 'existing', 'X-DV-CSRF': 'csrf' });
  const headers = correlationHeaders(input);
  assert.match(headers.get('traceparent'), format);
  assert.equal(headers.has('tracestate'), false);
  assert.equal(headers.has('baggage'), false);
  assert.equal(headers.get('X-Request-Id'), 'existing');
  assert.equal(headers.get('X-DV-CSRF'), 'csrf');
  assert.equal(input.get('traceparent'), 'old');
});

test('only relative backend APIs receive context and cannot redirect it to other origins', async () => {
  const previous = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (input, init) => { calls.push({ input, init }); return new Response(); };
  try {
    const controller = new AbortController();
    for (const path of ['/app/v1/session', '/guest/v1/evening?limit=10', '/v1/evenings']) {
      await backendFetch(path, { method: 'POST', body: 'private', credentials: 'same-origin', signal: controller.signal });
      const { init } = calls.at(-1);
      assert.match(init.headers.get('traceparent'), format);
      assert.equal(init.redirect, 'error');
      assert.equal(init.credentials, 'same-origin');
      assert.equal(init.body, 'private');
      assert.equal(init.signal, controller.signal);
    }
    for (const path of ['https://provider.example/v1/test', 'https://192.168.1.2/v1/test', '//provider.example/v1/test', '/downloads/index.json', '/dev-mode', '/v1\\provider.example/test']) {
      await backendFetch(path);
      assert.equal(calls.at(-1).init.headers, undefined);
    }
    assert.notEqual(calls[0].init.headers.get('traceparent'), calls[1].init.headers.get('traceparent'));
  } finally { globalThis.fetch = previous; }
});
