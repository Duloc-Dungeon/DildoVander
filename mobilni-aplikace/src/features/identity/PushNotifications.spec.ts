import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { nextTick } from 'vue';
const api = vi.hoisted(() => ({ request: vi.fn(), permission: vi.fn(), register: vi.fn(), unregister: vi.fn(), supported: true, setAccount: (_id: string | null) => {} }));
vi.mock('./session', async () => {
  const { ref } = await import('vue');
  const account = ref<{ id: string } | null>({ id: 'first' });
  api.setAccount = id => { account.value = id ? { id } : null; };
  return { account, accountDeletionRequested: ref(false), accountRequest: api.request };
});
vi.mock('../../platform/pushNotifications', () => ({
  supportsPush: () => api.supported, pushPlatform: () => 'web',
  nativePushConfigured: async () => true,
  hasPushPermission: async () => true, requestPushPermission: api.permission,
  registerPush: api.register, unregisterLocalPush: api.unregister,
}));
import PushNotifications from './PushNotifications.vue';

beforeEach(() => {
  api.setAccount('first'); api.supported = true;
  api.request.mockReset().mockResolvedValue({ web: true, android: false, ios: false, publicKey: 'key', enabled: false });
  api.permission.mockReset().mockResolvedValue(true);
  api.register.mockReset().mockResolvedValue({ platform: 'web', endpoint: 'https://push.example' });
  api.unregister.mockReset().mockResolvedValue(undefined);
});

describe('push settings', () => {
  it('never requests notification permission until the user enables it', async () => {
    const wrapper = mount(PushNotifications);
    await flushPromises();
    expect(api.permission).not.toHaveBeenCalled();
    await wrapper.get('button').trigger('click'); await flushPromises();
    expect(api.permission).toHaveBeenCalledOnce();
    expect(api.request).toHaveBeenCalledWith('/app/v1/push', 'PUT', { platform: 'web', endpoint: 'https://push.example' }, expect.any(AbortSignal));
    expect(wrapper.text()).toContain('Oznámení jsou zapnutá.');
    wrapper.unmount();
  });

  it('explains denied permission and sends no subscription', async () => {
    api.permission.mockResolvedValue(false);
    const wrapper = mount(PushNotifications); await flushPromises();
    await wrapper.get('button').trigger('click'); await flushPromises();
    expect(wrapper.get('[role="alert"]').text()).toContain('nejsou povolená');
    expect(api.register).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('does not save a token when the user switches accounts while granting permission', async () => {
    let finish!: (allowed: boolean) => void;
    api.permission.mockImplementation(() => new Promise<boolean>(resolve => { finish = resolve; }));
    const wrapper = mount(PushNotifications); await flushPromises();
    await wrapper.get('button').trigger('click');
    api.setAccount('second'); await nextTick();
    finish(true); await flushPromises();
    expect(api.register).not.toHaveBeenCalled();
    expect(api.request.mock.calls.some(call => call[1] === 'PUT')).toBe(false);
    expect(wrapper.text()).not.toContain('zapnutá');
    wrapper.unmount();
  });

  it('revokes delivery on the server before removing the local subscription', async () => {
    api.request.mockResolvedValue({ web: true, enabled: true });
    const wrapper = mount(PushNotifications); await flushPromises();
    await wrapper.get('button').trigger('click'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/push', 'DELETE', undefined, expect.any(AbortSignal));
    expect(api.unregister).toHaveBeenCalledOnce();
    expect(api.request.mock.invocationCallOrder.at(-1)!).toBeLessThan(api.unregister.mock.invocationCallOrder[0]!);
    expect(wrapper.text()).toContain('Oznámení jsou vypnutá.');
    wrapper.unmount();
  });

  it('shows an honest unavailable state when push is not configured', async () => {
    api.request.mockResolvedValue({ web: false, enabled: false });
    const wrapper = mount(PushNotifications); await flushPromises();
    expect(wrapper.text()).toContain('zatím nejsou připravena');
    expect(wrapper.find('button').exists()).toBe(false);
    wrapper.unmount();
  });

  it('still allows revocation while the provider is unavailable', async () => {
    api.request.mockResolvedValue({ web: false, enabled: true });
    const wrapper = mount(PushNotifications); await flushPromises();
    expect(wrapper.text()).toContain('zatím nejsou připravena');
    expect(wrapper.findAll('button')).toHaveLength(1);
    await wrapper.get('button').trigger('click'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/push', 'DELETE', undefined, expect.any(AbortSignal));
    expect(api.permission).not.toHaveBeenCalled();
    wrapper.unmount();
  });
});
