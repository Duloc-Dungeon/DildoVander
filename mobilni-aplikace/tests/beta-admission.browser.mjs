// Offline visual checks for the new beta enrollment and account access states.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const root = fileURLToPath(new URL('../dist/', import.meta.url));
const output = process.env.APP_SCREENSHOTS ?? fileURLToPath(new URL('../../.codex/artifacts/wand-beta-admission/', import.meta.url));
await mkdir(output, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.webp': 'image/webp', '.svg': 'image/svg+xml' };
const server = createServer(async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  let file = path.resolve(root, decodeURIComponent(url.pathname).replace(/^\//, '') || 'index.html');
  if (!file.startsWith(path.resolve(root) + path.sep)) file = path.join(root, 'index.html');
  try { if (!(await stat(file)).isFile()) file = path.join(root, 'index.html'); } catch { file = path.join(root, 'index.html'); }
  response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
  response.end(await readFile(file));
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const groupId = '0123456789abcdef0123456789abcdef';
const userId = 'abcdef0123456789abcdef0123456789';
let currentAccount = null;
let betaAvailable = true;
let memberActive = true;
let memberAccessState = 'active';
let guestLaunchAt = null;
const secondGroupId = '123456789abcdef0123456789abcdef0';
let roomGroupId = groupId;
let adminGroups = [];
const errors = [];
const screenshots = [];

async function installFixtures(context) {
  await context.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin !== origin) return route.abort();
    const json = value => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(value) });
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: currentAccount, csrfToken: 'fixture-only', origin });
    if (url.pathname === '/app/v1/enrollment/default') return json({ available: betaAvailable, groupId: betaAvailable ? groupId : null, groupName: 'Interní beta', fixedUniverse: false, universe: null });
    if (url.pathname === '/app/v1/customer/access') return json({ state: memberAccessState, groupId, groupName: 'Interní beta', joinedAt: Math.floor(Date.now() / 1000), accessRevision: 3, recruitmentOpen: false, launchAt: Date.now() + 86400000 });
    if (url.pathname === '/app/v1/launch') return json({ allowed: currentAccount?.roles.includes('Owner') ?? false, audience: 'beta', launchAt: currentAccount?.roles.includes('Owner') ? Date.now() + 86400000 * 2 : guestLaunchAt, serverNow: Date.now(), teaser: 'Čekárna fixture', initialLoadingMessage: 'Čekáme.' });
    if (url.pathname === '/app/v1/prelaunch/character') return json({ character: { profileId: 'fixture-profile', revision: 1, name: 'Ava Longname', universe: 'Za zrcadlem', status: 'ready' }, artwork: { status: 'running', welcome: null } });
    if (url.pathname === '/app/v1/prelaunch/character/portrait') return route.fulfill({ status: 404, body: '' });
    if (url.pathname === '/app/v1/admin/launch') return json({ revision: 2, betaEnabled: true, betaLaunchAt: Date.now() + 86400000 * 2, betaUnlocked: false, productionEnabled: false, productionLaunchAt: null, productionUnlocked: false, timeZone: 'Europe/Prague', teaser: 'Večer se blíží.', initialLoadingMessage: 'Čekáme na portrét.', personalizedLoadingEnabled: false, personalizedLoadingPrompt: '' });
    if (url.pathname === '/app/v1/admin/launch/artwork') return json({ jobs: [] });
    if (url.pathname === '/app/v1/admin/launch/audit') return json({ entries: [] });
    if (url.pathname === '/app/v1/admin/launch/beta-admission') return json({ revision: 4, groupId, groupName: 'Interní beta', recruitmentOpen: true, fixedUniverse: null });
    if (url.pathname === '/app/v1/admin/launch/groups') return json({ groups: adminGroups });
    if (url.pathname === '/app/v1/admin/launch/waiting-room') return json({ members: [{ userId, displayName: 'Ava Longname', joinedAt: Math.floor(Date.now() / 1000) - 86400, groupId: roomGroupId, groupName: adminGroups.find(group => group.id === roomGroupId)?.name ?? null, state: memberActive ? 'waiting' : 'revoked', accessRevision: 6, fixedUniverse: 'Za zrcadlem' }] });
    if (url.pathname === `/app/v1/admin/launch/waiting-room/${userId}/group` && request.method() === 'PUT') {
      roomGroupId = request.postDataJSON().targetGroupId;
      return json({});
    }
    if (url.pathname === `/app/v1/admin/launch/groups/${secondGroupId}/countdown` && request.method() === 'PUT') {
      const input = request.postDataJSON();
      adminGroups = adminGroups.map(group => group.id === secondGroupId ? { ...group, countdownDurationSeconds: input.durationSeconds, forceStartAtEnd: input.forceStartAtEnd, revision: group.revision + 1 } : group);
      return json({});
    }
    if (url.pathname === `/app/v1/admin/launch/groups/${secondGroupId}/countdown/confirm` && request.method() === 'POST') {
      adminGroups = adminGroups.map(group => group.id === secondGroupId ? { ...group, launchAt: Date.now() + group.countdownDurationSeconds * 1000, countdownConfirmedAt: Date.now(), revision: group.revision + 1 } : group);
      guestLaunchAt = adminGroups.find(group => group.id === secondGroupId).launchAt;
      return json({});
    }
    if (url.pathname.match(new RegExp(`/app/v1/admin/launch/groups/[0-9a-f]{32}/members/${userId}$`)) && request.method() === 'PUT') {
      memberActive = request.postDataJSON().active;
      return json({});
    }
    if (url.pathname === `/app/v1/admin/launch/groups/${groupId}/members`) return json({ members: [{ userId, displayName: 'Ava Longname', joinedAt: Math.floor(Date.now() / 1000) - 86400, accessActive: memberActive, accessRevision: 6 }] });
    if (url.pathname.startsWith('/app/v1/')) return route.fulfill({ status: 204, body: '' });
    return route.continue();
  });
}

