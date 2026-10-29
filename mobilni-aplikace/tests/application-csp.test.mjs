import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { test } from 'node:test';
import { applicationCsp, themeBootstrap } from '../vite.config.mjs';

test('the synchronous theme bootstrap participates in the strict build CSP', () => {
  const html = themeBootstrap().transformIndexHtml.handler('<head><meta charset="utf-8"><link rel="stylesheet" href="app.css"></head>');
  const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
  assert.ok(html.indexOf('charset') < html.indexOf('<script>'));
  assert.ok(html.indexOf('</script>') < html.indexOf('<link'));
  assert.ok(script.includes('DildoVanderTheme'));
  const [meta] = applicationCsp().transformIndexHtml.handler(html);
  assert.ok(meta.attrs.content.includes(`sha256-${createHash('sha256').update(script.replace(/\r\n?/g, '\n')).digest('base64')}`));
});

test('early capability capture CSP uses HTML parser line endings on every checkout', () => {
  const script = '\nwindow.capture = true;\n';
  const expected = `sha256-${createHash('sha256').update(script).digest('base64')}`;
  for (const ending of ['\n', '\r\n', '\r']) {
    const html = `<script>${script.replaceAll('\n', ending)}</script><script src="/entry.js"></script>`;
    const [meta] = applicationCsp().transformIndexHtml.handler(html);
    assert.ok(meta.attrs.content.includes(expected));
    assert.equal((meta.attrs.content.match(/sha256-/g) ?? []).length, 1);
    assert.ok(!meta.attrs.content.includes('unsafe-inline'));
  }
});

test('the MP4 player keeps third-party scripts and frames disabled', () => {
  const [meta] = applicationCsp().transformIndexHtml.handler('');
  const directives = new Map(meta.attrs.content.split(';').map(directive => {
    const [name, ...sources] = directive.trim().split(/\s+/);
    return [name, sources];
  }));
  assert.deepEqual(directives.get('script-src'), ["'self'"]);
  assert.deepEqual(directives.get('frame-src'), ["'none'"]);
  assert.deepEqual(directives.get('media-src'), ["'self'", 'blob:', 'https://dildovander.de']);
  assert.deepEqual(directives.get('object-src'), ["'none'"]);
  assert.ok(!meta.attrs.content.includes('unsafe-inline'));
  assert.ok(!meta.attrs.content.includes('unsafe-eval'));
});
