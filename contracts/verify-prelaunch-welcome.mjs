import assert from 'node:assert/strict';
import { validatePrelaunchArtwork, validatePrelaunchArtworkSource, validatePrelaunchWelcome } from './generated/prelaunch-welcome-validator.mjs';

const source = { url: 'https://commons.wikimedia.org/wiki/File:Portrait.jpg', title: 'Portrait', author: 'Artist', license: 'CC BY-SA 4.0', licenseUrl: 'https://creativecommons.org/licenses/by-sa/4.0/' };
assert.equal(validatePrelaunchArtworkSource(source), true);
assert.equal(validatePrelaunchArtwork({ status: 'ready' }), true);
assert.equal(validatePrelaunchArtwork({ status: 'ready', source: null }), true);
assert.equal(validatePrelaunchArtwork({ status: 'ready', source }), true);
for (const invalidSource of [null, { ...source, url: 'javascript:alert(1)' }, { ...source, licenseUrl: 'https://example.com/license' }, { ...source, title: 'x'.repeat(301) }, { ...source, extra: true }, { ...source, author: '<img src=x>' },
  { ...source, author: '' }, { ...source, title: 'bad\ntext' }, { ...source, license: 'All rights reserved' },
  { ...source, url: source.url + '?tracking=1' }, { ...source, licenseUrl: source.licenseUrl + '#fragment' }]) {
  assert.equal(validatePrelaunchArtworkSource(invalidSource), false);
}
assert.equal(validatePrelaunchArtwork({ status: 'ready', source: { ...source, url: 'javascript:alert(1)' } }), false);

for (const welcome of [
  { joke: 'Učebnice máš nastudované.', identity: 'Bystrá čarodějka.', teaser: 'Některé věci tu mají vlastní vůli.' },
  { joke: 'Tvá pověst dorazila před tebou. Už jsme se jí omluvili.', identity: '', teaser: '' }
]) {
  assert.equal(validatePrelaunchWelcome(welcome), true);
}
for (const welcome of [
  { joke: '', identity: '', teaser: '' },
  { joke: 'x'.repeat(121), identity: '', teaser: '' },
  { joke: 'x'.repeat(120), identity: 'y'.repeat(120), teaser: 'z'.repeat(100) },
  { joke: '', identity: '', teaser: 'x'.repeat(101) },
  { joke: '', identity: '', teaser: '', usedFactIds: ['internal'] },
  { joke: '', identity: '', teaser: '', teaserId: 'rivalry' },
  { joke: '', identity: '' }
]) {
  assert.equal(validatePrelaunchWelcome(welcome), false);
}
console.log('Generated prelaunch welcome contract checks passed.');

const variantWelcome = { joke: 'První.', jokes: ['První.', 'Druhý.', 'Třetí.'], identity: '', teaser: '' };
assert.ok(validatePrelaunchWelcome(variantWelcome));
assert.equal(validatePrelaunchWelcome({ ...variantWelcome, jokes: ['První.', 'První.', 'Třetí.'] }), false);
assert.equal(validatePrelaunchWelcome({ ...variantWelcome, jokes: ['Jiný.', 'Druhý.', 'Třetí.'] }), false);