async function capture(page, name, state, viewport, theme) {
  await page.evaluate(() => document.fonts.ready);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, name + ' horizontal overflow');
  await page.screenshot({ path: path.join(output, name + '.png'), fullPage: true });
  screenshots.push({ file: name + '.png', route: page.url().replace(origin, ''), state, viewport, theme, mode: 'offline browser fixture' });
}

let browser;
try {
  browser = await chromium.launch({
    ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH } : {}),
    ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}),
  });
  for (const theme of ['lord-voldemort', 'olivander']) {
    for (const [width, height] of [[320, 568], [375, 812], [390, 844], [430, 932]]) {
      currentAccount = null; betaAvailable = true; guestLaunchAt = null;
      const context = await browser.newContext({ viewport: { width, height }, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
      await context.addInitScript(value => localStorage.setItem('dildovander.theme', value), theme);
      await installFixtures(context);
      const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
      await page.goto(origin + '/signin');
      await page.getByRole('button', { name: 'Začít', exact: true }).click();
      await page.getByLabel('Jméno postavy').waitFor();
      assert.equal(await page.getByRole('checkbox').isChecked(), false);
      await capture(page, `beta-enroll-${width}x${height}-${theme}`, 'new participant form', `${width}x${height}`, theme);
      if (width === 390) {
        betaAvailable = false; await page.reload();
        await page.getByRole('button', { name: 'Začít', exact: true }).click();
        await page.getByText('Přijímání nových účastníků je nyní uzavřené.').waitFor();
        await capture(page, `beta-closed-${theme}`, 'recruitment closed', `${width}x${height}`, theme);
        currentAccount = { id: 'fixture-member', displayName: 'Ava Longname', roles: ['Customer'] }; guestLaunchAt = null;
        betaAvailable = true; await page.goto(origin + '/launch'); await page.locator('.portrait-stage').waitFor();
        await capture(page, `beta-waiting-${theme}`, 'accepted member waiting for group opening', `${width}x${height}`, theme);
      }
      await context.close();
    }
  }

  currentAccount = { id: 'fixture-owner', displayName: 'Owner', roles: ['Owner'] };
  memberActive = true;
  for (const theme of ['lord-voldemort', 'olivander']) {
    for (const [width, height] of [[320, 568], [390, 844], [430, 932]]) {
      roomGroupId = groupId;
      memberActive = true;
      adminGroups = [
        { id: groupId, name: 'Interní beta', launchAt: null, revision: 2, countdownDurationSeconds: 600, forceStartAtEnd: false, countdownConfirmedAt: null, openedAt: null },
        { id: secondGroupId, name: 'Sobota', launchAt: null, revision: 1, countdownDurationSeconds: 600, forceStartAtEnd: false, countdownConfirmedAt: null, openedAt: null },
      ];
      const adminContext = await browser.newContext({ viewport: { width, height }, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
      await adminContext.addInitScript(value => { localStorage.setItem('dildovander.theme', value); window.confirm = () => true; }, theme);
      await installFixtures(adminContext);
      const admin = await adminContext.newPage(); admin.on('pageerror', error => errors.push(error.message));
      await admin.goto(origin + '/settings/admin/launch');
      await admin.getByText('Ava Longname').waitFor({ state: 'attached' });
      await capture(admin, `waiting-room-${width}x${height}-${theme}`, 'owner roster with group assignments and countdown draft', `${width}x${height}`, theme);
      if (width === 390) {
        assert.equal(await admin.locator('#countdown-minutes').count(), 0);
        await admin.getByRole('button', { name: 'Nastavit datum a čas otevření', exact: true }).click();
        await admin.locator('#production-launch').waitFor();
        assert.equal(await admin.locator('#production-launch').evaluate(input => document.activeElement === input), true);
        await capture(admin, `default-production-schedule-${theme}`, 'default group uses production date and time', `${width}x${height}`, theme);
        await admin.locator('#waiting-room-title').click();
        await admin.getByLabel('Skupina pro Ava Longname').selectOption(secondGroupId);
        await capture(admin, `waiting-room-assignment-${theme}`, 'target group selected before confirmed move', `${width}x${height}`, theme);
        await admin.getByRole('button', { name: 'Přesunout' }).click();
        await admin.getByText('Sobota · Čeká').waitFor({ state: 'attached' });
        await admin.locator('#waiting-room-title').click();
        await capture(admin, `waiting-room-moved-${theme}`, 'waiting guest moved between groups with identity retained', `${width}x${height}`, theme);
        await admin.getByLabel('Spravovaná skupina', { exact: true }).selectOption(secondGroupId);
        await admin.getByLabel('Délka odpočtu v minutách').fill('15');
        await admin.getByLabel('Spustit večer automaticky po skončení odpočtu').check();
        await admin.getByRole('button', { name: 'Uložit návrh odpočtu' }).click();
        await admin.getByText(/Návrh je uložený/).waitFor();
        await capture(admin, `waiting-room-draft-${theme}`, 'saved countdown draft before owner confirmation', `${width}x${height}`, theme);
        await admin.getByRole('button', { name: 'Potvrdit a spustit odpočet' }).click();
        await admin.locator('section[aria-labelledby="group-countdown-title"]').getByRole('status').filter({ hasText: /Odpočet běží:/ }).waitFor();
        await capture(admin, `waiting-room-countdown-${theme}`, 'confirmed countdown with after-expiry auto-start enabled', `${width}x${height}`, theme);
        await admin.locator('#waiting-room-title').click();
        await admin.getByRole('button', { name: 'Odebrat přístup' }).click();
        await admin.locator('.waiting-room__identity > span').filter({ hasText: 'Přístup odebrán' }).waitFor({ state: 'attached' });
        await capture(admin, `waiting-room-revoked-${theme}`, 'participant access revoked from waiting room', `${width}x${height}`, theme);
      }
      await adminContext.close();
    }
  }

  currentAccount = { id: 'fixture-member', displayName: 'Ava Longname', roles: ['Customer'] };
  for (const theme of ['lord-voldemort', 'olivander']) {
    guestLaunchAt = null;
    const guestContext = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
    await guestContext.addInitScript(value => localStorage.setItem('dildovander.theme', value), theme);
    await installFixtures(guestContext);
    const guest = await guestContext.newPage(); guest.on('pageerror', error => errors.push(error.message));
    await guest.goto(origin + '/launch'); await guest.locator('.character-countdown-wait').getByText('Brzy').waitFor();
    await capture(guest, `guest-soon-unconfirmed-${theme}`, 'guest sees Soon before owner confirmation', '390x844', theme);
    guestLaunchAt = Date.now() + 60_000; await guest.reload(); await guest.locator('.character-countdown-grid').waitFor();
    await capture(guest, `guest-countdown-confirmed-${theme}`, 'guest countdown after owner confirmation', '390x844', theme);
    guestLaunchAt = Date.now() - 1000; await guest.reload(); await guest.locator('.character-countdown-wait').getByText('Brzy').waitFor();
    assert.equal(await guest.locator('.character-countdown-grid').count(), 0, 'expired non-force countdown stays Soon');
    await capture(guest, `guest-soon-after-expiry-${theme}`, 'guest remains Soon after non-force countdown expires', '390x844', theme);
    await guestContext.close();
  }

  currentAccount = { id: 'fixture-member', displayName: 'Ava Longname', roles: ['Customer'] };
  memberAccessState = 'revoked';
  const revokedContext = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
  await revokedContext.addInitScript(() => localStorage.setItem('dildovander.theme', 'lord-voldemort'));
  await installFixtures(revokedContext);
  const revoked = await revokedContext.newPage(); revoked.on('pageerror', error => errors.push(error.message));
  await revoked.goto(origin + '/');
  await revoked.getByText('Tvůj přístup k testování byl odebrán.').waitFor();
  await capture(revoked, 'beta-access-revoked', 'revoked member restricted account page', '390x844', 'lord-voldemort');
  await revokedContext.close();
  assert.deepEqual(errors, []);
  await writeFile(path.join(output, 'evidence.json'), JSON.stringify({ origin: 'loopback offline fixtures only', screenshots, consoleErrors: errors, checks: ['320, 375, 390 and 430 CSS pixel registration viewports', '320, 390 and 430 CSS pixel owner waiting room in both themes', 'closed enrollment, waiting, owner roster, explicit group move, countdown draft and confirmation, revoked account screen', 'no provider or production calls'] }, null, 2));
  console.log(JSON.stringify({ screenshots: screenshots.length, output, consoleErrors: errors }));
} finally {
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}
