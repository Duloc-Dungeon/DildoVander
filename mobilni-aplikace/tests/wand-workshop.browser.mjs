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
    await page.getByRole('button', { name: 'Otevřít dílnu', exact: true }).waitFor();
    assert.equal(requests.length, 0, 'opening the page creates no paid session');
    await page.getByRole('button', { name: 'Otevřít dílnu', exact: true }).click();
    await page.getByRole('heading', { name: /Co uděláš/ }).waitFor();
    const overflow = () => page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
    assert.equal(await overflow(), false, `${width}: no overflow`);
    assert.equal(await page.locator('.trunk-stage__trunk').evaluate(img => img.complete && img.naturalWidth > 0), true);
    assert.equal(await page.locator('.trunk-stage__trunk').evaluate(img => getComputedStyle(img).transform), 'none', 'reduced motion disables movement');
    await page.getByLabel('Tvoje odpověď', { exact: true }).focus();
    await page.keyboard.type('Cestu si overim sam.');
    await page.getByRole('button', { name: 'Odeslat odpověď', exact: true }).focus();
    assert.equal(await page.getByRole('button', { name: 'Odeslat odpověď', exact: true }).evaluate(button => button.matches(':focus-visible')), true);
    await page.keyboard.press('Enter');
    await page.getByText('Otázka 2 ze 3', { exact: true }).waitFor();
    await page.reload(); await page.getByText('Otázka 2 ze 3', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Odpovědět hlasem', exact: true }).click();
    await page.getByRole('button', { name: 'Odpovědět psaním', exact: true }).click();
    await page.getByLabel('Tvoje odpověď', { exact: true }).fill('Ukryje mě v mlze.');
    await page.getByRole('button', { name: 'Odeslat odpověď', exact: true }).click();
    await page.getByText('Otázka 3 ze 3', { exact: true }).waitFor();
    assert.equal(requests.some(request => request.method === 'POST' && request.path.includes('generation')), false);
    await page.screenshot({ path: path.join(output, `question3-${width}.png`), fullPage: true }); captures++;
    if (width === 390) {
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      assert.equal(await overflow(), false, '200% text: no overflow');
      await page.screenshot({ path: path.join(output, 'text-200-percent.png'), fullPage: true }); captures++;
      await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
      await page.setViewportSize({ width, height: 420 });
      await page.getByLabel('Tvoje odpověď', { exact: true }).focus();
      await page.getByRole('button', { name: 'Přeskočit tuto otázku', exact: true }).scrollIntoViewIfNeeded();
      assert.equal(await overflow(), false, 'reduced visual height: no overflow');
      await page.screenshot({ path: path.join(output, 'keyboard-height-390.png'), fullPage: true }); captures++;
      await page.setViewportSize({ width, height: 844 });
    }
    await page.getByRole('button', { name: 'Přeskočit tuto otázku', exact: true }).click();
    await page.getByText('Hůlka je uložená', { exact: true }).waitFor();
    assert.equal(await page.getByRole('heading', { name: /Co uděláš/ }).count(), 0);
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
    assert.equal(await page.getByRole('button', { name: 'Otevřít dílnu', exact: true }).count(), 0);
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ result: 'passed', widths: [320, 390, 1280], textScale: '200%', simulatedKeyboardHeight: 420, reducedMotion: true, captures, output, liveProviderCalls: 0 }));
} catch (error) {
  console.error(JSON.stringify({ url: lastPage?.url(), text: await lastPage?.locator('body').innerText(), errors }));
  await lastPage?.screenshot({ path: path.join(output, 'failure.png'), fullPage: true });
  throw error;
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
