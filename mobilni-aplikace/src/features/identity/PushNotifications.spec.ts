import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { nextTick } from 'vue';
const api = vi.hoisted(() => ({ request: vi.fn(), permission: vi.fn(), readPermission: vi.fn(), register: vi.fn(), unregister: vi.fn(), supported: true, setAccount: (_id: string | null) => {} }));
vi.mock('./session', async () => {
  const { ref } = await import('vue');
  const account = ref<{ id: string } | null>({ id: 'first' });
  api.setAccount = id => { account.value = id ? { id } : null; };
  return { account, accountDeletionRequested: ref(false), accountRequest: api.request };
});
vi.mock('../../platform/pushNotifications', () => ({
  supportsPush: () => api.supported, pushPlatform: () => 'web',
  nativePushConfigured: async () => true,
  readPushPermission: api.readPermission, requestPushPermission: api.permission,
  registerPush: api.register, unregisterLocalPush: api.unregister,
}));
import PushNotifications from './PushNotifications.vue';

beforeEach(() => {
  api.setAccount('first'); api.supported = true;
  sessionStorage.clear();
  api.request.mockReset().mockResolvedValue({ web: true, android: false, ios: false, publicKey: 'key', enabled: false });
  api.permission.mockReset().mockResolvedValue(true);
  api.readPermission.mockReset().mockResolvedValue('granted');
  api.register.mockReset().mockResolvedValue({ platform: 'web', endpoint: 'https://push.example' });
  api.unregister.mockReset().mockResolvedValue(undefined);
});

describe('contextual notification offer', () => {
  it('shows the waiting-room invitation without requesting OS permission on arrival', async () => {
    api.readPermission.mockResolvedValue('prompt');
    const wrapper = mount(PushNotifications, { props: { context: 'waiting' } });
    await flushPromises();
    expect(wrapper.get('h2').text()).toContain('až večer začne');
    expect(api.permission).not.toHaveBeenCalled();
    await wrapper.findAll('button').find(button => button.text() === 'Zapnout oznámení')!.trigger('click');
    await flushPromises();
    expect(api.permission).toHaveBeenCalledOnce();
    expect(api.register).toHaveBeenCalledOnce();
    expect(api.request).toHaveBeenCalledWith('/app/v1/push', 'PUT', expect.any(Object), expect.any(AbortSignal));
    expect(wrapper.find('section').exists()).toBe(false);
    wrapper.unmount();
  });

  it.each(['waiting', 'wand', 'game'] as const)('keeps Later across %s entry and reload, scoped to the account', async context => {
    const wrapper = mount(PushNotifications, { props: { context } });
    await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Později')!.trigger('click');
    expect(wrapper.find('section').exists()).toBe(false);
    wrapper.unmount();
    const returned = mount(PushNotifications, { props: { context } });
    await flushPromises();
    expect(returned.find('section').exists()).toBe(false);
    api.setAccount('second'); await flushPromises();
    expect(returned.find('section').exists()).toBe(true);
    expect(api.permission).not.toHaveBeenCalled();
    returned.unmount();
  });

  it('keeps Later effective when storage writes fail but reads still work', async () => {
    api.setAccount('write-denied-account');
    const blockedWrite = vi.spyOn(sessionStorage, 'setItem').mockImplementation(() => { throw new Error('quota'); });
    const wrapper = mount(PushNotifications, { props: { context: 'waiting' } });
    await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Později')!.trigger('click');
    expect(wrapper.find('section').exists()).toBe(false);
    expect(sessionStorage.getItem('wns:push-prompt:write-denied-account')).toBeNull();
    wrapper.unmount();
    const returned = mount(PushNotifications, { props: { context: 'game' } });
    await flushPromises(); expect(returned.find('section').exists()).toBe(false);
    returned.unmount(); blockedWrite.mockRestore();
  });

  it.each(['denied', 'enabled', 'unsupported', 'unconfigured'])('does not offer again when %s', async scenario => {
    if (scenario === 'denied') api.readPermission.mockResolvedValue('denied');
    if (scenario === 'enabled') api.request.mockResolvedValue({ web: true, enabled: true });
    if (scenario === 'unsupported') api.supported = false;
    if (scenario === 'unconfigured') api.request.mockResolvedValue({ web: false, enabled: false });
    const wrapper = mount(PushNotifications, { props: { context: 'waiting' } });
    await flushPromises();
    expect(wrapper.find('section').exists()).toBe(false);
    expect(api.permission).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('explains refusal once and leaves Later instead of requesting permission repeatedly', async () => {
    api.readPermission.mockResolvedValue('prompt'); api.permission.mockResolvedValue(false);
    const wrapper = mount(PushNotifications, { props: { context: 'waiting' } });
    await flushPromises(); await wrapper.get('button').trigger('click'); await flushPromises();
    expect(wrapper.get('[role="alert"]').text()).toContain('nejsou povolená');
    expect(wrapper.findAll('button').map(button => button.text())).toEqual(['Později']);
    expect(api.register).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('refreshes an already-mounted settings panel after accepting the contextual offer', async () => {
    let enabled = false;
    api.request.mockImplementation(async (_path, method) => {
      if (method === 'PUT') enabled = true;
      return { web: true, enabled };
    });
    const host = mount({ components: { PushNotifications }, template: '<div><PushNotifications /><PushNotifications context="game" /></div>' });
    await flushPromises();
    const [settings, offer] = host.findAllComponents(PushNotifications);
    expect(settings!.get('h2').attributes('id')).not.toBe(offer!.get('h2').attributes('id'));
    await offer!.get('button').trigger('click'); await flushPromises();
    expect(settings!.get('button').text()).toBe('Vypnout oznámení');
    host.unmount();
  });

  it('replaces a slow stale settings read when another mounted offer enables notifications', async () => {
    let releaseOldRead!: (value: unknown) => void;
    let reads = 0; let enabled = false;
    api.request.mockImplementation(async (_path, method) => {
      if (method === 'PUT') { enabled = true; return {}; }
      if (++reads === 1) return new Promise(resolve => { releaseOldRead = resolve; });
      return { web: true, enabled };
    });
    const host = mount({ components: { PushNotifications }, template: '<div><PushNotifications /><PushNotifications context="game" /></div>' });
    await flushPromises();
    const [settings, offer] = host.findAllComponents(PushNotifications);
    await offer!.get('button').trigger('click'); await flushPromises();
    expect(settings!.get('button').text()).toBe('Vypnout oznámení');
    releaseOldRead({ web: true, enabled: false }); await flushPromises();
    expect(settings!.get('button').text()).toBe('Vypnout oznámení');
    expect(offer!.find('section').exists()).toBe(false);
    host.unmount();
  });

  it('does not register the contextual offer for a replacement account', async () => {
    let finish!: (allowed: boolean) => void;
    api.permission.mockImplementation(() => new Promise<boolean>(resolve => { finish = resolve; }));
    const wrapper = mount(PushNotifications, { props: { context: 'wand' } });
    await flushPromises(); await wrapper.get('button').trigger('click');
    api.setAccount('second'); await nextTick(); finish(true); await flushPromises();
    expect(api.register).not.toHaveBeenCalled();
    expect(api.request.mock.calls.some(call => call[1] === 'PUT')).toBe(false);
    wrapper.unmount();
  });
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
