// Synthetic, read-only entrance states rendered from the production bundle.
import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createOpeningFixtureServer } from './fixtures/opening.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-character-entrance');
await mkdir(output, { recursive: true });
const { server, origin } = await createOpeningFixtureServer();
const portrait = await readFile(new URL('./fixtures/characters/matylda.webp', import.meta.url));
const character = { profileId: 'entrance-fixture', revision: 1, name: 'Mira Nocturne', universe: 'Za zrcadlem', status: 'ready', label: 'Petr' };
const welcome = { joke: 'Tvoje ego už dorazilo. Zabralo dvě židle.', identity: 'Mira Nocturne. Sběratelka cizích tajemství.', teaser: 'Dnes možná přijde řada na tvoje.' };
const browser = await chromium.launch();
const errors = [];
try {
  for (const [width, reducedMotion] of [[320, 'reduce'], [390, 'reduce'], [1280, 'reduce'], [390, 'no-preference']]) {
    const context = await browser.newContext({ viewport: { width, height: 900 }, reducedMotion });
    const page = await context.newPage();
    await page.clock.install({ time: new Date('2026-10-28T12:00:00Z') });
    await page.clock.pauseAt(new Date('2026-10-28T12:00:01Z'));
    page.setDefaultTimeout(10000);
    page.on('pageerror', error => errors.push(error.message));
    let state = 'missing';
    let portraitFails = false;
    const writes = [];
    await context.route('**/*', async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.origin !== origin) return route.abort();
      if (request.method() !== 'GET') writes.push(url.pathname);
      const json = value => route.fulfill({ contentType: 'application/json', body: JSON.stringify(value) });
      if (url.pathname === '/app/v1/prelaunch/character') {
        if (state === 'missing') return json({ character: null, artwork: null });
        return json({ character, artwork: { status: state === 'ready' ? 'ready' : state === 'failed' ? 'failed' : 'running', welcome, loadingMessage: welcome.joke } });
      }
      if (url.pathname === '/app/v1/prelaunch/character/portrait') return portraitFails
        ? route.fulfill({ status: 503, body: '{}' })
        : route.fulfill({ contentType: 'image/webp', body: portrait });
      if (['/app/v1/customer/sessions', '/app/v1/customer/completion-updates'].includes(url.pathname)) return json({ items: [] });
      if (url.pathname === '/app/v1/customer/character') return json({ profile: null });
      return route.continue();
    });
    async function inspect(label) {
      await page.evaluate(() => document.fonts.ready);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false, `${label}: horizontal overflow`);
      const next = page.getByRole('button', { name: 'Pokračovat', exact: true });
      if (await next.count()) {
        assert.equal(await next.isEnabled(), true, `${label}: continue is available after readiness or completed recovery`);
        assert(await next.evaluate(button => button.getBoundingClientRect().height >= 44));
      }
      await page.screenshot({ path: path.join(output, `${label}-${width}-${reducedMotion}.png`), fullPage: true });
    }
    await page.goto(`${origin}/__fixture/F`);
    await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
    await page.locator('[data-entrance="waiting"]').waitFor();
    assert.equal(await page.getByRole('button', { name: 'Pokračovat', exact: true }).count(), 0);
    await inspect('waiting');
    state = 'personal';
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.clock.fastForward(1000);
    await page.locator('[data-entrance="personal"]').waitFor();
    await page.clock.runFor(600);
    await page.getByText(welcome.joke, { exact: true }).waitFor();
    await inspect('personal');
    state = 'ready';
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.locator('.entrance-portrait').waitFor({ state: 'attached' });
    await page.clock.fastForward(2000);
    await page.locator('[data-entrance="portrait"]').waitFor();
    await page.getByRole('heading', { name: `${character.name} · ${character.label}`, exact: true }).waitFor();
    assert.equal(await page.locator('.entrance-portrait').getAttribute('aria-hidden'), 'false');
    assert.equal(await page.getByRole('button', { name: 'Pokračovat', exact: true }).isEnabled(), true);
    await inspect('portrait');
    await page.clock.fastForward(10000);
    if (reducedMotion === 'reduce') assert.equal(await page.locator('.entrance-portrait').evaluate(image => getComputedStyle(image).transitionDuration), '0s');
    await page.keyboard.press('Tab');
    await page.getByRole('button', { name: 'Pokračovat', exact: true }).focus();
    assert(await page.locator('.entrance-continue').evaluate(button => getComputedStyle(button).outlineStyle !== 'none'));
    await page.getByRole('button', { name: 'Pokračovat', exact: true }).click();
    await page.waitForURL('**/me/character');
    assert.equal(await page.evaluate(() => sessionStorage.getItem('wand.group-opening-seen.v1.midnight-fixture')), 'seen');

    // Start a fresh viewing session and let the image fail after personal copy is known.
    await page.evaluate(() => sessionStorage.clear());
    portraitFails = true;
    await page.goto(`${origin}/__fixture/F`);
    await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
    await page.locator('[data-entrance="placeholder"]').waitFor();
    await page.getByText('Portrét jsme nenašli.', { exact: false }).waitFor();
    await inspect('placeholder');
    portraitFails = false;
    await page.getByRole('button', { name: 'Zkusit načíst znovu', exact: true }).click();
    await page.locator('.entrance-portrait').waitFor({ state: 'attached' });
    await page.clock.fastForward(10000);
    await page.locator('[data-entrance="portrait"]').waitFor();
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    await inspect('large-text');
    assert.deepEqual(writes, [], 'the entrance never starts or retries paid generation');
    await context.close();
  }
  // One explicitly requested retry can fail without imposing the loading delay.
  const recoveryContext = await browser.newContext({ viewport: { width: 390, height: 844 } });
  const recoveryPage = await recoveryContext.newPage();
  let recoveryReads = 0;
  await recoveryContext.route('**/app/v1/prelaunch/character', route => {
    recoveryReads++;
    return route.fulfill({ status: 503, contentType: 'application/json', body: '{"code":"unavailable"}' });
  });
  await recoveryPage.goto(`${origin}/__fixture/F`);
  await recoveryPage.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  const recoveryRetry = recoveryPage.getByRole('button', { name: 'Zkusit načíst znovu', exact: true });
  await recoveryRetry.waitFor();
  assert.equal(await recoveryPage.locator('.entrance-continue').count(), 0);
  const readsBeforeRetry = recoveryReads;
  await recoveryRetry.click();
  await recoveryPage.locator('.entrance-continue').waitFor();
  assert.equal(recoveryReads, readsBeforeRetry + 1);
  assert.equal(await recoveryRetry.count(), 0);
  await recoveryPage.locator('.entrance-continue').click();
  await recoveryPage.waitForURL('**/me/character');
  await recoveryContext.close();

  // A guest can leave after five visible minutes if preparation never finishes.
  const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
  const page = await context.newPage();
  await page.clock.install({ time: new Date('2026-10-28T12:00:00Z') });
  await page.clock.pauseAt(new Date('2026-10-28T12:00:01Z'));
  await context.route('**/app/v1/prelaunch/character', route => route.fulfill({ contentType: 'application/json', body: '{"character":null,"artwork":null}' }));
  await page.goto(`${origin}/__fixture/F`);
  await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  await page.locator('[data-entrance="waiting"]').waitFor();
  await page.clock.fastForward(299900);
  assert.equal(await page.getByRole('button', { name: 'Pokračovat', exact: true }).count(), 0);
  await page.clock.fastForward(100);
  await page.getByRole('button', { name: 'Pokračovat', exact: true }).click();
  await page.waitForURL('**/me/character');
  await context.close();
  assert.deepEqual(errors, []);
  console.log(`Character entrance passed: 320/390/1280px, progressive data and portrait, missing portrait/retry, continuation after readiness or five visible minutes, 200% text, keyboard focus and reduced motion; no mutations. Screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
