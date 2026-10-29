import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
const api = vi.hoisted(() => ({ transport: vi.fn(), setAccount: (_value: unknown) => {} }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref<unknown>({ id: 'one' });
  api.setAccount = value => { account.value = value; };
  return { account, accountTransport: api.transport };
});
import SealedCaseView from './SealedCaseView.vue';

let wrapper: VueWrapper | undefined;
async function render() {
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/play/pod-peceti/:id?', component: SealedCaseView },
    { path: '/:pathMatch(.*)*', component: { template: '<div />' } },
  ] });
  await router.push('/play/pod-peceti');
  wrapper = mount(SealedCaseView, { global: { plugins: [router] } });
  await flushPromises();
  return { view: wrapper, router };
}
const marker = {
  privateSource: 'PRIVATE_EVENT_SOURCE_DO_NOT_RENDER',
  privateReference: 'PRIVATE_EVENT_REF_DO_NOT_RENDER',
  stalePrelude: 'STALE_PRELUDE_DO_NOT_RENDER',
};
function play(phase = 'planning', overrides: Record<string, unknown> = {}) {
  const players = [
    { id: 'one', name: 'Host 1', seat: 'S1', state: 'Awake', location: 'ARCHIV', mask: null, portraitUrl: null },
    { id: 'two', name: 'Host 2', seat: 'S2', state: 'Awake', location: null, mask: null, portraitUrl: null },
  ];
  return {
    id: `sc_${'a'.repeat(32)}`, phase, serverNow: 1000, chapter: 1, chapters: 6,
    planningEndsAt: 2000, quietEndsAt: 3000, finaleEndsAt: 9000,
    expectedPlayers: 8, readyPlayers: 2, ownId: 'one', ownReady: true, ownScenarioConsent: true,
    canManage: true, canStart: true, lobbyRevision: 1, ownState: 'Awake',
    ownTeam: 'Guardian', ownKit: 'Archivář', teamMembers: [], location: 'ARCHIV',
    locations: { ARCHIV: 'Archiv', SALON: 'Salon' }, adjacentLocations: ['SALON'], players,
    ownPlan: null,
    choices: [{ id: 'investigate', label: 'Pátrat v archivu', benefit: 'Přečíst stopu.', cost: 'Jeden tah.', risk: 'Nezaručeno.',
      plan: { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 4 } }],
    clues: [{ id: 'A1', chain: '1', family: 'A', candidates: ['two'], sourceEvent: marker.privateSource }],
    sources: [{ id: 'A1', label: 'Záznam z archivní knihy' }],
    ownOversight: false, ownItems: [], visibleItems: [], notes: [], statements: [], ownOutcomes: [],
    publicNarration: { text: 'Veřejná stopa případu.' },
    privateNarration: { text: marker.stalePrelude },
    finale: null,
    ...overrides,
  };
}
async function renderPlay(value: ReturnType<typeof play>) {
  api.transport.mockImplementation(async (url: string) => {
    if (url.endsWith('/current')) return Response.json({ id: null });
    return Response.json(value);
  });
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/play/pod-peceti/:id?', component: SealedCaseView },
    { path: '/:pathMatch(.*)*', component: { template: '<div />' } },
  ] });
  await router.push(`/play/pod-peceti/${value.id}`);
  wrapper = mount(SealedCaseView, { global: { plugins: [router] } });
  await flushPromises();
  return wrapper;
}
beforeEach(() => { api.transport.mockReset(); api.setAccount({ id: 'one' }); vi.useFakeTimers(); });
afterEach(() => { wrapper?.unmount(); wrapper = undefined; localStorage.clear(); vi.useRealTimers(); });

