import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright';
import { createSealedCaseFixtureServer, sealedCaseId } from './fixtures/sealed-case.mjs';

const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-sealed-dossier');
await mkdir(output, { recursive: true });
const browser = await chromium.launch();
const errors = [];
function contrast(first, second) {
  const luminance = color => {
    const values = color.match(/[\d.]+/g).slice(0, 3).map(value => {
      const channel = Number(value) / 255;
      return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
    });
    return values[0] * .2126 + values[1] * .7152 + values[2] * .0722;
  };
  const values = [luminance(first), luminance(second)].sort((a, b) => b - a);
  return (values[0] + .05) / (values[1] + .05);
}
try {
  for (const width of [320, 390, 760, 1280]) {
    const fixture = await createSealedCaseFixtureServer();
    const context = await browser.newContext({ viewport: { width, height: 900 }, reducedMotion: 'reduce' });
    try {
      const page = await context.newPage();
      const writes = [];
      page.on('pageerror', error => errors.push(error.message));
      page.on('request', request => { if (request.method() === 'POST') writes.push(request); });
      const overflow = async label => assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${label}: ${width}px`);
      const screenshot = name => page.screenshot({ path: path.join(output, `${name}-${width}.png`), fullPage: true });
      await page.goto(`${fixture.origin}/play/pod-peceti/${sealedCaseId}`);
      await page.getByText('Znovu jsi narazil na již známou stopu.', { exact: true }).waitFor();
      await page.getByText('Plán průzkumu', { exact: false }).click();
      await page.getByText('Část C · od kapitoly 3', { exact: true }).waitFor();
      await overflow('early schedule');
      await screenshot('story-route');
      await page.getByRole('button', { name: 'Moje akce', exact: true }).click();
      await page.getByRole('button', { name: /Chránit sebe/ }).click();
      await page.getByText('Plán průzkumu', { exact: false }).click();
      await page.getByRole('button', { name: 'Připravit přesun: Salon', exact: true }).first().click();
      assert.equal(writes.length, 0, 'route selection is local');
      await page.getByRole('button', { name: 'Výslovně potvrdit plán', exact: true }).click();
      await page.getByText('Uložený plán: Chránit', { exact: true }).waitFor();
      assert.equal(writes.length, 1, 'explicit confirmation is one write');
      assert.equal(writes[0].postDataJSON().plan.destination, 'SALON');
      assert.equal(writes[0].postDataJSON().plan.intent, 2, 'travel preserves the selected Guard action');
      await page.getByRole('button', { name: 'Deník', exact: true }).click();
      await page.getByRole('button', { name: 'Doložené stopy', exact: true }).click();
      await page.getByRole('heading', { name: 'Důkazní řetězce', exact: true }).waitFor();
      assert.equal(await page.getByText('A, B i C jsou ve tvém spisu.', { exact: true }).count(), 0);
      await page.locator('.sealed-evidence-part summary').first().click();
      await overflow('partial dossier');
      await screenshot('dossier-partial');
      const share = page.getByRole('button', { name: 'Připravit sdílení části A', exact: true }).first();
      await share.focus();
      await page.keyboard.press('Tab'); await page.keyboard.press('Shift+Tab');
      assert.equal(await share.evaluate(element => element.matches(':focus-visible')), true);
      assert.equal(await share.evaluate(element => document.getElementById(element.getAttribute('aria-describedby')).textContent), 'Řetězec R01');
      const paperColor = await page.locator('.sealed-dossier').evaluate(element => getComputedStyle(element).backgroundColor);
      for (const selector of ['.sealed-part-family', '.sealed-chain-links span']) {
        const metadataColor = await page.locator(selector).first().evaluate(element => getComputedStyle(element).color);
        assert.ok(contrast(metadataColor, paperColor) >= 4.5, `${selector} text contrast`);
      }
      assert.ok(contrast(await share.evaluate(element => getComputedStyle(element).outlineColor), paperColor) >= 3, 'focus is visible on the paper surface');
      assert.equal(await page.locator('.sealed-dossier').evaluate(element => getComputedStyle(element).animationName), 'none');
      const shareSize = await share.boundingBox();
      assert.ok(shareSize.height >= 44, 'touch target');
      await share.click();
      assert.equal(writes.length, 1, 'prepare share is local');
      const message = page.getByLabel('Zpráva', { exact: true });
      assert.ok((await message.inputValue()).startsWith('Stopa R01-A · řetězec R01 · část A.'));
      assert.equal(await message.evaluate(element => element === document.activeElement), true);
      await message.fill(`Moje vlastní poznámka.\n\n${await message.inputValue()}`);
      await page.getByRole('button', { name: 'Deník', exact: true }).click();
      await page.locator('.sealed-evidence-part summary').nth(1).click();
      await page.getByRole('button', { name: 'Připravit sdílení části B', exact: true }).click();
      assert.ok((await message.inputValue()).startsWith('Moje vlastní poznámka.'));
      assert.ok((await message.inputValue()).includes('Stopa R01-B · řetězec R01 · část B.'));
      assert.equal(writes.length, 1, 'two shares and custom text still require explicit send');
      await page.getByRole('combobox', { name: /Komu/ }).selectOption('p2');
      await page.getByRole('button', { name: 'Odeslat tvrzení', exact: true }).click();
      await page.getByText('Tvoje odeslaná sdělení: 1 · veřejná i soukromá.', { exact: true }).waitFor();
      assert.equal(writes.at(-1).postDataJSON().public, false);
      assert.deepEqual(writes.at(-1).postDataJSON().recipients, ['p2']);
      await page.getByRole('button', { name: 'Deník', exact: true }).click();
      await page.getByText('Uvádí řetězec R01, část A.', { exact: true }).waitFor();
      await page.getByText('Uvádí řetězec R01, část B.', { exact: true }).waitFor();
      assert.equal(await page.locator('.sealed-claims li').count(), 3, 'both references from the same private message are indexed');
      const ownClaim = page.locator('.sealed-claims li').filter({ has: page.getByText('Uvádí řetězec R01, část A.', { exact: true }) });
      assert.equal(await ownClaim.locator('details').evaluate(element => element.open), false, 'full claim text starts collapsed');
      assert.equal(await ownClaim.getByText('Stopu R01-A máš i ve vlastním spisu.', { exact: false }).isVisible(), true, 'ownership status remains outside the disclosure');
      const fullClaim = ownClaim.locator('summary');
      assert.equal(await fullClaim.getAttribute('aria-label'), 'Celé tvrzení 1: Mira, řetězec R01, část A');
      const claimSize = await fullClaim.boundingBox();
      assert.ok(claimSize.height >= 44, 'claim disclosure touch target');
      await fullClaim.focus(); await page.keyboard.press('Space');
      assert.equal(await ownClaim.locator('details').evaluate(element => element.open), true, 'keyboard opens the original statement');
      assert.ok((await ownClaim.locator('details p').innerText()).startsWith('Moje vlastní poznámka.'), 'authored prefix remains accessible');
      assert.ok((await ownClaim.locator('details p').innerText()).includes('Stopa R01-B'), 'the full multi-reference statement is preserved');
      await screenshot('shared-claims');
      await page.goto(`${fixture.origin}/play/pod-peceti/${sealedCaseId}?dossier=complete`);
      await page.getByRole('button', { name: 'Otevřít důkazní řetězce', exact: true }).click();
      await page.getByText('A, B i C jsou ve tvém spisu.', { exact: true }).waitFor();
      await page.getByText('Společný okruh kandidátů: Hermiona.', { exact: false }).waitFor();
      assert.equal(await page.getByText('Tuto stopu ve vlastním spisu nemáš.', { exact: true }).count(), 1, 'other chain claim remains unverified');
      await overflow('complete dossier');
      await screenshot('dossier-complete');
      await page.evaluate(() => document.documentElement.style.fontSize = '200%');
      await overflow('200% text');
      await screenshot('dossier-large-text');
    } finally { await context.close(); await fixture.close(); }
  }
  assert.deepEqual(errors, []);
  console.log(`PASS dossier: partial/complete chains, unverified claims and full-text disclosures, early routes, explicit plan, private share, keyboard, contrast/focus, reduced motion, 200% text, 320/390/760/1280px. ${output}`);
} finally { await browser.close(); }
