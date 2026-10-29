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
  it.each(['/', '/demo', '/demo/', '/DEMO'])('sends retired entry %s to the sign-in experience', async path => {
    await router.push(path);
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.name).toBe('signin');
  });

  it('keeps the archive behind account authentication', () => {
    expect(router.resolve('/archive').meta.requiresAccount).toBe(true);
  });

  it('retires the physical conversation bookmark behind the digital workshop account guard', async () => {
    expect(router.resolve('/me/conversation').matched[0].redirect).toBe('/play/wand');
    expect(router.resolve('/play/wand').meta.requiresAccount).toBe(true);
    await router.push('/me/conversation');
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.query.return).toBe('/play/wand');
  });

  it('preserves the director bookmark when retiring the home route', async () => {
    await router.push('/#director');
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.query.return).toBe('/suitcase/director');
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
