import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const appRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const productionDist = path.join(appRoot, 'dist');
const syntheticSecretCanaries = [
  'PP_SYNTHETIC_SERVER_SCENARIO_CANARY_7f2a',
  'PP_SYNTHETIC_FOREIGN_INTENT_CANARY_91bc',
];
const serverOnlyMarkers = [
  /serverSeed/i,
  /foreignIntent/i,
  /covertOrder/i,
  /SealedStoredRoster/i,
  /sealed-case-content-version\.json/i,
  ...syntheticSecretCanaries.map(value => new RegExp(value, 'i')),
];

async function scanAssetTree(root) {
  const findings = [];
  async function visit(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const file = path.join(directory, entry.name);
      const relative = path.relative(root, file).split(path.sep).join('/');
      if (serverOnlyMarkers.some(marker => marker.test(entry.name))) findings.push(`${relative}: server-only filename marker`);
      if (entry.isDirectory()) {
        await visit(file);
      } else if (entry.isFile()) {
        const bytes = await readFile(file);
        const text = bytes.toString('utf8');
        for (const marker of serverOnlyMarkers) {
          marker.lastIndex = 0;
          if (marker.test(text)) findings.push(`${relative}: server-only content marker ${marker}`);
        }
      }
    }
  }
  await visit(root);
  return findings;
}

test('clean output tree passes and synthetic server-only public/emitted assets fail', async t => {
  const fixture = await mkdtemp(path.join(os.tmpdir(), 'sealed-case-assets-'));
  t.after(() => rm(fixture, { recursive: true, force: true }));
  await mkdir(path.join(fixture, 'assets'), { recursive: true });
  await writeFile(path.join(fixture, 'index.html'), '<main>clean public output</main>');
  assert.deepEqual(await scanAssetTree(fixture), []);

  await writeFile(path.join(fixture, 'scenario.json'), JSON.stringify({ publicCopy: syntheticSecretCanaries[0] }));
  assert.equal((await scanAssetTree(fixture)).length, 1, 'a public copy containing a scenario canary must be rejected');
  await rm(path.join(fixture, 'scenario.json'));
  await writeFile(path.join(fixture, 'assets', 'unexpected.bin'), Buffer.from(syntheticSecretCanaries[1]));
  assert.equal((await scanAssetTree(fixture)).length, 1, 'an emitted binary asset containing an intent canary must be rejected');
});

test('fresh Vite production output contains no known server-only data markers', async t => {
  try {
    await readdir(productionDist);
  } catch (error) {
    if (error?.code === 'ENOENT') return t.skip('Run npm run test:sealed-case-assets to build and scan fresh dist output');
    throw error;
  }
  assert.deepEqual(await scanAssetTree(productionDist), []);
});
