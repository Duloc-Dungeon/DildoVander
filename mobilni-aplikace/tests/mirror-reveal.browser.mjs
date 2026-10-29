// Real animation time: fake clocks do not advance the browser's native animation timeline.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';
import { createOpeningFixtureServer } from './fixtures/opening.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-mirror-reveal');
await mkdir(output, { recursive: true });
const { server, origin } = await createOpeningFixtureServer();
const browser = await chromium.launch();
const errors = [];
const welcome = { joke: 'Tvoje ego už dorazilo. Zabralo dvě židle.', identity: 'Mira Nocturne. Sběratelka cizích tajemství.', teaser: 'Dnes možná přijde řada na tvoje.' };
try {
  for (const width of [320, 390, 1280]) {
    for (const reducedMotion of ['reduce', 'no-preference']) {
      for (const surface of ['waiting-room', 'entrance']) {
        const context = await browser.newContext({ viewport: { width, height: 900 }, reducedMotion });
        const page = await context.newPage();
        page.on('pageerror', error => errors.push(error.message));
        const writes = [];
        let portraitReads = 0;
        page.on('request', request => {
          if (request.method() !== 'GET') writes.push(request.url());
        });
        let releasePortrait;
        const portraitReady = new Promise(resolve => { releasePortrait = resolve; });
        await context.route(/\/app\/v1\/prelaunch\/character\/portrait(?:\?|$)/, async route => {
          portraitReads++;
          await portraitReady;
          await route.continue();
        });
        await context.route('**/app/v1/prelaunch/character', route => route.fulfill({
          contentType: 'application/json',
          body: JSON.stringify({
            character: { profileId: 'mirror-fixture', revision: 1, name: 'Mira Nocturne', universe: 'Za zrcadlem', status: 'ready' },
            artwork: { status: 'ready', welcome },
          }),
        }));
        await page.goto(`${origin}/__fixture/${surface === 'entrance' ? 'F' : 'E'}`);
        await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
        const mirror = page.locator('.portrait-mirror');
        await page.locator(surface === 'entrance' ? '.entrance-stage' : '.portrait-stage').scrollIntoViewIfNeeded();
        const waiting = page.locator('.mirror-waiting-glow');
        await waiting.waitFor({ state: 'attached' });
        const waitingAnimation = await waiting.evaluate(element => getComputedStyle(element).animationName);
        if (reducedMotion === 'reduce') assert.equal(waitingAnimation, 'none');
        else assert(waitingAnimation.startsWith('mirror-waiting-breath'));
        if (width === 390 && reducedMotion === 'no-preference') {
          await page.screenshot({ path: path.join(output, `${surface}-waiting-${width}.png`), fullPage: true });
          await page.setViewportSize({ width, height: 260 });
          await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
          await page.waitForFunction(() => getComputedStyle(document.querySelector('.mirror-waiting-glow')).animationPlayState === 'paused');
          await page.setViewportSize({ width, height: 900 });
          await page.locator(surface === 'entrance' ? '.entrance-stage' : '.portrait-stage').scrollIntoViewIfNeeded();
          await page.waitForFunction(() => getComputedStyle(document.querySelector('.mirror-waiting-glow')).animationPlayState === 'running');
        }
        releasePortrait();
        if (reducedMotion === 'no-preference') {
          await page.waitForFunction(() => {
            const mist = document.querySelector('.mirror-mist');
            return mist && parseFloat(getComputedStyle(mist).getPropertyValue('--mirror-opening')) > -10
              && parseFloat(getComputedStyle(mist).getPropertyValue('--mirror-opening')) < 80;
          });
          await page.screenshot({ path: path.join(output, `${surface}-clearing-${width}.png`), fullPage: true });
          if (width === 390) {
            await page.emulateMedia({ reducedMotion: 'reduce' });
            await page.waitForFunction(() => document.querySelector('.portrait-mirror')?.getAttribute('data-mirror') === 'open');
            await page.emulateMedia({ reducedMotion: 'no-preference' });
          }
        }
        await page.waitForFunction(() => document.querySelector('.portrait-mirror')?.getAttribute('data-mirror') === 'open');
        assert.equal(await waiting.count(), 0, 'waiting animation ends when the decoded portrait arrives');
        assert.equal(await mirror.locator('.mirror-mist').evaluate(element => getComputedStyle(element).display), 'none');
        await page.getByText(welcome.joke, { exact: true }).waitFor();
        assert.equal(await page.locator('.character-introduction').innerText(), welcome.joke);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
        await page.screenshot({ path: path.join(output, `${surface}-open-${width}-${reducedMotion}.png`), fullPage: true });
        // Repeated delivery of unchanged text keeps its DOM node and cannot restart the entrance.
        const line = await page.locator('.character-introduction').elementHandle();
        const refreshed = page.waitForResponse(response => response.url().endsWith('/app/v1/prelaunch/character'));
        await page.evaluate(() => window.dispatchEvent(new Event('online')));
        await refreshed;
        assert(await line.evaluate(element => element.isConnected));
        assert.equal(await mirror.getAttribute('data-mirror'), 'open');
        if (reducedMotion === 'no-preference') {
          const replay = page.getByRole('button', { name: 'Přehrát odhalení znovu', exact: true });
          assert(await replay.evaluate(button => button.getBoundingClientRect().height >= 44));
          const seen = await page.evaluate(() => Object.fromEntries(Object.entries(localStorage).filter(([key]) => key.startsWith('wand.portrait-seen.v1.'))));
          const readsBeforeReplay = portraitReads;
          await page.keyboard.press('Tab');
          await replay.focus();
          assert(await replay.evaluate(button => getComputedStyle(button).outlineStyle !== 'none'));
          await page.keyboard.press('Enter');
          await page.waitForFunction(() => document.querySelector('.portrait-mirror')?.getAttribute('data-mirror') === 'revealing');
          await page.waitForFunction(() => document.querySelector('.character-introduction')?.classList.contains('character-line-enter-active'));
          assert.equal(await line.evaluate(element => element.isConnected), false, 'explicit replay reintroduces the same text');
          if (width === 390) await page.screenshot({ path: path.join(output, `${surface}-replay-${width}.png`), fullPage: true });
          await page.waitForFunction(() => document.querySelector('.portrait-mirror')?.getAttribute('data-mirror') === 'open');
          assert(await replay.evaluate(button => document.activeElement === button), 'replay keeps keyboard focus');
          assert.equal(portraitReads, readsBeforeReplay, 'replay never fetches the portrait again');
          assert.deepEqual(await page.evaluate(() => Object.fromEntries(Object.entries(localStorage).filter(([key]) => key.startsWith('wand.portrait-seen.v1.')))), seen);
          assert.equal(await page.locator('.character-introduction').innerText(), welcome.joke);
        } else {
          assert.equal(await page.locator('.mirror-replay').count(), 0, 'reduced motion has no replay control');
        }
        if (reducedMotion === 'no-preference' && width === 390) {
          await page.emulateMedia({ reducedMotion: 'reduce' });
          await page.emulateMedia({ reducedMotion: 'no-preference' });
          assert.equal(await mirror.getAttribute('data-mirror'), 'open');
        }
        assert.deepEqual(writes, [], 'replaying never starts generation or another server mutation');
        await context.close();
      }
    }
  }
  assert.deepEqual(errors, []);
  console.log(`Shared mirror and moving text passed in waiting room and entrance: 320/390/1280px, animated waiting and offscreen pause, reduced/full motion, real-time mask, keyboard replay and retained focus, unchanged polling/storage and no generation or image reload. Screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
