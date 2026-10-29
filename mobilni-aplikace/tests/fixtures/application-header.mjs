import assert from 'node:assert/strict';

export async function assertApplicationHeader(page) {
  const header = page.locator('.application-topbar');
  await header.waitFor();
  assert.equal(await header.count(), 1, 'each screen has one shared top bar');
  assert.equal(await page.locator('.wns-wordmark').count(), 1, 'route content does not duplicate the brand');
  assert.equal(await header.locator('.wns-wordmark__name').innerText(), 'Wand\nNight Stand');
  assert.equal(await header.locator('.wns-wordmark__maker').innerText(), 'OlivanderIndustries™');
  assert.equal(await header.evaluate(element => element.scrollWidth <= element.clientWidth), true, 'top bar fits the viewport');
  assert.equal(await header.locator('.wns-wordmark').evaluate(element => {
    const title = getComputedStyle(element.querySelector('.wns-wordmark__name'));
    const maker = getComputedStyle(element.querySelector('.wns-wordmark__maker'));
    return title.fontFamily === maker.fontFamily && parseFloat(maker.fontSize) === parseFloat(title.fontSize) / 2;
  }), true, 'maker line keeps the same type family and half the title size');
}
