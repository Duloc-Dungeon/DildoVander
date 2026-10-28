// Real rendered navigation with synthetic account responses; no live providers or accounts.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createGameFixtureServer } from './fixtures/game.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-navigation');
await mkdir(output, { recursive: true });
const { server, origin } = await createGameFixtureServer();
const browser = await chromium.launch();
const errors = [];
const account = { id: 'one', displayName: 'Mira', roles: ['Customer'], openAiConsent: true };
try {
  for (const width of [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce' });
    const page = await context.newPage();
    page.setDefaultTimeout(10000);
    page.on('pageerror', error => errors.push(error.message));
    let sessionMode = 'ok';
    await context.route('**/*', async route => {
      const url = new URL(route.request().url());
      if (url.origin !== origin) return route.abort();
      const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
      if (url.pathname === '/app/v1/session') {
        if (sessionMode === 'offline') return json({}, 503);
        if (sessionMode === 'unauthorized') return route.fulfill({ status: 401, contentType: 'text/html', body: '<h1>Unauthorized</h1>' });
        return json({ enabled: true, account: sessionMode === 'anonymous' ? null : account, csrfToken: 'synthetic' });
      }
      if (url.pathname === '/app/v1/customer/character') return json({ profile: null });
      if (['/app/v1/customer/sessions', '/app/v1/customer/completion-updates', '/app/v1/archive'].includes(url.pathname)) return json({ items: [] });
      if (url.pathname === '/app/v1/account') return json({ sessions: [], grants: [], associations: [], passkeys: [], passkeyCount: 0 });
      return route.continue();
    });
    const navigation = page.getByRole('navigation', { name: 'Hlavní navigace', exact: true });
    async function checkNavigation(selected) {
      await navigation.waitFor();
      assert.deepEqual(await navigation.locator('a').evaluateAll(links => links.map(link => [link.textContent, link.getAttribute('href')])), [
        ['Večer', '/play'], ['Postava', '/me/character'], ['Památky', '/me/keepsakes'], ['Já', '/me/account'],
      ]);
      assert.equal(await page.locator('.primary-navigation').count(), 0);
      assert.equal(await navigation.locator('img').count(), 4);
      if (selected) assert.equal(await navigation.locator('[aria-current="page"]').textContent(), selected);
      await page.evaluate(() => document.fonts.ready);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false, `overflow at ${width}`);
      const bounds = await navigation.boundingBox();
      assert.equal(Math.round(bounds.x), 0);
      assert.equal(Math.round(bounds.width), width);
      assert.equal(Math.round(bounds.y + bounds.height), 844);
      assert(await navigation.locator('a').evaluateAll(links => links.every(link => link.getBoundingClientRect().height >= 44)));
      assert(await navigation.locator('span').evaluateAll(labels => labels.every(label => label.scrollWidth <= label.clientWidth + 1)), 'navigation labels remain inside their own tab');
    }
    await page.goto(`${origin}/__game/first-wand`);
    await page.getByRole('heading', { name: 'Nejdřív vlastní hůlku.' }).waitFor();
    await checkNavigation('Večer');
    await page.screenshot({ path: path.join(output, `play-${width}.png`) });
    await navigation.getByRole('link', { name: 'Já', exact: true }).click();
    await page.waitForURL('**/me/account');
    await page.locator('#customer-account-title').waitFor();
    await checkNavigation('Já');
    await page.screenshot({ path: path.join(output, `account-${width}.png`) });
    for (const [label, route] of [['Postava', 'character'], ['Památky', 'keepsakes']]) {
      await navigation.getByRole('link', { name: label, exact: true }).click();
      await page.waitForURL(`**/me/${route}`);
      await checkNavigation(label);
    }
    await navigation.getByRole('link', { name: 'Já', exact: true }).click();
    await page.waitForURL('**/me/account');
    await page.locator('.application-footer').getByRole('link', { name: 'Podpora', exact: true }).click();
    await page.waitForURL('**/support');
    await checkNavigation();
    await page.locator('main a[href="/privacy"]').click();
    await page.waitForURL('**/privacy');
    await page.goBack();
    await page.waitForURL('**/support');
    assert.equal(await page.getByRole('link', { name: '← Zpět', exact: true }).getAttribute('href'), '/me/account');
    await page.goForward();
    await page.waitForURL('**/privacy');
    await page.reload();
    const back = page.getByRole('link', { name: '← Zpět', exact: true });
    assert.equal(await back.getAttribute('href'), '/me/account');
    await back.click();
    await page.waitForURL('**/me/account');
    await checkNavigation('Já');

    // Failed verification cancels the new destination without losing established identity.
    sessionMode = 'offline';
    await navigation.getByRole('link', { name: 'Večer', exact: true }).click();
    await page.getByText('Připojení k účtu se nepodařilo ověřit.', { exact: false }).waitFor();
    assert.equal(new URL(page.url()).pathname, '/me/account');
    // The lifecycle refresh must retain the same identity and access policy too.
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await checkNavigation('Já');
    sessionMode = 'ok';
    await navigation.getByRole('link', { name: 'Večer', exact: true }).click();
    await page.waitForURL('**/play');
    await checkNavigation('Večer');
    await page.locator('.notification-stack button').first().click();

    await page.evaluate(() => document.documentElement.style.setProperty('--safe-area-inset-bottom', '34px'));
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    await checkNavigation('Večer');
    await page.waitForFunction(() => parseFloat(getComputedStyle(document.querySelector('.application-shell')).paddingBottom)
      >= document.querySelector('.wns-game-navigation').getBoundingClientRect().height - 1);
    await page.keyboard.press('Tab');
    await navigation.getByRole('link', { name: 'Já', exact: true }).focus();
    assert(await navigation.locator(':focus').evaluate(link => getComputedStyle(link).outlineStyle !== 'none'));
    await page.screenshot({ path: path.join(output, `navigation-large-text-${width}.png`) });

    sessionMode = 'unauthorized';
    await navigation.getByRole('link', { name: 'Já', exact: true }).click();
    await page.waitForURL('**/signin?**');
    assert.equal(await navigation.count(), 0, 'a real unauthorized response removes private navigation');
    sessionMode = 'anonymous';
    await page.goto(`${origin}/support`);
    await page.getByRole('link', { name: '← Zpět', exact: true }).waitFor();
    await page.locator('main a[href="/privacy"]').click();
    await page.waitForURL('**/privacy');
    await page.reload();
    assert.equal(await page.getByRole('link', { name: '← Zpět', exact: true }).getAttribute('href'), '/signin');
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(`Navigation browser checks passed: 320/390/1280px, four shared tabs, account destination, support/privacy return and reload, failed verification/retry, real 401, safe area, 200% text, focus. Screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
