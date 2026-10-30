import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { createPinia } from 'pinia';
import { createMemoryHistory, createRouter } from 'vue-router';
import { ref, type Ref } from 'vue';
import { Capacitor } from '@capacitor/core';
import AwButton from '../src/components/AwButton.vue';
import AwField from '../src/components/AwField.vue';
import App from '../src/application/App.vue';
import { safeReturnPath } from '../src/application/navigation';
import { deviceSession } from '../src/features/suitcase/deviceSession';
import { nativeDebug } from '../src/features/suitcase/nativeDebug';
import { account, accountDeletionRequested, accountRequest, offlineSession, offlineSessionExpiresAt, refreshIdentity } from '../src/features/identity/session';
import { installAccessGuard } from '../src/application/accessGuard';
import type { DeviceSnapshot } from '../src/features/device_connection/FakeDeviceConnection';

vi.mock('../src/features/identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref(null), accountRequest: vi.fn(async () => ({ allowed: true })), accountDeletionRequested: ref(false), offlineSession: ref(false), offlineSessionExpiresAt: ref(0), refreshIdentity: vi.fn(async () => {}), authenticationGenerationSnapshot: () => 0, hasRole: () => false };
});

const wrappers: Array<ReturnType<typeof mount>> = [];
const mockDeletionRequested = accountDeletionRequested as Ref<boolean>;
beforeEach(() => { account.value = null; mockDeletionRequested.value = false; offlineSession.value = false; offlineSessionExpiresAt.value = 0; deviceSession.snapshot.value = null; deviceSession.target.value = 'unavailable'; });
afterEach(() => { for (const wrapper of wrappers.splice(0)) wrapper.unmount(); document.body.innerHTML = ''; vi.restoreAllMocks(); });
function activeSnapshot(): DeviceSnapshot {
  return { source: 'e2-simulator', controlMode: 'manual', phase: 'ready', readyMask: 63,
    activeCompartment: null, busy: false, revision: 1, status: 'Označený syntetický stav',
    conversation: { canSubmit: true, canCancel: false, status: '', pending: false } as DeviceSnapshot['conversation'] };
}

