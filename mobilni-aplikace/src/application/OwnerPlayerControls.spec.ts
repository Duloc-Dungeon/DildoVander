import { mount, flushPromises } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../features/identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref(null), accountRequest: api.request };
});
import { account } from '../features/identity/session';
import OwnerPlayerControls from './OwnerPlayerControls.vue';
import { isCustomerAccount, canEnterStaffArea } from './access';
import { setOwnerPlayer } from './ownerMode';

const groups = [{ id: 'friday', name: 'Pátek' }, { id: 'saturday', name: 'Sobota' }];
beforeEach(() => {
  setOwnerPlayer(null, false);
  account.value = { id: 'owner', displayName: 'Owner', roles: ['Owner'] } as never;
  setOwnerPlayer(account.value, true);
  api.request.mockReset();
  api.request.mockResolvedValue({ groups, groupId: 'friday', accessRevision: 3 });
});
async function render() {
  const wrapper = mount(OwnerPlayerControls);
  await flushPromises();
  return { wrapper };
}

describe('owner player mode', () => {
  it('keeps player display mode separate from server roles and staff authorization', async () => {
    const { wrapper } = await render();
    expect(isCustomerAccount(account.value)).toBe(true);
    expect(canEnterStaffArea(account.value)).toBe(true);
    expect(account.value?.roles).toEqual(['Owner']);
    expect(wrapper.find('button').exists()).toBe(false);
    expect(wrapper.text()).toContain('Skupina hráčů');
  });

  it('switches the selected server context with revision protection and invalidates player views', async () => {
    setOwnerPlayer(account.value, true);
    const { wrapper } = await render();
    api.request.mockResolvedValueOnce({ groups, groupId: 'saturday', accessRevision: 4 });
    await wrapper.get('select').setValue('saturday');
    await flushPromises();
    expect(api.request).toHaveBeenLastCalledWith('/app/v1/customer/groups/active', 'PUT', { groupId: 'saturday', expectedRevision: 3 });
    expect(wrapper.emitted('contextChanged')).toHaveLength(1);
    expect(wrapper.get('select').element.value).toBe('saturday');
  });

  it('retains the old group after a failed switch', async () => {
    setOwnerPlayer(account.value, true);
    const { wrapper } = await render();
    api.request.mockRejectedValueOnce(new Error('conflict'));
    await wrapper.get('select').setValue('saturday');
    await flushPromises();
    expect(wrapper.get('select').element.value).toBe('friday');
    expect(wrapper.emitted('contextChanged')).toBeUndefined();
    expect(wrapper.text()).toContain('Skupinu se nepodařilo přepnout');
  });

  it('does not expose the controls or request group access for other accounts', async () => {
    account.value = { id: 'customer', displayName: 'Guest', roles: ['Customer'] } as never;
    const { wrapper } = await render();
    expect(wrapper.find('section').exists()).toBe(false);
    expect(api.request).not.toHaveBeenCalled();
    setOwnerPlayer(account.value, true);
    expect(isCustomerAccount(account.value)).toBe(true);
    account.value = { id: 'other-owner', displayName: 'Other', roles: ['Owner'] } as never;
    expect(isCustomerAccount(account.value)).toBe(false);
  });
});
