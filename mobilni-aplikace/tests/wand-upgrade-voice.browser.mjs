// Real microphone capture/worklet and rendered UI, with Chromium's synthetic
// microphone and a local simulated voice provider. No live AI or user audio.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createWorkshopFixtureServer } from './fixtures/wand-workshop.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-upgrade-voice');
await mkdir(output, { recursive: true });
const fixture = await createWorkshopFixtureServer();
const browser = await chromium.launch({ args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] });
const errors = [];
let lastPage;
async function within(promise, message) {
  let timeout;
  try {
    await Promise.race([promise, new Promise((_, reject) => { timeout = setTimeout(() => reject(new Error(message)), 10000); })]);
  } finally { clearTimeout(timeout); }
}
try {
  for (const width of [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, permissions: ['microphone'], reducedMotion: width === 390 ? 'no-preference' : 'reduce' });
    await context.addInitScript(() => {
      window.__upgradeVoiceTracks = [];
      const getUserMedia = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
      navigator.mediaDevices.getUserMedia = async constraints => {
        const stream = await getUserMedia(constraints);
        window.__upgradeVoiceTracks.push(...stream.getAudioTracks());
        return stream;
      };
    });
    let socket;
    let capturedFrames = 0;
    let resolveCapture;
    const captured = new Promise(resolve => { resolveCapture = resolve; });
    await context.route('**/workshop/sessions/*/ticket', route => {
      const url = new URL(route.request().url());
      assert.equal(route.request().postDataJSON().socketOrigin, fixture.origin);
      url.protocol = 'ws:';
      url.pathname = url.pathname.replace('/ticket', '/live');
      url.searchParams.set('ticket', 'synthetic');
      return route.fulfill({ json: { url: url.href, expiresAt: new Date(Date.now() + 60000).toISOString() } });
    });
    await context.routeWebSocket('**/workshop/sessions/*/live?*', route => {
      socket = route;
      route.onMessage(message => {
        if (typeof message !== 'string' && message.length > 0) {
          capturedFrames++;
          resolveCapture();
        }
      });
      route.send(JSON.stringify({ type: 'ready', playbackMode: 'stream' }));
    });
    const page = await context.newPage();
    lastPage = page;
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${fixture.origin}/__workshop/upgrade`);
    await page.getByText('Nová kapitola tvé hůlky', { exact: true }).waitFor();
    assert.equal(await page.locator('.trunk-stage__trunk').evaluate(image => image.complete && image.naturalWidth > 0), true);
    const talk = page.getByRole('button', { name: 'Promluvit s kufrem', exact: true });
    assert.equal(await talk.isEnabled(), true);
    await page.screenshot({ path: path.join(output, `upgrade-workshop-${width}.png`), fullPage: true });
    await talk.click();
    await page.getByText('Mikrofon je zapnutý.', { exact: false }).waitFor();
    await within(captured, 'The microphone sent no audio frames.');
    assert(capturedFrames > 0);
    assert.equal(fixture.requests.find(request => request.path.endsWith('/sessions') && request.method === 'POST').body.upgrade, true);
    await page.getByRole('button', { name: 'Ztlumit mikrofon', exact: true }).click();
    await page.getByText('Mikrofon je ztlumený.', { exact: false }).waitFor();
    assert.equal(await page.evaluate(() => window.__upgradeVoiceTracks.every(track => !track.enabled)), true);
    // Let already queued socket messages drain, then check several 20 ms PCM
    // intervals to prove that the muted microphone does not transmit audio.
    await new Promise(resolve => setTimeout(resolve, 120));
    const mutedFrames = capturedFrames;
    await new Promise(resolve => setTimeout(resolve, 250));
    assert.equal(capturedFrames, mutedFrames, 'Muting stops PCM transmission.');
    const resumed = new Promise(resolve => { resolveCapture = resolve; });
    await page.getByRole('button', { name: 'Zapnout mikrofon', exact: true }).click();
    await page.getByText('Mikrofon je zapnutý.', { exact: false }).waitFor();
    await within(resumed, 'Unmuting did not resume audio transmission.');
    assert.equal(await page.evaluate(() => window.__upgradeVoiceTracks.every(track => track.enabled)), true);
    assert(capturedFrames > mutedFrames);
    const caption = page.getByRole('region', { name: 'Průběžný přepis', exact: true });
    await caption.scrollIntoViewIfNeeded();
    const geometry = () => page.evaluate(() => {
      const top = selector => document.querySelector(selector).getBoundingClientRect().top + scrollY;
      return {
        sceneHeight: document.querySelector('.trunk-workshop__presentation').getBoundingClientRect().height,
        controlsTop: top('.trunk-workshop__media-controls'),
        captionHeight: document.querySelector('.trunk-workshop__caption').getBoundingClientRect().height,
        pageHeight: document.documentElement.scrollHeight,
        scrollY,
      };
    });
    const beforeStreaming = await geometry();
    for (let index = 0; index < 24; index++) {
      socket.send(JSON.stringify({ type: 'transcript', role: 'assistant', delta: `Dlouhý přepis zůstává u kufru ${index}. ` }));
    }
    await page.getByRole('heading', { name: /Dlouhý přepis zůstává u kufru 23/ }).waitFor();
    assert.deepEqual(await geometry(), beforeStreaming, `${width}: streaming does not move the page or controls`);
    assert.equal(await caption.evaluate(element => element.scrollHeight > element.clientHeight && element.scrollTop > 0), true);
    socket.send(Buffer.alloc(48000));
    await page.locator('.trunk-stage[data-stage="speaking"]').waitFor();
    assert.deepEqual(await geometry(), beforeStreaming, `${width}: speaking uses the same scene and control slots`);
    socket.send(JSON.stringify({ type: 'audio_cancelled' }));
    await page.locator('.trunk-stage[data-stage="listening"]').waitFor();
    assert.deepEqual(await geometry(), beforeStreaming, `${width}: listening does not shift the page`);
    socket.send(JSON.stringify({ type: 'turn', turn: { id: 'synthetic-turn', role: 'assistant', text: 'Hotový přepis.', createdAt: new Date().toISOString() } }));
    socket.send(JSON.stringify({ type: 'transcript', role: 'assistant', delta: 'Další samostatná věta.' }));
    await page.getByRole('heading', { name: 'Další samostatná věta.', exact: true }).waitFor();
    socket.send(JSON.stringify({ type: 'turn', turn: { id: 'next-synthetic-turn', role: 'assistant', text: 'Zachovám klid a přidám důraz.', createdAt: new Date().toISOString() } }));
    socket.send(JSON.stringify({ type: 'state', session: fixture.voiceTurn('Vylepši hůlku. Zachovej klid a přidej důraz.') }));
    await page.getByText('Zachovám klid a přidám důraz.', { exact: true }).first().waitFor();
    await page.screenshot({ path: path.join(output, `upgrade-microphone-${width}.png`), fullPage: true });
    socket.send(JSON.stringify({ type: 'state', session: fixture.finishVoice() }));
    await page.getByRole('heading', { name: 'Hůlka dostává tvar', exact: true }).waitFor();
    socket.send(JSON.stringify({ type: 'ended' }));
    await page.getByText('Hůlka je uložená a připravená ke hře.', { exact: true }).waitFor();
    assert.equal(await page.getByRole('link', { name: 'Pokračovat s hůlkou', exact: true }).getAttribute('href'), '/play');
    await page.waitForFunction(() => window.__upgradeVoiceTracks.length > 0 && window.__upgradeVoiceTracks.every(track => track.readyState === 'ended'));
    await page.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.trunk-stage__wand')?.naturalWidth > 0);
    await page.getByText('Vlastnosti a kouzla hůlky', { exact: true }).click();
    await page.getByRole('heading', { name: 'Tři kouzla. Tvůj způsob boje.', exact: true }).waitFor();
    assert.equal(await page.locator('.archive-wand-spells > li').count(), 3);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.screenshot({ path: path.join(output, `upgrade-wand-${width}.png`), fullPage: true });
    await page.reload();
    await page.getByText('Hůlka je uložená a připravená ke hře.', { exact: true }).waitFor();
    await page.waitForFunction(() => document.querySelector('.trunk-stage__wand')?.naturalWidth > 0);
    assert.equal(fixture.requests.filter(request => request.path.endsWith('/sessions') && request.method === 'POST').length, 1);
    assert.equal(fixture.requests.some(request => request.path.endsWith('/finish')), false, 'Voice completion arrives from the server, without a text-mode finish request.');
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ result: 'passed', widths: [320, 390, 1280], captures: 9, microphone: 'Chromium fake device with real capture/worklet/PCM', provider: 'simulated', liveProviderCalls: 0, output }));
} catch (error) {
  console.error(JSON.stringify({ url: lastPage?.url(), text: await lastPage?.locator('body').innerText(), errors }));
  await lastPage?.screenshot({ path: path.join(output, 'failure.png'), fullPage: true });
  throw error;
} finally { await browser.close(); await new Promise(resolve => fixture.server.close(resolve)); }
