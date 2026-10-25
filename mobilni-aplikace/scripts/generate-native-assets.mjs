// Original project artwork, AGPL-3.0-only. No downloaded image or font is used.
// Run after Capacitor sync and before compiling the native asset catalog.
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync } from 'node:zlib';

const mobileRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const assetCatalog = resolve(mobileRoot, 'ios/App/App/Assets.xcassets');
const appIconOutput = resolve(assetCatalog, 'AppIcon.appiconset/AppIcon-512@2x.png');
const launchImageSet = resolve(assetCatalog, 'BrandLaunch.imageset');
const launchOutput = resolve(launchImageSet, 'BrandLaunch.png');
const launchMetadata = resolve(launchImageSet, 'Contents.json');
const size = 1024;

function rgb(hex) {
  return [0, 2, 4].map(index => parseInt(hex.slice(index, index + 2), 16));
}

const background = rgb('251d26');
const gold = rgb('e0bc7e');

function segmentDistance(x, y, start, end) {
  const dx = end[0] - start[0];
  const dy = end[1] - start[1];
  const lengthSquared = dx * dx + dy * dy;
  const projection = Math.max(0, Math.min(1,
    ((x - start[0]) * dx + (y - start[1]) * dy) / lengthSquared));
  return Math.hypot(x - start[0] - projection * dx, y - start[1] - projection * dy);
}

const mark = [
  [192, 320],
  [352, 720],
  [512, 432],
  [672, 720],
  [832, 320],
];
const strokeRadius = 28;
const moon = { x: 592, y: 220, outerRadius: 140, innerX: 680, innerY: 228, innerRadius: 130 };

const rows = Buffer.alloc(size * (1 + size * 3));
for (let y = 0; y < size; y++) {
  const row = y * (1 + size * 3);
  rows[row] = 0; // PNG filter: none, RGB with no alpha channel.
  for (let x = 0; x < size; x++) {
    const pixel = [...background];
    const px = x + 0.5;
    const py = y + 0.5;
    const distanceToMark = Math.min(...mark.slice(1).map((point, index) =>
      segmentDistance(px, py, mark[index], point)));
    const markCoverage = Math.max(0, Math.min(1, strokeRadius + 0.5 - distanceToMark));
    const outerMoonCoverage = Math.max(0, Math.min(1,
      moon.outerRadius + 0.5 - Math.hypot(px - moon.x, py - moon.y)));
    const innerMoonCoverage = Math.max(0, Math.min(1,
      moon.innerRadius + 0.5 - Math.hypot(px - moon.innerX, py - moon.innerY)));
    const moonCoverage = Math.min(outerMoonCoverage, 1 - innerMoonCoverage);
    const coverage = Math.max(markCoverage, moonCoverage);
    for (let channel = 0; channel < 3; channel++) {
      pixel[channel] += coverage * (gold[channel] - pixel[channel]);
    }
    for (let channel = 0; channel < 3; channel++) {
      rows[row + 1 + x * 3 + channel] = Math.round(pixel[channel]);
    }
  }
}

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const checksum = Buffer.alloc(4);
  checksum.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, checksum]);
}

const header = Buffer.alloc(13);
header.writeUInt32BE(size, 0);
header.writeUInt32BE(size, 4);
header[8] = 8; // Eight bits per RGB channel.
header[9] = 2; // Truecolor; App Store icons must be opaque.
const png = Buffer.concat([
  Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
  chunk('IHDR', header),
  chunk('IDAT', deflateSync(rows, { level: 9 })),
  chunk('IEND', Buffer.alloc(0)),
]);

await Promise.all([
  mkdir(dirname(appIconOutput), { recursive: true }),
  mkdir(launchImageSet, { recursive: true }),
]);
await Promise.all([
  writeFile(appIconOutput, png),
  writeFile(launchOutput, png),
  writeFile(launchMetadata, `${JSON.stringify({
    images: [{ filename: 'BrandLaunch.png', idiom: 'universal' }],
    info: { author: 'xcode', version: 1 },
  }, null, 2)}\n`),
]);
console.log('Generated opaque 1024x1024 iOS app icon and launch image with Wand Night Stand mark.');
