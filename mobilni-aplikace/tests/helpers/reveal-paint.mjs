import assert from 'node:assert/strict';

// A bright strip BEHIND the effect makes any uncovered top pixels measurable.
export async function installTopPaintProbe(page) {
  await page.evaluate(() => {
    const strip = document.createElement('div');
    strip.id = 'reveal-paint-probe';
    strip.style.cssText = 'position:fixed;inset:0 0 auto;height:48px;background:#ff00ff;z-index:150;pointer-events:none';
    document.body.append(strip);
  });
}

export async function assertCoveredTop(page, path) {
  const screenshot = await page.screenshot({ path });
  const leakedPixels = await page.evaluate(async encoded => {
    const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
    const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
    const buffer = new OffscreenCanvas(bitmap.width, bitmap.height);
    const context = buffer.getContext('2d');
    context.drawImage(bitmap, 0, 0);
    const pixels = context.getImageData(0, 0, bitmap.width, Math.min(bitmap.height, Math.ceil(44 * devicePixelRatio))).data;
    bitmap.close();
    let leaked = 0;
    for (let index = 0; index < pixels.length; index += 4) {
      if (pixels[index] > 240 && pixels[index + 1] < 16 && pixels[index + 2] > 240) leaked++;
    }
    return leaked;
  }, screenshot.toString('base64'));
  assert.equal(leakedPixels, 0, 'the opaque opening frame covers every painted pixel at the top');
}

export async function removeTopPaintProbe(page) {
  await page.evaluate(() => document.getElementById('reveal-paint-probe')?.remove());
}
