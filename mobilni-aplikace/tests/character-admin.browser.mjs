// Isolated admin UI regression; no production accounts, media or AI providers.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, stat, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-character-admin-browser');
const mediaPath = fileURLToPath(new URL('../src/assets/brand/game/boss.webp', import.meta.url));
const media = await readFile(mediaPath);
await mkdir(output, { recursive: true });
const original = () => ({
  profileId: '12345678123442348234123456789abc', revision: 2,
  userInput: { name: 'Luna Lovegoodová', world: 'Harry Potter', version: 'Filmová verze', description: 'Pozoruje svět trochu jinak.', language: 'cs' },
  status: 'ready', enrichment: { summary: 'Připravený profil' }, errorCode: null, updatedAt: 1792540800,
  portrait: { status: 'ready', hasImage: true, errorCode: null, updatedAt: 1792540800 },
});
let character = original();
let writes = [];
const summary = profile => ({ profileId: profile.profileId, revision: profile.revision, userInput: { name: profile.userInput.name, world: profile.userInput.world }, status: profile.status, updatedAt: profile.updatedAt });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.webp': 'image/webp', '.svg': 'image/svg+xml', '.png': 'image/png' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    const json = (value, status = 200) => response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'private, no-store' }).end(JSON.stringify(value));
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'fixture-admin', displayName: 'Správce', roles: ['Administrator'] }, csrfToken: 'fixture' });
    if (url.pathname === '/app/v1/launch') return json({ allowed: true });
    if (url.pathname === '/app/v1/admin/characters') {
      return json(url.searchParams.has('after')
        ? { items: [summary({ ...character, profileId: 'abcdef0123456789abcdef0123456789', userInput: { name: 'Robin', world: 'Fiction' } })], nextCursor: null }
        : { items: [summary(character)], nextCursor: character.profileId });
    }
    if (url.pathname.startsWith(`/app/v1/admin/characters/${character.profileId}`)) {
      if (request.method === 'GET') {
        if (url.pathname.endsWith('/portrait')) return response.writeHead(200, { 'Content-Type': 'image/webp', 'Cache-Control': 'private, no-store' }).end(media);
        return json(character);
      }
      const parts = [];
      for await (const chunk of request) parts.push(chunk);
      const bytes = Buffer.concat(parts);
      writes.push({ path: url.pathname, query: url.search, headers: request.headers, bytes });
      const portrait = url.pathname.endsWith('/portrait');
      const payload = portrait ? null : JSON.parse(bytes.toString());
      const expected = portrait ? Number(url.searchParams.get('expectedRevision')) : payload.expectedRevision;
      if (expected !== character.revision) return json({ code: 'character_revision_conflict' }, 409);
      assert.equal(request.headers['x-dv-csrf'], 'fixture');
      assert.equal(request.headers['x-dv-confirm'], 'yes');
      character = { ...character, revision: character.revision + 1, ...(portrait ? {} : { userInput: payload.userInput, status: 'saved', enrichment: null }) };
      return json(character);
    }
    if (url.pathname.startsWith('/app/')) return json({});
    let file = path.resolve(dist, '.' + url.pathname);
    if (path.relative(dist, file).startsWith('..')) return response.writeHead(403).end();
    try { if (!(await stat(file)).isFile()) file = path.join(dist, 'index.html'); } catch { file = path.join(dist, 'index.html'); }
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream' }).end(await readFile(file));
  } catch (error) { response.writeHead(500).end(); console.error(error); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
if (process.argv.includes('--preview')) {
  console.log(`Synthetic character admin preview: ${origin}/settings/admin/characters`);
} else {
  const browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH } : {}) });
  try {
    const errors = [];
    for (const width of [320, 390, 1280]) {
      character = original(); writes = [];
      const context = await browser.newContext({ viewport: { width, height: 900 }, reducedMotion: 'reduce' });
      const page = await context.newPage();
      page.on('pageerror', error => errors.push(error.message));
      page.on('dialog', dialog => dialog.accept());
      const capture = async state => {
        await page.evaluate(() => document.fonts.ready);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false, `${width}px ${state}: no horizontal overflow`);
        await page.screenshot({ path: path.join(output, `${state}-${width}.png`), fullPage: true });
      };
      await page.goto(origin + '/settings/admin/characters');
      await page.getByRole('heading', { name: 'Postavy a portréty', exact: true }).waitFor();
      const navigation = page.getByRole('navigation', { name: 'Správa aplikace', exact: true });
      assert.equal(await navigation.locator('[aria-current="page"]').textContent(), 'Postavy');
      await navigation.getByRole('link', { name: 'Účty', exact: true }).focus();
      await page.keyboard.press('Tab');
      assert.equal(await navigation.getByRole('link', { name: 'Večer', exact: true }).evaluate(node => node === document.activeElement), true);
      await page.getByRole('button', { name: 'Načíst další postavy', exact: true }).click();
      await page.getByText('Robin', { exact: true }).waitFor();
      assert.equal(await page.locator('.character-admin__list button').count(), 2);
      await page.getByLabel('Hledat podle jména nebo světa').fill('nikdo');
      await page.getByText('Hledání neodpovídá žádná postava.').waitFor();
      await page.getByRole('button', { name: 'Zrušit hledání', exact: true }).click();
      assert.equal(await page.getByLabel('Hledat podle jména nebo světa').inputValue(), '');
      assert.equal(await page.locator('.character-admin__list button').count(), 2);
      await page.getByLabel('Hledat podle jména nebo světa').fill('potter');
      await page.locator('.character-admin__list button').click();
      await page.locator('.character-admin__portrait > img[src^="blob:"]').waitFor();
      assert.equal(await page.locator('#character-detail-title').evaluate(node => node === document.activeElement), true);
      await capture('editor');
      await page.getByLabel('Jméno postavy').fill('Luna upravená');
      await page.getByRole('button', { name: 'Uložit údaje', exact: true }).click();
      await page.getByText('Údaje postavy byly uloženy.').waitFor();
      assert.equal(await page.locator('.admin-confirmation').getAttribute('role'), 'status');
      assert.equal(await page.locator('.admin-confirmation__seal img').evaluate(node => getComputedStyle(node).animationName), 'none');
      await capture('saved');
      assert.equal(character.userInput.name, 'Luna upravená');
      assert.equal(character.revision, 3);
      assert.deepEqual(Object.keys(JSON.parse(writes[0].bytes)).sort(), ['expectedRevision', 'userInput']);
      await page.getByLabel('Vybrat obrázek', { exact: true }).setInputFiles({ name: 'invalid.svg', mimeType: 'image/svg+xml', buffer: Buffer.from('<svg/>') });
      await page.getByText('Vyberte neprázdný obrázek PNG, JPEG nebo WebP do 8 MB.').waitFor();
      await page.getByLabel('Vybrat obrázek', { exact: true }).setInputFiles(mediaPath);
      await page.getByAltText('Náhled náhradního obrázku').waitFor();
      await capture('replacement-preview');
      await page.getByRole('button', { name: 'Nahrát náhradní obrázek', exact: true }).click();
      await page.getByText('Náhradní obrázek byl uložen.').waitFor();
      assert.equal(character.revision, 4);
      assert.equal(writes[1].query, '?expectedRevision=3');
      assert.equal(writes[1].headers['content-type'], 'image/webp');
      assert.deepEqual(writes[1].bytes, media);
      await page.getByLabel('Popis', { exact: true }).fill('Moje rozepsané změny');
      character = { ...character, revision: 5 };
      await page.getByRole('button', { name: 'Uložit údaje', exact: true }).click();
      await page.getByText(/Postavu mezitím někdo změnil/).waitFor();
      assert.equal(await page.getByLabel('Popis', { exact: true }).inputValue(), 'Moje rozepsané změny');
      assert.equal(await page.getByRole('button', { name: 'Uložit údaje', exact: true }).isDisabled(), true);
      await capture('conflict');
      await page.getByRole('button', { name: 'Načíst aktuální verzi', exact: true }).click();
      await page.getByText(/revize 5 ·/).waitFor();
      await page.getByLabel('Jméno postavy').focus();
      await page.keyboard.press('Tab');
      assert.equal(await page.getByLabel('Svět', { exact: true }).evaluate(node => node === document.activeElement), true);
      await context.close();
    }
    assert.deepEqual(errors, []);
    console.log(`Character admin browser checks passed: 320/390/1280px; admin navigation, search recovery, keyboard focus, save feedback, reduced motion, manual edit, invalid file, replacement preview/upload, revision conflict. Screenshots: ${output}`);
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
}
