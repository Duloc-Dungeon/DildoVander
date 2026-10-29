import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer as createViteServer } from 'vite';

// Own the loopback harness unless a caller explicitly supplies its server.
const server = process.env.PUSH_BROWSER_URL ? undefined : await createViteServer({
  root: fileURLToPath(new URL('../', import.meta.url)),
  server: { host: '127.0.0.1', port: 0 },
});
let browser;
const screenshots = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'dildovander-push-entry');
await mkdir(screenshots, { recursive: true });
try {
  await server?.listen();
  const origin = process.env.PUSH_BROWSER_URL ?? server.resolvedUrls.local[0].replace(/\/$/, '');
  browser = await chromium.launch();
  for (const viewport of [{ width: 390, height: 844 }, { width: 1280, height: 900 }]) {
    const page = await browser.newPage({ viewport });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${origin}/tests/push-settings-harness.html`);
    await page.getByRole('button', { name: 'Zapnout oznámení', exact: true }).click();
    await page.getByRole('button', { name: 'Poslat zkušební oznámení' }).click();
    await page.getByText('Zkušební oznámení bylo zařazeno k odeslání.').waitFor();
    await page.getByRole('button', { name: 'Vypnout oznámení', exact: true }).click();
    await page.getByText('Oznámení jsou vypnutá.', { exact: true }).waitFor();
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Settings overflow the viewport');
    await page.goto(`${origin}/tests/push-settings-harness.html?scenario=denied`);
    await page.getByRole('button', { name: 'Zapnout oznámení', exact: true }).click();
    await page.getByRole('alert').getByText(/Oznámení nejsou povolená/).waitFor();
    await page.goto(`${origin}/tests/push-settings-harness.html?scenario=unavailable`);
    await page.getByText('Oznámení pro toto zařízení zatím nejsou připravena.').waitFor();
    assert.equal(await page.getByRole('button').count(), 0);
    assert.deepEqual(errors, []);
    await page.close();
    for (const context of ['waiting', 'wand', 'game']) {
      const offer = await browser.newPage({ viewport });
      offer.on('pageerror', error => errors.push(error.message));
      await offer.goto(`${origin}/tests/push-settings-harness.html?context=${context}`);
      const enable = offer.getByRole('button', { name: 'Zapnout oznámení', exact: true });
      await enable.waitFor();
      assert.equal(await offer.evaluate(() => window.pushFixture.permissionRequests), 0, 'entry does not lose the browser permission gesture');
      assert(await offer.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Notification offer fits the viewport');
      const box = await enable.boundingBox();
      assert(box && box.height >= 44, 'Enable has a touch target');
      await enable.focus();
      assert(await enable.evaluate(element => element === document.activeElement), 'Enable is keyboard reachable');
      await offer.screenshot({ path: path.join(screenshots, `${context}-${viewport.width}.png`), fullPage: true });
      await enable.press('Enter');
      await offer.waitForFunction(() => window.pushFixture.registrations === 1);
      await offer.locator('.push-offer').waitFor({ state: 'detached' });
      assert.equal(await offer.evaluate(() => window.pushFixture.permissionRequests), 1);
      await offer.close();
    }
    const deferred = await browser.newPage({ viewport });
    deferred.on('pageerror', error => errors.push(error.message));
    await deferred.goto(`${origin}/tests/push-settings-harness.html?context=waiting`);
    await deferred.getByRole('button', { name: 'Později', exact: true }).click();
    await deferred.reload();
    assert.equal(await deferred.locator('.push-offer').count(), 0, 'Later survives returning to the waiting room');
    assert.equal(await deferred.evaluate(() => window.pushFixture.permissionRequests), 0);
    await deferred.close();
    const denied = await browser.newPage({ viewport });
    denied.on('pageerror', error => errors.push(error.message));
    await denied.goto(`${origin}/tests/push-settings-harness.html?context=waiting&scenario=deny-request`);
    await denied.getByRole('button', { name: 'Zapnout oznámení', exact: true }).click();
    await denied.getByRole('alert').getByText(/Oznámení nejsou povolená/).waitFor();
    assert.equal(await denied.getByRole('button', { name: 'Zapnout oznámení', exact: true }).count(), 0, 'Refusal does not lead to repeated permission requests');
    assert.equal(await denied.evaluate(() => window.pushFixture.registrations), 0);
    await denied.close();
    assert.deepEqual(errors, []);
    console.log(`Push settings browser fixture passed at ${viewport.width}x${viewport.height}`);
  }
} finally { await browser?.close(); await server?.close(); }
