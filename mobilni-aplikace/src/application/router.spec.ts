import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { interfaceLanguagePreference } from './interfaceLanguage';
import { router } from './router';

beforeEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
});

afterEach(async () => {
  await router.push('/');
  interfaceLanguagePreference.setLanguage('cs');
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

  it('preserves the director bookmark when retiring the home route', async () => {
    await router.push('/#director');
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.query.return).toBe('/suitcase/director');
  });
});

describe('localized document titles', () => {
  it('updates the current guest route title immediately when the interface language changes', async () => {
    await router.push('/support');
    expect(document.title).toBe('S čím potřebujete pomoct? · Wand Night Stand');

    interfaceLanguagePreference.setLanguage('en');
    expect(document.title).toBe('S čím potřebujete pomoct? · Wand Night Stand');
    expect(router.currentRoute.value.path).toBe('/support');
  });
});
