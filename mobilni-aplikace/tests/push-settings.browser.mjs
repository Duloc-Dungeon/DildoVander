import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const origin = process.env.PUSH_BROWSER_URL ?? 'http://127.0.0.1:5179';
const browser = await chromium.launch();
try {
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
    console.log(`Push settings browser fixture passed at ${viewport.width}x${viewport.height}`);
  }
} finally { await browser.close(); }
