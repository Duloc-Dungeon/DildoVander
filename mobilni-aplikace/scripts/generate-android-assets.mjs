// Reuse the checksum-verified original emblem; no second branding source.
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const manifest = JSON.parse(await readFile(resolve(root, 'src/assets/brand/manifest.json'), 'utf8'));
const name = 'emblem-1024.png';
const bytes = await readFile(resolve(root, 'src/assets/brand', name));
const entry = manifest.assets.find(asset => asset.file === name);
if (!entry || entry.bytes !== bytes.length || entry.sha256 !== createHash('sha256').update(bytes).digest('hex')) {
  throw new Error('Android emblem checksum mismatch');
}
const output = resolve(root, 'android/app/src/main/res/drawable-nodpi');
await mkdir(output, { recursive: true });
await writeFile(resolve(output, 'brand_emblem.png'), bytes);
