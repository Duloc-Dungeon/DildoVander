// Supply the path of an installed sharp package; no runtime dependency required.
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
const require = createRequire(import.meta.url);
const sharp = require(process.argv[2] || 'sharp');
const directory = new URL('../src/assets/arcade/', import.meta.url);
for (const [name, width] of [['storeroom', 900], ['spark', 128]]) {
  await sharp(fileURLToPath(new URL(`${name}.png`, directory)))
    .resize({ width }).webp({ quality: 86, alphaQuality: 100 })
    .toFile(fileURLToPath(new URL(`${name}.webp`, directory)));
}
