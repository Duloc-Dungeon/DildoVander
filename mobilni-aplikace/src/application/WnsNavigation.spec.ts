import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import WnsNavigation from './WnsNavigation.vue';

describe('WNS customer navigation', () => {
  it.each(['/play', '/play/challenge', '/play/teams', '/me/character', '/me/keepsakes', '/me/account', '/play/wand'])('selects the current section on %s with the same four destinations', async path => {
    const router = createRouter({ history: createMemoryHistory(), routes: ['/play', '/play/challenge', '/play/teams', '/me/character', '/me/keepsakes', '/me/account', '/play/wand'].map(path => ({ path, component: { template: '<div />' } })) });
    await router.push(path);
    const wrapper = mount(WnsNavigation, { global: { plugins: [router] } });
    expect(wrapper.findAll('a').map(link => [link.text(), link.attributes('href')])).toEqual([
      ['Dílna', '/play/wand'], ['Hry', '/play'], ['Památky', '/me/keepsakes'], ['Já', '/me/account'],
    ]);
    expect(wrapper.findAll('[aria-current="page"]')).toHaveLength(1);
    expect(wrapper.get('[aria-current="page"]').attributes('href')).toBe(path === '/play/wand' || path === '/me/character' ? '/play/wand' : path.startsWith('/play/') ? '/play' : path);
    expect(wrapper.findAll('img')).toHaveLength(4);
    expect(wrapper.text()).not.toContain('kufr');
    wrapper.unmount();
  });
  it('keeps archived duel details in the keepsakes section', async () => {
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/archive/duel', component: { template: '<div />' } },
    ] });
    await router.push('/archive/duel?id=synthetic');
    const wrapper = mount(WnsNavigation, { global: { plugins: [router] } });
    expect(wrapper.findAll('[aria-current="page"]')).toHaveLength(1);
    expect(wrapper.get('[aria-current="page"]').text()).toBe('Památky');
    wrapper.unmount();
  });
});
