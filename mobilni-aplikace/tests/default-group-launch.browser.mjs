// Verifies server access revalidation through the real app shell without live services.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';
import { createOpeningFixtureServer } from './fixtures/opening.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-default-group-launch');
await mkdir(output, { recursive: true });
const { server, origin } = await createOpeningFixtureServer();
const browser = await chromium.launch();
const errors = [];
try {
  for (const width of [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, reducedMotion: 'reduce' });
    const page = await context.newPage();
    await page.clock.install();
    page.setDefaultTimeout(10_000);
    page.on('pageerror', error => errors.push(error.message));
    let allowed = true;
    let offline = false;
    let malformed = false;
    let launchAt = null;
    const writes = [];
    await context.route('**/*', async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.origin !== origin) return route.abort();
      if (request.method() !== 'GET') writes.push(url.pathname);
      if (url.pathname === '/app/v1/launch') return route.fulfill({
        status: offline ? 503 : 200,
        contentType: 'application/json',
        body: JSON.stringify(offline ? { code: 'unavailable' } : malformed ? {} : { allowed, audience: 'production', launchAt, serverNow: Date.now() }),
      });
      return route.continue();
    });
    await page.goto(`${origin}/__fixture/E`);
    await page.evaluate(() => sessionStorage.setItem('wand.group-opening-seen.v1.midnight-fixture', 'seen'));
    await page.goto(`${origin}/me/character`);
    await page.waitForURL('**/me/character');
    await page.waitForLoadState('networkidle');
    async function policyPoll() {
      const response = page.waitForResponse(response => new URL(response.url()).pathname === '/app/v1/launch');
      await page.clock.fastForward(15_000);
      await response;
      await page.clock.runFor(100);
    }

    // An unavailable policy does not prove the server closed an existing evening.
    offline = true;
    await policyPoll();
    assert.equal(new URL(page.url()).pathname, '/me/character');
    offline = false;
    malformed = true;
    await policyPoll();
    assert.equal(new URL(page.url()).pathname, '/me/character');
    malformed = false;
    allowed = false;
    launchAt = Date.now() + 3_600_000;
    await policyPoll();
    await page.waitForURL('**/launch');
    await page.locator('.countdown').waitFor();
    assert.equal(await page.locator('.character-countdown-grid > div').count(), 4);
    const countdown = await page.locator('.character-countdown-grid strong').allTextContents();
    assert(countdown.some(value => Number(value) > 0), 'future group opening remains visible in the countdown');
    assert.equal(await page.getByRole('button', { name: 'Odhlásit se', exact: true }).isEnabled(), true);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false);
    await page.evaluate(() => document.fonts.ready);
    await page.locator('.character-countdown-grid').scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(output, `rescheduled-waiting-${width}.png`), fullPage: true });

    // Account recovery remains reachable while the evening is closed.
    await page.goto(`${origin}/me/account`);
    await page.waitForURL('**/me/account');
    await page.goto(`${origin}/launch`);
    await page.locator('.countdown').waitFor();

    // At the server-confirmed deadline the prior guest returns without a new sign-in.
    allowed = true;
    launchAt = Date.now();
    await page.clock.fastForward(15_000);
    await page.waitForURL('**/me/character');
    assert.deepEqual(writes, []);
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(`Default production schedule passed: open screen -> waiting -> reopened, 320/390/1280px; no writes. Screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
