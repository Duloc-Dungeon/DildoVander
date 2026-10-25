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

describe('localized document titles', () => {
  it('updates the current guest route title immediately when the interface language changes', async () => {
    await router.push('/support');
    expect(document.title).toBe('S čím potřebujete pomoct? · Wand Night Stand');

    interfaceLanguagePreference.setLanguage('en');
    expect(document.title).toBe('How can we help? · Wand Night Stand');
    expect(router.currentRoute.value.path).toBe('/support');
  });
});
