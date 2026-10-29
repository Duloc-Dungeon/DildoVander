import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';

const api = vi.hoisted(() => ({ request: vi.fn(), disconnect: vi.fn(), discover: vi.fn(), connect: vi.fn(), start: vi.fn(), status: vi.fn(), end: vi.fn(), supported: true, setAccount: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref<{ id: string; displayName: string; roles: string[]; openAiConsent?: boolean }>({ id: 'customer-1', displayName: 'Guest', roles: ['Customer'], openAiConsent: true });
  api.setAccount.mockImplementation((value: typeof account.value) => { account.value = value; });
  return { account, accountDeletionRequested: ref(false), accountRequest: api.request, authenticatePasskey: vi.fn(), signOut: vi.fn() };
});
vi.mock('./customerBle', () => ({
  customerBle: {
    supported: () => api.supported,
    disconnect: api.disconnect,
    discover: api.discover,
    connect: api.connect,
    start: api.start,
    status: api.status,
    end: api.end,
  },
}));
import CustomerView from './CustomerView.vue';
vi.mock('../game/WandWorkshop.vue', () => ({ default: { name: 'WandWorkshop', emits: ['created', 'active-change'], template: '<div data-test="wand-workshop" />' } }));
vi.mock('./PrivateCharacterAvatar.vue', () => ({ default: { template: '<div data-test="character-portrait" />' } }));

const profile = { profileId: 'profile-1', revision: 1, userInput: { name: 'Luna', world: 'Moon Saga' }, status: 'ready' };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(complete => { resolve = complete; }); return { promise, resolve }; }
const router = async (path = '/me/character') => {
  const instance = createRouter({ history: createMemoryHistory(), routes: [{ path: '/me/character', component: CustomerView }, { path: '/play/wand', component: { template: '<div />' } }, { path: '/me/account', component: { template: '<div />' } }, { path: '/play', component: { template: '<div />' } }, { path: '/support', component: { template: '<div />' } }] });
  await instance.push(path); await instance.isReady(); return instance;
};

afterEach(() => { vi.clearAllMocks(); api.disconnect.mockResolvedValue(undefined); api.supported = true; api.setAccount({ id: 'customer-1', displayName: 'Guest', roles: ['Customer'], openAiConsent: true }); interfaceLanguagePreference.setLanguage('cs'); vi.useRealTimers(); });

