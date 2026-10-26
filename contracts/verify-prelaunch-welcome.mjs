import assert from 'node:assert/strict';
import { validatePrelaunchWelcome } from './generated/prelaunch-welcome-validator.mjs';

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
