import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
vi.mock('../features/identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref(null) };
});
import { account } from '../features/identity/session';
import EveningSetupLinks from './EveningSetupLinks.vue';

afterEach(() => { account.value = null; });
describe('evening setup entry points', () => {
  it.each(['Owner', 'Administrator'])('offers the complete evening path to %s', role => {
    account.value = { id: 'host', displayName: 'Hostitel', roles: [role] };
    const wrapper = mount(EveningSetupLinks, { global: { stubs: { RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' } } } });
    expect(wrapper.findAll('a').map(link => link.attributes('href'))).toEqual([
      '/settings/admin/launch#waiting-room', ...(role === 'Owner' ? ['/settings/admin#invitations'] : []), '/play',
    ]);
    expect(wrapper.text()).not.toContain('Kufr');
    wrapper.unmount();
  });
  it.each([null, 'Operator', 'Viewer', 'Customer', 'Tester'])('does not offer administration to %s', role => {
    account.value = role ? { id: 'guest', displayName: 'Host', roles: [role] } : null;
    const wrapper = mount(EveningSetupLinks);
    expect(wrapper.find('section').exists()).toBe(false);
    wrapper.unmount();
  });
});
