// Offline owner workflow regression. Passkey transport is a labelled fixture;
// this verifies browser recovery and mutations, not authenticator cryptography.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const root = fileURLToPath(new URL('../dist/', import.meta.url));
const output = process.env.APP_SCREENSHOTS ?? fileURLToPath(new URL('../../.codex/artifacts/owner-flows/', import.meta.url));
await mkdir(output, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.webp': 'image/webp', '.svg': 'image/svg+xml' };
const server = createServer(async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  if (/^\/(app\/v1|v1|guest)\//.test(url.pathname)) {
    response.writeHead(404, { 'Content-Type': 'application/json' }).end('{}');
    return;
  }
  let file = path.resolve(root, decodeURIComponent(url.pathname).replace(/^\//, '') || 'index.html');
  if (!file.startsWith(path.resolve(root) + path.sep)) file = path.join(root, 'index.html');
  try { if (!(await stat(file)).isFile()) file = path.join(root, 'index.html'); }
  catch { file = path.join(root, 'index.html'); }
  response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' }).end(await readFile(file));
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const errors = [];
const screenshots = [];
let browser;

async function capture(page, name) {
  await page.evaluate(() => document.fonts.ready);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${name}: horizontal overflow`);
  await page.screenshot({ path: path.join(output, `${name}.png`), fullPage: true });
  screenshots.push(`${name}.png`);
}

try {
  browser = await chromium.launch({
    ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH } : {}),
    ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}),
  });
  for (const [width, height] of [[320, 568], [390, 844], [1280, 900]]) {
    const context = await browser.newContext({ viewport: { width, height }, isMobile: width < 500, hasTouch: width < 500, reducedMotion: 'reduce' });
    let recent = false;
    let group = null;
    let groupAttempts = 0;
    let countdownAttempts = 0;
    let invitationWrites = 0;
    let reauthentications = 0;
    let ownerGroupId = 'owner-friday';
    let ownerAccessRevision = 1;
    await context.addInitScript(() => {
      window.confirm = () => true;
      localStorage.setItem('dildovander.theme', 'olivander');
      Object.defineProperty(navigator.credentials, 'get', { value: async () => ({
        id: 'fixture-credential', rawId: new Uint8Array([1, 2, 3]).buffer, type: 'public-key',
        getClientExtensionResults: () => ({}),
        response: { clientDataJSON: new ArrayBuffer(1), authenticatorData: new ArrayBuffer(1), signature: new ArrayBuffer(1), userHandle: null },
      }) });
    });
    await context.route('**/*', async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.origin !== origin) return route.abort();
      const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
      const rejected = () => json({ code: 'recent_passkey_required' }, 403);
      if (url.pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'fixture-owner', displayName: 'Olivander', roles: ['Owner'] }, csrfToken: 'fixture-only', origin });
      if (url.pathname === '/app/v1/launch') return json({ allowed: false });
      if (url.pathname === '/app/v1/customer/groups/active') {
        const input = request.postDataJSON();
        assert.equal(input.expectedRevision, ownerAccessRevision);
        assert.equal(input.groupId, 'owner-saturday');
        ownerGroupId = input.groupId;
        ownerAccessRevision++;
        return json({ groups: [{ id: 'owner-friday', name: 'Pátek' }, { id: 'owner-saturday', name: 'Sobota' }], groupId: ownerGroupId, accessRevision: ownerAccessRevision });
      }
      if (url.pathname === '/app/v1/customer/groups') return json({ groups: [{ id: 'owner-friday', name: 'Pátek' }, { id: 'owner-saturday', name: 'Sobota' }], groupId: ownerGroupId, accessRevision: ownerAccessRevision });
      if (url.pathname === '/app/v1/customer/character') return json({ profile: null });
      if (url.pathname === '/app/v1/customer/sessions') return json({ items: [] });
      if (url.pathname === '/app/v1/customer/wand') return json({ hasCharacter: false, wand: null, tier: 0 });
      if (url.pathname === '/app/v1/game') return json({ opponents: [], incoming: [], active: [], history: [] });
      if (url.pathname === '/app/v1/passkeys/reauthentication-options') return json({ challenge: 'AQID', allowCredentials: [] });
      if (url.pathname === '/app/v1/passkeys/reauthenticate') { recent = true; reauthentications++; return json({}); }
      if (url.pathname === '/app/v1/admin/users') return json({ users: [], grants: [] });
      if (url.pathname === '/app/v1/admin/model-releases') return json({ enabled: false, active: null, releases: [], invalidReleases: 0, truncated: false, integrity: 'fixture' });
      if (url.pathname === '/app/v1/admin/operations/status') return json({ modes: null, readiness: { accounts: true, keepsakes: false, keepsakeGeneration: false, passkeyRelyingParty: 'localhost', nativeAssociationConfigured: false }, archive: [] });
      if (url.pathname === '/app/v1/admin/launch/groups') {
        if (request.method() === 'POST') {
          groupAttempts++;
          if (!recent) return rejected();
          assert.equal(request.postDataJSON().name, 'Páteční večer');
          assert.equal(request.postDataJSON().adultContentEnabled, width !== 390);
          assert.equal(request.postDataJSON().flirtEnabled, true);
          group = { id: 'fixture-group', name: 'Páteční večer', revision: 1, launchAt: null, countdownDurationSeconds: null, forceStartAtEnd: false, countdownConfirmedAt: null, openedAt: null,
            adultContentEnabled: request.postDataJSON().adultContentEnabled, flirtEnabled: request.postDataJSON().flirtEnabled };
          return json({ group });
        }
        return json({ groups: group ? [group] : [] });
      }
      if (url.pathname === '/app/v1/admin/invitations') {
        assert.equal(request.postDataJSON().launchGroupId, group.id);
        assert.equal(request.postDataJSON().role, 'Customer');
        invitationWrites++;
        return json({ token: 'fixture-invitation-token-1234567890', purpose: 'invite' });
      }
      if (url.pathname === '/app/v1/admin/launch') return json({ revision: 1, betaEnabled: false, betaLaunchAt: null, betaUnlocked: false, productionEnabled: false, productionLaunchAt: null, productionUnlocked: false, timeZone: 'Europe/Prague', teaser: '', initialLoadingMessage: '', personalizedLoadingEnabled: false, personalizedLoadingPrompt: '' });
      if (url.pathname === '/app/v1/admin/launch/artwork') return json({}, 503);
      if (url.pathname === '/app/v1/admin/launch/audit') return json({ entries: [] });
      if (url.pathname === '/app/v1/admin/launch/beta-admission') return json({ revision: 1, groupId: group?.id ?? null, groupName: group?.name ?? null, recruitmentOpen: false, fixedUniverse: null });
      if (url.pathname === '/app/v1/admin/launch/waiting-room') return json({ members: [] });
      if (url.pathname === '/app/v1/admin/launch/groups/fixture-group/countdown') {
        countdownAttempts++;
        if (!recent) return rejected();
        const input = request.postDataJSON();
        assert.equal(input.expectedRevision, group.revision);
        group = { ...group, countdownDurationSeconds: input.durationSeconds, forceStartAtEnd: input.forceStartAtEnd, revision: group.revision + 1 };
        return json({ group });
      }
      if (url.pathname === '/app/v1/admin/launch/groups/fixture-group/countdown/confirm') {
        assert.equal(request.postDataJSON().expectedRevision, group.revision);
        assert.equal(group.countdownDurationSeconds, 60);
        group = { ...group, launchAt: Date.now() + 60000, countdownConfirmedAt: Date.now(), revision: group.revision + 1 };
        return json({ group });
      }
      if (url.pathname === '/app/v1/admin/launch/groups/fixture-group/start') {
        assert.equal(request.postDataJSON().expectedRevision, group.revision);
        group = { ...group, openedAt: Date.now(), revision: group.revision + 1 };
        return json({ group });
      }
      if (/^\/(app\/v1|v1)\//.test(url.pathname)) return json({});
      return route.continue();
    });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    await page.clock.install();
    await page.goto(`${origin}/settings/admin#invitations`);
    await page.locator('#invitations').waitFor();
    await page.getByRole('combobox', { name: 'Role nového účtu' }).selectOption('Customer');
    await page.locator('.invitation-group summary').click();
    assert.equal(await page.getByRole('checkbox', { name: 'Dvojsmysly 18+', exact: true }).isChecked(), true);
    assert.equal(await page.getByRole('checkbox', { name: 'Flirt', exact: true }).isChecked(), true);
    if (width === 390) await page.getByRole('checkbox', { name: 'Dvojsmysly 18+', exact: true }).uncheck();
    await page.getByLabel('Název skupiny').fill('Páteční večer');
    await page.getByRole('button', { name: 'Vytvořit skupinu', exact: true }).click();
    await page.getByRole('button', { name: 'Ověřit totožnost přístupovým klíčem', exact: true }).click();
    await page.getByText('Totožnost byla ověřena. Skupinu nyní uložte znovu.').waitFor();
    assert.equal(groupAttempts, 1, 'reauthentication must not replay group creation');
    await page.getByRole('button', { name: 'Vytvořit skupinu', exact: true }).click();
    await page.getByText('Skupina je uložená.', { exact: false }).waitFor();
    assert.equal(await page.getByRole('checkbox', { name: 'Dvojsmysly 18+', exact: true }).isChecked(), width !== 390);
    assert.equal(await page.getByRole('checkbox', { name: 'Flirt', exact: true }).isChecked(), true);
    await page.getByRole('button', { name: 'Vytvořit pozvánku', exact: true }).click();
    await page.locator('#invitation-link').waitFor();
    assert.equal(invitationWrites, 1);
    await capture(page, `owner-invitation-${width}`);
    await page.getByRole('link', { name: 'Nastavit odpočet a otevření večera' }).click();
    await page.locator('#waiting-room').waitFor();
    await page.getByRole('region', { name: 'Připravenost večera', exact: true }).waitFor();
    assert.equal(await page.getByLabel('Časové pásmo', { exact: true }).isVisible(), false, 'global launch settings are secondary to group preparation');
    await page.getByText('Nastavení přístupu můžete dál upravovat.', { exact: false }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Potvrdit a spustit odpočet' }).isDisabled(), true);
    recent = false;
    await page.getByLabel('Délka odpočtu v minutách').fill('1');
    await page.getByRole('button', { name: 'Uložit návrh odpočtu' }).click();
    await page.getByRole('button', { name: 'Ověřit totožnost přístupovým klíčem', exact: true }).click();
    await page.getByText('Totožnost byla ověřena. Původní změnu nyní odešlete znovu.').waitFor();
    assert.equal(countdownAttempts, 1, 'reauthentication must not replay countdown mutation');
    assert.equal(await page.getByLabel('Délka odpočtu v minutách').inputValue(), '1');
    await page.getByRole('button', { name: 'Uložit návrh odpočtu' }).click();
    await page.getByRole('button', { name: 'Potvrdit a spustit odpočet' }).click();
    await page.getByText('Odpočet byl potvrzen a spuštěn.').waitFor();
    await page.clock.fastForward(120000);
    await page.getByRole('button', { name: 'Spustit noc', exact: true }).click();
    await page.getByText('Noc už je otevřená.').waitFor();
    assert.equal(reauthentications, 2);
    await capture(page, `owner-evening-${width}`);
    await page.getByRole('navigation', { name: 'Sekce aplikace', exact: true }).getByRole('link', { name: 'Účet', exact: true }).click();
    await page.waitForURL(`${origin}/me/account`);
    assert.equal(await page.getByRole('link', { name: 'Večer a pozvánky', exact: true }).getAttribute('href'), '/settings/admin/launch');
    await page.getByRole('navigation', { name: 'Hlavní navigace', exact: true }).getByRole('link', { name: 'Hry', exact: true }).click();
    await page.waitForURL(`${origin}/play`);
    const sound = page.locator('.sound-toggle');
    assert.equal(await sound.getAttribute('aria-pressed'), 'false');
    await sound.click();
    assert.equal(await sound.getAttribute('aria-pressed'), 'true');
    assert.equal(await sound.getAttribute('aria-label'), 'Zvuk zapnutý, vypnout');
    assert(await sound.evaluate(element => {
      const rect = element.getBoundingClientRect();
      return rect.width >= 44 && rect.height >= 44;
    }));
    await capture(page, `owner-sound-enabled-${width}`);
    await sound.click();
    assert.equal(await sound.getAttribute('aria-pressed'), 'false');
    await Promise.all([
      page.waitForResponse(response => response.url().endsWith('/app/v1/customer/groups/active') && response.request().method() === 'PUT'),
      page.getByRole('combobox', { name: /Skupina hráčů/ }).selectOption('owner-saturday'),
    ]);
    assert.equal(ownerGroupId, 'owner-saturday');
    await page.getByRole('link', { name: 'Moje postava', exact: false }).click();
    await page.waitForURL(`${origin}/me/character`);
    await page.locator('.customer-form').waitFor();
    assert.equal(await page.locator('details.character-extra').evaluate(element => element.open), false);
    assert.equal(await page.locator('.application-sidebar').count(), 0);
    assert.equal(await page.locator('.wns-game-navigation').count(), 1);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await capture(page, `owner-player-${width}`);
    await page.getByRole('navigation', { name: 'Hlavní navigace', exact: true }).getByRole('link', { name: 'Já', exact: true }).click();
    await page.getByRole('link', { name: 'Účty a oprávnění', exact: true }).click();
    await page.waitForURL(`${origin}/settings/admin`);
    await context.close();
  }
  assert.deepEqual(errors, []);
  await writeFile(path.join(output, 'evidence.json'), JSON.stringify({ fixture: 'loopback only; fake passkey transport', screenshots, pageErrors: errors, checks: ['owner group creation recovery', 'explicit one-use invitation creation', 'initial countdown draft required', 'expired owner authentication recovery without mutation replay', 'confirmed countdown and manual evening opening', 'owner player mode and group switch before launch', 'owner character creation form and player navigation', '320, 390, 1280 pixel widths', 'optional portrait failure does not block launch settings'] }, null, 2));
  console.log(JSON.stringify({ output, screenshots: screenshots.length, pageErrors: errors }));
} finally {
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}
