import { mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { nextTick } from 'vue';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { launchAllowed } from '../../application/launchAccess';
import PrivacyView from './PrivacyView.vue';
import SupportView from './SupportView.vue';

const global = {
  stubs: {
    RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
  },
};

beforeEach(() => {
  launchAllowed.value = true;
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
});

afterEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
});

describe('guest information pages', () => {
  it('localizes support while preserving contact and route destinations', async () => {
    const wrapper = mount(SupportView, { global });
    expect(wrapper.get('h1').text()).toBe('S čím potřebujete pomoct?');
    expect(wrapper.text()).toContain('Přístup k účtu sám o sobě neuděluje oprávnění');
    expect(wrapper.get('a[href^="mailto:"]').attributes('href')).toBe('mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20support');
    expect(wrapper.get('a[href^="mailto:"]').text()).toBe('support@dildovander.de');

    interfaceLanguagePreference.setLanguage('en');
    await nextTick();
    expect(wrapper.get('h1').text()).toBe('S čím potřebujete pomoct?');
    expect(wrapper.text()).toContain('Přístup k účtu sám o sobě neuděluje oprávnění');
    expect(wrapper.get('a[href="/account-deletion"]').text()).toBe('Stav smazání');
    expect(wrapper.get('a[href="/privacy"]').text()).toBe('Jak aplikace zpracovává a uchovává data');
    expect(wrapper.get('a[href^="mailto:"]').attributes('href')).toBe('mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20support');
    expect(wrapper.get('a[href^="mailto:"]').text()).toBe('support@dildovander.de');
    wrapper.unmount();
  });

  it('localizes privacy disclosures without changing their consent, retention, or deletion boundaries', async () => {
    const wrapper = mount(PrivacyView, { global });
    expect(wrapper.get('h1').text()).toBe('Ochrana soukromí');
    expect(wrapper.text()).not.toContain('OpenAI');
    expect(wrapper.text()).toContain('potvrzuješ při registraci');
    expect(wrapper.text()).toContain('nesmaže cizí památku');
    expect(wrapper.text()).toContain('24 hodin nebo 5 000 záznamů');
    expect(wrapper.get('a[href^="mailto:"]').attributes('href')).toBe('mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20privacy');
    expect(wrapper.get('a[href^="mailto:"]').text()).toBe('support@dildovander.de');

    interfaceLanguagePreference.setLanguage('en');
    await nextTick();
    expect(wrapper.get('h1').text()).toBe('Ochrana soukromí');
    expect(wrapper.text()).not.toContain('OpenAI');
    expect(wrapper.text()).toContain('potvrzuješ při registraci');
    expect(wrapper.text()).toContain('nesmaže cizí památku');
    expect(wrapper.text()).toContain('24 hodin nebo 5 000 záznamů');
    expect(wrapper.text()).toContain('potvrzení místního dokončení není potvrzením jejich odstranění');
    expect(wrapper.get('a[href="https://openai.com/policies/privacy-policy/"]').attributes('target')).toBe('_blank');
    expect(wrapper.get('a[href="/support"]').text()).toBe('Podpora a bezpečné odeslání žádosti');
    expect(wrapper.get('a[href^="mailto:"]').attributes('href')).toBe('mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20privacy');
    expect(wrapper.get('a[href^="mailto:"]').text()).toBe('support@dildovander.de');
    wrapper.unmount();
  });
});