describe('customer character and digital workshop', () => {
  it('shows only the current server-assigned wand on the profile and clears it when the account changes', async () => {
    const wand = { wandId: 'wand-1', physicalSlot: 2, name: 'Měsíční ozvěna', material: 'Tis', core: 'Vlákno', temperament: 'Klidná', lore: 'Příběh.', designDescription: 'Digitální kresba.', affinity: 'Světlo', weakness: 'Hluk',
      spells: ['strike', 'trick', 'shield'].map((action, index) => ({ action, name: `Kouzlo ${index}`, description: 'Popis kouzla.' })) };
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile, generatedWand: { ...wand, profileId: 'private', accountId: 'private' } } : { items: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } }); await flushPromises();
    expect(wrapper.text()).toContain('Měsíční ozvěna'); expect(wrapper.text()).toContain('nemění fyzickou rekvizitu'); expect(wrapper.text()).not.toContain('private');
    const pending = deferred<{ profile: typeof profile }>(); api.request.mockImplementation(() => pending.promise);
    api.setAccount({ id: 'another-customer', displayName: 'Other', roles: ['Customer'] });
    await flushPromises(); expect(wrapper.text()).not.toContain('Měsíční ozvěna');
    wrapper.unmount(); pending.resolve({ profile });
  });

  it('leads with identity and one workshop entry without mounting another screen', async () => {
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile } : { items: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } }); await flushPromises();
    expect(wrapper.get('.character-overview').text()).toContain('Luna');
    expect(wrapper.get('.character-next-step').attributes('href')).toBe('/play/wand');
    expect(wrapper.findAll('.character-next-step')).toHaveLength(1);
    expect(wrapper.findComponent({ name: 'WandWorkshop' }).exists()).toBe(false);
    expect(wrapper.get('#character-confirmation-title').element.compareDocumentPosition(wrapper.get('[data-test="character-portrait"]').element) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(wrapper.findAll<HTMLDetailsElement>('details.character-details').every(details => !details.element.open)).toBe(true);
    expect(api.request.mock.calls.every(call => call[0] === '/app/v1/customer/character')).toBe(true);
    wrapper.unmount();
  });

  it('updates the next step when preparation completes and clears it across account changes', async () => {
    vi.useFakeTimers();
    const wand = { wandId: 'wand-voice', physicalSlot: 2, name: 'Hlasová ozvěna', material: 'Tis', core: 'Vlákno', temperament: 'Klidná', lore: 'Příběh.', designDescription: 'Kresba.', affinity: 'Světlo', weakness: 'Hluk',
      spells: ['strike', 'trick', 'shield'].map(action => ({ action, name: action, description: 'Kouzlo.' })) };
    const poll = deferred<{ profile: typeof profile; generatedWand: typeof wand }>();
    let reads = 0;
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) return ++reads === 1 ? { profile: { ...profile, status: 'queued' } } : poll.promise;
      return { items: [] };
    });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } }); await flushPromises();
    expect(wrapper.text()).toContain('Postava se připravuje');
    expect(wrapper.find('.character-next-step').exists()).toBe(false);
    expect(wrapper.findComponent({ name: 'WandWorkshop' }).exists()).toBe(false);
    await vi.advanceTimersByTimeAsync(3000); await flushPromises();
    poll.resolve({ profile, generatedWand: wand }); await flushPromises();
    expect(wrapper.get('.character-next-step').attributes('href')).toBe('/play');
    expect(wrapper.findAll('.character-next-step')).toHaveLength(1);
    expect(wrapper.text()).toContain('Hlasová ozvěna');
    const accountLoad = deferred<{ profile: typeof profile | null }>();
    api.request.mockImplementation(() => accountLoad.promise);
    api.setAccount({ id: 'other-customer', displayName: 'Jiný host', roles: ['Customer'], openAiConsent: true }); await flushPromises();
    expect(wrapper.find('.character-next-step').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('Hlasová ozvěna');
    accountLoad.resolve({ profile: null }); await flushPromises();
    expect(wrapper.find('#character-name').exists()).toBe(true);
    wrapper.unmount();
  });

  it('offers preparation help for a failed profile without opening the workshop', async () => {
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile: { ...profile, status: 'failed' } } : { items: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } }); await flushPromises();
    expect(wrapper.get('.character-next-step').attributes('href')).toBe('/support');
    expect(wrapper.get('.character-next-step').text()).toBe('Pomoc s přípravou');
    expect(api.request.mock.calls.every(call => call[1] === undefined || call[1] === 'GET')).toBe(true);
    wrapper.unmount();
  });

  it('hides editing and replacement from a customer with a character', async () => {
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile } : { items: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } }); await flushPromises();
    expect(wrapper.find('#character-name').exists()).toBe(false);
    expect(wrapper.find('#character-openai-consent').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('OpenAI'); expect(wrapper.text()).not.toContain('Nová postava');
    expect(wrapper.text()).toContain('Na účtu máš jednu postavu');
    expect(api.request.mock.calls.some(call => call[1] === 'PUT')).toBe(false); wrapper.unmount();
  });
  it('lets administrators edit the same character with recorded account consent', async () => {
    api.setAccount({ id: 'admin', displayName: 'Admin', roles: ['Administrator'], openAiConsent: true });
    api.request.mockImplementation(async (path: string, method?: string, body?: Record<string, unknown>) =>
      path.endsWith('/character') ? { profile: method === 'PUT' ? { ...profile, revision: 2, userInput: body?.userInput } : profile } : { items: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } }); await flushPromises();
    expect(wrapper.get<HTMLDetailsElement>('details.character-extra').element.open).toBe(true);
    await wrapper.get('#character-name').setValue('Luna opravená'); await wrapper.get('form.customer-form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/customer/character', 'PUT', expect.objectContaining({
      expectedProfileId: profile.profileId, expectedRevision: profile.revision, openAiConsent: true, portraitConsent: true,
      userInput: expect.objectContaining({ name: 'Luna opravená' }),
    }), expect.any(AbortSignal)); expect(wrapper.text()).not.toContain('Nová postava'); wrapper.unmount();
  });
  it('creates one first profile at revision zero then removes creation controls', async () => {
    api.request.mockImplementation(async (path: string, method?: string, body?: Record<string, unknown>) => {
      if (path.endsWith('/character') && method === 'PUT') return { profile: { profileId: 'profile-1', revision: 1, userInput: body?.userInput, status: 'queued' } };
      return path.endsWith('/character') ? { profile: null } : { items: [] };
    });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } }); await flushPromises();
    await wrapper.get('#character-name').setValue('Luna'); await wrapper.get('#character-world').setValue('Moon Saga');
    await wrapper.get('form').trigger('submit'); await flushPromises();
    const initial = api.request.mock.calls.find(call => call[0].endsWith('/character') && call[1] === 'PUT')![2];
    expect(initial).toMatchObject({ expectedRevision: 0, expectedProfileId: null, openAiConsent: true, portraitConsent: true, userInput: { name: 'Luna', world: 'Moon Saga' } });
    expect(initial).not.toHaveProperty('newCharacter'); expect(wrapper.find('#character-name').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('Nová postava'); expect(api.request.mock.calls.filter(call => call[1] === 'PUT')).toHaveLength(1); wrapper.unmount();
  });

  it('starts first creation with optional details closed and preserves them when collapsed before saving', async () => {
    api.request.mockImplementation(async (path: string, method?: string, body?: Record<string, unknown>) => {
      if (path.endsWith('/character') && method === 'PUT') return { profile: { profileId: 'profile-1', revision: 1, userInput: body?.userInput, status: 'queued' } };
      return path.endsWith('/character') ? { profile: null } : { items: [] };
    });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } });
    await flushPromises();
    const optionalDetails = wrapper.get<HTMLDetailsElement>('details.character-extra');
    expect(optionalDetails.element.open).toBe(false);
    optionalDetails.element.open = true;
    await wrapper.get('#character-name').setValue('Luna');
    await wrapper.get('#character-world').setValue('Moon Saga');
    await wrapper.get('#character-version').setValue('První kniha');
    await wrapper.get('#character-description').setValue('Sbírá příběhy.');
    await wrapper.get('#character-language').setValue('Čeština');
    optionalDetails.element.open = false;
    await wrapper.get('form.customer-form').trigger('submit');
    await flushPromises();
    const saved = api.request.mock.calls.find(call => call[0].endsWith('/character') && call[1] === 'PUT')![2];
    expect(saved.userInput).toMatchObject({ name: 'Luna', world: 'Moon Saga', version: 'První kniha', description: 'Sbírá příběhy.', language: 'Čeština' });
    wrapper.unmount();
  });

  it('does not create a character without recorded account consent', async () => {
    api.setAccount({ id: 'legacy', displayName: 'Guest', roles: ['Customer'], openAiConsent: false });
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile: null } : { items: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } }); await flushPromises();
    await wrapper.get('form').trigger('submit'); await flushPromises();
    expect(api.request.mock.calls.some(call => call[1] === 'PUT')).toBe(false);
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined(); wrapper.unmount();
  });

  it('ignores an older profile response after the signed-in account changes', async () => {
    const oldProfile = deferred<{ profile: typeof profile }>();
    api.request.mockImplementation((path: string) => {
      if (path.endsWith('/character')) return api.request.mock.calls.filter(call => call[0].endsWith('/character')).length === 1
        ? oldProfile.promise : Promise.resolve({ profile: { ...profile, profileId: 'new-profile', userInput: { name: 'Mira', world: 'Star Book' } } });
      return Promise.resolve({ items: [] });
    });
    const instance = createRouter({ history: createMemoryHistory(), routes: [{ path: '/me/character', component: CustomerView }] });
    await instance.push('/me/character'); await instance.isReady();
    const wrapper = mount(CustomerView, { global: { plugins: [instance] } });
    api.setAccount({ id: 'customer-2', displayName: 'Second guest', roles: ['Customer'] });
    await flushPromises();
    oldProfile.resolve({ profile }); await flushPromises();
    expect(wrapper.get('#character-confirmation-title').text()).toBe('Mira');
    wrapper.unmount();
  });

  it('keeps the profile read-only across legacy language preference changes', async () => {
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile } : { items: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } }); await flushPromises();
    const requestCount = api.request.mock.calls.length; interfaceLanguagePreference.setLanguage('en'); await flushPromises();
    expect(wrapper.text()).toContain('Moje postava'); expect(wrapper.find('#character-name').exists()).toBe(false);
    expect(api.request).toHaveBeenCalledTimes(requestCount); expect(api.request.mock.calls.some(call => call[1] === 'PUT')).toBe(false); wrapper.unmount();
  });

  it('keeps the character and digital workshop independent from retired device sessions', async () => {
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) return { profile };
      throw new Error(`Unexpected device request ${path}`);
    });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();
    expect(wrapper.findAll('a').some(link => link.attributes('href') === '/play/wand')).toBe(true);
    expect(wrapper.text()).not.toMatch(/Bluetooth|Najít kufr|Připojení ke kufru/);
    expect(api.request.mock.calls.every(call => call[0] === '/app/v1/customer/character')).toBe(true);
    expect(api.discover).not.toHaveBeenCalled();
    wrapper.unmount();
    expect(api.disconnect).not.toHaveBeenCalled();
  });
});
