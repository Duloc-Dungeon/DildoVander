// Materialize the original Wand Night Stand logo through the existing iOS build path.
// Artwork, exact prompt, license and checksums: src/assets/brand/manifest.json.
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const mobileRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const sourceRoot = resolve(mobileRoot, 'src/assets/brand');
const manifest = JSON.parse(await readFile(resolve(sourceRoot, 'manifest.json'), 'utf8'));
async function checkedAsset(name) {
  const entry = manifest.assets.find(asset => asset.file === name);
  const bytes = await readFile(resolve(sourceRoot, name));
  if (!entry || bytes.length !== entry.bytes || createHash('sha256').update(bytes).digest('hex') !== entry.sha256) {
    throw new Error(`Consumer artwork checksum mismatch: ${name}`);
  }
  return bytes;
}
const png = await checkedAsset('emblem-1024.png');
if (!png.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))
    || png.readUInt32BE(16) !== 1024 || png.readUInt32BE(20) !== 1024 || png[24] !== 8 || png[25] !== 2) {
  throw new Error('Native logo must be an opaque 1024x1024 eight-bit RGB PNG.');
}
const catalog = resolve(mobileRoot, 'ios/App/App/Assets.xcassets');
const launchSet = resolve(catalog, 'BrandLaunch.imageset');
await mkdir(launchSet, { recursive: true });
await mkdir(resolve(catalog, 'AppIcon.appiconset'), { recursive: true });
await writeFile(resolve(catalog, 'AppIcon.appiconset/AppIcon-512@2x.png'), png);
await writeFile(resolve(launchSet, 'BrandLaunch.png'), png);
await writeFile(resolve(launchSet, 'Contents.json'), JSON.stringify({
  images: [{ filename: 'BrandLaunch.png', idiom: 'universal' }], info: { author: 'xcode', version: 1 },
}, null, 2) + '\n');

// The installer is intentionally distributed as a standalone script. Embed its
// small original logo instead of depending on a particular hosted app revision.
const installer = resolve(mobileRoot, 'scripts/publish-ios-install.py');
const source = await readFile(installer, 'utf8');
const marker = /# BEGIN GENERATED CONSUMER BRAND MARK\r?\n[\s\S]*?# END GENERATED CONSUMER BRAND MARK/;
if (!marker.test(source)) throw new Error('Standalone installer brand marker missing.');
const touch = await checkedAsset('emblem-touch.png');
const embedded = `# BEGIN GENERATED CONSUMER BRAND MARK\nINSTALLER_BRAND_MARK = ${JSON.stringify(`data:image/png;base64,${touch.toString('base64')}`)}\n# END GENERATED CONSUMER BRAND MARK`;
await writeFile(installer, source.replace(marker, embedded));
console.log('Generated opaque iOS icon, native launch logo and standalone installer mark from the original WNS emblem.');
