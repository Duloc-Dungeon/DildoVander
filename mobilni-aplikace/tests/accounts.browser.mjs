// Real UI + real HTTPS backend fixture + Chrome's virtual authenticator.
// The fixture clears inherited configuration and registers no AI providers.
import assert from 'node:assert/strict';
import { readFile, mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const origin = new URL(process.env.ACCOUNT_BROWSER_URL ?? '');
if (origin.protocol !== 'https:' || origin.hostname !== 'localhost' || origin.pathname !== '/' || origin.search || origin.hash)
  throw new Error('Accounts browser suite requires the isolated localhost HTTPS fixture.');
const delivery = await readFile(process.env.ACCOUNT_BOOTSTRAP_FILE, 'utf8');
const bootstrap = new URL(delivery.split(/\r?\n/)[0]);
if (bootstrap.origin !== origin.origin || bootstrap.pathname !== '/signin' || !/^#invite=[A-Za-z0-9_-]{32,256}$/.test(bootstrap.hash))
  throw new Error('The fixture bootstrap delivery does not match its origin.');
let browser;
const errors = [];
try {
  try { browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL,
    ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? {executablePath:process.env.PLAYWRIGHT_EXECUTABLE_PATH} : {}) }); }
  catch (error) { if (process.platform !== 'win32' || process.env.PLAYWRIGHT_CHANNEL) throw error; browser = await chromium.launch({ channel: 'chrome' }); }
  async function participant() {
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const page = await context.newPage(); page.setDefaultTimeout(15000);
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', dialog => dialog.accept());
    await context.route('**/*', route => new URL(route.request().url()).origin === origin.origin ? route.continue() : route.abort());
    const cdp = await context.newCDPSession(page);
    await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', { options: {
      protocol: 'ctap2', transport: 'internal', hasResidentKey: true,
      hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true,
    } });
    return { context, page };
  }
  async function enroll(page, link, name, expectedName = name) {
    await page.goto(link);
    await page.getByLabel('Jméno postavy', { exact: true }).fill(name);
    await page.getByLabel('Univerzum', { exact: true }).fill('Izolovaný testovací svět');
    assert.equal(new URL(page.url()).hash, '', 'enrollment fragment removed before application requests');
    const accept = page.getByRole('button', { name: 'Přijmout pozvánku', exact: true });
    assert(await accept.isDisabled(), 'registration waits for an explicit processing choice');
    const anonymousSession = await (await page.request.get(origin.origin + '/app/v1/session')).json();
    const denied = await page.request.post(origin.origin + '/app/v1/enrollment/options', {
      headers: { Origin: origin.origin, 'X-DV-CSRF': anonymousSession.csrfToken },
      data: { token: new URL(link).hash.slice('#invite='.length), displayName: name },
    });
    assert.equal(denied.status(), 409, 'the real server also denies registration without processing consent');
    assert.equal((await denied.json()).code, 'ai_consent_required');
    await page.getByRole('checkbox', { name: /Souhlasím.*OpenAI/ }).check();
    await accept.click();
    await page.waitForURL(url => url.pathname === '/launch');
    await page.goto(origin.origin + '/settings');
    await page.getByRole('heading', { name: expectedName, exact: true }).waitFor();
  }
  const owner = await participant();
  await enroll(owner.page, bootstrap.href, 'Izolovaný vlastník', 'Olivander');
  const ownerSession = await owner.context.request.get(origin.origin + '/app/v1/session');
  assert.equal(ownerSession.status(), 200);
  const ownerAccount = (await ownerSession.json()).account;
  assert(ownerAccount.roles.includes('Owner'));
  assert.equal(ownerAccount.displayName, 'Olivander', 'Owner keeps its fixed name despite a different registration input');
  await owner.page.goto(origin.origin + '/settings/admin');
  await owner.page.locator('.invitation-group summary').click();
  await owner.page.getByLabel('Název skupiny', { exact: true }).fill('Izolovaná skupina');
  const createdGroupResponse = owner.page.waitForResponse(response =>
    response.url() === origin.origin + '/app/v1/admin/launch/groups' && response.request().method() === 'POST');
  await owner.page.getByRole('button', { name: 'Vytvořit skupinu', exact: true }).click();
  const createdGroup = await createdGroupResponse;
  assert.equal(createdGroup.status(), 200);
  const { group: invitationGroup } = await createdGroup.json();
  assert.equal(invitationGroup.launchAt, null, 'new groups wait for an opening configured in evening management');
  assert.equal(Object.hasOwn(invitationGroup, 'countdownDurationSeconds'), false, 'the removed countdown draft is absent');
  await owner.page.getByText('Skupina je uložená. Termín spuštění náborové skupiny nastavíte ve správě večera. Již otevřené skupiny zůstávají přístupné.').waitFor();
  assert.equal(await owner.page.locator('#recovery-account').count(), 0, 'account recovery cannot be selected');
  assert.equal(await owner.page.getByRole('button', { name: 'Vytvořit kód obnovy', exact: true }).count(), 0);
  await owner.page.goto(origin.origin + '/settings/admin/launch');
  await owner.page.getByLabel('Skupina pro nově připojené hosty', { exact: true }).selectOption(invitationGroup.id);
  // This identity journey needs an already open group; scheduling itself has a separate suite.
  const pastOpening = new Date(Date.now() - 86400000).toISOString().slice(0, 16);
  await owner.page.getByLabel('Kdy se spustí aplikace pro tuto skupinu', { exact: true }).fill(pastOpening);
  await owner.page.getByLabel('Přijímat nové účastníky', { exact: true }).check();
  const savedOpeningResponse = owner.page.waitForResponse(response =>
    response.url() === origin.origin + '/app/v1/admin/launch/beta-admission' && response.request().method() === 'PUT');
  await owner.page.getByRole('button', { name: 'Uložit skupinu, spuštění a nábor', exact: true }).click();
  assert.equal((await savedOpeningResponse).status(), 200);
  await owner.page.goto(origin.origin + '/settings/admin');
  await owner.page.getByLabel('Skupina pozvánky', { exact: true }).selectOption(invitationGroup.id);
  // A newer app talking to an older backend can receive invalid_role. Exercise
  // the real rejection and in-app diagnostic without inventing a successful invite.
  await owner.page.getByLabel('Role nového účtu', { exact: true }).evaluate(select => select.add(new Option('Unsupported role', 'UnsupportedRole')));
  await owner.page.getByLabel('Role nového účtu', { exact: true }).selectOption('UnsupportedRole');
  const rejectedInvite = owner.page.waitForResponse(response => response.url() === origin.origin + '/app/v1/admin/invitations' && response.request().method() === 'POST');
  await owner.page.getByRole('button', { name: 'Vytvořit pozvánku', exact: true }).click();
  assert.equal((await rejectedInvite).status(), 400);
  await owner.page.getByText('HTTP 400 · invalid_role', { exact: true }).waitFor();
  await owner.page.getByText('Server nepodporuje zvolenou roli.', { exact: false }).waitFor();
  assert.equal(await owner.page.getByLabel('Soukromý odkaz pro přijetí', { exact: true }).count(), 0);
  await owner.page.getByLabel('Role nového účtu', { exact: true }).selectOption('Viewer');
  await owner.page.getByRole('button', { name: 'Vytvořit pozvánku', exact: true }).click();
  if (await owner.page.locator('dialog[open]').count()) await owner.page.locator('dialog[open]').getByRole('button', { name: /Potvrdit|Vytvořit/, exact: false }).click();
  await owner.page.getByLabel('Soukromý odkaz pro přijetí', { exact: true }).waitFor();
  const invitation = await owner.page.getByLabel('Soukromý odkaz pro přijetí', { exact: true }).inputValue();
  const viewer = await participant();
  await enroll(viewer.page, invitation, 'Izolovaný návštěvník');
  await viewer.page.getByLabel('Název přístupového klíče 1', { exact: true }).fill('Testovací Chrome');
  await viewer.page.getByRole('button', { name: 'Uložit název klíče', exact: true }).click();
  await viewer.page.getByText('Název přístupového klíče byl uložen.', { exact: true }).waitFor();
  assert.equal((await (await viewer.context.request.get(origin.origin + '/app/v1/account')).json()).passkeys[0].name, 'Testovací Chrome');
  assert.equal((await viewer.context.request.get(origin.origin + '/app/v1/admin/users')).status(), 403);
  await viewer.page.goto(origin.origin + '/settings/admin');
  await viewer.page.waitForURL(url => url.pathname === '/settings');
  assert.equal(await viewer.page.getByLabel('Role nového účtu', { exact: true }).count(), 0, 'Viewer cannot mount admin controls');
  await viewer.page.goto(origin.origin + '/suitcase');
  await viewer.page.waitForURL(url => url.pathname === '/play');
  await viewer.page.goto(origin.origin + '/admin/customer-sessions');
  await viewer.page.waitForURL(url => url.pathname === '/settings');
  await viewer.page.goto(origin.origin + '/settings');
  // Hold the actual expiry Set-Cookie response. A JS-only stale-response fence
  // cannot protect a new cookie if logout and a fresh login run concurrently.
  let releaseLogout;
  let logoutReady;
  let holdLogout = true;
  const logoutHeld = new Promise(resolveHeld => { logoutReady = resolveHeld; });
  const logoutRelease = new Promise(resolveRelease => { releaseLogout = resolveRelease; });
  let signInOptions = 0;
  viewer.page.on('request', request => {
    if (new URL(request.url()).pathname === '/app/v1/passkeys/request-options') signInOptions++;
  });
  await viewer.context.route(origin.origin + '/app/v1/session', async route => {
    if (route.request().method() !== 'DELETE' || !holdLogout) return route.continue();
    holdLogout = false;
    const response = await route.fetch(); logoutReady();
    await logoutRelease; await route.fulfill({ response });
  });
  await viewer.page.getByRole('button', { name: 'Odhlásit toto zařízení', exact: true }).click();
  let logoutDeadline;
  try {
    await Promise.race([logoutHeld, new Promise((_, reject) => {
      logoutDeadline = setTimeout(() => reject(new Error('Current-session logout did not use the serialized logout route.')), 15000);
    })]);
  } finally { clearTimeout(logoutDeadline); }
  await viewer.page.waitForURL(url => url.pathname === '/signin');
  const signIn = viewer.page.getByRole('button', { name: 'Přihlásit přístupovým klíčem', exact: true });
  await signIn.waitFor(); assert(await signIn.isDisabled());
  assert.equal(signInOptions, 0, 'no new cookie ceremony while logout response is pending');
  releaseLogout();
  await signIn.waitFor({ state: 'visible' });
  await signIn.click(); await viewer.page.waitForURL(url => url.pathname === '/settings');
  assert.equal((await (await viewer.context.request.get(origin.origin + '/app/v1/session')).json()).account.displayName, 'Izolovaný návštěvník');
  const logoutCompleted = viewer.page.waitForResponse(response => response.url() === origin.origin + '/app/v1/session' && response.request().method() === 'DELETE');
  await viewer.page.getByRole('button', { name: 'Odhlásit toto zařízení', exact: true }).click();
  await logoutCompleted;
  await viewer.page.waitForURL(url => url.pathname === '/signin');
  await signIn.waitFor({ state: 'visible' });
  const signedOut = await viewer.context.request.get(origin.origin + '/app/v1/session');
  assert.equal((await signedOut.json()).account, null);
  await viewer.page.goto(origin.origin + '/signin?return=%2Farchive');
  await viewer.page.getByRole('button', { name: 'Přihlásit přístupovým klíčem', exact: true }).click();
  await viewer.page.waitForURL(url => url.pathname === '/me/keepsakes');
  assert.equal((await (await viewer.context.request.get(origin.origin + '/app/v1/session')).json()).account.displayName, 'Izolovaný návštěvník');
  const replay = await participant();
  await replay.page.goto(invitation);
  await replay.page.getByLabel('Jméno postavy', { exact: true }).fill('Nepovolené opakování');
  await replay.page.getByLabel('Univerzum', { exact: true }).fill('Izolovaný testovací svět');
  await replay.page.getByRole('checkbox', { name: /Souhlasím.*OpenAI/ }).check();
  await replay.page.getByRole('button', { name: 'Přijmout pozvánku', exact: true }).click();
  await replay.page.getByRole('alert').filter({ hasText: /Pozvánka už neplatí/ }).waitFor();
  assert.equal((await (await replay.context.request.get(origin.origin + '/app/v1/session')).json()).account, null);

  await viewer.page.goto(origin.origin + '/settings');
  await viewer.page.getByRole('button', { name: 'Ověřit účet pro smazání', exact: true }).click();
  const deleteButton = viewer.page.getByRole('button', { name: 'Trvale smazat můj účet', exact: true });
  await deleteButton.waitFor();
  const screenshots = process.env.ACCOUNT_BROWSER_SCREENSHOTS;
  if (screenshots) {
    await mkdir(screenshots, { recursive: true });
    await viewer.page.setViewportSize({ width: 390, height: 844 });
    await viewer.page.screenshot({ path: `${screenshots}/account-deletion-confirm-390.png`, fullPage: true });
    assert(await viewer.page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'deletion confirmation fits mobile viewport');
  }
  const deletionResponse = viewer.page.waitForResponse(response => response.request().method() === 'DELETE' && new URL(response.url()).pathname === '/app/v1/account');
  await deleteButton.click();
  assert.equal((await deletionResponse).status(), 202);
  await viewer.page.waitForURL(url => url.pathname === '/account-deletion' && !url.search && !url.hash);
  await viewer.page.getByRole('heading', { name: 'Stav smazání účtu', exact: true }).waitFor();
  const completed = viewer.page.getByText('Smazání účtu a jeho soukromých dat v aplikaci bylo dokončeno.', { exact: true });
  await completed.waitFor({ timeout: 45000 });
  assert.equal((await (await viewer.context.request.get(origin.origin + '/app/v1/session')).json()).account, null);
  assert.equal((await viewer.context.request.get(origin.origin + '/app/v1/account')).status(), 401);
  assert.equal((await owner.context.request.get(origin.origin + '/app/v1/account')).status(), 200, 'deleting the Viewer preserves the Owner');
  await viewer.page.bringToFront();
  await viewer.page.reload();
  await completed.waitFor();
  if (screenshots) {
    for (const width of [390, 1440]) {
      await viewer.page.setViewportSize({ width, height: 900 });
      assert(await viewer.page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'anonymous deletion receipt fits viewport');
      await viewer.page.screenshot({ path: `${screenshots}/account-deletion-completed-${width}.png`, fullPage: true });
    }
  }
  assert.deepEqual(errors, []);
  console.log('PASS real HTTPS accounts: controlled owner bootstrap, browser passkeys and naming, single-use invitation, Viewer denial, logout and passkey sign-in, verified deletion and anonymous completion receipt after reload, other account preservation. No provider adapters or production state.');
} finally { await browser?.close(); }
