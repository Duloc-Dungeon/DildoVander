import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { interfaceLanguagePreference } from './interfaceLanguage';
import { router } from './router';

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json({ enabled: true, account: null, csrfToken: 'synthetic' })));
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
});

afterEach(async () => {
  await router.push('/');
  interfaceLanguagePreference.setLanguage('cs');
  vi.unstubAllGlobals();
});

describe('customer entry routes', () => {
  it('redirects the old game chronicle route to the shared Keepsakes timeline', async () => {
    expect(router.resolve('/play/chronicle').matched[0]?.redirect).toBe('/me/keepsakes');
    await router.push('/play/chronicle');
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.query.return).toBe('/me/keepsakes');
  });

  it.each(['/', '/demo', '/demo/', '/DEMO'])('sends retired entry %s to the sign-in experience', async path => {
    await router.push(path);
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.name).toBe('signin');
  });

  it.each([
    ['/archive', '/me/keepsakes'],
    ['/archive/chronicle', '/me/keepsakes'],
    ['/keepsake/chronicle.html', '/me/keepsakes'],
    ['/keepsake/evening/', '/me/keepsakes'],
    ['/keepsake/evening/index.html', '/me/keepsakes'],
    ['/tonight', '/play'],
    ['/suitcase', '/play'],
    ['/suitcase/diagnostics', '/play'],
    ['/suitcase/director', '/play'],
    ['/connections', '/play'],
    ['/app/', '/play'],
    ['/companion/', '/play'],
    ['/workshop', '/play/wand'],
    ['/workshop/index.html', '/play/wand'],
    ['/workshop/viewer.html', '/play/wand'],
    ['/simulator/', '/play/wand'],
  ])('retires %s behind the current %s account guard', async (path, destination) => {
    await router.push(path);
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.query.return).toBe(destination);
    expect(router.resolve(destination).meta.requiresAccount).toBe(true);
  });

  it('retires the physical conversation bookmark behind the digital workshop account guard', async () => {
    expect(router.resolve('/me/conversation').matched[0].redirect).toBe('/play/wand');
    expect(router.resolve('/play/wand').meta.requiresAccount).toBe(true);
    await router.push('/me/conversation');
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.query.return).toBe('/play/wand');
  });

  it('drops the retired director fragment at the sign-in entry', async () => {
    await router.push('/#director');
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.hash).toBe('');
    expect(router.currentRoute.value.query.return).toBeUndefined();
  });
});

describe('localized document titles', () => {
  it('updates the current guest route title immediately when the interface language changes', async () => {
    await router.push('/support');
    expect(document.title).toBe('S čím potřebuješ pomoct? · Wand Night Stand');

    interfaceLanguagePreference.setLanguage('en');
    expect(document.title).toBe('S čím potřebuješ pomoct? · Wand Night Stand');
    expect(router.currentRoute.value.path).toBe('/support');
  });
});