describe('application boundaries', () => {
  it.each([false, true])('restores the shell for another account without reloading the document (signin return: %s)', async viaSignIn => {
    const policyReads: string[] = [];
    vi.mocked(accountRequest).mockImplementation(async path => {
      policyReads.push(`${account.value?.id}:${path}`);
      return (path === '/app/v1/launch' ? { allowed: true } : { state: 'active', accessRevision: 1 }) as never;
    });
    const router = createRouter({ history: createMemoryHistory(), routes: [
      ...['/', '/signin', '/launch', '/play', '/play/wand', '/me/keepsakes', '/access-revoked'].map(path => ({ path, component: { template: '<p>Stránka</p>' } })),
      { path: '/me/account', component: { template: '<p>Můj účet</p>' }, meta: { requiresAccount: true } },
    ] });
    account.value = { id: 'previous', displayName: 'Předchozí host', roles: ['Customer'], openAiConsent: true };
    await router.push('/me/account');
    const wrapper = mount(App, { global: { plugins: [router, createPinia()] } }); wrappers.push(wrapper);
    try {
      await flushPromises();
      expect(wrapper.find('.wns-game-navigation').exists()).toBe(true);
      if (viaSignIn) {
        account.value = null;
        await flushPromises();
        expect(router.currentRoute.value.path).toBe('/signin');
      }
      account.value = { id: 'next', displayName: 'Nový host', roles: ['Customer'], openAiConsent: true };
      if (viaSignIn) await router.replace('/me/account');
      await flushPromises();
      expect(router.currentRoute.value.path).toBe('/me/account');
      expect(wrapper.find('.wns-game-navigation').exists()).toBe(true);
      expect(wrapper.find('.account-link').text()).toBe('Nový host');
      expect(wrapper.text()).not.toContain('Předchozí host');
      expect(policyReads).toContain('next:/app/v1/customer/access');
      expect(policyReads).toContain('next:/app/v1/launch');
    } finally { vi.mocked(accountRequest).mockResolvedValue({ allowed: true }); }
  });

  it.each([
    { allowed: false, membership: 'active' },
    { allowed: true, membership: 'revoked' },
    { allowed: true, membership: null },
  ])('does not reuse the previous shell when the next account has $membership membership and launch=$allowed', async policy => {
    vi.mocked(accountRequest).mockImplementation(async path => {
      const previous = account.value?.id === 'previous';
      return (path === '/app/v1/launch'
        ? { allowed: previous || policy.allowed }
        : { state: previous ? 'active' : policy.membership, accessRevision: 1 }) as never;
    });
    const router = createRouter({ history: createMemoryHistory(), routes: [
      ...['/', '/signin', '/launch', '/play', '/play/wand', '/me/keepsakes', '/access-revoked'].map(path => ({ path, component: { template: '<p>Stránka</p>' } })),
      { path: '/me/account', component: { template: '<p>Můj účet</p>' }, meta: { requiresAccount: true } },
    ] });
    account.value = { id: 'previous', displayName: 'Předchozí host', roles: ['Customer'], openAiConsent: true };
    await router.push('/me/account');
    const wrapper = mount(App, { global: { plugins: [router, createPinia()] } }); wrappers.push(wrapper);
    try {
      await flushPromises();
      expect(wrapper.find('.wns-game-navigation').exists()).toBe(true);
      account.value = { id: 'next', displayName: 'Nový host', roles: ['Customer'], openAiConsent: true };
      await flushPromises();
      expect(wrapper.find('.wns-game-navigation').exists()).toBe(false);
      expect(wrapper.find('.account-link').exists()).toBe(false);
      expect(wrapper.text()).not.toContain('Předchozí host');
      expect(router.currentRoute.value.path).toBe('/me/account');
    } finally { vi.mocked(accountRequest).mockResolvedValue({ allowed: true }); }
  });

  it.each([
    { deletion: true, destination: '/account-deletion' },
    { deletion: false, destination: '/signin' },
  ])('starts local privacy cleanup before routing account loss to $destination', async ({ deletion, destination }) => {
    const events: string[] = [];
    const publicView = { setup() { events.push('public-view'); }, template: '<p>Anonymous view</p>' };
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/', component: { template: '<p>Landing</p>' } },
      { path: '/signin', component: publicView },
      { path: '/account-deletion', component: publicView },
      { path: '/suitcase', component: { template: '<input aria-label="Private draft" />' }, meta: { requiresAccount: true, operation: true } },
    ] });
    account.value = { id: 'owner', displayName: 'Owner', roles: ['Owner'] };
    await router.push('/suitcase');
    let finishCleanup!: () => void;
    const cleanup = new Promise<void>(resolve => { finishCleanup = resolve; });
    vi.spyOn(nativeDebug, 'invalidatePrivateState').mockImplementation(() => { events.push('redact'); });
    vi.spyOn(deviceSession, 'clearUserGrants').mockImplementation(() => { events.push('cleanup-start'); return cleanup; });
    const wrapper = mount(App, { global: { plugins: [router, createPinia()] } }); wrappers.push(wrapper);
    try {
      mockDeletionRequested.value = deletion;
      account.value = null;
      expect(events).toEqual(['redact', 'cleanup-start']);
      // A subsequent offline-state change must not replace the deletion receipt
      // with the ordinary sign-in redirect while navigation is settling.
      offlineSession.value = true;
      await flushPromises();
      expect(router.currentRoute.value.path).toBe(destination);
      expect(wrapper.find('main input').exists()).toBe(false);
      expect(events).toEqual(['redact', 'cleanup-start', 'public-view']);
      expect(router.currentRoute.value.query.return).toBe(deletion ? undefined : '/suitcase');
    } finally { finishCleanup(); await cleanup; }
  });

  it('removes cloud views on offline transition without retiring the paired local session', async () => {
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/', component: { template: '<p>Landing</p>' } },
      { path: '/suitcase', component: { template: '<p>Local controls</p>' }, meta: { requiresAccount: true, operation: true, offlineLocal: true } },
      { path: '/suitcase/director', component: { template: '<input aria-label="Cloud draft" />' }, meta: { requiresAccount: true, operation: true } },
    ] });
    installAccessGuard(router);
    account.value = { id: 'owner', displayName: 'Owner', roles: ['Owner'] };
    await router.push('/suitcase/director');
    const cleanup = vi.spyOn(deviceSession, 'clearUserGrants');
    const wrapper = mount(App, { global: { plugins: [router, createPinia()] } }); wrappers.push(wrapper);
    expect(wrapper.find('main input').exists()).toBe(true);
    offlineSessionExpiresAt.value = Date.now() + 60000;
    offlineSession.value = true;
    await flushPromises();
    expect(wrapper.find('main input').exists()).toBe(false);
    expect(router.currentRoute.value.query.access).toBe('online');
    expect(cleanup).not.toHaveBeenCalled();
    vi.mocked(refreshIdentity).mockClear();
    await router.push('/suitcase'); await flushPromises();
    expect(wrapper.text()).toContain('Local controls');
    expect(refreshIdentity).not.toHaveBeenCalled();
  });
  it('unmounts cached operations on logout and discards their drafts before another account enters', async () => {
    let mounts = 0;
    const operation = { setup() { ++mounts; return { draft: ref('') }; }, template: '<input aria-label="Private draft" v-model="draft" />' };
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/', component: { template: '<p>Public landing</p>' } },
      { path: '/signin', component: { template: '<p>Sign in</p>' } },
      { path: '/suitcase', name: 'private-operation', component: operation, meta: { keepAlive: true, requiresAccount: true, operation: true } },
    ] });
    installAccessGuard(router);
    account.value = { id: 'first', displayName: 'First', roles: ['Operator'] };
    await router.push('/suitcase');
    const wrapper = mount(App, { global: { plugins: [router, createPinia()] } });
    wrappers.push(wrapper);
    await wrapper.get('main input').setValue('Private first-account draft');
    account.value = null;
    await flushPromises();
    expect(wrapper.find('main input').exists()).toBe(false);
    expect(router.currentRoute.value.path).toBe('/signin');
    account.value = { id: 'second', displayName: 'Second', roles: ['Operator'] };
    await router.push('/suitcase'); await flushPromises();
    expect((wrapper.get('main input').element as HTMLInputElement).value).toBe('');
    expect(mounts).toBe(2);
    account.value = { ...account.value, roles: ['Viewer'] };
    await flushPromises();
    expect(wrapper.find('main input').exists()).toBe(false);
    expect(router.currentRoute.value.query.access).toBe('operator');
  });
  it.each([false, true])('keeps browser-only install navigation out of native context (native=%s)', async native => {
    vi.spyOn(Capacitor, 'isNativePlatform').mockReturnValue(native);
    account.value = { id: 'operator', displayName: 'Operator', roles: ['Operator'] };
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<p>Current operation</p>' } }] });
    await router.push('/'); await router.isReady();
    const wrapper = mount(App, { global: { plugins: [router, createPinia()] } });
    wrappers.push(wrapper);
    await flushPromises();
    expect(wrapper.find('.application-install-link').exists()).toBe(!native);
    expect(wrapper.get('main').text()).toBe('Current operation');
  });
  it('keeps the fixed customer shell free of appearance controls without replacing the route, draft or focus', async () => {
    let mounts = 0;
    const editor = {
      setup() { mounts += 1; return { draft: ref('') }; },
      template: '<label>Rozepsaná poznámka<input v-model="draft" /></label>',
    };
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: editor }] });
    await router.push('/'); await router.isReady();
    account.value = { id: 'operator', displayName: 'Operator', roles: ['Operator'] };
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [router, createPinia()] } });
    wrappers.push(wrapper);
    const input = wrapper.get('main input');
    await input.setValue('Poznámka zůstává');
    (input.element as HTMLInputElement).focus();
    expect(wrapper.find('select').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('Preferences');
    expect(wrapper.get('main input').element).toBe(input.element);
    expect((input.element as HTMLInputElement).value).toBe('Poznámka zůstává');
    expect(document.activeElement).toBe(input.element);
    expect(mounts).toBe(1);
  });
  it('rejects unsafe login return locations', () => {
    for (const value of ['//evil.test', 'https://evil.test', '/\\evil.test', '/signin', '/guest/v1', '/settings\nadmin']) expect(safeReturnPath(value)).toBe('/');
    expect(safeReturnPath('/play/wand?part=tray')).toBe('/play/wand?part=tray');
    expect(safeReturnPath('/settings/admin')).toBe('/settings/admin');
  });
  it('uses native form and accessible field semantics', async () => {
    const button = mount(AwButton, { props: { busy: true, variant: 'primary' }, slots: { default: 'Uložit' } });
    wrappers.push(button);
    expect(button.attributes('type')).toBe('button'); expect(button.attributes('disabled')).toBeDefined(); expect(button.attributes('aria-busy')).toBe('true');
    const field = mount(AwField, { props: { id: 'name', label: 'Jméno', hint: 'Vaše jméno', error: 'Povinné pole' },
      slots: { default: '<input id="name" aria-describedby="name-hint name-error" aria-invalid="true">' } });
    wrappers.push(field); expect(field.get('label').attributes('for')).toBe('name'); expect(field.get('#name-error').attributes('role')).toBe('alert');
  });
  it('keeps the shell and urgent Stop available during navigation and pending work', async () => {
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/', component: { template: '<h1>Domov</h1>' } },
      { path: '/me/keepsakes', component: { template: '<h1>Moje památky</h1>' } },
    ] });
    await router.push('/'); await router.isReady();
    account.value = { id: 'operator', displayName: 'Operator', roles: ['Operator'] };
    deviceSession.snapshot.value = activeSnapshot(); deviceSession.pending.value = true;
    const stop = vi.spyOn(deviceSession, 'stop').mockImplementation(() => {});
    nativeDebug.pending.value = false;
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [router, createPinia()] } }); wrappers.push(wrapper);
    const header = wrapper.get('header').element;
    const button = wrapper.findAll('button').find(button => button.text() === 'Stop')!; expect(button.attributes('disabled')).toBeUndefined();
    await button.trigger('click'); expect(stop).toHaveBeenCalledTimes(1);
    await router.push('/me/keepsakes'); await flushPromises(); expect(wrapper.get('header').element).toBe(header); expect(wrapper.get('main').text()).toContain('Moje památky');
    expect(wrapper.findAll('nav').some(nav => nav.attributes('aria-label') === 'Sekce aplikace')).toBe(true);
    const sessionRefresh = vi.mocked(refreshIdentity); sessionRefresh.mockClear();
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(sessionRefresh).toHaveBeenCalledTimes(1);
    wrapper.unmount(); wrappers.splice(wrappers.indexOf(wrapper), 1);
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(sessionRefresh).toHaveBeenCalledTimes(1);
    deviceSession.pending.value = false;
  });
});
