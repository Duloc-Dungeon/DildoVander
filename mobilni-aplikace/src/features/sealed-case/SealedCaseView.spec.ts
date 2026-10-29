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
import { legacyScenario, memoryRecord, type Play, type ScenarioSummary } from './sealedCase';

const aurors: ScenarioSummary = {
  ...legacyScenario, id: 'hp-aurors', title: 'Smrtijedi a bystrozorové: Případ zamčeného letaxu',
  summary: 'Někdo přesměroval letax v bezpečném domě. Stopy musí ukázat, kdo zasáhl do přepojení.',
  tone: 'Infiltrace a nedůvěra', guardianLabel: 'Bystrozorové', conspiratorLabel: 'Smrtijedi',
};
const neighbours: ScenarioSummary = {
  ...legacyScenario, id: 'hp-neighbours', title: 'Společenství vlastníků krbu',
  summary: 'Společný krb má odvézt všechny do práce. Někdo si jej rezervoval až do oběda.',
  tone: 'Sousedská komedie', guardianLabel: 'Sousedé', conspiratorLabel: 'Předbíhači',
  echoLabel: 'Domovní ozvěna', echoExplanation: 'Do konce případu jsi bez těla, ale dál pátráš, doručuješ svědectví a hlasuješ.',
  extremeActionLabel: 'Proměnit v domovní ozvěnu',
  extremeConsequence: 'Potvrzuji proměnu postavy v domovní ozvěnu do konce případu a možnou spotřebu jediného Krajního činu. Postava dál pátrá a hlasuje, ale nemá fyzické akce.',
};
const scenarioCatalog = { defaultScenarioId: legacyScenario.id, scenarios: [legacyScenario, aurors, neighbours] };

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
      .mockImplementation(async (url: string) => Response.json(url.endsWith('/scenarios') ? scenarioCatalog : { id: null }));
    const { router } = await render();
    api.setAccount({ id: 'two' });
    await flushPromises();
    finish(Response.json({ id: `sc_${'1'.repeat(32)}` }));
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/play/pod-peceti');
    expect(api.transport).toHaveBeenCalledTimes(3);
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
      .mockImplementation(async (url: string) => Response.json(url.endsWith('/scenarios') ? scenarioCatalog : { id: null }));
    const { view } = await render();
    expect(view.find('[role="alert"]').exists()).toBe(true);
    await view.findAll('button').find(button => button.text() === 'Obnovit stav')!.trigger('click');
    await flushPromises();
    expect(view.find('[role="alert"]').exists()).toBe(false);
    expect(view.text()).toContain('Založit případ');
  });
});

