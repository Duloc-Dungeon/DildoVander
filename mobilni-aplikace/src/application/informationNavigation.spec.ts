import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { createRouter, createWebHistory, type RouterHistory } from 'vue-router';
import { account } from '../features/identity/session';
import InformationBackLink from '../features/information/InformationBackLink.vue';
import { informationReturnPath, installInformationNavigation } from './informationNavigation';

let history: RouterHistory;
function createInformationRouter() {
  history = createWebHistory();
  const router = createRouter({ history, routes: ['/signin', '/launch', '/play', '/me/account', '/privacy', '/support'].map(path => ({ path, component: { template: '<div />' } })) });
  installInformationNavigation(router);
  return router;
}
beforeEach(() => { window.history.replaceState({}, '', '/'); account.value = null; });
afterEach(() => { history?.destroy(); account.value = null; window.history.replaceState({}, '', '/'); });

describe('information page return navigation', () => {
  it.each(['/signin?return=/me/account', '/launch', '/me/account'])('preserves %s across support, privacy and a reload', async source => {
    let router = createInformationRouter();
    await router.push(source);
    await router.push('/support');
    await router.push('/privacy');
    expect(history.state.informationReturn).toBe(source);
    history.destroy();
    router = createInformationRouter();
    await router.push('/privacy');
    const wrapper = mount(InformationBackLink, { global: { plugins: [router] } });
    expect(wrapper.get('a').attributes('href')).toBe(source);
    await wrapper.get('a').trigger('click');
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(router.currentRoute.value.fullPath).toBe(source);
    wrapper.unmount();
  });

  it.each([false, true])('offers a safe destination on a direct visit (signed in=%s)', async signedIn => {
    if (signedIn) account.value = { id: 'synthetic', displayName: 'Host', roles: ['Customer'] };
    const router = createInformationRouter();
    await router.push('/support');
    const wrapper = mount(InformationBackLink, { global: { plugins: [router] } });
    expect(wrapper.get('a').attributes('href')).toBe(signedIn ? '/me/account' : '/signin');
    wrapper.unmount();
  });

  it.each(['//example.com', 'https://example.com', '/\\example.com', '/privacy', '/support', '/unknown', '/signin\n'])('rejects unsafe or looping return destinations: %s', value => {
    expect(informationReturnPath(value)).toBeUndefined();
  });
});
