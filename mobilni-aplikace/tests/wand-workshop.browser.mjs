import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';
import { createWorkshopFixtureServer } from './fixtures/wand-workshop.mjs';
const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-trunk-workshop');
await mkdir(output, { recursive: true });
const { server, origin, requests } = await createWorkshopFixtureServer();
const browser = await chromium.launch();
const errors = [];
let captures = 0;
let lastPage;
try {
  for (const width of [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce', isMobile: width < 500, hasTouch: width < 500 });
    const page = await context.newPage(); lastPage = page; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${origin}/__workshop/ready`);
    await page.getByRole('button', { name: 'Raději psát', exact: true }).click();
    const overflow = () => page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
    assert.equal(await overflow(), false, `${width}: no overflow`);
    assert.equal(await page.locator('.trunk-stage__trunk').evaluate(img => img.complete && img.naturalWidth > 0), true);
    assert.equal(await page.locator('.trunk-stage__trunk').evaluate(img => getComputedStyle(img).transform), 'none');
    const exchanges = width === 320 ? 2 : 4;
    for (let index = 0; index < exchanges; index++) {
      await page.getByLabel('Tvoje sdělení kufru', { exact: true }).fill(index === 1 ? 'Nevím' : `Zachraň most ${index}.`);
      await page.getByRole('button', { name: 'Odeslat', exact: true }).click();
      await page.getByRole('button', { name: 'Odeslat', exact: true }).waitFor({ state: 'visible' });
      await page.waitForFunction(() => !document.querySelector('textarea')?.disabled);
    }
    assert.equal(requests.some(request => request.path.endsWith('/finish')), false);
    await page.reload(); await page.getByRole('button', { name: 'Raději psát', exact: true }).click();
    await page.getByRole('heading', { name: /Kufr si pamatuje/ }).waitFor();
    assert.equal(await page.getByText(/Otázka .* ze 3/).count(), 0);
    await page.screenshot({ path: path.join(output, `conversation-${width}.png`), fullPage: true }); captures++;
    if (width === 390) {
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      assert.equal(await overflow(), false, '200% text: no overflow');
      await page.screenshot({ path: path.join(output, 'text-200-percent.png'), fullPage: true }); captures++;
      await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
      await page.setViewportSize({ width, height: 420 });
      await page.getByLabel('Tvoje sdělení kufru').focus();
      assert.equal(await overflow(), false, 'keyboard-height: no overflow');
      await page.screenshot({ path: path.join(output, 'keyboard-height-390.png'), fullPage: true }); captures++;
      await page.setViewportSize({ width, height: 844 });
      let failNextPoll = true;
      await page.route('**/app/v1/customer/wand/workshop', async route => {
        if (failNextPoll) { failNextPoll = false; await route.fulfill({ status: 503, contentType: 'application/json', body: '{}' }); }
        else await route.continue();
      });
      await page.getByText('Stav se nepodařilo načíst.', { exact: false }).waitFor();
      assert.equal(await page.getByLabel('Tvoje sdělení kufru').isEnabled(), true);
      assert.equal(await overflow(), false, 'temporary state error: no overflow');
      await page.screenshot({ path: path.join(output, 'state-recovery-390.png'), fullPage: true }); captures++;
      await page.getByText('Stav se nepodařilo načíst.', { exact: false }).waitFor({ state: 'hidden' });
      assert.equal(await page.getByRole('heading', { name: /Kufr si pamatuje/ }).isVisible(), true);
      await page.unroute('**/app/v1/customer/wand/workshop');
    }
    await page.getByRole('button', { name: 'To stačí, vyrob hůlku', exact: true }).focus(); await page.keyboard.press('Enter');
    await page.getByText('Hůlka je uložená', { exact: true }).waitFor();

    await page.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
    await page.locator('.trunk-stage__wand').waitFor();
    await page.waitForFunction(() => document.querySelector('.trunk-stage__wand')?.naturalWidth > 0);
    assert.equal(await overflow(), false, `${width}: result no overflow`);
    const properties = page.getByText('Vlastnosti a kouzla hůlky', { exact: true });
    await properties.focus(); await page.keyboard.press('Enter');
    assert.equal(await page.getByRole('heading', { name: 'Tři kouzla. Tvůj způsob boje.', exact: true }).isVisible(), true);
    await properties.focus(); await page.keyboard.press('Enter');
    assert.equal(requests.filter(request => request.path.endsWith('/sessions') && request.method === 'POST').length, 1);
    await page.screenshot({ path: path.join(output, `result-${width}.png`), fullPage: true }); captures++;
    await page.goto(`${origin}/__workshop/captured`);
    await page.getByText('Dílnu uzavřel Baron.', { exact: false }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Promluvit s kufrem', exact: true }).count(), 0);
    await page.goto(`${origin}/__workshop/preparing`);
    await page.getByText('Ještě připravujeme tvoji postavu.', { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Raději psát', exact: true }).count(), 0);
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ result: 'passed', widths: [320, 390, 1280], textScale: '200%', simulatedKeyboardHeight: 420, reducedMotion: true, captures, output, liveProviderCalls: 0 }));
} catch (error) {
  console.error(JSON.stringify({ url: lastPage?.url(), text: await lastPage?.locator('body').innerText(), errors }));
  await lastPage?.screenshot({ path: path.join(output, 'failure.png'), fullPage: true });
  throw error;
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
