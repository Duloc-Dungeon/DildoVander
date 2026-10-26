import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import InterfaceLanguageSelect from './InterfaceLanguageSelect.vue';
import { createInterfaceLanguagePreference, type InterfaceLanguageStorage } from './interfaceLanguage';

class MemoryStorage implements InterfaceLanguageStorage {
  private readonly values = new Map<string, string>();
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
}

describe('InterfaceLanguageSelect', () => {
  it('shows Czech as the only interface language and scopes its persistence hint', async () => {
    const storage = new MemoryStorage();
    storage.setItem('dildovander.interface-language.v1.account.account-a', 'en');
    const preference = createInterfaceLanguagePreference(storage);
    const wrapper = mount(InterfaceLanguageSelect, { props: { preference, accountId: 'account-a' } });

    expect(wrapper.text()).toContain('Jazyk rozhraní');
    expect(wrapper.text()).toContain('Čeština');
    expect(wrapper.text()).not.toContain('English');
    expect(wrapper.find('select').exists()).toBe(false);
    expect(wrapper.get('.interface-language-select__value').attributes('aria-describedby'))
      .toBe(wrapper.get('small').attributes('id'));
    expect(preference.language.value).toBe('cs');

    await wrapper.setProps({ accountId: 'account-b' });
    expect(preference.language.value).toBe('cs');
    expect(wrapper.get('small').text()).toContain('Pro tento účet');
    wrapper.unmount();
  });

  it('keeps the Czech indicator usable when storage is unavailable', () => {
    const blockedStorage: InterfaceLanguageStorage = {
      getItem() { throw new Error('storage denied'); },
      setItem() { throw new Error('storage denied'); },
    };
    const preference = createInterfaceLanguagePreference(blockedStorage);
    const wrapper = mount(InterfaceLanguageSelect, { props: { preference, accountId: 'account-a' } });

    expect(wrapper.get('.interface-language-select__value').text()).toBe('Čeština');
    expect(wrapper.get('small').text()).toContain('jen v tomto spuštění');
    wrapper.unmount();
  });
});
