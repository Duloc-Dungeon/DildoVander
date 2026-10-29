import { mount, flushPromises } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import CustomerChronicle from './CustomerChronicle.vue';

const state = vi.hoisted(() => ({
  accountRequest: vi.fn(), accountTransport: vi.fn(), account: null as any, setAccount: (_value: unknown) => {},
}));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref({ id: '399b5bbdd3f647c5b3d7a8167ce8b922' });
  state.account = account;
  state.setAccount = value => { account.value = value as typeof account.value; };
  return {
    account,
    accountDeletionRequested: ref(false), offlineSession: ref(false), signingOut: ref(false),
    accountRequest: state.accountRequest, accountTransport: state.accountTransport,
    shareNativeChronicleImage: vi.fn(),
  };
});

const eventId = '15ca3a2a-8612-4d66-8fc4-8e65a64c9712';
const locked = {
  id: eventId, sourceKind: 'yolo', sourceId: '7d2a779a73e94ab19f2225acdd108eaa',
  occurredAt: 1793300000, illustrationState: 'not_requested', revealed: false, revision: 1,
  title: 'SECRET result', summary: 'SECRET summary',
};
const revealed = {
  id: eventId, sourceKind: 'yolo', sourceId: '7d2a779a73e94ab19f2225acdd108eaa',
  groupId: '779bc47013c24400b28b8a4507d56367', occurredAt: 1793300000,
  title: 'Doručení s následky', summary: 'Berta doručila konvici.',
  participants: [{ accountId: '399b5bbdd3f647c5b3d7a8167ce8b922', displayName: 'Berta', role: 'challenger', visualReferenceVersion: 'v1' }],
  result: 'winner', winnerId: '399b5bbdd3f647c5b3d7a8167ce8b922', story: ['Plán', 'Komplikace', 'Rozsudek'],
  decisions: [], awards: [], illustrationState: 'not_requested', revealed: true, revision: 2,
};

async function setup(onRouter?: (router: ReturnType<typeof createRouter>) => void) {
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/me/keepsakes', component: CustomerChronicle }, { path: '/:pathMatch(.*)*', component: { template: '<div />' } },
  ] });
  onRouter?.(router);
  await router.push('/me/keepsakes');
  await router.isReady();
  const wrapper = mount(CustomerChronicle, { global: {
    plugins: [router],
    stubs: { AwButton: { props: ['busy', 'disabled', 'variant'], template: '<button :disabled="disabled" @click="$emit(\'click\')"><slot /></button>' } },
  } });
  await flushPromises();
  return wrapper;
}

