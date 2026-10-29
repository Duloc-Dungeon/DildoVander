// Real guest UI + isolated HTTPS backend + Chrome's synthetic virtual passkey.
// The backend fixture owns all state; this file only observes the rendered app
// and makes one selected response disappear after the server has handled it.
import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import { chromium } from 'playwright';

const origin = new URL(process.env.ACCOUNT_BROWSER_URL ?? '');
if (origin.protocol !== 'https:' || origin.hostname !== 'localhost' || origin.pathname !== '/' || origin.search || origin.hash)
  throw new Error('Guest journey requires the isolated localhost HTTPS fixture.');
const delivery = await readFile(process.env.ACCOUNT_BOOTSTRAP_FILE, 'utf8');
const invitation = new URL(delivery.split(/\r?\n/)[0]);
if (invitation.origin !== origin.origin || invitation.pathname !== '/signin' || !/^#invite=[A-Za-z0-9_-]{32,256}$/.test(invitation.hash))
  throw new Error('The fixture invitation delivery does not match its origin.');
const screenshotDir = process.env.APP_SCREENSHOTS;
if (!screenshotDir) throw new Error('APP_SCREENSHOTS must point to the task evidence directory.');
await mkdir(screenshotDir, { recursive: true });

let browser;
const errors = [];
try {
  try {
    browser = await chromium.launch({
      channel: process.env.PLAYWRIGHT_CHANNEL,
      ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH } : {}),
    });
  } catch (error) {
    if (process.platform !== 'win32' || process.env.PLAYWRIGHT_CHANNEL) throw error;
    browser = await chromium.launch({ channel: 'chrome' });
  }

  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 390, height: 844 } });
  try {
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    page.on('pageerror', error => errors.push(error.message));
    await context.route('**/*', route => new URL(route.request().url()).origin === origin.origin ? route.continue() : route.abort());
    const cdp = await context.newCDPSession(page);
    await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', { options: {
      protocol: 'ctap2', transport: 'internal', hasResidentKey: true,
      hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true,
    } });

    // A real browser cancellation should leave the invitation and user-entered
    // details intact. Reject only the first passkey creation ceremony.
    await page.addInitScript(() => {
      const create = navigator.credentials.create.bind(navigator.credentials);
      let rejectOnce = true;
      Object.defineProperty(navigator.credentials, 'create', {
        configurable: true,
        value(options) {
          if (rejectOnce) {
            rejectOnce = false;
            return Promise.reject(new DOMException('The user cancelled the passkey prompt.', 'NotAllowedError'));
          }
          return create(options);
        },
      });
    });

    await page.goto(invitation.href);
    await page.getByLabel('Jméno postavy', { exact: true }).fill('Testovací host');
    await page.getByLabel('Univerzum', { exact: true }).fill('Svět nočního stolku');
    const consent = page.getByRole('checkbox', { name: /Souhlasím se zpracováním údajů postavy/ });
    await consent.check();
    const accept = page.getByRole('button', { name: /Přijmout pozvánku|vytvořit klíč/i });
    await accept.click();
    await page.getByRole('alert').waitFor();
    assert.equal(await page.getByLabel('Jméno postavy', { exact: true }).inputValue(), 'Testovací host');
    assert.equal(await consent.isChecked(), true, 'cancelling passkey creation keeps the draft and consent');
    assert.match(await page.locator('[role="alert"]').innerText(), /zrušen|nepodařilo|klíč/i);

    // Let the genuine account registration succeed, while dropping only the
    // response to its automatic character save after the backend commits it.
    let droppedSaveCount = 0;
    await context.route(origin.origin + '/app/v1/prelaunch/character', async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      droppedSaveCount++;
      const response = await route.fetch();
      assert(response.ok(), 'server accepted the actual character save');
      await route.abort('failed');
    });
    const registrationResponse = page.waitForResponse(response =>
      response.url() === origin.origin + '/app/v1/enrollment/complete' && response.request().method() === 'POST');
    await accept.click();
    const registered = await registrationResponse;
    assert(registered.ok(), 'the real server accepted guest registration');
    await page.waitForURL(url => url.pathname === '/launch');
    await page.getByText('Testovací host', { exact: true }).waitFor();
    await page.getByRole('heading', { name: /odpočet|čekání|Testovací host/i }).waitFor();
    const launchResponse = await context.request.get(origin.origin + '/app/v1/launch');
    assert.equal(launchResponse.status(), 200);
    const launchPolicy = await launchResponse.json();
    assert.equal(launchPolicy.allowed, false, 'the backend has not opened this guest group');
    assert(launchPolicy.launchAt > launchPolicy.serverNow, 'the real waiting-room countdown is in the future');
    await page.locator('.countdown').waitFor();
    assert.equal(droppedSaveCount, 1, 'automatic registration attempted exactly one character creation');

    const sessionResponse = await context.request.get(origin.origin + '/app/v1/session');
    assert.equal(sessionResponse.status(), 200);
    const session = await sessionResponse.json();
    assert(session.account, 'the real server has an authenticated guest account');
    assert.equal(session.account.openAiConsent, true, 'explicit consent reached the server');
    assert(session.account.roles.some(role => /Customer|Guest/i.test(role)), 'the session is a guest/customer account');

    const characterResponse = await context.request.get(origin.origin + '/app/v1/prelaunch/character');
    assert.equal(characterResponse.status(), 200);
    const characterState = await characterResponse.json();
    assert.equal(characterState.character.name, 'Testovací host');
    assert.equal(characterState.character.universe, 'Svět nočního stolku');
    const profileId = characterState.character.profileId;
    assert(profileId, 'the server persisted a character profile');
    const savesBeforeReload = droppedSaveCount;
    await page.reload();
    await page.waitForURL(url => url.pathname === '/launch');
    await page.getByText('Testovací host', { exact: true }).waitFor();
    const afterReload = await (await context.request.get(origin.origin + '/app/v1/prelaunch/character')).json();
    assert.equal(afterReload.character.profileId, profileId, 'reload reconciles the character already committed by the backend');
    assert.equal(droppedSaveCount, savesBeforeReload, 'reload does not issue a duplicate create');
    await page.waitForFunction(() => {
      const image = document.querySelector('.portrait-stage-image');
      return image instanceof HTMLImageElement && image.complete && image.naturalWidth > 0;
    });
    await page.screenshot({ path: `${screenshotDir}/guest-launch-mobile.png`, fullPage: true });
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'guest waiting room fits the mobile viewport');

    // Use the same authenticated HTTPS backend for the whole preparation.
    // Only the fixture's generation providers are offline doubles.
    await page.getByRole('button', { name: 'Vytvořit hůlku', exact: true }).click();
    await page.getByRole('button', { name: 'Raději psát', exact: true }).click();
    const answer = 'Hůlka má najít cestu domů i v mlze.';
    await page.getByLabel('Tvoje sdělení kufru', { exact: true }).fill(answer);
    const savedAnswer = page.waitForResponse(response =>
      /\/workshop\/sessions\/[^/]+\/messages$/.test(new URL(response.url()).pathname)
      && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Odeslat', exact: true }).click();
    const answerResponse = await savedAnswer;
    assert.equal(answerResponse.status(), 200);
    const asking = (await answerResponse.json()).session;
    assert(asking.messages.some(message => message.role === 'user' && message.text === answer));

    await page.reload();
    await page.getByRole('button', { name: 'Pokračovat v tvorbě hůlky', exact: true }).click();
    await page.getByRole('button', { name: 'Raději psát', exact: true }).click();
    const resumed = await (await context.request.get(origin.origin + '/app/v1/customer/wand/workshop')).json();
    assert.equal(resumed.session.id, asking.id, 'reload resumes the same durable workshop');
    assert.equal(resumed.session.messages.filter(message => message.role === 'user' && message.text === answer).length, 1);
    const finish = page.waitForResponse(response => response.url().endsWith(`/workshop/sessions/${asking.id}/finish`)
      && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'To stačí, vyrob hůlku', exact: true }).click();
    assert.equal((await finish).status(), 200);
    await page.getByRole('button', { name: 'Počkat na začátek večera', exact: true }).waitFor({ timeout: 60000 });
    const completed = await (await context.request.get(origin.origin + '/app/v1/customer/wand/workshop')).json();
    assert.equal(completed.session.id, asking.id);
    assert.equal(completed.session.status, 'complete');
    const wandId = completed.session.wand.wandId;
    assert(wandId, 'manufacture persisted the selected wand');
    const ownedWand = await (await context.request.get(origin.origin + '/app/v1/customer/wand')).json();
    assert.equal(ownedWand.wand.wandId, wandId, 'the game uses the wand created in prelaunch');
    await page.locator('.trunk-stage__wand').waitFor({ timeout: 60000 });
    await page.waitForFunction(() => {
      const image = document.querySelector('.trunk-stage__wand');
      return image instanceof HTMLImageElement && image.complete && image.naturalWidth > 0;
    });
    await page.screenshot({ path: `${screenshotDir}/guest-wand-complete-mobile.png`, fullPage: true });
    await page.getByRole('button', { name: 'Počkat na začátek večera', exact: true }).click();
    await page.reload();
    await page.getByRole('button', { name: 'Prohlédnout hůlku', exact: true }).waitFor();
    assert.equal(await page.locator('[aria-current="step"]').innerText(), 'Čekárna');

    await page.goto(origin.origin + '/me/account');
    await page.getByRole('button', { name: 'Odhlásit toto zařízení', exact: true }).click();
    await page.waitForURL(url => url.pathname === '/signin');
    const signInOptions = page.waitForResponse(response =>
      response.url() === origin.origin + '/app/v1/passkeys/request-options' && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Přihlásit přístupovým klíčem', exact: true }).click();
    assert.equal((await signInOptions).status(), 200);
    await page.waitForURL(url => ['/launch', '/me/character', '/me/account', '/settings'].includes(url.pathname));
    const signedInSession = await (await context.request.get(origin.origin + '/app/v1/session')).json();
    assert.equal(signedInSession.account.id, session.account.id, 'same virtual authenticator signs into the same guest account');
    assert.equal(signedInSession.account.openAiConsent, true);
    const signedInCharacter = await (await context.request.get(origin.origin + '/app/v1/prelaunch/character')).json();
    assert.equal(signedInCharacter.character.profileId, profileId, 'the character remains attached after passkey sign-in');
    const signedInWand = await (await context.request.get(origin.origin + '/app/v1/customer/wand')).json();
    assert.equal(signedInWand.wand.wandId, wandId, 'the same manufactured wand survives logout and passkey sign-in');

    // A consumed invitation and a syntactically valid but unknown invitation
    // both receive the same user-facing rejection; neither exposes token data.
    const anonymous = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 390, height: 844 } });
    try {
      const anonymousPage = await anonymous.newPage();
      anonymousPage.setDefaultTimeout(15000);
      await anonymous.route('**/*', route => new URL(route.request().url()).origin === origin.origin ? route.continue() : route.abort());
      const anonymousCdp = await anonymous.newCDPSession(anonymousPage);
      await anonymousCdp.send('WebAuthn.enable');
      await anonymousCdp.send('WebAuthn.addVirtualAuthenticator', { options: {
        protocol: 'ctap2', transport: 'internal', hasResidentKey: true,
        hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true,
      } });
      async function rejectInvitation(link) {
        await anonymousPage.goto(link);
        await anonymousPage.getByLabel('Jméno postavy', { exact: true }).fill('Opakovaný pokus');
        await anonymousPage.getByLabel('Univerzum', { exact: true }).fill('Svět nočního stolku');
        await anonymousPage.getByRole('checkbox', { name: /Souhlasím se zpracováním údajů postavy/ }).check();
        await anonymousPage.getByRole('button', { name: /Přijmout pozvánku|vytvořit klíč/i }).click();
        const alert = anonymousPage.locator('[role="alert"]');
        await alert.waitFor();
        return alert.innerText();
      }
      const usedMessage = await rejectInvitation(invitation.href);
      const invalidMessage = await rejectInvitation(`${origin.origin}/signin#invite=${'A'.repeat(48)}`);
      assert.equal(invalidMessage, usedMessage, 'used and invalid invitations share the generic rejection');
      assert(!invalidMessage.includes('A'.repeat(20)), 'rejection does not echo invitation material');
    } finally {
      await anonymous.close();
    }

    assert.deepEqual(errors, [], `the guest journey produced no uncaught page errors: ${errors.join('; ')}`);
    process.stdout.write('Guest journey browser evidence passed: consent, cancelled passkey recovery, server-confirmed character save, workshop text persistence and resume, wand manufacture, waiting room reload, passkey sign-in with the same wand, generic invite rejection, and mobile viewport. Offline providers only.\n');
  } finally {
    await context.close();
  }
} finally {
  if (browser) await browser.close();
}
