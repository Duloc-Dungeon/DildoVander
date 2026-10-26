import { describe, expect, it, vi } from 'vitest';
import {
  createInterfaceLanguagePreference,
  type InterfaceLanguageStorage,
} from './interfaceLanguage';

class MemoryStorage implements InterfaceLanguageStorage {
  readonly values = new Map<string, string>();
  writes = 0;
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.writes += 1; this.values.set(key, value); }
}

describe('interface language preference', () => {
  it('always renders Czech across account/device scopes and normalizes legacy English preferences', () => {
    const storage = new MemoryStorage();
    storage.values.set('dildovander.interface-language.v1.account.account-a', 'en');
    const preference = createInterfaceLanguagePreference(storage);
    expect(preference.language.value).toBe('cs');

    preference.setScope('account-a');
    expect(preference.language.value).toBe('cs');
    expect(preference.persistenceStatus.value).toBe('saved');
    expect(preference.setLanguage('en')).toBe(true);
    expect(preference.language.value).toBe('cs');
    expect(storage.values.get('dildovander.interface-language.v1.account.account-a')).toBe('cs');

    preference.setScope('account-b');
    expect(preference.language.value).toBe('cs');
    preference.setScope(null);
    expect(preference.language.value).toBe('cs');
    const restarted = createInterfaceLanguagePreference(storage);
    restarted.setScope('account-a');
    expect(restarted.language.value).toBe('cs');
  });

  it('falls back to Czech for malformed or unsupported stored values', () => {
    const storage = new MemoryStorage();
    storage.values.set('dildovander.interface-language.v1.account.account-a', 'fr');
    const preference = createInterfaceLanguagePreference(storage);
    preference.setScope('account-a');
    expect(preference.language.value).toBe('cs');
    expect(preference.persistenceStatus.value).toBe('unverified');
    expect(preference.setLanguage('de')).toBe(false);
  });

  it('remains Czech when browser storage is denied or throws', () => {
    const deniedStorage: InterfaceLanguageStorage = {
      getItem() { throw new Error('private browsing denied'); },
      setItem() { throw new Error('private browsing denied'); },
    };
    const preference = createInterfaceLanguagePreference(deniedStorage);
    expect(preference.language.value).toBe('cs');
    expect(preference.persistenceStatus.value).toBe('unavailable');
    expect(preference.setLanguage('en')).toBe(true);
    expect(preference.language.value).toBe('cs');
    expect(preference.persistenceStatus.value).toBe('unavailable');
  });

  it('uses Czech when accessing localStorage itself throws', () => {
    const deniedWindow = Object.defineProperty({}, 'localStorage', {
      configurable: true,
      get() { throw new Error('private browsing denied'); },
    });
    vi.stubGlobal('window', deniedWindow);
    try {
      const preference = createInterfaceLanguagePreference();
      expect(preference.language.value).toBe('cs');
      expect(preference.persistenceStatus.value).toBe('unavailable');
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