describe('sealed case scenario selection', () => {
  it('submits the selected story separately from duration and keeps it during a discovery refresh', async () => {
    api.transport.mockImplementation(async (url: string, options: RequestInit) => {
      if (options.method === 'POST') return Response.json({ id: 'sc_new' });
      return Response.json(url.endsWith('/scenarios') ? scenarioCatalog : url.endsWith('/current') ? { id: null } : play('lobby', { scenario: neighbours }));
    });
    const { view } = await render();
    expect(view.get('#sealed-scenario').findAll('option').map(option => option.text())).toEqual(scenarioCatalog.scenarios.map(scenario => scenario.title));
    expect((view.get('#sealed-scenario').element as HTMLSelectElement).value).toBe(legacyScenario.id);
    await view.get('#sealed-scenario').setValue(neighbours.id);
    const duration = view.findAll('label').find(label => label.text().includes('Délka hry'))!.get('select');
    await duration.setValue('full');
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect((view.get('#sealed-scenario').element as HTMLSelectElement).value).toBe(neighbours.id);
    expect(view.get('#sealed-scenario-summary').text()).toBe(neighbours.summary);
    await view.findAll('button').find(button => button.text() === 'Založit případ')!.trigger('click');
    await flushPromises();
    const creation = api.transport.mock.calls.find(([url, options]) => url === '/app/v1/sealed-cases' && options.method === 'POST');
    expect(JSON.parse(creation![1].body)).toMatchObject({ scenarioId: neighbours.id, variant: 'full', expectedPlayers: 8 });
  });

  it('pins the original scenario when retrying an unacknowledged creation after reload', async () => {
    let first = true;
    api.transport.mockImplementation(async (url: string, options: RequestInit) => {
      if (options.method === 'POST') {
        if (first) { first = false; throw new Error('Lost acknowledgement'); }
        return Response.json({ id: 'sc_recovered' });
      }
      return Response.json(url.endsWith('/scenarios') ? scenarioCatalog : url.endsWith('/current') ? { id: null } : play('lobby', { scenario: aurors }));
    });
    const initial = await render();
    await initial.view.get('#sealed-scenario').setValue(aurors.id);
    await initial.view.findAll('button').find(button => button.text() === 'Založit případ')!.trigger('click');
    await flushPromises();
    initial.view.unmount(); wrapper = undefined;
    const { view } = await render();
    expect((view.get('#sealed-scenario').element as HTMLSelectElement).value).toBe(aurors.id);
    expect(view.get('#sealed-scenario').attributes('disabled')).toBeDefined();
    await view.findAll('button').find(button => button.text() === 'Ověřit původní založení')!.trigger('click');
    await flushPromises();
    const creations = api.transport.mock.calls.filter(([url, options]) => url === '/app/v1/sealed-cases' && options.method === 'POST');
    expect(creations).toHaveLength(2);
    expect(JSON.parse(creations[1][1].body)).toEqual(JSON.parse(creations[0][1].body));
    expect(JSON.parse(creations[1][1].body).scenarioId).toBe(aurors.id);
  });

  it('recovers an accepted creation before allowing a new scenario after its released finale', async () => {
    let currentId: string | null = null;
    let loseAcknowledgement = true;
    const receipts = new Map<string, string>();
    let serverView = play('lobby', { id: 'sc_recovered', scenario: aurors });
    api.transport.mockImplementation(async (url: string, options: RequestInit) => {
      if (options.method === 'POST') {
        const body = JSON.parse(options.body as string);
        const receipt = receipts.get(body.operationId) ?? `sc_created_${receipts.size + 1}`;
        receipts.set(body.operationId, receipt); currentId = receipt;
        serverView = play('lobby', { id: receipt, scenario: body.scenarioId === neighbours.id ? neighbours : aurors });
        if (loseAcknowledgement) { loseAcknowledgement = false; throw new Error('Accepted, acknowledgement lost'); }
        return Response.json({ id: receipt });
      }
      return Response.json(url.endsWith('/scenarios') ? scenarioCatalog : url.endsWith('/current') ? { id: currentId } : serverView);
    });
    const { view, router } = await render();
    await view.get('#sealed-scenario').setValue(aurors.id);
    const button = (text: string) => view.findAll('button').find(item => item.text() === text)!;
    await button('Založit případ').trigger('click'); await flushPromises();
    expect(currentId).toBe('sc_created_1');
    window.dispatchEvent(new Event('online')); await flushPromises();
    // The current group case could have been created by another host. Its ID
    // alone must neither acknowledge our request nor bypass its recovery.
    expect(router.currentRoute.value.path).toBe('/play/pod-peceti');
    expect(view.get('#sealed-scenario').attributes('disabled')).toBeDefined();
    await button('Ověřit původní založení').trigger('click'); await flushPromises();
    expect(router.currentRoute.value.path).toBe('/play/pod-peceti/sc_created_1');
    expect(localStorage.getItem('dv:sealed-create:one')).toBeNull();
    expect(receipts.size).toBe(1);

    serverView = play('finished', {
      id: currentId, scenario: aurors,
      finale: { phase: 'released', ballotSize: 1, ownSubmitted: true, ownBallot: [], runoffCandidates: [], runoffPlaces: 0,
        selected: ['two'], winner: aurors.guardianLabel, conspirators: ['two'], reconstruction: [] },
    });
    window.dispatchEvent(new Event('online')); await flushPromises();
    await button('Založit další případ').trigger('click'); await flushPromises();
    expect(router.currentRoute.value.fullPath).toBe('/play/pod-peceti?new=1');
    expect(currentId).toBe('sc_created_1'); // The server still exposes the finished case as current.
    expect(view.get('#sealed-scenario').attributes('disabled')).toBeUndefined();
    await view.get('#sealed-scenario').setValue(neighbours.id);
    await button('Založit případ').trigger('click'); await flushPromises();
    const creations = api.transport.mock.calls.filter(([url, options]) => url === '/app/v1/sealed-cases' && options.method === 'POST')
      .map(([, options]) => JSON.parse(options.body));
    expect(creations[1]).toEqual(creations[0]);
    expect(creations[2].scenarioId).toBe(neighbours.id);
    expect(creations[2].operationId).not.toBe(creations[0].operationId);
    expect(receipts.size).toBe(2);
    expect(router.currentRoute.value.path).toBe('/play/pod-peceti/sc_created_2');
  });

  it.each(['success', 'rejection'])('ignores an old account creation %s while the next account recovers its own request', async result => {
    let finishA!: (response: Response) => void;
    let finishB!: (response: Response) => void;
    let posts = 0;
    const requestB = { operationId: 'account-b-pending', expectedPlayers: 8, variant: 'full', scenarioId: neighbours.id };
    localStorage.setItem('dv:sealed-create:two', JSON.stringify(requestB));
    api.transport.mockImplementation(async (url: string, options: RequestInit) => {
      if (options.method === 'POST') return new Promise<Response>(resolve => { if (++posts === 1) finishA = resolve; else finishB = resolve; });
      return Response.json(url.endsWith('/scenarios') ? scenarioCatalog : url.endsWith('/current') ? { id: null } : play('lobby', { id: 'sc_account_b', scenario: neighbours }));
    });
    const { view, router } = await render();
    await view.get('#sealed-scenario').setValue(aurors.id);
    await view.findAll('button').find(button => button.text() === 'Založit případ')!.trigger('click');
    api.setAccount({ id: 'two' }); await flushPromises();
    const verify = () => view.findAll('button').find(button => button.text() === 'Ověřit původní založení')!;
    expect((view.get('#sealed-scenario').element as HTMLSelectElement).value).toBe(neighbours.id);
    await verify().trigger('click'); await flushPromises();
    expect(verify().attributes('aria-busy')).toBe('true');
    finishA(result === 'success' ? Response.json({ id: 'sc_account_a' }) : Response.json({ code: 'idempotency_key_reused' }, { status: 409 }));
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/play/pod-peceti');
    expect(view.find('[role="alert"]').exists()).toBe(false);
    expect(verify().attributes('aria-busy')).toBe('true');
    expect(JSON.parse(localStorage.getItem('dv:sealed-create:two')!)).toEqual(requestB);
    expect(localStorage.getItem('dv:sealed-create:one')).not.toBeNull();
    finishB(Response.json({ id: 'sc_account_b' })); await flushPromises();
    expect(router.currentRoute.value.path).toBe('/play/pod-peceti/sc_account_b');
    expect(localStorage.getItem('dv:sealed-create:two')).toBeNull();
  });

  it('does not silently create a different story when the catalog is unavailable', async () => {
    api.transport.mockImplementation(async (url: string) => url.endsWith('/scenarios')
      ? Response.json({ code: 'unavailable' }, { status: 503 }) : Response.json({ id: null }));
    const { view } = await render();
    expect(view.text()).toContain('Nabídku scénářů se nepodařilo načíst');
    expect(view.findAll('button').find(button => button.text() === 'Založit případ')!.attributes('disabled')).toBeDefined();
    expect(api.transport.mock.calls.some(([, options]) => options.method === 'POST')).toBe(false);
  });

  it('offers the original story when an older server has no catalog route', async () => {
    api.transport.mockImplementation(async (url: string) => url.endsWith('/scenarios')
      ? Response.json({}, { status: 404 }) : Response.json({ id: null }));
    const { view } = await render();
    expect(view.get('#sealed-scenario').findAll('option').map(option => option.text())).toEqual([legacyScenario.title]);
    expect(view.findAll('button').find(button => button.text() === 'Založit případ')!.attributes('disabled')).toBeUndefined();
  });

  it('uses scenario-specific roles, evidence and extreme-action consent without leaking another story', async () => {
    const value = play('planning', {
      scenario: neighbours, ownObjective: 'Dolož původní přepojení společného krbu.', ownTeam: 'Conspirator',
      locations: { ARCHIV: 'Kniha odjezdů', POKOJE: 'Podkrovní chodba', SALON: 'Společný krb' },
      clues: [{ id: 'A1', chain: '1', family: 'A', candidates: ['two'], sourceEvent: marker.privateSource,
        label: 'Otisk letaxového popela', description: 'Stopa dokládá zásah do předchozího přepojení, nikoli dnešní polohu.' }],
      choices: [{ id: 'extreme', label: neighbours.extremeActionLabel, benefit: 'Ozvěna do konce hry.', cost: 'Jeden tah.', risk: 'Lze odvrátit.',
        plan: { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 9 } }],
    });
    const view = await renderPlay(value);
    expect(view.text()).toContain(neighbours.title);
    expect(view.text()).toContain('Tvoje strana: Předbíhači');
    expect(view.text()).toContain('Dolož původní přepojení společného krbu.');
    const button = (label: string) => view.findAll('button').find(item => item.text().includes(label))!;
    await button('Moje akce').trigger('click');
    await button(neighbours.extremeActionLabel).trigger('click');
    expect(view.get('.sealed-lethal').text()).toContain(neighbours.extremeConsequence);
    expect(view.get('.sealed-lethal').text()).not.toContain('usmrtit');
    expect(button('Výslovně potvrdit plán').attributes('disabled')).toBeDefined();
    await view.get('.sealed-lethal input').setValue(true);
    expect(button('Výslovně potvrdit plán').attributes('disabled')).toBeUndefined();
    expect(view.text()).toContain('Kniha odjezdů nebo Podkrovní chodba');
    await button('Deník').trigger('click'); await button('Doložené stopy').trigger('click');
    expect(view.text()).toContain('Otisk letaxového popela');
    expect(view.text()).toContain('Stopa dokládá zásah do předchozího přepojení');
    expect(view.text()).not.toContain(legacyScenario.title);
    expect(view.text()).not.toContain(marker.privateSource);
  });

  it('shows the frozen public prelude before scenario consent without exposing the private narrator', async () => {
    const prelude = 'Původní změna pořadníku nastala před tvými tahy. Přidělená strana je součástí této zápletky.';
    const view = await renderPlay(play('lobby', { scenario: neighbours, ownReady: false, ownScenarioConsent: false,
      publicNarration: { text: prelude }, privateNarration: { text: 'PRIVATE_LOBBY_NARRATOR' } }));
    const text = view.text();
    expect(text).toContain(prelude);
    expect(text.indexOf(prelude)).toBeLessThan(text.indexOf('Přijímám scénářovou minulost'));
    expect(text).not.toContain('PRIVATE_LOBBY_NARRATOR');
  });

  it('excludes observed scenario echoes from oversight and displays the selected chapter title', async () => {
    const view = await renderPlay(play('planning', { scenario: neighbours, ownOversight: true, chapterTitle: 'První nejdřív', players: [
      { id: 'one', name: 'Host 1', seat: 'S1', state: 'Awake', location: 'ARCHIV', mask: null, portraitUrl: null },
      { id: 'two', name: 'Ozvěna souseda', seat: 'S2', state: neighbours.echoLabel, location: 'ARCHIV', mask: null, portraitUrl: null },
      { id: 'three', name: 'Starší ozvěna', seat: 'S3', state: 'Echo', location: 'ARCHIV', mask: null, portraitUrl: null },
      { id: 'four', name: 'Neznámý soused', seat: 'S4', state: 'Stav neznáš', location: null, mask: null, portraitUrl: null },
    ] }));
    expect(view.findAll('h2').map(heading => heading.text())).toContain('První nejdřív');
    await view.findAll('button').find(button => button.text() === 'Moje akce')!.trigger('click');
    const selector = view.findAll('label').find(label => label.text().includes('Nepovinné doporučení dohledu'))!.get('select');
    expect(selector.findAll('option').map(option => option.text())).toEqual(['Zdržuji se', 'Neznámý soused']);
  });

  it('carries the selected story into released results and both exports without public private-context leakage', async () => {
    const finished = play('finished', {
      scenario: neighbours, notes: ['PRIVATE_NOTE_CANARY'],
      statements: [{ id: 'private', actor: 'one', body: 'PRIVATE_CHAT_CANARY', recipients: ['two'], public: false, createdAt: 1 }],
      finale: { phase: 'released', ballotSize: 1, ownSubmitted: true, ownBallot: [], runoffCandidates: [], runoffPlaces: 0,
        selected: ['two'], winner: 'Sousedé', conspirators: ['two'], reconstruction: [], message: 'Krb znovu patří všem. První odjíždí ten, kdo musí vysvětlit zpoždění.' },
    });
    const view = await renderPlay(finished);
    expect(view.text()).toContain('Vítězí Sousedé');
    expect(view.text()).toContain('Krb znovu patří všem. První odjíždí ten, kdo musí vysvětlit zpoždění.');
    expect(view.text()).not.toContain(legacyScenario.title);
    const publicRecord = memoryRecord(finished as unknown as Play, false, null);
    const privateRecord = memoryRecord(finished as unknown as Play, true, { name: 'Host 1', team: 'Sousedé' });
    expect(publicRecord.title).toBe(`Pod pečetí · ${neighbours.title}`);
    expect(publicRecord.scenarioId).toBe(neighbours.id);
    expect(privateRecord.scenarioId).toBe(neighbours.id);
    expect(JSON.stringify(publicRecord)).not.toContain('PRIVATE_NOTE_CANARY');
    expect(JSON.stringify(privateRecord)).toContain('PRIVATE_NOTE_CANARY');
    expect(JSON.stringify(privateRecord)).not.toContain('PRIVATE_CHAT_CANARY');
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
