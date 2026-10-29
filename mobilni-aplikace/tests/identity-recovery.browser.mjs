// Exercises recovery through the real shell using only a loopback synthetic backend.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';
import { createOpeningFixtureServer } from './fixtures/opening.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-identity-recovery');
await mkdir(output, { recursive: true });
const { server, origin } = await createOpeningFixtureServer();
const browser = await chromium.launch();
const errors = [];
try {
  for (const width of [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce' });
    const page = await context.newPage();
    page.setDefaultTimeout(10_000);
    page.on('pageerror', error => errors.push(error.message));
    let signedIn = true;
    let characterFails = true;
    let logoutFails = true;
    let sessionFails = false;
    const writes = [];
    const tokens = [];
    await context.route('**/*', async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.origin !== origin) return route.abort();
      const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
      if (request.method() !== 'GET') writes.push([request.method(), url.pathname]);
      if (url.pathname === '/app/v1/session') {
        if (request.method() === 'DELETE') {
          if (logoutFails) return route.abort('connectionfailed');
          const token = request.headers()['x-dv-csrf'];
          tokens.push(token);
          if (token !== 'renewed-synthetic-token') return json({ code: 'csrf_required' }, 400);
          signedIn = false;
          return route.fulfill({ status: 204 });
        }
        if (sessionFails) return json({ code: 'unavailable' }, 503);
        return json({ enabled: true, account: signedIn ? { id: 'midnight-fixture', displayName: 'Synthetic guest', roles: ['Customer'], openAiConsent: true } : null, csrfToken: logoutFails ? 'initial-synthetic-token' : 'renewed-synthetic-token', origin });
      }
      if (url.pathname === '/app/v1/prelaunch/character' && characterFails) return json({ code: 'unavailable' }, 503);
      return route.continue();
    });
    async function inspect(label) {
      await page.evaluate(() => document.fonts.ready);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false, `${label}: overflow at ${width}`);
      await page.screenshot({ path: path.join(output, `${label}-${width}.png`), fullPage: true });
    }
    await page.goto(`${origin}/__fixture/E`);
    await page.getByRole('alert').filter({ hasText: 'Tvoji postavu' }).waitFor();
    await page.locator('.countdown').waitFor();
    const logout = page.getByRole('button', { name: 'Odhlásit se', exact: true });
    assert.equal(await logout.isEnabled(), true);
    assert(await logout.evaluate(button => button.getBoundingClientRect().height >= 44));
    await logout.focus();
    assert.notEqual(await logout.evaluate(button => getComputedStyle(button).outlineStyle), 'none');
    assert.equal(await page.locator('.portrait-stage').count(), 0);
    await inspect('character-error');
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    await inspect('character-error-large-text');
    await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
    characterFails = false;
    await page.getByRole('button', { name: 'Zkusit znovu', exact: true }).click();
    await page.getByRole('alert').waitFor({ state: 'detached' });
    await page.getByRole('heading', { name: 'Mira Nocturne', exact: true }).waitFor();
    await inspect('waiting-recovered');
    await logout.click();
    await page.waitForURL('**/signin?logout=unconfirmed');
    await page.getByRole('alert').filter({ hasText: 'Odhlášení se nepodařilo potvrdit' }).waitFor();
    await inspect('logout-unconfirmed');
    logoutFails = false;
    await page.getByRole('button', { name: 'Odhlásit se', exact: true }).click();
    await page.waitForURL(`${origin}/signin`);
    assert.deepEqual(tokens, ['', 'renewed-synthetic-token']);
    assert.equal(signedIn, false);
    assert.equal(await page.locator('button[type="submit"]').isEnabled(), true);
    assert(writes.every(([method, route]) => method === 'DELETE' && route === '/app/v1/session'));
    await inspect('signed-out');

    // A fresh document has accountsEnabled=false until session verification succeeds.
    sessionFails = true;
    await page.goto(`${origin}/signin?return=/launch`);
    const retry = page.getByRole('button', { name: 'Zkusit načíst přihlášení znovu', exact: true });
    await retry.waitFor();
    assert.equal(await page.locator('button[type="submit"]').isEnabled(), false);
    await inspect('signin-unavailable');
    sessionFails = false;
    await retry.click();
    await retry.waitFor({ state: 'detached' });
    assert.equal(await page.locator('button[type="submit"]').isEnabled(), true);
    await inspect('signin-recovered');
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(`Identity recovery passed at 320, 390 and 1280px. Screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