describe('sealed case discovery lifecycle', () => {
  it('ignores a previous account response when discovering the current case', async () => {
    let finish!: (response: Response) => void;
    api.transport.mockImplementationOnce(() => new Promise<Response>(resolve => { finish = resolve; }))
      .mockResolvedValue(Response.json({ id: null }));
    const { router } = await render();
    api.setAccount({ id: 'two' });
    await flushPromises();
    finish(Response.json({ id: `sc_${'1'.repeat(32)}` }));
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/play/pod-peceti');
    expect(api.transport).toHaveBeenCalledTimes(2);
  });

  it('does not restart polling when an outstanding request settles after leaving', async () => {
    let finish!: (response: Response) => void;
    api.transport.mockImplementation(() => new Promise<Response>(resolve => { finish = resolve; }));
    const { view } = await render();
    view.unmount(); wrapper = undefined;
    finish(Response.json({ id: null }));
    await flushPromises();
    await vi.advanceTimersByTimeAsync(15000);
    expect(api.transport).toHaveBeenCalledTimes(1);
  });

  it('clears a discovery failure after a successful refresh with no current case', async () => {
    api.transport.mockResolvedValueOnce(Response.json({ code: 'unavailable' }, { status: 503 }))
      .mockResolvedValue(Response.json({ id: null }));
    const { view } = await render();
    expect(view.find('[role="alert"]').exists()).toBe(true);
    await view.findAll('button').find(button => button.text() === 'Obnovit stav')!.trigger('click');
    await flushPromises();
    expect(view.find('[role="alert"]').exists()).toBe(false);
    expect(view.text()).toContain('Založit případ');
  });
});

