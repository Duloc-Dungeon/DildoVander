import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import CustomerJourneyCard from './CustomerJourneyCard.vue';
import type { CustomerJourneyInput } from './customerJourney';

const customer = { id: 'customer', displayName: 'Host', roles: ['Customer'] };

beforeEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
});

async function render(journey: CustomerJourneyInput, preferGame = false) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }],
  });
  await router.push('/');
  return mount(CustomerJourneyCard, { props: { journey, preferGame }, global: { plugins: [router] } });
}

describe('customer next-action guidance', () => {
  it('offers one character action for a new customer and keeps account help visible', async () => {
    const wrapper = await render({ account: customer, profile: null });
    expect(wrapper.get('.aw-button--primary').attributes('href')).toBe('/me/character');
    expect(wrapper.text()).toContain('bez přípravy pomocí AI');
    expect(wrapper.get('a[href="/me/account"]').text()).toContain('pomoc');
    wrapper.unmount();
  });

  it('announces failed verification and links to recovery without offering a start', async () => {
    const wrapper = await render({ account: customer, profile: { status: 'ready' }, error: true });
    expect(wrapper.get('[role="status"]').text()).toContain('není ověřený');
    expect(wrapper.get('.aw-button--primary').attributes('href')).toBe('/me/character');
    expect(wrapper.text()).not.toContain('Postava je uložená.');
    expect(wrapper.find('button').exists()).toBe(false);
    wrapper.unmount();
  });

  it('returns to an unresolved session despite a missing profile and explains reachability', async () => {
    const wrapper = await render({ account: customer, profile: null, sessionState: 'recovering' });
    expect(wrapper.get('.aw-button--primary').text()).toBe('Otevřít dílnu');
    expect(wrapper.text()).toContain('přímo v aplikaci');
    wrapper.unmount();
  });

  it('sends a ready character into the game while retaining conversation consent there', async () => {
    const wrapper = await render({ account: customer, profile: { status: 'saved' } }, true);
    expect(wrapper.get('.aw-button--primary').attributes('href')).toBe('/play');
    expect(wrapper.get('.aw-button--primary').text()).toBe('Přejít do hry');
    expect(wrapper.text()).toContain('Postava je připravená. Můžeš pokračovat do hry.');
    wrapper.unmount();
  });

  it('reactively localizes an active unknown session without changing its route or action', async () => {
    const wrapper = await render({ account: customer, profile: null, sessionState: 'future-active-state' });
    const action = wrapper.get('.aw-button--primary');
    expect(action.text()).toBe('Otevřít dílnu');
    expect(action.attributes('href')).toBe('/play/wand');

    interfaceLanguagePreference.setLanguage('en');
    await wrapper.vm.$nextTick();

    expect(wrapper.text()).toContain('Postava, hra a tvoje památky');
    expect(wrapper.text()).toContain('Kufrovu dílnu najdeš přímo v aplikaci.');
    expect(action.text()).toBe('Otevřít dílnu');
    expect(action.attributes('href')).toBe('/play/wand');
    wrapper.unmount();
  });
});
