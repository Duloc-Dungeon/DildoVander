// Rendered guard and navigation regressions with synthetic accounts only.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-access-navigation');
await mkdir(output, { recursive: true });
const { server, origin } = await createGameFixtureServer();
const browser = await chromium.launch();
const errors = [];
try {
  for (const width of [390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce' });
    const page = await context.newPage();
    page.setDefaultTimeout(10000);
    page.on('pageerror', error => errors.push(error.message));
    let roles = null;
    let membership = 'active';
    let sessionUnavailable = false;
    await context.route('**/*', async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.origin !== origin) return route.abort();
      const json = (body, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
      if (request.method() !== 'GET') return json({ code: 'fixture_read_only' }, 503);
      if (url.pathname === '/app/v1/session') return sessionUnavailable
        ? json({ code: 'unavailable' }, 503)
        : json({ enabled: true, account: roles ? { id: 'synthetic', displayName: 'Test', roles, openAiConsent: true } : null, csrfToken: 'synthetic' });
      if (url.pathname === '/app/v1/launch') return json({ allowed: true, serverNow: Date.now() });
      if (url.pathname === '/app/v1/customer/access') return json({ state: membership, accessRevision: 1 });
      if (url.pathname === '/app/v1/account') return json({ passkeyCount: 0, passkeys: [], sessions: [] });
      if (url.pathname.startsWith('/app/v1/testing-sessions') || url.pathname.startsWith('/app/v1/persona-lab')) return json({ code: 'provider_not_configured' }, 503);
      return route.continue();
    });

    sessionUnavailable = true;
    await page.goto(`${origin}/SUPPORT/?source=guard#help`);
    await page.waitForURL(`${origin}/support?source=guard#help`);
    await page.getByRole('heading', { level: 1 }).waitFor();
    await page.goto(`${origin}/missing-screen`);
    await page.getByRole('heading', { name: 'Tahle stránka tu není.', exact: true }).waitFor();
    assert.equal(new URL(page.url()).pathname, '/missing-screen');
    await page.screenshot({ path: path.join(output, `not-found-${width}.png`) });

    sessionUnavailable = false;
    await page.goto(`${origin}/PLAY/WAND/?resume=1`);
    await page.waitForURL(url => url.pathname === '/signin' && url.searchParams.get('return') === '/play/wand?resume=1');

    roles = ['Customer'];
    await page.goto(`${origin}/me/account`);
    await page.getByRole('heading', { name: 'Můj účet', exact: true }).waitFor();
    assert.equal(await page.getByRole('navigation', { name: 'Nástroje účtu' }).count(), 0);
    await page.goto(`${origin}/persona-lab`);
    await page.waitForURL(`${origin}/me/character`);

    roles = ['Beta', 'Tester'];
    membership = 'revoked';
    await page.goto(`${origin}/ME/ACCOUNT/`);
    await page.waitForURL(`${origin}/me/account`);
    const tools = page.getByRole('navigation', { name: 'Nástroje účtu' });
    await tools.getByRole('link', { name: 'Testovací relace', exact: true }).waitFor();
    assert.deepEqual(await tools.locator('a').evaluateAll(links => links.map(link => new URL(link.href).pathname)), ['/testing-sessions', '/persona-lab']);
    await page.screenshot({ path: path.join(output, `tester-tools-${width}.png`), fullPage: true });
    await tools.getByRole('link', { name: 'Testovací relace', exact: true }).click();
    await page.waitForURL(`${origin}/testing-sessions`);
    await page.getByRole('heading', { level: 1 }).waitFor();
    await page.goto(`${origin}/persona-lab`);
    await page.getByRole('heading', { name: 'AI laboratoř', exact: true }).waitFor();
    assert.equal(new URL(page.url()).pathname, '/persona-lab');
    await page.goto(`${origin}/launch`);
    await page.waitForURL(`${origin}/me/character`);

    roles = ['Operator'];
    await page.goto(`${origin}/settings`);
    const staffTools = page.getByRole('navigation', { name: 'Nástroje účtu' });
    await staffTools.getByRole('link', { name: 'Zákaznické relace', exact: true }).waitFor();
    assert.deepEqual(await staffTools.locator('a').evaluateAll(links => links.map(link => new URL(link.href).pathname)), ['/persona-lab', '/admin/customer-sessions']);
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(`Access navigation passed at 390px and 1280px; screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
