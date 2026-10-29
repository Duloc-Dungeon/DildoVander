// Repeatable native-bridge simulation against the production bundle. No remote
// services, accounts, installers, signing or hardware are involved.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, stat, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
await stat(path.join(dist, 'index.html'));
const screenshots = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-application-updates');
await mkdir(screenshots, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.ttf': 'font/ttf', '.svg': 'image/svg+xml', '.png': 'image/png', '.webp': 'image/webp' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    if (/^\/(app|v1|guest)\//.test(url.pathname)) {
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}');
      return;
    }
    let file = path.resolve(dist, `.${url.pathname}`);
    if (file !== path.resolve(dist) && !file.startsWith(path.resolve(dist) + path.sep)) {
      response.writeHead(404).end(); return;
    }
    try { if (!(await stat(file)).isFile()) file = path.join(dist, 'index.html'); }
    catch { file = path.join(dist, 'index.html'); }
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    response.end(await readFile(file));
  } catch { response.writeHead(500).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;
try {
  try { browser = await chromium.launch(); }
  catch (error) {
    if (process.platform !== 'win32') throw error;
    browser = await chromium.launch({ channel: 'chrome' });
  }
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
  await context.route('**/*', route => route.request().url().startsWith(origin) ? route.continue() : route.abort());
  await context.addInitScript(() => {
    window.CapacitorCustomPlatform = { name: 'ios' };
    window.__versionPolicy = { minimumBuild: '10.10.1', latestBuild: '10.10.2', updateUrl: 'https://example.test/ios/' };
    window.__versionChecks = 0;
    window.__versionResume = () => {};
    if (!sessionStorage.getItem('fixture.deferred-startup')) {
      window.__versionDelay = new Promise(resolve => { window.__releaseVersion = resolve; });
      sessionStorage.setItem('fixture.deferred-startup', 'true');
    }
    window.Capacitor = {
      PluginHeaders: [
        { name: 'App', methods: [{ name: 'getInfo', rtype: 'promise' }, { name: 'addListener', rtype: 'callback' }, { name: 'removeListener', rtype: 'promise' }] },
        { name: 'KufrIdentity', methods: ['request', 'cancelAccountRequest'].map(name => ({ name, rtype: 'promise' })) },
      ],
      nativeCallback: async (_plugin, method, options, callback) => {
        if (method === 'addListener' && options.eventName === 'appStateChange') window.__versionResume = () => callback({ isActive: true });
        return 'fixture-listener';
      },
      nativePromise: async (plugin, method, options) => {
        if (plugin === 'App' && method === 'getInfo') return { id: 'synthetic.app', name: 'Fixture', version: '1.0', build: localStorage.getItem('fixture.installed-build') ?? '10.9.1' };
        if (plugin === 'KufrIdentity' && method === 'request') {
          let body = {};
          if (options.path === '/app/v1/application-version') {
            window.__versionChecks++;
            if (localStorage.getItem('fixture.offline')) throw new Error('Synthetic offline');
            if (window.__versionDelay) await window.__versionDelay;
            body = { ios: window.__versionPolicy, android: { minimumBuild: '0', latestBuild: '0', updateUrl: '' } };
          } else if (options.path === '/app/v1/session') body = { enabled: false, account: null, csrfToken: null };
          else if (options.path === '/app/v1/launch') body = { allowed: false };
          return { status: 200, body: JSON.stringify(body) };
        }
        return {};
      },
    };
  });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(`${origin}/play`);
  await page.waitForFunction(() => window.__versionChecks === 1);
  assert.equal(await page.locator('.application-shell').count(), 0, 'initial policy verification must finish before route mount');
  await page.evaluate(() => window.__releaseVersion());
  await page.getByRole('heading', { name: 'Je čas na aktualizaci' }).waitFor();
  const heading = page.getByRole('heading', { name: 'Je čas na aktualizaci' });
  assert.equal(await heading.evaluate(element => document.activeElement === element), true, 'update is announced through heading focus');
  assert.equal(await heading.evaluate(element => getComputedStyle(element).outlineStyle), 'none', 'automatic heading focus has no border');
  assert.equal(await page.locator('.application-shell').count(), 0, 'deep route must remain unmounted');
  const update = page.getByRole('link', { name: 'Aktualizovat aplikaci' });
  assert.equal(await update.getAttribute('href'), 'https://example.test/ios/');
  await update.focus();
  await page.keyboard.press('Tab');
  assert.equal(await page.getByRole('button', { name: 'Zkontrolovat znovu' }).evaluate(element => element === document.activeElement), true);
  for (const [width, height] of [[390, 844], [1280, 800]]) {
    await page.setViewportSize({ width, height });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `no horizontal overflow at ${width}`);
    assert.equal(await update.evaluate(element => element.getBoundingClientRect().height >= 44), true);
    await page.screenshot({ path: path.join(screenshots, `required-${width}.png`) });
  }
  await page.evaluate(() => localStorage.setItem('fixture.offline', 'true'));
  await page.reload();
  await page.getByRole('alert').waitFor();
  assert.equal(await page.locator('.application-shell').count(), 0, 'cached minimum survives offline restart');
  await page.getByRole('button', { name: 'Zkontrolovat znovu' }).click();
  await page.waitForFunction(() => !document.querySelector('.application-update-actions button')?.disabled);
  assert.equal(await page.locator('.application-shell').count(), 0);
  await page.evaluate(() => {
    localStorage.removeItem('fixture.offline');
    window.__versionResume();
  });
  await page.waitForFunction(() => window.__versionChecks >= 3);
  await page.getByRole('heading', { name: 'Je čas na aktualizaci' }).waitFor();
  await page.evaluate(() => localStorage.setItem('fixture.installed-build', '10.10.1'));
  await page.reload();
  await page.locator('.application-shell').waitFor();
  await page.getByRole('complementary', { name: 'Aktualizace aplikace' }).waitFor();
  await page.evaluate(() => localStorage.setItem('fixture.installed-build', '10.10.2'));
  await page.reload();
  await page.locator('.application-shell').waitFor();
  assert.equal(await page.getByRole('complementary', { name: 'Aktualizace aplikace' }).count(), 0);
  assert.deepEqual(errors, [], 'no uncaught browser errors');
  console.log(`Application update browser checks passed at ${origin}; viewports 390x844 and 1280x800; screenshots ${screenshots}`);
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
