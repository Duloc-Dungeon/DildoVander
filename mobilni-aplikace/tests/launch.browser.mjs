// Runs against the production bundle with a deterministic in-memory backend.
// No account data, network service, or paid generation provider is used.
import assert from 'node:assert/strict';
import { assertApplicationHeader } from './fixtures/application-header.mjs';
import { createServer } from 'node:http';
import { readFile, mkdir, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const screenshots = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'dildovander-launch-browser');
await stat(path.join(dist, 'index.html'));
await mkdir(screenshots, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.svg': 'image/svg+xml' };
const server = createServer(async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  if (/^\/(?:app\/v1|v1|guest)\//.test(url.pathname)) { response.writeHead(404, { 'Content-Type': 'application/json' }).end('{}'); return; }
  const relative = decodeURIComponent(url.pathname).replace(/^\//, '');
  let file = path.resolve(dist, relative || 'index.html');
  if (!file.startsWith(path.resolve(dist) + path.sep) && file !== path.join(dist, 'index.html')) file = path.join(dist, 'index.html');
  try { await stat(file); } catch { file = path.join(dist, 'index.html'); }
  response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
  response.end(await readFile(file));
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const image = await readFile(new URL('./fixtures/characters/matylda.webp', import.meta.url));
// Optional local media remains outside the repository; no video is checked in.
const rickrollVideo = process.env.RICKROLL_TEST_MP4 ? await readFile(process.env.RICKROLL_TEST_MP4) : null;
let account = { id: 'launch-guest', displayName: 'Host', roles: ['Customer'], openAiConsent: true };
let characterReads = 0;
let character = null;
let artwork = { status: 'queued', loadingMessage: null, url: null, errorCode: null };
let config = { revision: 4, timeZone: 'Europe/Prague', personalizedLoadingEnabled: true, personalizedLoadingPrompt: 'Věta hravě a tajemně.' };
let jobs = [{ profileId: 'portrait-fixture', revision: 3, status: 'failed', attempt: 2, errorCode: 'provider_unavailable', updatedAt: Date.now() }];
let audit = [{ actorId: 'admin-fixture', changedAt: Date.now(), revision: 4, changeSummary: 'schedule' }];
const writes = [];
const errors = [];
const loadedScripts = [];
let releasePolicy;
const pendingPolicy = new Promise(resolve => { releasePolicy = resolve; });
let browser;
let countdownDeadline = null;
try {
  browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH } : {}), ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 1, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
  await context.addInitScript(() => { window.confirm = () => true; });
  await context.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url());
    if (request.resourceType() === 'script') loadedScripts.push(url.pathname);
    if (url.origin !== origin) { await route.abort(); return; }
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (url.pathname === '/app/v1/launch/rickroll') return json({ available: !!rickrollVideo, url: rickrollVideo ? '/app/v1/launch/rickroll.mp4' : null });
    if (url.pathname === '/app/v1/launch/rickroll.mp4' && rickrollVideo) {
      const range = /^bytes=(\d+)-(\d*)$/.exec(request.headers().range ?? '');
      const start = range ? Number(range[1]) : 0;
      const end = range?.[2] ? Math.min(Number(range[2]), rickrollVideo.length - 1) : rickrollVideo.length - 1;
      return route.fulfill({ status: range ? 206 : 200, contentType: 'video/mp4',
        headers: { 'Accept-Ranges': 'bytes', ...(range ? { 'Content-Range': `bytes ${start}-${end}/${rickrollVideo.length}` } : {}) },
        body: rickrollVideo.subarray(start, end + 1) });
    }
    if (url.pathname === '/app/v1/session' && request.method() === 'GET') return json({ enabled: true, account, csrfToken: 'fake-csrf', origin });
    if (url.pathname === '/app/v1/passkeys/request-options') return json({ challenge: 'AQID', allowCredentials: [] });
    if (url.pathname === '/app/v1/passkeys/sign-in') {
      account = { id: 'launch-guest', displayName: 'Ron Weasley', roles: ['Customer'], openAiConsent: true };
      return json({});
    }
    if (url.pathname === '/app/v1/customer/character') { characterReads++; return json({ code: 'launch_required' }, 403); }
    if (url.pathname === '/app/v1/account') return json({ sessions: [], grants: [], associations: [], passkeys: [], passkeyCount: 1, recentPasskey: false });
    if (url.pathname === '/app/v1/push' && request.method() === 'GET') return json({ web: false, android: false, ios: false, publicKey: null, enabled: false });
    if (url.pathname === '/app/v1/session' && request.method() === 'DELETE') { account = null; return route.fulfill({ status: 204, body: '' }); }
    if (url.pathname === '/app/v1/customer/access') return json({ state: 'active', groupId: 'launch-fixture-beta', groupName: 'Interní beta', joinedAt: Math.floor(Date.now() / 1000), accessRevision: 1, fixedUniverse: null, recruitmentOpen: false, launchAt: null });
    if (url.pathname === '/app/v1/enrollment/default') return json({ available: true, groupId: 'launch-fixture-beta', groupName: 'Interní beta', fixedUniverse: null, universe: null });
    if (url.pathname === '/app/v1/launch') { await pendingPolicy; return json({ allowed: !!account && (['Owner', 'Administrator', 'Operator', 'Tester'].some(role => account.roles.includes(role)) || countdownDeadline !== null && Date.now() >= countdownDeadline), audience: account?.roles.includes('Beta') ? 'beta' : 'production', launchAt: countdownDeadline ?? Date.now() + 48 * 60 * 60 * 1000, serverNow: Date.now(), personalizedLoadingEnabled: config.personalizedLoadingEnabled }); }
    if (url.pathname === '/app/v1/prelaunch/character' && request.method() === 'GET') return json({ character, artwork: character ? artwork : null });
    if (url.pathname === '/app/v1/prelaunch/character' && request.method() === 'PUT') {
      const body = request.postDataJSON(); writes.push(body);
      character = { profileId: 'launch-profile-1', revision: 1, name: body.name, universe: body.universe, status: 'queued' };
      artwork = { status: 'queued', loadingMessage: null, url: null, errorCode: null };
      return json({ character, artwork });
    }
    if (url.pathname === '/app/v1/customer/character/label' && request.method() === 'PUT') {
      const body = request.postDataJSON();
      assert.equal(body.profileId, character.profileId);
      assert.equal(body.expectedLabel, character.label ?? null);
      assert(!('name' in body) && !('userInput' in body));
      character = { ...character, label: body.label.trim() || null };
      return json({ profile: { label: character.label } });
    }
    if (url.pathname === '/app/v1/prelaunch/character/portrait') {
      if (!account || url.searchParams.get('profileId') !== character?.profileId || Number(url.searchParams.get('revision')) !== character?.revision) return json({}, 404);
      return route.fulfill({ status: 200, contentType: 'image/webp', body: image });
    }
    if (url.pathname === '/app/v1/prelaunch/character/retry' && request.method() === 'POST') { writes.push(request.postDataJSON()); artwork = { status: 'queued', loadingMessage: null, url: null, errorCode: null }; return json({ character, artwork }); }
    if (url.pathname === '/app/v1/admin/users') return json({ users: [], grants: [] });
    if (url.pathname === '/app/v1/admin/operations/status') return json({ modes: null, readiness: { accounts: true, keepsakes: false, keepsakeGeneration: false, passkeyRelyingParty: 'localhost', nativeAssociationConfigured: false }, archive: [] });
    if (url.pathname === '/app/v1/admin/operations/logs') return json({ storeId: 'fixture', nextCursor: 0, hasMore: false, truncated: false, droppedWrites: 0, entries: [] });
    if (url.pathname === '/app/v1/admin/launch' && request.method() === 'GET') return json(config);
    if (url.pathname === '/app/v1/admin/launch' && request.method() === 'PUT') {
      const body = request.postDataJSON(); writes.push(body); config = { ...config, ...body, revision: config.revision + 1 };
      audit = [{ actorId: 'admin-fixture', changedAt: Date.now(), revision: config.revision, changeSummary: 'personalization' }, ...audit];
      return json(config);
    }
    if (url.pathname === '/app/v1/admin/launch/artwork') return json({ jobs });
    if (url.pathname === '/app/v1/admin/launch/audit') return json({ entries: audit });
    if (url.pathname === '/app/v1/admin/launch/personalization-preview' && request.method() === 'POST') { writes.push(request.postDataJSON()); return json({ message: 'Hvězdy se schovaly, aby tvá postava měla lepší nástup.' }); }
    if (/^\/app\/v1\/admin\/launch\/artwork\/[^/]+\/retry$/.test(url.pathname) && request.method() === 'POST') { writes.push(request.postDataJSON()); jobs = jobs.map(job => ({ ...job, status: 'queued' })); return json({ jobs }); }
    if (url.pathname.startsWith('/app/v1/')) return json({});
    await route.continue();
  });

  const page = await context.newPage();
  page.setDefaultTimeout(12000);
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  const firstPolicyRequest = page.waitForRequest(request => new URL(request.url()).pathname === '/app/v1/launch');
  await page.goto(`${origin}/`, { waitUntil: 'domcontentloaded' });
  await firstPolicyRequest;
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  assert.equal(await page.locator('.application-sidebar, .application-header, .application-footer').count(), 0,
    'unknown guest policy must not flash postlaunch navigation before its response');
  await assertApplicationHeader(page);
  assert.equal(await page.getByRole('button', { name: 'Stop', exact: true }).count(), 0,
    'ordinary invitation without an active operation has no hardware control');
  releasePolicy();
  await page.waitForLoadState('networkidle');
  await page.locator('.group-opening-art').waitFor();
  assert.equal(await page.locator('.character-countdown-grid').count(), 0);
  await page.screenshot({ path: path.join(screenshots, 'launch-mobile-w-entrance.png'), fullPage: true });
  await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  await page.getByRole('heading', { name: /Vstup na vlastní pověst/ }).waitFor();
  assert.equal(await page.locator('.application-topbar__brand').count(), 1);
  assert.equal(await page.getByText(/DildoVander|tester|operátor|administrace/i).count(), 0);
  assert.equal(await page.getByLabel('Jméno postavy').count(), 1);
  assert.equal(await page.locator('.application-sidebar').count(), 0);
  assert.equal(await page.getByText('Rozhovor s kufrem', { exact: false }).count(), 0);
  assert.equal(await page.getByText('Památky', { exact: false }).count(), 0);
  await page.goto(`${origin}/privacy`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Soukromí před spuštěním' }).waitFor();
  assert.equal(await page.locator('.application-sidebar').count(), 0);
  assert.equal(await page.getByText(/konverzac|kufr|památk|příběh/i).count(), 0);
  await page.goto(`${origin}/support`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Pomoc s pozvánkou a účtem' }).waitFor();
  assert.equal(await page.locator('.application-sidebar').count(), 0);
  assert.equal(await page.getByText(/konverzac|kufr|památk|příběh/i).count(), 0);
  await page.goto(`${origin}/`, { waitUntil: 'networkidle' });
  await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  await page.getByRole('heading', { name: /Vstup na vlastní pověst/ }).waitFor();
  await page.getByLabel('Jméno postavy').fill('Matylda');
  await page.getByLabel('Univerzum').fill('Město za zrcadlem');
  assert.equal(await page.getByRole('checkbox').count(), 0, 'registration consent is already recorded');
  assert.doesNotMatch(await page.locator('body').innerText(), /OpenAI/i);
  assert.equal(await page.getByLabel('Jméno postavy').inputValue(), 'Matylda');
  await page.screenshot({ path: path.join(screenshots, 'launch-mobile-character-entry.png'), fullPage: true });
  await page.getByRole('button', { name: 'Připravit překvapení' }).click();
  await page.getByRole('heading', { name: /Matylda/ }).waitFor();
  assert.match(await page.locator('.launch-status').innerText(), /Chvíli strpení|Postava je uložená/);
  assert.match(await page.locator('.countdown').innerText(), /dní/);
  await page.locator('.launch-joke').filter({ hasText: /^(Portrét: způsob, jak udělat dojem a ani se přitom nepohnout\.|Portrét zachytí i prchavý dojem\. Rám mu znemožní útěk\.|Na portrétu můžeš hledět do budoucnosti\. Bez povinnosti tam dojít\.)$/ }).waitFor();
  assert.equal(await page.locator('[aria-current="step"]').innerText(), 'Portrét');
  assert.doesNotMatch(await page.locator('.launch-copy').innerText(), /Poznáváme postavu|Mícháme barvy|rešerš|malujeme/i);
  await page.locator('.launch-notifications > summary').click();
  await page.getByRole('heading', { name: 'Oznámení', exact: true }).waitFor();
  await page.getByText('Oznámení pro toto zařízení zatím nejsou připravena.').waitFor();
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Prelaunch push settings overflow');
  await page.locator('.launch-notifications > summary').click();
  assert.deepEqual(Object.keys(writes.at(-1)).sort(), ['expectedRevision', 'name', 'openAiConsent', 'universe']);
  assert.equal(writes.at(-1).openAiConsent, true);
  assert.equal(await page.locator('.launch-label-options').evaluate(details => details.open), false, 'optional labels start collapsed');
  await page.locator('.launch-label-options summary').click();
  await page.getByLabel('Rozlišovací označení (nepovinné)').fill('U okna');
  await page.getByRole('button', { name: 'Uložit označení', exact: true }).click();
  await page.getByRole('heading', { name: 'Matylda · U okna', exact: true }).waitFor();
  assert.equal(character.name, 'Matylda');
  assert.equal(writes.length, 1, 'display labels never repeat AI creation');
  assert.equal(await page.getByText(/Summary|Appearance|Facts/).count(), 0);
  await page.screenshot({ path: path.join(screenshots, 'launch-mobile-countdown.png'), fullPage: true });
  for (const [label, viewport] of [['tablet', { width: 820, height: 1180 }], ['desktop', { width: 1440, height: 1000 }]]) {
    await page.setViewportSize(viewport);
    await page.screenshot({ path: path.join(screenshots, `launch-${label}-countdown.png`), fullPage: true });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${label} must not overflow`);
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.evaluate(() => localStorage.setItem('dildovander.interface-language.v1.account.launch-guest', 'en'));
  await page.reload();
  await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  await page.getByRole('heading', { name: /Matylda/ }).waitFor();
  await page.setViewportSize({ width: 320, height: 740 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'Czech narrow mobile must not overflow after loading a legacy English preference');
  assert.equal(await page.getByRole('combobox', { name: /Jazyk rozhraní|Interface language/ }).count(), 0);
  assert.equal(await page.locator('html').getAttribute('lang'), 'cs');
  await page.screenshot({ path: path.join(screenshots, 'launch-mobile-czech-migrated.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  character.status = 'ready';
  artwork = { status: 'running', loadingMessage: 'null', welcome: { joke: ' null ', identity: '', teaser: '' }, finalMessage: 'NEVER SHOW BEFORE READY', url: null, errorCode: null };
  await page.reload({ waitUntil: 'networkidle' });
  await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  await page.locator('.launch-joke').filter({ hasText: /^(Portrét: způsob, jak udělat dojem a ani se přitom nepohnout\.|Portrét zachytí i prchavý dojem\. Rám mu znemožní útěk\.|Na portrétu můžeš hledět do budoucnosti\. Bez povinnosti tam dojít\.)$/ }).waitFor();
  const fallbackLoading = await page.locator('.launch-status').innerText();
  assert.match(fallbackLoading, /Chvíli strpení|Postava je uložená/);
  assert.doesNotMatch(fallbackLoading, /rešerš|malujeme|avatar/i);
  assert.equal(await page.getByText('NEVER SHOW BEFORE READY').count(), 0);
  await page.screenshot({ path: path.join(screenshots, 'launch-mobile-character-loading-fallback.png'), fullPage: true });
  artwork.loadingMessage = 'Matyldo, město za zrcadlem nám poslalo tvůj spis. Zrcadla požádala o přestávku.';
  await page.reload({ waitUntil: 'networkidle' });
  await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  await page.getByText(artwork.loadingMessage, { exact: true }).waitFor();
  assert.equal(await page.getByText('NEVER SHOW BEFORE READY').count(), 0);
  await page.screenshot({ path: path.join(screenshots, 'launch-mobile-character-loading.png'), fullPage: true });
  artwork = { status: 'ready', loadingMessage: null, finalMessage: 'Matyldo, tvůj portrét. Zrcadla z tvého města si oddychla. Čekala jsi snad víc? Olivander dnes nezvládá ani vlastní odraz.', url: null, errorCode: null };
  await page.reload({ waitUntil: 'networkidle' });
  await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  await page.getByAltText('Portrét tvé postavy').waitFor({ state: 'visible' });
  await page.getByRole('heading', { name: /Matylda/ }).waitFor();
  await page.locator('.portrait-stage').scrollIntoViewIfNeeded();
  await page.locator('.launch-final').waitFor();
  assert.match(await page.locator('.launch-final').innerText(), /Zrcadla z tvého města/);
  assert.equal(await page.getByAltText('Portrét tvé postavy').evaluate(image => image.naturalWidth), 512);
  await page.screenshot({ path: path.join(screenshots, 'launch-mobile-personalized-ready.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({ path: path.join(screenshots, 'launch-desktop-personalized-ready.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  character = { profileId: 'legacy-profile', revision: 8, name: 'Eliška', universe: 'Starý svět', status: 'ready' };
  const savedCharacter = character;
  character = { ...character, name: 'Ron Weasley', universe: 'Harry Potter' };
  account = null;
  await context.addInitScript(() => {
    Object.defineProperty(navigator.credentials, 'get', { value: async () => ({
      id: 'fixture', rawId: new Uint8Array([1, 2, 3]).buffer, type: 'public-key',
      getClientExtensionResults: () => ({}),
      response: { clientDataJSON: new ArrayBuffer(1), authenticatorData: new ArrayBuffer(1), signature: new ArrayBuffer(1), userHandle: null },
    }) });
  });
  await page.goto(`${origin}/signin?return=/me/account`, { waitUntil: 'networkidle' });
  await page.getByRole('button', { name: 'Přihlásit přístupovým klíčem', exact: true }).click();
  await page.waitForURL(`${origin}/launch`);
  await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  await page.getByRole('button', { name: 'Vytvořit hůlku', exact: true }).waitFor();
  assert.equal(await page.getByRole('heading', { name: 'Ron Weasley', exact: true }).count(), 1);
  await page.screenshot({ path: path.join(screenshots, 'returning-guest-wand-preparation.png'), fullPage: true });
  const readsBeforeAccount = characterReads;
  await page.goto(`${origin}/me/account`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Můj účet', exact: true }).waitFor();
  await page.getByText(artwork.finalMessage, { exact: true }).waitFor();
  assert.equal(characterReads, readsBeforeAccount, 'account settings do not request a prelaunch-blocked character');
  assert.equal(await page.getByAltText('Portrét tvé postavy').count(), 0, 'the account joke does not load a portrait');
  assert.equal(await page.getByText('Stav rozhovoru se nepodařilo načíst.', { exact: false }).count(), 0);
  await page.evaluate(() => document.documentElement.style.setProperty('--safe-area-inset-top', '50px'));
  assert(await page.getByRole('heading', { name: 'Můj účet', exact: true }).evaluate(element => element.getBoundingClientRect().top >= 50));
  await page.screenshot({ path: path.join(screenshots, 'waiting-room-account-safe-area.png'), fullPage: true });
  character = savedCharacter;
  await page.goto(`${origin}/launch`, { waitUntil: 'networkidle' });
  artwork = null;
  await page.reload({ waitUntil: 'networkidle' });
  await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
  await page.getByText('Postava je uložená. Ještě jí připravíme portrét.').waitFor();
  assert.equal(await page.getByRole('checkbox').count(), 0);
  await Promise.all([
    page.waitForResponse(response => response.url().endsWith('/app/v1/prelaunch/character/retry') && response.request().method() === 'POST'),
    page.getByRole('button', { name: 'Připravit překvapení' }).click(),
  ]);
  assert(writes.some(write => write.expectedProfileId === 'legacy-profile' && write.expectedRevision === 8));

  await page.goto(`${origin}/signin#invite=fixture-invitation-token-1234567890`, { waitUntil: 'networkidle' });
  assert.equal(await page.getByText(/kufr|dílna|archiv|konverzac|památk/i).count(), 0, await page.locator('body').innerText());
  await page.getByRole('button', { name: /Odhlásit a pokračovat/ }).click();
  await page.getByLabel('Jméno postavy', { exact: true }).waitFor();
  assert.equal(await page.getByLabel('Kód pozvánky', { exact: true }).count(), 0, 'known invitation code stays in memory, not a redundant field');
  assert.equal(await page.locator('.application-sidebar').count(), 0);
  await page.screenshot({ path: path.join(screenshots, 'launch-mobile-invitation.png'), fullPage: true });

  assert.equal(loadedScripts.some(script => /(?:LaunchManagement(?:View|Panel)?|CustomerAdminView|PersonaLabView|TestingSessionsView|DirectorPanel)-/.test(script)), false, 'prelaunch guest must not load privileged view chunks');
  account = { id: 'launch-admin', displayName: 'Admin', roles: ['Owner'], openAiConsent: true };
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`${origin}/settings/admin`, { waitUntil: 'networkidle' });
  await page.getByRole('link', { name: 'Otevřít správu spuštění' }).click();
  await page.getByRole('heading', { name: 'Příprava večera' }).waitFor();
  assert.equal(await page.locator('#beta-launch, #launch-audience, #countdown-minutes, #production-launch, #launch-teaser, #launch-loading').count(), 0);
  await page.getByText('Osobní věty při přípravě postavy', { exact: true }).click();
  await page.locator('#launch-prompt').fill('Nový pokyn pro osobní větu.');
  await page.getByRole('button', { name: 'Uložit osobní věty', exact: true }).click();
  assert(writes.some(write => write.expectedRevision === 4 && write.personalizedLoadingPrompt === 'Nový pokyn pro osobní větu.'));
  const beforePreview = writes.length;
  assert.equal(beforePreview, 3);
  await page.getByRole('button', { name: 'Vytvořit ukázku' }).click();
  await page.getByText(/Hvězdy se schovaly/).waitFor();
  await page.locator('details.launch-admin__disclosure > summary').filter({ hasText: 'Příprava portrétů' }).click();
  await page.getByRole('button', { name: 'Opakovat přípravu' }).click();
  assert.equal(writes.length, 5);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: path.join(screenshots, 'launch-desktop-admin.png'), fullPage: true });
  for (const [audience, width, height] of rickrollVideo ? [['production', 390, 844], ['beta', 1440, 1000]] : []) {
    account = { id: 'launch-guest', displayName: 'Host', roles: audience === 'beta' ? ['Customer', 'Beta'] : ['Customer'], openAiConsent: true };
    countdownDeadline = Date.now() + 5_000;
    await page.setViewportSize({ width, height });
    await page.goto(`${origin}/launch`, { waitUntil: 'networkidle' });
    assert.equal(await page.locator('.character-countdown-grid').count(), 0);
    await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
    await page.locator('.character-countdown-grid').waitFor();
    assert.equal(await page.locator('.rickroll-video').isVisible(), false);
    // Only the W entrance is tapped; playback at zero requires no second tap.
    await page.locator('.rickroll-video').waitFor();
    await page.waitForFunction(() => {
      const video = document.querySelector('.rickroll-video video');
      return video && !video.paused && video.currentTime > 0 && video.volume === 0.8 && !video.muted;
    });
    assert.equal(await page.locator('.group-opening, .reception-waiting').count(), 0);
    const frame = await page.locator('.rickroll-video video').boundingBox();
    assert(frame && frame.width >= 200 && frame.height >= 200 && frame.x + frame.width <= width);
    await page.screenshot({ path: path.join(screenshots, `rickroll-${audience}.png`), fullPage: true });
    await page.locator('.rickroll-countdown').waitFor();
    assert.equal(await page.locator('.rickroll-video button').isDisabled(), true);
    await page.getByRole('button', { name: 'Pokračovat', exact: true }).click({ timeout: 20_000 });
    await page.reload({ waitUntil: 'networkidle' });
    await page.locator('.group-opening').waitFor();
    assert.equal(await page.locator('.rickroll-video').isVisible(), false, 'restart after zero must enter normally');
  }
  if (rickrollVideo) {
    countdownDeadline = Date.now() - 2 * 60_000;
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${origin}/me/account`, { waitUntil: 'networkidle' });
    const overlay = page.getByRole('dialog', { name: 'Hudební přivítání' });
    await overlay.waitFor();
    const play = overlay.getByRole('button', { name: 'Přehrát video', exact: true });
    if (await play.isVisible()) await play.click();
    await page.waitForFunction(() => document.querySelector('.group-rickroll video')?.currentTime > 1);
    assert.equal(await overlay.locator('.rickroll-countdown').isVisible(), true);
    await page.keyboard.press('Escape');
    assert.equal(await overlay.isVisible(), true, 'Escape cannot skip the playback countdown');
    await page.screenshot({ path: path.join(screenshots, 'rickroll-late-arrival-390.png') });
    await overlay.getByRole('button', { name: 'Pokračovat', exact: true }).click({ timeout: 20_000 });
    await overlay.waitFor({ state: 'hidden' });
    countdownDeadline = Date.now() - 16 * 60_000;
    await page.reload({ waitUntil: 'networkidle' });
    assert.equal(await overlay.isVisible(), false, 'the opening window expires after fifteen minutes');
  }
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ mobile: '390x844', tablet: '820x1180', desktop: '1440x1000', realMp4Playback: !!rickrollVideo, states: ['countdown', 'character creation', 'portrait pending and ready', 'invitation account switch', 'personalization save', 'personalization preview', 'artwork retry'], screenshots, mockedWrites: writes.length }, null, 2));
  await context.close();
} finally {
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}