describe('CustomerChronicle', () => {
  beforeEach(() => {
    state.accountRequest.mockReset();
    state.accountTransport.mockReset();
    state.accountRequest.mockImplementation(async (path: string, method: string) => {
      if (path.startsWith('/app/v1/chronicle?')) return { items: [locked], nextCursor: null, badges: [] };
      if (path === `/app/v1/chronicle/${eventId}` && method === 'GET') return locked;
      if (path === `/app/v1/chronicle/${eventId}/reveal`) return { revealed: true, item: revealed };
      throw new Error(`Unexpected call ${method} ${path}`);
    });
  });

  it('does not render hidden titles or results before the owner explicitly opens the event', async () => {
    const wrapper = await setup();
    expect(wrapper.text()).not.toContain('SECRET');
    expect(wrapper.text()).toContain('Vzpomínka čeká na otevření');
    await wrapper.get('.chronicle__entry').trigger('click');
    await flushPromises();
    expect(wrapper.text()).not.toContain('SECRET');
    await wrapper.get('.chronicle__detail button').trigger('click');
    await flushPromises();
    expect(state.accountRequest).toHaveBeenCalledWith(`/app/v1/chronicle/${eventId}/reveal`, 'POST', {}, expect.any(AbortSignal));
    expect(wrapper.text()).toContain('Doručení s následky');
    expect(wrapper.text()).toContain('Výsledek: Vítězství · Berta');
    wrapper.unmount();
  });

  it('keeps a selected event loading when the same owner object is refreshed during navigation', async () => {
    const secondId = '25ca3a2a-8612-4d66-8fc4-8e65a64c9712';
    const secondLocked = { ...locked, id: secondId, sourceId: '22345678123442348234123456789abc' };
    let resolveDetail!: (value: typeof secondLocked) => void;
    state.accountRequest.mockImplementation((path: string) => {
      if (path.startsWith('/app/v1/chronicle?')) return Promise.resolve({ items: [locked, secondLocked], nextCursor: null, badges: [] });
      if (path === `/app/v1/chronicle/${secondId}`) return new Promise(resolve => { resolveDetail = resolve; });
      if (path === `/app/v1/chronicle/${eventId}`) return Promise.resolve(locked);
      throw new Error(`Unexpected call GET ${path}`);
    });
    let router: ReturnType<typeof createRouter> | undefined;
    const wrapper = await setup(value => { router = value; });
    await wrapper.findAll('.chronicle__entry')[1].trigger('click');
    await flushPromises();
    expect(router?.currentRoute.value.query.event).toBe(secondId);
    expect(wrapper.text()).toContain('Otevírám vzpomínku…');

    state.setAccount({ id: '399b5bbdd3f647c5b3d7a8167ce8b922', refreshed: true });
    await flushPromises();
    resolveDetail(secondLocked);
    await flushPromises();

    expect(wrapper.text()).toContain('Otevřít událost?');
    expect(wrapper.text()).not.toContain('Otevírám vzpomínku…');
    expect(state.accountRequest.mock.calls.filter(([path]) => path.startsWith('/app/v1/chronicle?'))).toHaveLength(1);
    wrapper.unmount();
  });

  it('shows YOLO assignment and situation separately from its three named story beats', async () => {
    const fullStory = ['Zadání: doručit konvici.', 'Situace: most hlídá ospalý troll.', 'Plán: Berta nabídla sušenku.',
      'Komplikace: troll chtěl účtenku.', 'Poslední špatný nápad: konvice předstírala diplom.'];
    state.accountRequest.mockImplementation(async (path: string, method: string) => {
      if (path.startsWith('/app/v1/chronicle?')) return { items: [locked], nextCursor: null, badges: [] };
      if (path === `/app/v1/chronicle/${eventId}` && method === 'GET') return locked;
      if (path === `/app/v1/chronicle/${eventId}/reveal`) return { revealed: true, item: { ...revealed, story: fullStory } };
      throw new Error(`Unexpected call ${method} ${path}`);
    });
    const wrapper = await setup();
    await wrapper.get('.chronicle__entry').trigger('click');
    await flushPromises();
    await wrapper.get('.chronicle__detail button').trigger('click');
    await flushPromises();
    expect(wrapper.get('.chronicle__yolo-context').text()).toContain('Zadání: doručit konvici.');
    expect(wrapper.get('.chronicle__yolo-context').text()).toContain('Situace: most hlídá ospalý troll.');
    const beats = wrapper.findAll('.chronicle__story--yolo li');
    expect(beats).toHaveLength(3);
    expect(beats.map(beat => beat.get('strong').text())).toEqual(['Plán', 'Komplikace', 'Poslední špatný nápad']);
    expect(beats[2].text()).toContain('konvice předstírala diplom.');
    wrapper.unmount();
  });

  it.each([
    { name: 'owner as the actual YOLO winner', sourceKind: 'yolo', result: 'winner', winnerId: '399b5bbdd3f647c5b3d7a8167ce8b922', participants: [
      { accountId: '399b5bbdd3f647c5b3d7a8167ce8b922', displayName: 'Mira', role: 'challenger', visualReferenceVersion: 'v1' },
      { accountId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', displayName: 'Eliška', role: 'opponent', visualReferenceVersion: 'v1' },
    ], expected: 'Vítězství · Mira' },
    { name: 'the opponent winning a duel', sourceKind: 'duel', result: 'opponent', winnerId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', participants: [
      { accountId: '399b5bbdd3f647c5b3d7a8167ce8b922', displayName: 'Mira', role: 'challenger', visualReferenceVersion: 'v1' },
      { accountId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', displayName: 'Eliška', role: 'opponent', visualReferenceVersion: 'v1' },
    ], expected: 'Prohra · vítězí Eliška' },
    { name: 'the challenger winning a duel', sourceKind: 'duel', result: 'challenger', winnerId: '399b5bbdd3f647c5b3d7a8167ce8b922', participants: [
      { accountId: '399b5bbdd3f647c5b3d7a8167ce8b922', displayName: 'Mira', role: 'challenger', visualReferenceVersion: 'v1' },
      { accountId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', displayName: 'Eliška', role: 'opponent', visualReferenceVersion: 'v1' },
    ], expected: 'Vítězství · Mira' },
    { name: 'a YOLO fiasco without an invented winner', sourceKind: 'yolo', result: 'fiasco', winnerId: undefined, participants: [
      { accountId: '399b5bbdd3f647c5b3d7a8167ce8b922', displayName: 'Mira', role: 'challenger', visualReferenceVersion: 'v1' },
      { accountId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', displayName: 'Eliška', role: 'opponent', visualReferenceVersion: 'v1' },
    ], expected: 'Společné fiasko' },
    { name: 'both members of the winning team', sourceKind: 'team', result: 'a', winnerId: undefined, participants: [
      { accountId: '399b5bbdd3f647c5b3d7a8167ce8b922', displayName: 'Mira', role: 'team-a', visualReferenceVersion: 'v1' },
      { accountId: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', displayName: 'Tomáš', role: 'team-a', visualReferenceVersion: 'v1' },
      { accountId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', displayName: 'Eliška', role: 'team-b', visualReferenceVersion: 'v1' },
      { accountId: 'cccccccccccccccccccccccccccccccc', displayName: 'Pavel', role: 'team-b', visualReferenceVersion: 'v1' },
    ], expected: 'Vítězný tým · Mira + Tomáš' },
    { name: 'a group rescue without describing it as a personal win', sourceKind: 'boss', result: 'rescue', winnerId: '399b5bbdd3f647c5b3d7a8167ce8b922', participants: [
      { accountId: '399b5bbdd3f647c5b3d7a8167ce8b922', displayName: 'Mira', role: 'rescuer', visualReferenceVersion: 'v1' },
    ], expected: 'Ollivander je volný' },
  ])('renders $name from the owner-facing event data', async scenario => {
    const item = { ...revealed, sourceKind: scenario.sourceKind, sourceId: scenario.sourceKind === 'boss' ? 'rescue-779bc47013c24400b28b8a4507d56367' : revealed.sourceId,
      result: scenario.result, winnerId: scenario.winnerId, participants: scenario.participants };
    state.accountRequest.mockImplementation(async (path: string, method: string) => {
      if (path.startsWith('/app/v1/chronicle?')) return { items: [locked], nextCursor: null, badges: [] };
      if (path === `/app/v1/chronicle/${eventId}` && method === 'GET') return locked;
      if (path === `/app/v1/chronicle/${eventId}/reveal`) return { revealed: true, item };
      throw new Error(`Unexpected call ${method} ${path}`);
    });
    const wrapper = await setup();
    await wrapper.get('.chronicle__entry').trigger('click');
    await flushPromises();
    await wrapper.get('.chronicle__detail button').trigger('click');
    await flushPromises();
    expect(wrapper.get('.chronicle__result').text()).toBe(`Výsledek: ${scenario.expected}`);
    wrapper.unmount();
  });

  it('loads badge emblems from their earned event and releases their object URLs', async () => {
    const award = { id: 'first-win', title: 'První vítězství', scope: 'duel', evidence: 'Rozhodující tah.', eventId, acquiredAt: 1793300000 };
    state.accountRequest.mockImplementation(async (path: string) => path.startsWith('/app/v1/chronicle?')
      ? { items: [locked], nextCursor: null, badges: [award] } : locked);
    state.accountTransport.mockResolvedValue(new Response(Uint8Array.of(1, 2, 3), { headers: { 'Content-Type': 'image/png' } }));
    const createUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:chronicle-badge');
    const revokeUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    const wrapper = await setup();
    try {
      await flushPromises();
      expect(state.accountTransport).toHaveBeenCalledWith(`/app/v1/chronicle/${eventId}/badges/first-win/image`, expect.objectContaining({ cache: 'no-store', redirect: 'error' }));
      expect(wrapper.get('.chronicle__badge-shelf img').attributes('src')).toBe('blob:chronicle-badge');
    } finally { wrapper.unmount(); }
    expect(createUrl).toHaveBeenCalledOnce();
    expect(revokeUrl.mock.calls[0]?.[0]).toBe('blob:chronicle-badge');
  });

  it('can find participant filters in events beyond the first page', async () => {
    const older = { ...revealed, id: '25ca3a2a-8612-4d66-8fc4-8e65a64c9712', participants: [
      { accountId: 'c'.repeat(32), displayName: 'Luna ze staršího večera', role: 'participant', visualReferenceVersion: 'v1' },
    ] };
    state.accountRequest.mockImplementation(async (path: string) => {
      if (path === '/app/v1/chronicle?limit=50') return { items: [locked], nextCursor: 'older-cursor', badges: [] };
      if (path === '/app/v1/chronicle?before=older-cursor&limit=50') return { items: [older], nextCursor: null, badges: [] };
      return { items: [locked], nextCursor: null, badges: [] };
    });
    const wrapper = await setup();
    await wrapper.get('.chronicle__participant-scan button').trigger('click');
    await flushPromises();
    expect(wrapper.findAll('select')[1].text()).toContain('Luna ze staršího večera');
    expect(wrapper.text()).toContain('Účastníci z celé kroniky načtení');
    wrapper.unmount();
  });
});
