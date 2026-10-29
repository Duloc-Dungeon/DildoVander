// Repeatable isolated checks of the compiled app. All APIs are synthetic and
// non-local network requests are blocked; this suite never controls equipment.
import assert from 'node:assert/strict';
import { readFile, stat, mkdir } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const screenshots = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'dildovander-application-browser');
await stat(path.join(dist, 'index.html'));
await mkdir(screenshots, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.svg': 'image/svg+xml', '.json': 'application/json' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    if (request.method !== 'GET') { response.writeHead(405).end(); return; }
    if (/^\/(?:app\/v1|guest\/v1|v1|models)\//.test(url.pathname)) { response.writeHead(404, { 'Content-Type': 'application/json' }).end('{}'); return; }
    const relative = decodeURIComponent(url.pathname).replace(/^\//, '');
    let file = path.resolve(dist, relative || 'index.html');
    if (!file.startsWith(dist)) { response.writeHead(403).end(); return; }
    try { if (!(await stat(file)).isFile()) file = path.join(dist, 'index.html'); }
    catch { file = path.join(dist, 'index.html'); }
    const bytes = await readFile(file);
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    response.end(bytes);
  } catch { if (!response.headersSent) response.writeHead(500).end(); else response.destroy(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;
let page;
const errors = [];
try {
  try { browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH } : {}), ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) }); }
  catch (error) {
    if (process.env.PLAYWRIGHT_CHANNEL || process.platform !== 'win32') throw error;
    browser = await chromium.launch({ channel: 'chrome' });
  }
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
  page = await context.newPage();
  page.setDefaultTimeout(10000);
  const requests = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error' && /Content Security Policy|unsafe-eval|Refused to|EvalError/i.test(message.text())) errors.push(message.text());
  });
  let sessionAccount = null;
  let customerSessions = [];
  let failedCustomerPolls = 0;
  let customerStatusReads = 0;
  const customerEndRequests = [];
  let customerProfile = { profileId: 'browser-profile', revision: 2, userInput: { name: 'Luna', world: 'Moon Saga' }, status: 'ready', enrichment: { summary: 'A prepared character summary.', sources: ['Public character guide'] } };
  const characterWrites = [];
  const preferenceWrites = [];
  let savedPreferences = { schemaVersion: 1, preferenceRevision: 0, conversationLanguage: 'cs-CZ', tone: 'roast', persisted: false };
  const resultLocator = 'b1dfd34f-5362-4e60-8e31-a7fac4b326c8';
  let sessionResult = {
    schemaVersion: 1, sessionId: 'browser-recovering-session', resultLocator: null,
    readiness: { state: 'pending', reason: 'awaiting_result', canRetry: false, shouldPoll: true, hasStory: false, hasArtwork: false },
    accessKind: null, canReadPrivate: false, canManage: false, canUnlink: false,
  };
  const ownerAccount = { id: 'browser-owner', displayName: 'Testovací vlastník', roles: ['Owner'], openAiConsent: true };
  const homeHeading = () => 'Moje postava';
  await context.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url());
    if (url.origin !== origin) { await route.abort(); return; }
    requests.push({ path: url.pathname, method: request.method(), url: request.url() });
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: sessionAccount, csrfToken: 'isolated-browser-csrf' });
    if (sessionAccount?.roles?.includes('Customer') && url.pathname === '/app/v1/customer/access') return json({ state: 'active', groupId: 'browser-beta', groupName: 'Interní beta', joinedAt: Math.floor(Date.now() / 1000), accessRevision: 1, fixedUniverse: null, recruitmentOpen: false, launchAt: null });
    if (url.pathname === '/app/v1/enrollment/default') return json({ available: true, fixedUniverse: null });
    if (url.pathname === '/app/v1/launch') return json({ allowed: true, audience: 'production', launchAt: null, serverNow: Date.now(), teaser: '', initialLoadingMessage: '', personalizedLoadingEnabled: false });
    if (sessionAccount && url.pathname === '/app/v1/account') return json({ sessions: [{ id: 'browser-session', installationName: 'Tento prohlížeč', current: true, trusted: false, expiresAt: 1900000000 }], grants: [], associations: [], passkeyCount: 1, passkeys: [{ id: 'browser-passkey', name: 'Můj klíč', createdAt: '2026-10-20T00:00:00Z', isBackedUp: true }], devices: [] });
    if (sessionAccount && url.pathname === '/app/v1/customer/character') {
      if (request.method() === 'PUT') {
        const body = request.postDataJSON();
        assert.equal(body.openAiConsent, true, 'character work uses recorded account consent');
        assert.equal(body.portraitConsent, true, 'first creation and administrator edits request a matching portrait');
        assert.equal(body.newCharacter, undefined, 'the application never replaces a character identity');
        assert.equal(body.expectedProfileId, customerProfile?.profileId ?? null);
        assert.equal(body.expectedRevision, customerProfile?.revision ?? 0);
        assert(!customerProfile || sessionAccount.roles.some(role => ['Owner', 'Administrator'].includes(role)), 'only an administrator may edit an existing character');
        characterWrites.push(body);
        customerProfile = { profileId: customerProfile?.profileId ?? 'browser-first-profile', revision: (customerProfile?.revision ?? 0) + 1, userInput: body.userInput, status: 'queued' };
      }
      return json({ profile: customerProfile });
    }
    if (sessionAccount && url.pathname === '/app/v1/prelaunch/character' && request.method() === 'GET') return json({
      character: customerProfile ? { profileId: customerProfile.profileId, revision: customerProfile.revision,
        name: customerProfile.userInput.name, universe: customerProfile.userInput.world, status: customerProfile.status } : null,
      artwork: null,
    });
    if (sessionAccount && url.pathname === '/app/v1/customer/wand/workshop') return json({ eligible: false, reason: 'wand_character_required', tier: 1, profile: null, session: null });
    if (sessionAccount && url.pathname === '/app/v1/customer/conversation-preferences') {
      if (request.method() === 'PUT') {
        const body = request.postDataJSON();
        assert.equal(body.profileId, customerProfile.profileId);
        assert.equal(body.profileRevision, customerProfile.revision);
        assert.equal(body.expectedPreferenceRevision, savedPreferences.preferenceRevision);
        preferenceWrites.push(body);
        savedPreferences = { schemaVersion: 1, preferenceRevision: savedPreferences.preferenceRevision + 1,
          conversationLanguage: body.conversationLanguage, tone: body.tone, persisted: true };
      } else assert.equal(request.method(), 'GET');
      return json({ ...savedPreferences, profileId: customerProfile.profileId, profileRevision: customerProfile.revision });
    }
    if (sessionAccount && url.pathname === '/app/v1/customer/sessions') {
      customerStatusReads += 1;
      if (failedCustomerPolls > 0) {
        failedCustomerPolls -= 1;
        return json({ code: 'temporarily_unavailable' }, 503);
      }
      return json({ items: customerSessions });
    }
    if (sessionAccount && url.pathname === '/app/v1/customer/sessions/browser-recovering-session/result') {
      assert.equal(request.method(), 'GET', 'result observation is read-only');
      return json(sessionResult);
    }
    if (sessionAccount && request.method() === 'POST' && url.pathname === '/app/v1/customer/sessions/browser-recovering-session/end') {
      customerEndRequests.push(url.pathname);
      customerSessions = customerSessions.map(session => ({ ...session, endRequested: true }));
      return json({}, 202);
    }
    if (sessionAccount && url.pathname === '/app/v1/customer/admin/sessions') return json({ items: [] });
    if (sessionAccount && url.pathname === '/app/v1/customer/admin/characters') return json({ items: [] });
    if (sessionAccount && url.pathname === '/app/v1/customer/completion-updates') return json({ schemaVersion: 1, items: [], nextCursor: null });
    if (sessionAccount && /^\/app\/v1\/customer\/admin\/devices\/[^/]+\/waiting$/.test(url.pathname)) return json({ schemaVersion: 1, availability: { state: 'unknown', reason: 'runtime_unverified', fresh: false }, event: null, head: null, count: 0 });
    if (sessionAccount && url.pathname === '/app/v1/admin/users') return json({ users: [], grants: [] });
    if (sessionAccount && url.pathname === '/app/v1/archive/associations') return json({ items: [] });
    if (url.pathname.startsWith('/app/v1/')) return sessionAccount
      ? json({ code: 'not_available' }, 404)
      : json({ code: 'account_required' }, 401);
    if (url.pathname === '/guest/v1/evening') return json({ schemaVersion: 1, evening: null, items: [], latest: null });
    if (url.pathname === '/guest/v1/bootstrap') return json({ schemaVersion: 1, csrfToken: 'isolated-guest-csrf' });
    if (url.pathname === '/guest/v1/claim') return json({ locator: '11111111-1111-4111-8111-111111111111' });
    if (url.pathname.startsWith('/guest/v1/')) return json({ code: 'not_available' }, 404);
    await route.continue();
  });
  const hasHeading = title => page.getByRole('heading', { name: title, exact: true }).waitFor();
  const fits = async () => {
    await page.evaluate(async () => {
      await document.fonts.ready;
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    });
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1 ? [] : [...document.querySelectorAll('main *')]
      .filter(element => element.getBoundingClientRect().right > innerWidth + 1)
      .slice(0, 8).map(element => ({ tag: element.tagName, class: element.className, width: element.getBoundingClientRect().width })));
    assert.equal(overflow.length, 0, `layout has no horizontal page overflow: ${JSON.stringify(overflow)}`);
  };

  // Launch is already open in this fixture: anonymous entry must still use
  // onboarding and never revive the retired home/demo/archive shell.
  for (const entry of ['/', '/demo', '/demo/', '/DEMO']) {
    await page.goto(origin + entry);
    await page.waitForURL(url => url.pathname === '/signin');
    await hasHeading('Vstup na vlastní pověst.');
    assert.equal(await page.locator('.home-layout, .customer-demo, .primary-navigation, .shell-preferences').count(), 0);
  }
  for (const width of [1440, 390, 320]) {
    await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
    await page.goto(origin);
    await hasHeading('Vstup na vlastní pověst.');
    const titleLayout = () => page.locator('h1').evaluate(element => ({
      left: element.getBoundingClientRect().left,
      width: element.getBoundingClientRect().width,
      size: getComputedStyle(element).fontSize,
      align: getComputedStyle(element).textAlign,
    }));
    const arrivalTitle = await titleLayout();
    const artSize = await page.locator('.signin-art').boundingBox();
    const artFits = async () => {
      assert(await page.locator('.signin-art img').evaluate(image => {
        const art = image.parentElement.getBoundingClientRect();
        const rect = image.getBoundingClientRect();
        const style = getComputedStyle(image);
        return rect.left >= art.left && rect.top >= art.top
          && rect.right <= art.right + 1 && rect.bottom <= art.bottom + 1
          && style.objectFit === 'contain' && parseFloat(style.paddingTop) >= 16;
      }), 'the whole illustration fits inside its panel with space around it');
    };
    await artFits();
    await fits();
    await page.screenshot({ path: path.join(screenshots, `entry-arrival-${width}.png`), fullPage: true });
    await page.getByRole('button', { name: 'Už mám přístup', exact: true }).click();
    await hasHeading('Zpátky na scénu.');
    assert.deepEqual(await titleLayout(), arrivalTitle, 'returning sign-in shares the arrival grid and type');
    const signInArt = await page.locator('.signin-art').boundingBox();
    assert.equal(signInArt.width, artSize.width);
    assert.equal(signInArt.height, artSize.height);
    await artFits();
    await fits();
    await page.screenshot({ path: path.join(screenshots, `entry-signin-${width}.png`), fullPage: true });
    await page.getByRole('button', { name: 'Zpět', exact: true }).click();
    await page.getByRole('button', { name: 'Začít', exact: true }).click();
    await hasHeading('Jak ti budou říkat?');
    assert.deepEqual(await titleLayout(), arrivalTitle, 'registration shares the same content width and title scale');
    assert.equal(await page.locator('.paper-surface, .signin-language').count(), 0);
    assert(await page.locator('button[type="submit"]').isDisabled(), 'registration requires explicit consent');
    await page.getByLabel('Jméno postavy', { exact: true }).fill('Hermiona Grangerová');
    await page.getByLabel('Univerzum', { exact: true }).fill('Harry Potter');
    await page.locator('.consent-fieldset input').check();
    assert(await page.locator('button[type="submit"]').isEnabled());
    await fits();
    await page.screenshot({ path: path.join(screenshots, `entry-registration-${width}.png`), fullPage: true });
    if (width === 320) {
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      await fits();
      await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
    }
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  for (const [route, title] of [['/support', 'S čím potřebuješ pomoct?'], ['/privacy', 'Ochrana soukromí']]) {
    await page.goto(origin + route);
    await hasHeading(title);
    assert.equal(new URL(page.url()).pathname, route, 'support and privacy remain public without an account');
    assert(await page.getByRole('main').getByRole('link', { name: 'support@dildovander.de', exact: true }).isVisible());
    for (const width of [1440, 390, 320]) {
      await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
      await fits();
      await page.screenshot({ path: path.join(screenshots, `${route.slice(1)}-${width}.png`), fullPage: true });
    }
  }
  await page.goto(origin);
  await hasHeading('Vstup na vlastní pověst.');
  await page.setViewportSize({ width: 1440, height: 1000 });
  assert.equal(await page.getByRole('button', { name: 'Stop', exact: true }).count(), 0);
  assert.equal(await page.locator('.primary-navigation, .shell-preferences').count(), 0);
  for (const protectedPath of ['/settings/admin', '/play', '/play/match/' + 'a'.repeat(32)]) {
    await page.goto(origin + protectedPath);
    await page.waitForURL(url => url.pathname === '/signin');
    assert.equal(new URL(page.url()).searchParams.get('return'), protectedPath);
  }
  for (const entry of ['/me', '/me/']) {
    await page.goto(origin + entry);
    await page.waitForURL(url => url.pathname === '/signin' && url.searchParams.get('return') === '/me/character');
  }
  await page.goto(origin + '/signin'); await page.getByRole('heading', { name: 'Vstup na vlastní pověst.', exact: true }).waitFor();
  assert.equal(await page.locator('.lost-access-help').count(), 0, 'first arrival keeps recovery on the returning-access path');
  await page.getByRole('button', { name: 'Už mám přístup', exact: true }).click();
  await page.getByRole('heading', { name: 'Zpátky na scénu.', exact: true }).waitFor();
  const lostAccess = page.locator('.lost-access-help');
  const lostAccessSummary = lostAccess.locator('summary');
  assert.equal(await lostAccess.getAttribute('open'), null);
  await lostAccessSummary.focus();
  await page.keyboard.press('Enter');
  assert.notEqual(await lostAccess.getAttribute('open'), null);
  assert(await lostAccess.getByRole('link', { name: 'kontaktuj podporu' }).isVisible());
  assert.match(await lostAccess.innerText(), /pouze vlastník služby/);
  assert.equal(await page.locator('.shell-preferences').count(), 0, 'sign-in keeps the prelaunch shell free of feature navigation and preferences');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(origin + '/signin');
  await page.getByRole('button', { name: 'Už mám přístup', exact: true }).click();
  const customerEnrollmentAction = page.getByRole('button', { name: 'Vytvořit zákaznický účet', exact: true });
  const keyArt = page.locator('.signin-art--violet');
  const artHeightBeforeFocus = await keyArt.evaluate(image => image.getBoundingClientRect().height);
  await customerEnrollmentAction.focus();
  assert.equal(await keyArt.evaluate(image => image.getBoundingClientRect().height), artHeightBeforeFocus,
    'button focus cannot move the action by resizing artwork between pointerdown and click');
  await customerEnrollmentAction.click();
  await page.getByLabel('Jak ti máme říkat?').waitFor();
  assert.equal(await page.locator('.lost-access-help').count(), 0, 'enrollment keeps its own recovery guidance');
  assert(await page.getByLabel('Jak ti máme říkat?').isVisible());
  assert.equal(await page.locator('#account-invite').count(), 0, 'customer registration does not ask for an invite');
  const registrationConsent = page.locator('.consent-fieldset input[type="checkbox"]');
  assert.equal(await registrationConsent.isChecked(), false, 'registration consent is explicit and initially unchecked');
  assert.equal(await registrationConsent.getAttribute('required'), '');
  assert(await page.locator('form button[type="submit"]').isDisabled(), 'registration is blocked until consent is granted');

  // Customer pages use isolated API fixtures. The end request below changes only
  // synthetic session state; browser mode never requests BLE or a start ticket.
  sessionAccount = { id: 'browser-customer', displayName: 'Testovací host', roles: ['Customer'], openAiConsent: true };
  for (const entry of ['/me', '/me/']) {
    await page.goto(origin + entry);
    await page.waitForURL(url => url.pathname === '/me/character');
    await hasHeading('Moje postava');
  }
  await page.goto(origin + '/me/character'); await hasHeading('Moje postava');
  const customerNavigation = page.getByRole('navigation', { name: 'Hlavní navigace', exact: true });
  assert.equal(await customerNavigation.getByRole('link').count(), 4);
  assert.equal(await customerNavigation.getByRole('link', { name: 'Hry', exact: true }).getAttribute('href'), '/play');
  assert.equal(await page.getByRole('button', { name: 'Stop', exact: true }).count(), 0);
  await page.getByRole('heading', { name: 'Luna', exact: true }).waitFor();
  const assertCustomerReadOnly = async (target = page) => {
    assert.equal(await target.locator('#character-name').count(), 0, 'an existing customer character has no editor');
    assert.equal(await target.getByText('Upravit nebo upřesnit postavu', { exact: true }).count(), 0, 'refinement belongs to administrators');
    assert.equal(await target.getByRole('button', { name: 'Nová postava', exact: true }).count(), 0, 'an account cannot add another character');
    assert.equal(await target.locator('#new-character-name').count(), 0, 'no hidden replacement form exists');
    assert.equal(await target.locator('#character-openai-consent').count(), 0, 'recorded consent is not requested again');
    assert(!/OpenAI/.test(await target.locator('main').innerText()), 'the post-registration character view does not repeat the provider name');
    assert.equal(await target.locator('.trunk-workshop').count(), 0, 'the character overview opens the workshop as its own destination');
  };
  await assertCustomerReadOnly();
  assert.equal(await page.locator('.character-next-step').count(), 1, 'the ready character has one main next step');
  assert.equal(await page.locator('.character-next-step').getAttribute('href'), '/play/wand');
  assert.equal(await page.getByText('A prepared character summary.', { exact: true }).count(), 0, 'internal preparation details are absent from the customer view');
  assert.equal(characterWrites.length, 0, 'viewing a saved character cannot mutate it');
  for (const width of [1440, 390, 320]) {
    await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
    await fits(); await assertCustomerReadOnly();
    const action = await page.locator('.character-next-step').boundingBox();
    const dock = await customerNavigation.boundingBox();
    assert(action.y + action.height < dock.y, 'identity and its next step fit above the navigation on first view');
    await page.screenshot({ path: path.join(screenshots, `customer-character-${width}.png`), fullPage: true });
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  await fits(); await assertCustomerReadOnly();
  await page.screenshot({ path: path.join(screenshots, 'customer-character-320-text-200.png'), fullPage: true });
  await page.evaluate(() => { document.documentElement.style.fontSize = ''; });

  // Administrative edits retain the existing profile identity. Local brief ideas
  // remain drafts until one explicit save, using the consent recorded on the account.
  const originalCustomerProfile = customerProfile;
  const customerAccount = sessionAccount;
  sessionAccount = { id: 'browser-admin', displayName: 'Syntetický administrátor', roles: ['Administrator'], openAiConsent: true };
  await page.goto(origin + '/me/character'); await hasHeading('Moje postava');
  await page.getByText('Upravit nebo upřesnit postavu', { exact: true }).click();
  assert.equal(await page.locator('#character-name').inputValue(), 'Luna');
  assert.equal(await page.locator('#character-openai-consent').count(), 0);
  await page.getByRole('button', { name: '+ Skryté tajemství', exact: true }).click();
  assert.match(await page.locator('#character-description').inputValue(), /skrývá tajemství/);
  assert.equal(characterWrites.length, 0, 'brief ideas stay local until explicit save');
  await page.getByText('Podrobnosti přípravy a zdroje', { exact: true }).click();
  assert.equal(await page.getByText('Public character guide').count(), 1);
  await page.locator('#character-name').fill('Luna upřesněná');
  await page.getByRole('button', { name: 'Uložit změny', exact: true }).click();
  await page.getByRole('heading', { name: 'Luna upřesněná', exact: true }).waitFor();
  assert.equal(characterWrites.length, 1, 'one administrative save issues one mutation');
  assert.equal(characterWrites[0].expectedProfileId, originalCustomerProfile.profileId);
  assert.equal(customerProfile.profileId, originalCustomerProfile.profileId, 'administrator editing preserves the account character identity');
  assert.equal(customerProfile.revision, originalCustomerProfile.revision + 1);
  assert.equal(await page.getByRole('button', { name: 'Nová postava', exact: true }).count(), 0, 'administrators edit rather than replace the identity');

  // First creation also works when native dialog APIs are absent. Once saved,
  // a second page and reload both expose only that same character.
  sessionAccount = { id: 'browser-first-customer', displayName: 'První host', roles: ['Customer'], openAiConsent: true };
  customerProfile = null;
  const fallbackPage = await context.newPage();
  fallbackPage.on('pageerror', error => errors.push(error.message));
  await fallbackPage.addInitScript(() => {
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: undefined });
    Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: undefined });
  });
  await fallbackPage.goto(origin + '/me/character');
  await fallbackPage.getByRole('heading', { name: 'Moje postava', exact: true }).waitFor();
  assert.equal(await fallbackPage.locator('details.character-extra').evaluate(element => element.open), false);
  assert.equal(await fallbackPage.locator('#character-description').isVisible(), false, 'first creation starts with only the essential fields');
  await fallbackPage.locator('#character-name').fill('Mira');
  await fallbackPage.locator('#character-world').fill('Star Book');
  for (const width of [1440, 390, 320]) {
    await fallbackPage.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
    assert(await fallbackPage.getByRole('button', { name: 'Vytvořit postavu', exact: true }).isVisible());
    assert(await fallbackPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    await fallbackPage.screenshot({ path: path.join(screenshots, `customer-first-character-${width}.png`), fullPage: true });
  }
  await fallbackPage.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  await fallbackPage.getByRole('button', { name: 'Vytvořit postavu', exact: true }).scrollIntoViewIfNeeded();
  assert(await fallbackPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
  await fallbackPage.screenshot({ path: path.join(screenshots, 'customer-first-character-320-text-200.png'), fullPage: true });
  await fallbackPage.evaluate(() => { document.documentElement.style.fontSize = ''; });
  const optionalCharacterDetails = fallbackPage.locator('details.character-extra > summary');
  await optionalCharacterDetails.press('Enter');
  await fallbackPage.locator('#character-description').fill('Rozepsaný nepovinný příběh.');
  await optionalCharacterDetails.press('Enter');
  assert.equal(await fallbackPage.locator('#character-description').isVisible(), false);
  assert.equal(await fallbackPage.locator('#character-description').inputValue(), 'Rozepsaný nepovinný příběh.', 'collapsing optional detail preserves the draft');
  await fallbackPage.getByRole('button', { name: 'Vytvořit postavu', exact: true }).click();
  await fallbackPage.getByRole('heading', { name: 'Mira', exact: true }).waitFor();
  assert.equal(characterWrites.length, 2);
  assert.equal(characterWrites[1].expectedProfileId, null);
  assert.equal(characterWrites[1].expectedRevision, 0);
  assert.equal(customerProfile.revision, 1);
  const createdProfileId = customerProfile.profileId;
  await assertCustomerReadOnly(fallbackPage);
  await fallbackPage.reload();
  await fallbackPage.getByRole('heading', { name: 'Mira', exact: true }).waitFor();
  await assertCustomerReadOnly(fallbackPage);
  assert.equal(characterWrites.length, 2, 'reload cannot create a second character');
  assert.equal(customerProfile.profileId, createdProfileId);
  await fallbackPage.close();
  sessionAccount = customerAccount;
  customerProfile = originalCustomerProfile;
  await page.goto(origin);
  await hasHeading('Moje postava');
  await page.getByRole('link', { name: 'Otevřít dílnu', exact: true }).waitFor();
  assert(await page.getByRole('navigation', { name: 'Hlavní navigace', exact: true }).getByRole('link', { name: /Památky/ }).isVisible());
  const customerNav = page.getByRole('navigation', { name: 'Hlavní navigace', exact: true });
  assert.deepEqual(await customerNav.getByRole('link').evaluateAll(links => links.map(link => link.getAttribute('href'))), ['/play/wand', '/play', '/me/keepsakes', '/me/account']);
  for (const removedEntry of ['Rozhovor']) {
    assert.equal(await customerNav.getByRole('link', { name: removedEntry, exact: true }).count(), 0, `${removedEntry} is not part of customer navigation`);
  }
  // The current customer shortcut keeps conversation entry in the game flow.
  await page.goto(origin + '/me/conversation');
  await page.waitForURL('**/play/wand');
  await hasHeading('Kufrova dílna');
  assert.equal(customerEndRequests.length, 0);
  assert.equal(requests.filter(request => request.path.includes('start-tickets')).length, 0);
  for (const width of [1440, 390, 320]) {
    await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
    await fits();
    await page.screenshot({ path: path.join(screenshots, `customer-wand-game-${width}.png`), fullPage: true });
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  await fits();
  await page.screenshot({ path: path.join(screenshots, 'customer-wand-game-320-large-text.png'), fullPage: true });
  await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
  await customerNav.getByRole('link', { name: 'Já', exact: true }).click();
  await page.getByRole('heading', { name: 'Přístupové klíče' }).waitFor();
  assert.equal(await page.getByLabel('Název přístupového klíče 1').count(), 1);
  assert.equal(await page.getByRole('heading', { name: 'Verze aplikace' }).count(), 0);
  assert.equal(await page.getByRole('heading', { name: 'Přidělené prostředky' }).count(), 0);
  await page.getByRole('heading', { name: 'Přihlášená zařízení' }).waitFor();
  assert.equal(await page.locator('h1').count(), 1, 'embedded account security view does not add a second page heading');

  // Customer reading frames and navigation must work across the dock breakpoint,
  // narrow phones and enlarged text, without changing the destinations or APIs.
  let collectionItems = [{
    locator: resultLocator, linkedAt: 1_761_200_000, officialName: 'Večer, který se poněkud protáhl',
    storyTitle: 'Luna a velmi nepraktické vítězství', characterName: 'Luna z NeuvěřitelněDlouhéhoKouzelnickéhoRodu',
    eveningAt: 1_761_100_000, hasPortrait: false, accessKind: 'originalOwner',
    canManage: true, canReadPrivate: true, canUnlink: false,
    readiness: { state: 'ready', reason: null, canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: false },
  }];
  const collectionRoute = '**/app/v1/archive/collection?*';
  await context.route(collectionRoute, route => route.fulfill({ json: { schemaVersion: 1, items: collectionItems, nextCursor: null } }));
  for (const width of [1440, 768, 390, 320]) {
    await page.setViewportSize({ width, height: width >= 768 ? 1000 : 844 });
    for (const [destination, heading] of [['/me/account', 'Můj účet'], ['/me/keepsakes', 'Moje památky']]) {
      await page.goto(origin + destination); await hasHeading(heading);
      if (destination === '/me/keepsakes') {
        const archive = page.locator('.customer-keepsakes__archive');
        assert.equal(await archive.getAttribute('open'), null, 'workshop keepsakes start collapsed below the chronicle');
        assert.equal(await page.locator('.collection-card').count(), 0, 'workshop collection mounts only after expansion');
        await archive.locator('summary').focus();
        await page.keyboard.press('Enter');
        await page.locator('.collection-card').waitFor();
        assert.equal(await page.locator('.collection-tools').getAttribute('open'), null, 'search and management start collapsed');
        assert.equal(await page.locator('.collection-item-details').getAttribute('open'), null, 'access metadata starts collapsed');
        assert.equal(await page.getByRole('link', { name: 'Otevřít památku', exact: true }).count(), 1);
        await page.locator('.collection-open').scrollIntoViewIfNeeded();
        const opening = await page.locator('.collection-open').boundingBox();
        const navigation = await customerNav.boundingBox();
        assert(opening.y + opening.height < navigation.y, 'the expanded keepsake can open above the fixed dock');
        if (width === 390) {
          await page.locator('.collection-tools > summary').focus();
          await page.keyboard.press('Enter');
          await page.getByRole('searchbox', { name: 'Hledat v památkách' }).waitFor({ state: 'visible' });
          await fits();
          await page.keyboard.press('Enter');
          await page.getByRole('searchbox', { name: 'Hledat v památkách' }).waitFor({ state: 'hidden' });
        }
      }
      await fits();
      const frame = await page.locator('.customer-page').boundingBox();
      assert(Math.abs(frame.x + frame.width / 2 - width / 2) < 12, 'customer content is centered');
      const dock = await customerNav.boundingBox();
      assert(dock.width <= (width >= 768 ? 576 : width), 'desktop dock stays compact');
      assert(await customerNav.getByRole('link').evaluateAll(links => links.every(link => {
        const rect = link.getBoundingClientRect(); return rect.width >= 44 && rect.height >= 44;
      })), 'customer navigation retains touch targets');
      await page.evaluate(() => window.scrollTo({ top: document.documentElement.scrollHeight, behavior: 'instant' }));
      const footer = await page.locator('.application-footer').boundingBox();
      assert(footer.y + footer.height <= dock.y + 1, 'the final footer links can scroll above the fixed customer dock');
      await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'instant' }));
      await page.screenshot({ path: path.join(screenshots, `customer-${destination.split('/').at(-1)}-${width}.png`), fullPage: true });
      if (width === 320) {
        await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
        await fits();
        await page.screenshot({ path: path.join(screenshots, `customer-${destination.split('/').at(-1)}-320-text-200.png`), fullPage: true });
        await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
      }
    }
  }
  const savedCollectionItems = collectionItems;
  collectionItems = [];
  await page.locator('.collection-tools > summary').click();
  await page.getByRole('button', { name: 'Obnovit památky', exact: true }).click();
  await page.locator('.collection-empty').waitFor(); await fits();
  await page.screenshot({ path: path.join(screenshots, 'customer-keepsakes-empty-320.png'), fullPage: true });
  collectionItems = savedCollectionItems;
  await page.getByRole('button', { name: 'Obnovit památky', exact: true }).click();
  await page.locator('.collection-open').waitFor();
  await fits();
  await context.unroute(collectionRoute);
  for (const protectedPath of ['/persona-lab', '/settings', '/settings/admin', '/admin/customer-sessions']) {
    await page.goto(origin + protectedPath);
    await page.waitForURL(url => url.pathname === '/me/character');
    assert.equal(await page.getByRole('heading', { name: 'Moje postava' }).count(), 1, `customer denied ${protectedPath}`);
  }
  // The remainder exercises existing operations with a labelled synthetic owner.
  sessionAccount = ownerAccount;
  await page.goto(origin); await hasHeading(homeHeading());
  assert.equal(new URL(page.url()).pathname, '/me/character');
  assert.equal(await page.locator('.primary-navigation, .owner-player-controls').count(), 0);
  await page.goto(origin + '/me/account');
  const administrationEntry = page.getByRole('link', { name: 'Večer a pozvánky', exact: true });
  await administrationEntry.click();
  await page.waitForURL(url => url.pathname === '/settings/admin/launch');
  await hasHeading('Večer a pozvánky');
  await page.goto(origin + '/me/account');
  await page.getByRole('link', { name: 'Účty a oprávnění', exact: true }).click();
  await page.waitForURL(url => url.pathname === '/settings/admin');
  await page.goto(origin + '/admin/customer-sessions'); await hasHeading('Zákaznické relace');
  await page.getByRole('heading', { name: 'Příprava postav' }).waitFor();
  assert.equal(await page.getByText('Žádná příprava postavy není v evidenci.').count(), 1);
  await page.goto(origin + '/settings');
  assert.equal(await page.getByLabel('Barevné téma', { exact: true }).count(), 0);
  assert.equal(await page.locator('.shell-preferences, .interface-language-select').count(), 0);
  const nav = page.getByRole('navigation', { name: 'Sekce aplikace' });
  assert.deepEqual(await nav.getByRole('link').evaluateAll(links => links.map(link => link.getAttribute('href'))), ['/', '/play', '/settings/admin/launch', '/me/account']);
  for (const removedEntry of ['Dílna', 'Archiv', 'Správa']) {
    assert.equal(await nav.getByRole('link', { name: removedEntry, exact: true }).count(), 0, `${removedEntry} is not in staff primary navigation`);
  }
  for (const [label, heading] of [['Domů', homeHeading()], ['Večer', 'Večer a pozvánky']]) {
    await page.goto(origin + '/settings');
    await hasHeading('Nastavení');
    await nav.getByRole('link', { name: label, exact: true }).click(); await hasHeading(heading);
    assert.equal(await page.locator('.application-shell').count(), 1);
  }
  await nav.getByRole('link', { name: 'Hra', exact: true }).click();
  await page.waitForURL(url => url.pathname === '/play');
  assert.equal(await page.locator('.application-shell').count(), 1, 'the staff game link stays inside the application shell');
  for (const route of ['/settings/admin', '/signin']) {
    await page.goto(origin + route); await page.getByRole('main').waitFor(); await fits();
    await page.reload(); await page.getByRole('main').waitFor();
  }
  await page.goto(origin + '/settings'); await hasHeading('Nastavení');
  assert.equal(await page.getByRole('heading', { name: 'Verze aplikace' }).count(), 0, 'settings does not expose build metadata');
  assert.equal(await page.getByRole('heading', { name: 'Přidělené prostředky' }).count(), 0, 'settings does not expose assignment administration');
  const legacyDestinations = [
    ...['/tonight', '/suitcase', '/suitcase/diagnostics', '/suitcase/director', '/connections', '/app/', '/app/legacy', '/companion/', '/companion/legacy'].map(legacy => [legacy, '/play']),
    ...['/workshop', '/workshop/index.html', '/simulator/'].map(legacy => [legacy, '/play/wand']),
    ...['/archive', '/archive/chronicle', '/keepsake/chronicle.html', '/keepsake/evening/'].map(legacy => [legacy, '/me/keepsakes']),
  ];
  for (const [legacy, destination] of legacyDestinations) {
    await page.goto(origin + legacy);
    await page.waitForURL(url => url.pathname === destination);
  }
  await page.goto(origin + '/unavailable-page'); await hasHeading('Tahle stránka tu není.');

  // Real inline capture runs before imports; no claim is exchanged on a GET.
  const claimCount = () => requests.filter(request => request.path === '/guest/v1/claim').length;
  const before = claimCount();
  await page.goto(origin + '/keepsake/#c=' + 'a'.repeat(40));
  await page.getByRole('button', { name: 'Otevřít moji památku', exact: true }).waitFor();
  assert.equal(new URL(page.url()).hash, ''); assert.equal(claimCount(), before);
  assert.match(await page.locator('meta[name=robots]').getAttribute('content'), /noindex/);
  await page.getByRole('button', { name: 'Otevřít moji památku', exact: true }).click();
  await page.waitForURL(/\?k=/); assert.equal(claimCount(), before + 1);
  assert(requests.every(request => !request.url.includes('a'.repeat(40))), 'claim capability never appears in a request URL');

  for (const theme of ['v2']) {
    await page.goto(origin);
    for (const width of [1440, 390, 320]) {
      await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
      for (const route of ['/', '/play', '/play/wand', '/me/keepsakes', '/archive/duel', '/keepsake/', '/signin', '/settings', '/settings/admin', '/me/account', '/unavailable-page']) {
        await page.goto(origin + route); await page.getByRole('main').waitFor(); await page.waitForTimeout(100); await fits();
        const currentPath = new URL(page.url()).pathname;
        if (currentPath === '/signin') {
          assert.equal(await page.locator('.application-header, .primary-navigation, .application-footer, .shell-preferences').count(), 0, 'prelaunch sign-in does not expose ordinary feature navigation');
          continue;
        }
        if (width <= 720 && !['/play', '/play/wand'].includes(currentPath)) {
          assert(await page.locator('.application-header').evaluate(header => header.getBoundingClientRect().height < 150), 'collapsed mobile header leaves room for the current task');
          assert(await page.locator('.primary-navigation a').evaluateAll(links => links.every(link => {
            const rect = link.getBoundingClientRect();
            return rect.width >= 44 && rect.height >= 44;
          })), 'staff mobile navigation retains 44px targets in both dimensions');
          const selected = page.locator('.primary-navigation a.selected');
          if (await selected.count()) assert(await selected.evaluate(link => {
            const rect = link.getBoundingClientRect();
            const nav = link.closest('nav').getBoundingClientRect();
            return rect.left >= nav.left - 2 && rect.right <= nav.right + 2;
          }), 'deep-linked navigation stays visible in the scrolling bar');
        }
        if (new URL(page.url()).pathname === '/signin') {
          assert.equal(await page.locator('.application-header, .primary-navigation, .application-footer').count(), 0, 'sign-in hides ordinary navigation and feature links');
        } else if (currentPath === '/me/account') {
          await hasHeading('Můj účet');
          const installer = page.getByRole('link', { name: 'Aplikace pro iPhone', exact: true });
          assert.equal(await installer.getAttribute('href'), '/ios/');
          assert(await installer.isVisible(), 'the iPhone distribution link remains visible');
          assert(await installer.evaluate(link => link.getBoundingClientRect().height >= 44), 'the iPhone link retains a 44px touch target');
        }
        const name = route.replace(/^\/|\/$/g, '').replaceAll('/', '-') || 'home';
        await page.screenshot({ path: path.join(screenshots, `${theme}-${name}-${width}.png`), fullPage: true });
      }
      assert(await page.locator('.primary-navigation a').evaluateAll(links => links.every(link => link.getBoundingClientRect().height >= 44)), 'navigation targets retain 44px height');
    }
    await page.goto(origin); await hasHeading(homeHeading());
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    await fits();
    await page.waitForFunction(() => parseFloat(getComputedStyle(document.querySelector('.application-shell')).paddingBottom)
      >= document.querySelector('.wns-game-navigation').getBoundingClientRect().height - 1);
    assert.equal(await page.getByRole('button', { name: 'Stop', exact: true }).count(), 0, 'idle home does not display an equipment Stop control');
    await page.screenshot({ path: path.join(screenshots, `${theme}-home-320-text-200.png`), fullPage: true });
    await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
  }
  await page.goto(origin); await hasHeading(homeHeading());
  await page.getByRole('link', { name: 'Přejít na obsah', exact: true }).focus();
  await page.keyboard.press('Enter');
  assert.equal(await page.locator(':focus').getAttribute('id'), 'main-content', 'the keyboard skip link reaches the customer content');

  // Synthetic owner UI evidence only. This does not authenticate against a
  // backend, create a passkey or permit any management mutation.
  const ownerRequests = [];
  const owner = { id: 'synthetic-owner', displayName: 'SYNTETICKÝ VLASTNÍK · UI FIXTURE', roles: ['Owner'], openAiConsent: true };
  let ownerSessionActive = true;
  await context.route('**/app/v1/**', async route => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    ownerRequests.push({ path: pathname, method: request.method() });
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (request.method() !== 'GET') return json({ code: 'synthetic_fixture_read_only' }, 405);
    if (pathname === '/app/v1/session') return json({ enabled: true, account: ownerSessionActive ? owner : null, csrfToken: 'synthetic-owner-csrf' });
    if (pathname === '/app/v1/launch') return json({ allowed: true, audience: 'privileged', launchAt: null, serverNow: Date.now(), teaser: '', initialLoadingMessage: '', personalizedLoadingEnabled: false });
    if (pathname === '/app/v1/account') return json({
      devices: [], associations: [], grants: [], passkeyCount: 1,
      passkeys: [{ id: 'synthetic-key', name: 'Syntetický přístupový klíč', createdAt: '2026-10-20T00:00:00Z', isBackedUp: true }],
      sessions: [{ id: 'synthetic-session', installationName: 'Syntetický prohlížeč', current: true, trusted: false, expiresAt: 1900000000 }],
    });
    if (pathname === '/app/v1/archive/associations') return json({ items: [] });
    if (pathname === '/app/v1/admin/users') return json({ users: [{ ...owner, disabled: false }], grants: [] });
    if (pathname === '/app/v1/admin/model-releases') return json({ enabled: false, active: null, releases: [], invalidReleases: 0, truncated: false, integrity: 'synthetic-fixture' });
    if (pathname === '/app/v1/admin/operations/status') return json({
      modes: { revision: 'synthetic-fixture', activeConversations: 0, personalityId: 'fixture', personalities: [{ id: 'fixture', name: 'Syntetická osobnost' }], characterEnabled: false, characterTone: 'Reserved', characterFinaleProfile: 'Blocked', characterVersion: 'synthetic-fixture' },
      readiness: { accounts: true, keepsakes: false, keepsakeGeneration: false, passkeyRelyingParty: 'synthetic-fixture', nativeAssociationConfigured: false },
      archive: [],
    });
    return route.fallback();
  });
  for (const theme of ['v2']) {
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
      await page.goto(origin + '/settings');
      await page.getByLabel('Název přístupového klíče 1', { exact: true }).waitFor();
      await fits();
      await page.screenshot({ path: path.join(screenshots, `${theme}-synthetic-owner-settings-${width}.png`), fullPage: true });
      await page.goto(origin + '/settings/admin');
      const resource = page.getByLabel('Identifikátor prostředku', { exact: true });
      await resource.fill('synthetic-unsaved-grant');
      const original = await resource.elementHandle();
      assert.equal(await resource.inputValue(), 'synthetic-unsaved-grant', 'inspection preserves an unsaved management draft');
      assert(await resource.evaluate((element, previous) => element === previous, original), 'inspection retains the management control');
      await fits();
      await page.screenshot({ path: path.join(screenshots, `${theme}-synthetic-owner-admin-${width}.png`), fullPage: true });
    }
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(origin + '/settings');
  await page.getByLabel('Název přístupového klíče 1', { exact: true }).waitFor();
  await page.evaluate(() => {
    document.documentElement.style.setProperty('--safe-area-inset-top', '24px');
    document.documentElement.style.setProperty('--safe-area-inset-bottom', '32px');
  });
  assert(await page.locator('.application-header').evaluate(element => parseFloat(getComputedStyle(element).paddingTop) >= 24), 'Android SystemBars top inset protects the header');
  const mobileNavigation = page.locator('.primary-navigation');
  assert(await mobileNavigation.evaluate(element => element.getBoundingClientRect().bottom <= innerHeight - 32), 'Android SystemBars bottom inset lifts the floating navigation');
  await mobileNavigation.getByRole('link', { name: 'Domů', exact: true }).click();
  await hasHeading('Moje postava');
  assert.equal(await page.locator('.primary-navigation, .owner-player-controls').count(), 0, 'owner home uses the customer shell without an administration banner');
  const customerBottomNavigation = page.getByRole('navigation', { name: 'Hlavní navigace', exact: true });
  assert(await customerBottomNavigation.getByRole('link').evaluateAll(links => links.every(link => link.getBoundingClientRect().bottom <= innerHeight - 32)), 'customer navigation controls remain above the bottom safe area');
  await fits();
  await page.screenshot({ path: path.join(screenshots, 'android-system-insets-390.png'), fullPage: true });
  await page.evaluate(() => {
    document.documentElement.style.removeProperty('--safe-area-inset-top');
    document.documentElement.style.removeProperty('--safe-area-inset-bottom');
  });
  assert(ownerRequests.every(request => request.method === 'GET'), 'UI checks never send a management mutation');
  await page.goto(origin + '/me/account');
  await hasHeading('Můj účet');
  sessionAccount = null;
  ownerSessionActive = false;
  await page.evaluate(() => window.dispatchEvent(new Event('online')));
  await page.waitForURL(url => url.pathname === '/signin');
  assert.equal(await page.getByRole('heading', { name: 'Můj účet' }).count(), 0, 'logout removes private account content');
  await page.goBack();
  assert.notEqual(new URL(page.url()).pathname, '/me/account', 'history cannot reopen the logged-out account');
  assert.deepEqual(errors, [], 'no uncaught application errors');
  console.log(`PASS compiled application: route views and compatibility redirects, synthetic owner settings/admin, management draft preservation with no writes, iPhone links, navigation, deep links, claim capture, consistent onboarding, focus, fixed design, keyboard, 1440/390/320 layouts and 200% text. Screenshots: ${screenshots}`);
  await context.close();
} catch (error) {
  console.error('Application errors:', errors);
  if (page) {
    console.error('Application state on failure:', await page.locator('main').innerText({ timeout: 1000 }).catch(() => 'main unavailable'));
    console.error('Application layout on failure:', await page.evaluate(() => ({
      viewport: innerWidth, width: document.documentElement.scrollWidth,
      overflow: [...document.querySelectorAll('body *')].map(element => ({
        tag: element.tagName, classes: typeof element.className === 'string' ? element.className : '',
        right: element.getBoundingClientRect().right,
      })).filter(element => element.right > innerWidth + 1).slice(0, 12),
    })).catch(() => 'layout unavailable'));
    await page.screenshot({ path: path.join(screenshots, 'failure.png'), fullPage: true, timeout: 3000 }).catch(() => {});
  }
  throw error;
} finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
