// Recruitment contract and responsive administration, using the real app and loopback fixtures.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';
import { createOpeningFixtureServer } from './fixtures/opening.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-recruitment-audit');
await mkdir(output, { recursive: true });
const { server, origin } = await createOpeningFixtureServer();
const browser = await chromium.launch();
const errors = [];
const screenshots = [];
try {
  for (const width of [320, 390, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 900 }, reducedMotion: 'reduce' });
    const oldOpening = Date.now() - 86400000;
    let groups = [
      { id: 'old', name: 'Páteční hosté', revision: 2, launchAt: oldOpening, openedAt: oldOpening },
      { id: 'new', name: 'Sobotní hosté', revision: 1, launchAt: null, openedAt: null },
    ];
    let admission = { revision: 3, groupId: 'old', groupName: 'Páteční hosté', recruitmentOpen: true, fixedUniverse: null };
    const writes = [];
    await context.route('**/*', async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.origin !== origin) return route.abort();
      const json = value => route.fulfill({ contentType: 'application/json', body: JSON.stringify(value) });
      if (url.pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'owner', displayName: 'Správce', roles: ['Owner'], openAiConsent: true }, csrfToken: 'offline-fixture', origin });
      if (url.pathname === '/app/v1/launch') return json({ allowed: true, audience: 'privileged', launchAt: null, serverNow: Date.now() });
      if (url.pathname === '/app/v1/admin/launch') return json({ revision: 1, timeZone: 'Europe/Prague', personalizedLoadingEnabled: true, personalizedLoadingPrompt: 'Původní pokyn.' });
      if (url.pathname === '/app/v1/admin/launch/artwork') return json({ jobs: [] });
      if (url.pathname === '/app/v1/admin/launch/audit') return json({ entries: [] });
      if (url.pathname === '/app/v1/admin/launch/groups') return json({ groups });
      if (url.pathname === '/app/v1/admin/launch/waiting-room') return json({ members: [{ userId: 'waiting', displayName: 'Nová účastnice', groupId: 'new', groupName: 'Sobotní hosté', state: 'waiting', joinedAt: Math.floor(Date.now() / 1000), accessRevision: 1, fixedUniverse: null }] });
      if (url.pathname === '/app/v1/admin/launch/beta-admission') {
        if (request.method() === 'PUT') {
          const input = request.postDataJSON();
          writes.push({ path: url.pathname, input });
          assert.equal(request.headers()['x-dv-confirm'], 'yes');
          assert.equal(input.expectedRevision, admission.revision);
          const group = groups.find(item => item.id === input.groupId);
          assert.equal(input.expectedGroupRevision, group.revision);
          groups = groups.map(item => item.id === group.id ? { ...item, revision: item.revision + 1, launchAt: input.launchAt } : item);
          admission = { ...admission, ...input, revision: admission.revision + 1, groupName: group.name };
        }
        return json(admission);
      }
      return route.continue();
    });
    const page = await context.newPage();
    page.on('dialog', dialog => dialog.accept());
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(origin + '/settings/admin/launch');
    await page.locator('#admission-management-group').waitFor();
    assert.equal(await page.locator('#admission-launch').isDisabled(), true);
    await page.locator('#admission-management-group').selectOption('new');
    await page.locator('#admission-launch').fill('2099-10-30T10:00');
    await page.getByRole('button', { name: 'Uložit skupinu, spuštění a nábor', exact: true }).click();
    await page.getByText('Skupina, termín spuštění a nábor jsou uložené.', { exact: true }).waitFor();
    assert.equal(writes.length, 1);
    assert.deepEqual(writes[0].input, { expectedRevision: 3, groupId: 'new', expectedGroupRevision: 1, launchAt: Date.UTC(2099, 9, 30, 9), recruitmentOpen: true, fixedUniverse: null });
    assert.equal(groups[0].openedAt, oldOpening);
    assert.equal(groups[0].launchAt, oldOpening);
    assert.equal(await page.locator('#management-group').inputValue(), 'old', 'roster group is independent of recruitment target');
    assert.equal(await page.locator('#countdown-minutes, #production-launch, #beta-launch, #launch-teaser, #launch-loading').count(), 0);
    assert.equal(await page.getByText('Náhled čekárny', { exact: true }).count(), 0);
    await page.locator('#admission-management-group').selectOption('old');
    assert.equal(await page.locator('#admission-launch').isDisabled(), true);
    await page.locator('#admission-management-group').selectOption('new');
    await page.locator('#admission-launch').focus();
    assert.equal(await page.locator('#admission-launch').evaluate(input => document.activeElement === input), true);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false, `no horizontal overflow at ${width}`);
    const screenshot = path.join(output, `recruitment-${width}.png`);
    await page.screenshot({ path: screenshot, fullPage: true });
    screenshots.push(screenshot);
    await context.close();
  }

  // Old members retain entry; new members stay in the real launch scene until their group's date.
  for (const allowed of [true, false]) {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
    await context.route('**/app/v1/launch', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify({ allowed, audience: 'production', launchAt: allowed ? Date.now() - 1000 : Date.now() + 3600000, serverNow: Date.now() }) }));
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(origin + '/__fixture/E');
    if (allowed) {
      await page.evaluate(() => sessionStorage.setItem('wand.group-opening-seen.v1.midnight-fixture', 'seen'));
      await page.goto(origin + '/me/character');
      await page.waitForURL('**/me/character');
    } else {
      await page.waitForURL('**/launch');
      await page.getByRole('button', { name: 'Vstoupit', exact: true }).click();
      await page.locator('.character-countdown-grid').waitFor();
      assert.equal(await page.locator('.character-countdown-grid > div').count(), 4);
    }
    await context.close();
  }
  assert.deepEqual(errors, []);
  await writeFile(path.join(output, 'evidence.json'), JSON.stringify({ origin: 'loopback synthetic fixtures only', viewports: [320, 390, 1280], screenshots, consoleErrors: errors, checks: ['atomic group/date/recruitment save', 'opened group preserved', 'old controls absent', 'old member entry and new member waiting', 'focus and overflow'] }, null, 2));
  console.log(`PASS recruitment administration and guest routing; screenshots: ${output}`);
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
