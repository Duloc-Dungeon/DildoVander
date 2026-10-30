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
    const voiceStart = page.getByRole('button', { name: 'Promluvit s kufrem', exact: true });
    assert.equal(await page.locator('#workshop-elevenlabs-consent').count(), 0, `${width}: saved unified consent needs no repeated voice checkbox`);
    assert.equal(await voiceStart.isEnabled(), true);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${width}: voice disclosure fits`);
    await page.screenshot({ path: path.join(output, `voice-consent-${width}.png`), fullPage: true }); captures++;
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
    if (width === 1280) {
      await page.locator('.trunk-workshop__transcript summary').click();
      await page.evaluate(() => {
        const scene = document.querySelector('.trunk-workshop__scene').getBoundingClientRect();
        const header = document.querySelector('.application-topbar').getBoundingClientRect();
        window.scrollTo(0, scrollY + scene.top - header.height + 32);
      });
      assert(await page.locator('.trunk-workshop__presentation').evaluate(element =>
        element.getBoundingClientRect().top >= document.querySelector('.application-topbar').getBoundingClientRect().bottom + 15), 'desktop workshop stays below the sticky brand header');
      await page.screenshot({ path: path.join(output, 'desktop-sticky-header.png') }); captures++;
      await page.locator('.trunk-workshop__transcript summary').click();
      await page.evaluate(() => window.scrollTo(0, 0));
    }
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
    await page.getByText('Hůlka je uložená a připravená ke hře.', { exact: true }).waitFor();

    if (width === 390) {
      let artworkFailure = 'fetch';
      await page.route('**/artwork/assets/*', route => artworkFailure === 'fetch'
        ? route.fulfill({ status: 503, body: '' })
        : artworkFailure === 'decode'
          ? route.fulfill({ contentType: 'image/png', body: 'invalid image bytes' })
          : route.continue());
      await page.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
      await page.getByText('Ilustraci se nepodařilo načíst.', { exact: true }).waitFor();
      artworkFailure = 'decode';
      await page.evaluate(() => {
        window.__artworkDecodeFailures = 0;
        document.addEventListener('error', event => {
          if (event.target instanceof HTMLImageElement && event.target.classList.contains('trunk-stage__wand')) window.__artworkDecodeFailures++;
        }, true);
      });
      await page.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
      await page.waitForFunction(() => window.__artworkDecodeFailures > 0);
      await page.waitForFunction(() => document.querySelector('.trunk-stage')?.getAttribute('data-stage') === 'revealed'
        && document.querySelector('.trunk-workshop__scene')?.getAttribute('data-artwork-visible') === 'false');
      await page.getByText('Ilustraci se nepodařilo načíst.', { exact: true }).waitFor();
      await page.screenshot({ path: path.join(output, 'artwork-decode-failed-390.png'), fullPage: true }); captures++;
      assert.equal(await page.getByRole('heading', { name: 'Tichý přístav', exact: true }).count(), 1);
      artworkFailure = '';
    }

    await page.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
    await page.locator('.trunk-stage__wand').waitFor();
    await page.waitForFunction(() => document.querySelector('.trunk-stage__wand')?.naturalWidth > 0);
    assert.equal(await overflow(), false, `${width}: result no overflow`);
    const repair = page.getByRole('button', { name: 'Opravit ilustraci na průhledném pozadí', exact: true });
    const assertNoticeBelowArtwork = async () => {
      const art = await page.locator('.trunk-stage__artwork').boundingBox();
      const notice = await page.locator('.trunk-workshop__artwork-notice').boundingBox();
      assert(art && notice && notice.y >= art.y + art.height, `${width}: artwork notice stays below the illustration`);
      const button = await repair.boundingBox();
      assert(button && button.height >= 44, `${width}: repair remains a usable touch target`);
    };
    await assertNoticeBelowArtwork();
    await repair.focus();
    await page.keyboard.press('Tab');
    await page.keyboard.press('Shift+Tab');
    assert.equal(await repair.evaluate(button => button.matches(':focus-visible')), true);
    if (width === 390) {
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      await assertNoticeBelowArtwork();
      assert.equal(await overflow(), false, '200% result text: no overflow');
      await page.screenshot({ path: path.join(output, 'result-text-200-percent.png'), fullPage: true }); captures++;
      await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
    }
    const properties = page.getByText('Vlastnosti a kouzla hůlky', { exact: true });
    await properties.focus(); await page.keyboard.press('Enter');
    assert.equal(await page.getByRole('heading', { name: 'Tři kouzla. Tvůj způsob boje.', exact: true }).isVisible(), true);
    await properties.focus(); await page.keyboard.press('Enter');
    assert.equal(requests.filter(request => request.path.endsWith('/sessions') && request.method === 'POST').length, 1);
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: path.join(output, `result-${width}.png`), fullPage: true }); captures++;
    await page.goto(`${origin}/__workshop/upgrade`);
    await page.getByText('Nová kapitola tvé hůlky', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Raději psát', exact: true }).click();
    assert.equal(requests.find(request => request.path.endsWith('/sessions') && request.method === 'POST').body.upgrade, true);
    assert.equal(requests.some(request => request.path.endsWith('/workshop') && request.upgrade === '1'), true);
    for (const text of ['Chci vylepšit svou hůlku.', 'Zachovej její klid a přidej důraz.']) {
      await page.getByLabel('Tvoje sdělení kufru').fill(text);
      await page.getByRole('button', { name: 'Odeslat', exact: true }).click();
      await page.waitForFunction(() => !document.querySelector('textarea')?.disabled);
    }
    await page.getByRole('button', { name: 'To stačí, vyrob hůlku', exact: true }).click();
    await page.getByText('Hůlka je uložená a připravená ke hře.', { exact: true }).waitFor();
    await page.reload();
    await page.getByText('Hůlka je uložená a připravená ke hře.', { exact: true }).waitFor();
    assert.equal(requests.filter(request => request.path.endsWith('/sessions') && request.method === 'POST').length, 1);
    assert.equal(await overflow(), false, `${width}: upgraded wand result no overflow`);
    await page.screenshot({ path: path.join(output, `upgrade-result-${width}.png`), fullPage: true }); captures++;
    await page.goto(`${origin}/__workshop/captured`);
    await page.getByText('Dílnu uzavřel Baron.', { exact: false }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Promluvit s kufrem', exact: true }).count(), 0);
    await page.goto(`${origin}/__workshop/preparing`);
    await page.getByText('Ještě připravujeme tvoji postavu.', { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Raději psát', exact: true }).count(), 0);
    await context.close();
  }
  // A newly made wand earns one visual acknowledgement; opening it again does not.
  const completionContext = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'no-preference' });
  const completionPage = await completionContext.newPage(); lastPage = completionPage;
  completionPage.on('pageerror', error => errors.push(error.message));
  await completionPage.goto(`${origin}/__workshop/ready`);
  await completionPage.getByRole('button', { name: 'Raději psát', exact: true }).click();
  await completionPage.getByLabel('Tvoje sdělení kufru', { exact: true }).fill('Chci hůlku, která zachová klid.');
  await completionPage.getByRole('button', { name: 'Odeslat', exact: true }).click();
  await completionPage.getByRole('button', { name: 'To stačí, vyrob hůlku', exact: true }).click();
  const completion = completionPage.locator('.trunk-workshop__saved');
  await completion.waitFor();
  await completion.locator('.aw-notice__icon img').evaluate(image => image.decode());
  assert(await completion.locator('.aw-notice__icon img').evaluate(image => image.naturalWidth > 0));
  assert.equal(await completionPage.locator('.trunk-workshop').getAttribute('data-new-wand'), 'true');
  assert.notEqual(await completion.locator('.aw-notice__icon').evaluate(icon => getComputedStyle(icon).animationName), 'none');
  await completionPage.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
  await completionPage.locator('.wand-reveal-field').waitFor();
  await completionPage.emulateMedia({ reducedMotion: 'reduce' });
  assert.equal(await completion.locator('.aw-notice__icon').evaluate(icon => getComputedStyle(icon).animationName), 'none');
  await completionPage.reload();
  await completion.waitFor();
  assert.equal(await completionPage.locator('.trunk-workshop').getAttribute('data-new-wand'), 'false');
  await completionPage.locator('.trunk-stage__wand').waitFor();
  await completionPage.locator('.trunk-stage__wand').evaluate(image => image.decode());
  assert.equal(await completionPage.locator('.wand-reveal-field').count(), 0, 'restored artwork never mounts the reveal');
  await completionPage.emulateMedia({ reducedMotion: 'no-preference' });
  assert.equal(await completion.locator('.aw-notice__icon').evaluate(icon => getComputedStyle(icon).animationName), 'none');
  await completionPage.goto('about:blank');
  await completionPage.goBack();
  await completionPage.locator('.trunk-stage__wand').waitFor();
  await completionPage.locator('.trunk-stage__wand').evaluate(image => image.decode());
  assert.equal(await completionPage.locator('.wand-reveal-field').count(), 0, 'returning to saved artwork stays static');
  await completionPage.screenshot({ path: path.join(output, 'completion-confirmed-390.png'), fullPage: true }); captures++;
  await completionContext.close();
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ result: 'passed', widths: [320, 390, 1280], textScale: '200%', simulatedKeyboardHeight: 420, reducedMotion: true, captures, output, liveProviderCalls: 0 }));
} catch (error) {
  console.error(JSON.stringify({ url: lastPage?.url(), text: await lastPage?.locator('body').innerText(), errors }));
  await lastPage?.screenshot({ path: path.join(output, 'failure.png'), fullPage: true });
  throw error;
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
