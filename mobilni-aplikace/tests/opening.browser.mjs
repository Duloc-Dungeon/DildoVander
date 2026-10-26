// Repeatable mobile visual acceptance against the production bundle and local fixtures.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createOpeningFixtureServer } from './fixtures/opening.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-midnight-table');
await mkdir(output, { recursive: true });
const { server, origin } = await createOpeningFixtureServer();
const browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH } : {}), ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
const records = [];
const errors = [];
async function inspect(page, name, details) {
  await page.evaluate(() => document.fonts.ready);
  await page.locator('img').evaluateAll(images => Promise.all(images.map(image => image.decode().catch(() => {}))));
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${name}: horizontal overflow`);
  assert.deepEqual(await page.locator('button,input,select,a').evaluateAll(elements => elements.filter(element => {
    const r = element.getBoundingClientRect();
    return r.width > 0 && (r.left < -.5 || r.right > innerWidth + .5);
  }).map(element => element.textContent?.trim().slice(0, 40))), [], `${name}: clipped controls`);
  if (await page.locator('.portrait-stage').count()) {
    const ratio = await page.locator('.portrait-stage').evaluate(element => {
      const r = element.getBoundingClientRect(); return r.width / r.height;
    });
    assert.ok(Math.abs(ratio - .8) < .002, `${name}: portrait geometry`);
  }
  await page.screenshot({ path: path.join(output, `${name}.png`), fullPage: true });
  records.push({ file: `${name}.png`, ...details });
}
try {
  for (const theme of ['lord-voldemort', 'olivander']) {
    for (const width of [320, 375, 390, 430]) {
      for (const state of ['A', 'B', 'C', 'D', 'E', 'F']) {
        const context = await browser.newContext({ viewport: { width, height: 844 }, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
        await context.addInitScript(theme => localStorage.setItem('dildovander.theme', theme), theme);
        const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
        await page.goto(`${origin}/__fixture/${state}`);
        if (state === 'A') await page.getByRole('button', { name: 'Začít', exact: true }).waitFor();
        if (state === 'B') await page.getByRole('button', { name: 'Přihlásit přístupovým klíčem', exact: true }).waitFor();
        if (state === 'C') {
          await page.getByLabel('Jméno postavy', { exact: true }).waitFor();
          assert.equal(await page.getByRole('checkbox').isChecked(), false);
        }
        if (state === 'D') await page.locator('[data-curtain="closed"]').waitFor();
        if (state === 'E') { await page.locator('.portrait-stage').scrollIntoViewIfNeeded(); await page.locator('[data-curtain="open"]').waitFor(); }
        if (state === 'F') await page.getByRole('button', { name: 'Vstoupit', exact: true }).waitFor();
        await inspect(page, `${state}-${width}-${theme}`, { state, width, theme, reducedMotion: true });
        if (state === 'C' && width === 390) {
          await page.getByLabel('Jméno postavy', { exact: true }).fill('Mira s mimořádně dlouhým příjmením a ještě delší pověstí');
          await page.getByLabel('Jméno postavy', { exact: true }).focus();
          await page.setViewportSize({ width, height: 440 });
          await page.evaluate(() => { document.documentElement.style.fontSize = '125%'; });
          await inspect(page, `C-keyboard-larger-${theme}`, { state: 'C', width, height: 440, keyboard: 'emulated viewport; not native keyboard', largerText: '125%' });
        }
        await context.close();
      }
    }
  }
  // Entry stays usable during the optional opening motion, without animation events.
  {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'no-preference' });
    const page = await context.newPage();
    await page.goto(`${origin}/__fixture/F`);
    await page.getByRole('button', { name: 'Vstoupit', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Vstoupit', exact: true }).isEnabled(), true);
    await inspect(page, 'F-optional-motion', { state: 'F', width: 390, reducedMotion: false });
    await page.getByRole('button', { name: 'Přeskočit animaci', exact: true }).click();
    await page.waitForURL('**/me/character');
    await context.close();
  }
  for (const state of ['fixed-universe', 'enrollment-error', 'consent', 'portrait-error', 'offline', 'paused', 'revoked']) {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${origin}/__fixture/${state}`);
    if (['fixed-universe', 'enrollment-error'].includes(state)) {
      await page.getByRole('button', { name: 'Začít', exact: true }).click();
      await page.getByLabel('Jméno postavy', { exact: true }).waitFor();
      if (state === 'fixed-universe') {
        await page.getByText('Univerzum je určené pozvánkou.', { exact: true }).waitFor();
        assert.equal(await page.locator('input#account-universe').count(), 0);
      } else await page.getByText(/Možnost přijetí pozvánky se nepodařilo ověřit/).waitFor();
    }
    else if (state === 'consent') { await page.locator('input[type="checkbox"]').waitFor(); assert.equal(await page.locator('input[type="checkbox"]').isChecked(), false); }
    else if (state === 'revoked') await page.waitForURL('**/access-revoked');
    else await page.locator('.launch-footer').waitFor();
    assert.equal(await page.getByRole('button', { name: 'Vstoupit', exact: true }).count(), 0, `${state}: no unauthorized entry`);
    await inspect(page, `failure-${state}`, { state, width: 390 });
    await context.close();
  }
  assert.deepEqual(errors, []);
  await writeFile(path.join(output, 'evidence.json'), JSON.stringify({ records, consoleErrors: errors, reference: 'design-system/reference/midnight-table-board.png', nativeDeviceVerified: false }, null, 2));
  console.log(JSON.stringify({ screenshots: records.length, output, consoleErrors: errors }));
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