describe('sealed case focused views and disclosure', () => {
  it('keeps each view button in sync with the visible panel', async () => {
    const view = await renderPlay(play());
    const button = (label: string) => view.findAll('button').find(item => item.text().includes(label))!;

    expect(button('Příběh').attributes('aria-pressed')).toBe('true');
    expect(view.text()).toContain('Veřejná stopa případu.');
    await button('Moje akce').trigger('click');
    await flushPromises();
    expect(button('Moje akce').attributes('aria-pressed')).toBe('true');
    await button('Pátrat v archivu').trigger('click');
    await flushPromises();
    expect(view.text()).toContain('Výslovně potvrdit plán');
    expect(view.findAll('summary').map(item => item.text())).toContain('Další možnosti tahu');
    expect(view.findAll('details').every(item => !item.element.hasAttribute('open'))).toBe(true);
    const labels = view.findAll('label').map(item => item.text());
    for (const label of ['Adresát / cíl', 'Místo', 'Předmět / stopa']) {
      expect(labels.some(text => text.includes(label))).toBe(true);
    }
    const advanced = view.findAll('details').find(item => item.find('summary').text() === 'Další možnosti tahu')!;
    expect(advanced.findAll('label').some(item => item.text().includes('Podmínka'))).toBe(true);
    expect(button('Příběh').attributes('aria-pressed')).toBe('false');
    await button('Deník').trigger('click');
    await flushPromises();
    expect(button('Deník').attributes('aria-pressed')).toBe('true');
    await button('Doložené stopy').trigger('click');
    await flushPromises();
    expect(view.text()).toContain('Záznam z archivní knihy');
  });

  it('starts the roster and owner controls collapsed, and hides owner controls from guests', async () => {
    const ownerView = await renderPlay(play());
    const summaries = ownerView.findAll('summary').map(item => item.text());
    expect(summaries.some(summary => summary.startsWith('Postavy u stolu'))).toBe(true);
    expect(summaries).toContain('Vedení večera');
    expect(ownerView.findAll('details').every(item => !item.element.hasAttribute('open'))).toBe(true);

    ownerView.unmount(); wrapper = undefined;
    const guestView = await renderPlay(play('planning', { canManage: false, canStart: false }));
    expect(guestView.findAll('summary').map(item => item.text()).some(summary => summary.startsWith('Postavy u stolu'))).toBe(true);
    expect(guestView.findAll('summary').map(item => item.text())).not.toContain('Vedení večera');

    guestView.unmount(); wrapper = undefined;
    const aiOwnerView = await renderPlay(play('lobby', { canManageAi: true, aiAvailable: true, readyPlayers: 1, players: [
      { id: 'one', name: 'Host 1', seat: 'S1', state: 'Awake', location: 'ARCHIV', mask: null, portraitUrl: null },
    ] }));
    const aiSummary = aiOwnerView.findAll('summary').find(item => item.text() === 'Doplnit AI hráče');
    expect(aiSummary).toBeDefined();
    const closedAiDetails = aiOwnerView.findAll('details').find(item => item.find('summary').text() === 'Doplnit AI hráče')!;
    expect(closedAiDetails.element.hasAttribute('open')).toBe(false);
    expect(closedAiDetails.findAll('button').some(item => item.text() === 'Přidat AI hráče')).toBe(true);
  });

  it('shows the released verdict before exports and keeps internal source identifiers out of the view', async () => {
    const finished = play('finished', {
      clues: [{ id: 'A1', chain: '1', family: 'A', candidates: ['two'], sourceEvent: marker.privateSource }],
      ownOutcomes: [{ chapter: 1, actor: 'one', target: null, intent: 'Investigate', result: 'Success', eventRef: marker.privateReference }],
      finale: { phase: 'released', ballotSize: 1, ownSubmitted: true, ownBallot: [], runoffCandidates: [], runoffPlaces: 0,
        selected: ['two'], winner: 'Strážci', conspirators: ['two'],
        reconstruction: [{ chapter: 1, actor: 'one', target: 'two', intent: 'Investigate', result: 'Success', eventRef: marker.privateReference }] },
    });
    const view = await renderPlay(finished);
    const text = view.text();
    expect(text.indexOf('Vítězí Strážci')).toBeGreaterThanOrEqual(0);
    expect(text.indexOf('Vítězí Strážci')).toBeLessThan(text.indexOf('Stáhnout veřejnou rekonstrukci'));
    expect(text).not.toContain(marker.privateSource);
    expect(text).not.toContain(marker.privateReference);
    expect(text).not.toContain(marker.stalePrelude);
  });

  it('keeps ballot choices limited to other players and renders an explicit empty clue state', async () => {
    const ballot = play('finished', {
      clues: [],
      finale: { phase: 'ballot', ballotSize: 1, ownSubmitted: false, ownBallot: [], runoffCandidates: [], runoffPlaces: 0,
        selected: null, winner: null, conspirators: null, reconstruction: null },
    });
    const view = await renderPlay(ballot);
    expect(view.text()).toContain('Vyber 1 ostatní hráče');
    const ballotValues = view.find('fieldset').findAll('input[type="checkbox"]').map(input => (input.element as HTMLInputElement).value);
    expect(ballotValues).toEqual(['two']);
    expect(ballotValues).not.toContain('one');
    await view.findAll('button').find(item => item.text() === 'Deník')!.trigger('click');
    await flushPromises();
    await view.findAll('button').find(item => item.text() === 'Doložené stopy')!.trigger('click');
    await flushPromises();
    expect(view.text()).toContain('Zatím nemáš novou doloženou stopu.');
  });

  it('uses the same stable clue label in a resource selector and the journal', async () => {
    const view = await renderPlay(play('planning', {
      clues: [
        { id: 'A1', chain: '1', family: 'A', candidates: ['two'], sourceEvent: marker.privateSource },
        { id: 'B2', chain: '2', family: 'B', candidates: ['two'], sourceEvent: `${marker.privateSource}_B` },
      ],
      sources: [{ id: 'A1', label: 'Záznam z archivní knihy' }],
    }));
    const button = (label: string) => view.findAll('button').find(item => item.text().includes(label))!;
    await button('Moje akce').trigger('click');
    await flushPromises();
    await button('Pátrat v archivu').trigger('click');
    await flushPromises();
    const resource = view.findAll('label').find(item => item.text().includes('Předmět / stopa'))!.find('select');
    const optionLabels = resource.findAll('option').map(option => option.text());
    expect(optionLabels).toContain('Záznam z archivní knihy');
    expect(optionLabels).toContain('Stopa 2');

    await button('Deník').trigger('click');
    await flushPromises();
    await button('Doložené stopy').trigger('click');
    await flushPromises();
    expect(view.findAll('h3').map(heading => heading.text())).toContain('Stopa 2');
  });

  it('updates one atomic live phase status when a refresh advances the case', async () => {
    const current = play('planning');
    const view = await renderPlay(current);
    const status = () => view.find('[role="status"][aria-atomic="true"]');
    expect(status().text()).toContain('Čas na plán');
    current.phase = 'quiet';
    window.dispatchEvent(new Event('online'));
    await flushPromises();
    expect(status().text()).toContain('Klidový úsek');
    expect(view.findAll('[role="status"][aria-atomic="true"]')).toHaveLength(1);
  });
});
