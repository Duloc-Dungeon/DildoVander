import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { readFile, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = new URL('../src/assets/wns-v2/', import.meta.url);
test('WNS v2 copied assets preserve approved source bytes and exclude demo portraits', async () => {
  const source = JSON.parse(await readFile(new URL('source-manifest.json', root), 'utf8'));
  const trunk = JSON.parse(await readFile(new URL('art/trunk-workshop/manifest.json', root), 'utf8'));
  let checked = 0;
  for (const directory of ['art', 'audio', 'fonts', 'icons']) {
    const files = await readdir(new URL(`${directory}/`, root), { recursive: true, withFileTypes: true });
    for (const entry of files.filter(file => file.isFile())) {
      const full = pathToFileURL(join(entry.parentPath, entry.name));
      const relative = full.pathname.slice(root.pathname.length);
      assert.ok(!/matylda|sampler/.test(relative));
      const trunkAsset = relative.startsWith('art/trunk-workshop/');
      if (trunkAsset && ['manifest.json', 'README.md'].includes(entry.name)) continue; // Source metadata, not raster exports.
      const record = trunkAsset
        ? trunk.files.find(item => item.file === `assets/${entry.name}` && item.role === 'production-export')
        : source.items.find(item => item.path === `assets/${relative}`);
      if (entry.name === 'manrope.css') continue; // CSS is included but not an image/font/audio manifest record.
      assert.ok(record, `source provenance: ${relative}`);
      const bytes = await readFile(full);
      assert.equal(createHash('sha256').update(bytes).digest('hex'), record.sha256, relative);
      checked++;
    }
  }
  assert.ok(checked > 100, 'licensed source pack was actually checked');
});
