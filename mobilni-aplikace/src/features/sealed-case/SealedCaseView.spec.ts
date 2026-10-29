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
import { legacyScenario, memoryRecord, type Plan, type Play, type ScenarioSummary } from './sealedCase';

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
    await view.findAll('label').find(item => item.text().includes('Adresát / cíl'))!.find('select').setValue('two');
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

describe('sealed case short action menu', () => {
  const shortChoices = [
    { id: 'investigate', label: 'Pátrat v archivu', benefit: 'Najít stopu.', cost: 'Jeden tah.', risk: 'Nezaručeno.', plan: { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 4 } },
    { id: 'guard', label: 'Chránit sebe', benefit: 'Ochrana.', cost: 'Jeden tah.', risk: 'Není důkaz.', plan: { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 2 } },
    { id: 'delay-A1', label: 'Zdržet stopu · A · A1', benefit: 'Odložit.', cost: 'Jeden tah.', risk: 'Může selhat.', plan: { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 7, method: 'DELAY', resourceId: 'A1' } },
  ];

  it.each(['Guardian', 'Conspirator'])('shows the same three public actions for %s and keeps selection local', async ownTeam => {
    const value = play('planning', { chapters: 3, ownTeam, choices: shortChoices });
    const view = await renderPlay(value);
    await view.findAll('button').find(item => item.text() === 'Moje akce')!.trigger('click');
    await flushPromises();
    const choices = view.findAll('.sealed-choices button').map(item => item.text());
    expect(choices).toHaveLength(3);
    expect(view.findAll('.sealed-choices button strong').map(item => item.text())).toEqual(['Pátrat', 'Chránit sebe', 'Zdržet stopu']);
    expect(choices.join(' ')).toContain('novou stopu');
    expect(choices.join(' ')).toContain('nepotvrzuje, že k útoku došlo');
    expect(choices.join(' ')).toContain('již získané kopie zůstávají');
    await view.findAll('.sealed-choices button')[1]!.trigger('click');
    await flushPromises();
    expect(view.text()).toContain('Chránit sebe spotřebuje hlavní tah');
    expect(view.findAll('.sealed-draft label').some(item => item.text().includes('Adresát / cíl'))).toBe(false);
    expect(api.transport.mock.calls.some(([, options]) => options.method === 'POST')).toBe(false);
    await view.findAll('button').find(item => item.text() === 'Výslovně potvrdit plán')!.trigger('click');
    await flushPromises();
    const request = api.transport.mock.calls.find(([url, options]) => url.endsWith('/actions') && options.method === 'POST');
    expect(JSON.parse(request![1].body).plan).toMatchObject({ intent: 2, targetId: null });
  });

  it('only allows a currently visible source for short DELAY and sends no clue or redirect parameters', async () => {
    const value = play('planning', { chapters: 3, choices: shortChoices,
      clues: [{ id: 'A1', chain: '1', family: 'A', candidates: ['two'], sourceEvent: marker.privateSource },
        { id: 'C9', chain: '9', family: 'C', candidates: ['two'], sourceEvent: marker.privateSource }],
      sources: [{ id: 'A1', label: 'Záznam z archivní knihy' }], ownItems: [{ id: 'WAND', name: 'Hůlka', ownerId: 'one', transferable: true }],
      visibleItems: [{ id: 'OTHER', name: 'Cizí předmět', ownerId: 'two', transferable: true }] });
    const view = await renderPlay(value);
    await view.findAll('button').find(item => item.text() === 'Moje akce')!.trigger('click');
    await flushPromises();
    await view.findAll('.sealed-choices button').find(item => item.text().startsWith('Zdržet stopu'))!.trigger('click');
    await flushPromises();
    const resource = view.findAll('label').find(item => item.text().includes('Předmět / stopa'))!.find('select');
    expect(resource.findAll('option').map(item => item.text())).toEqual(['Vyber dostupný zdroj', 'Záznam z archivní knihy']);
    expect(view.findAll('label').some(item => item.text().includes('Zásah do jediného doručení'))).toBe(false);
    expect(view.text()).toContain('ID zdroje samo o sobě nezpřístupňuje jeho obsah');
    expect(view.text()).toContain('Zůstáváš v místě zdroje, který teď vidíš');
    expect(view.findAll('.sealed-draft label').some(item => item.text().startsWith('Místo'))).toBe(false);
    expect(api.transport.mock.calls.some(([, options]) => options.method === 'POST')).toBe(false);
    await resource.setValue('A1');
    await view.findAll('button').find(item => item.text() === 'Výslovně potvrdit plán')!.trigger('click');
    await flushPromises();
    const request = api.transport.mock.calls.find(([url, options]) => url.endsWith('/actions') && options.method === 'POST');
    expect(JSON.parse(request![1].body).plan).toMatchObject({ intent: 7, resourceId: 'A1', method: 'DELAY', targetId: null, destination: null });
    expect(JSON.stringify(JSON.parse(request![1].body).plan)).not.toContain('C9');
    expect(JSON.stringify(JSON.parse(request![1].body).plan)).not.toContain('REDIRECT');
  });

  it('preserves a valid adjacent move when switching local Investigate and Guard drafts, then clears it for DELAY', async () => {
    const view = await renderPlay(play('planning', { chapters: 3, choices: shortChoices }));
    await view.findAll('button').find(item => item.text() === 'Moje akce')!.trigger('click');
    await flushPromises();
    await view.findAll('.sealed-choices button').find(item => item.text().startsWith('Pátrat'))!.trigger('click');
    await flushPromises();
    const destination = () => view.findAll('label').find(item => item.text().startsWith('Místo'))!.find('select');
    await destination().setValue('SALON');
    await view.findAll('.sealed-choices button').find(item => item.text().startsWith('Chránit sebe'))!.trigger('click');
    await flushPromises();
    expect((destination().element as HTMLSelectElement).value).toBe('SALON');
    await view.findAll('.sealed-choices button').find(item => item.text().startsWith('Pátrat'))!.trigger('click');
    await flushPromises();
    expect((destination().element as HTMLSelectElement).value).toBe('SALON');
    await view.findAll('.sealed-choices button').find(item => item.text().startsWith('Zdržet stopu'))!.trigger('click');
    await flushPromises();
    expect(view.findAll('label').some(item => item.text().startsWith('Místo'))).toBe(false);
    expect(api.transport.mock.calls.some(([, options]) => options.method === 'POST')).toBe(false);
  });

  it('explains missing visible sources before allowing a delay request', async () => {
    const view = await renderPlay(play('planning', { chapters: 3, choices: shortChoices, sources: [] }));
    await view.findAll('button').find(item => item.text() === 'Moje akce')!.trigger('click');
    await flushPromises();
    expect(view.text()).toContain('v tomto místě nevidíš žádnou uvolněnou stopu');
    expect(view.findAll('.sealed-choices button')[2]!.attributes('disabled')).toBeDefined();
    expect(api.transport.mock.calls.some(([, options]) => options.method === 'POST')).toBe(false);
  });

  it('blocks a restored short delay draft when its source is no longer in the permitted view', async () => {
    const value = play('planning', { chapters: 3, choices: shortChoices,
      sources: [{ id: 'A1', label: 'Záznam z archivní knihy' }] });
    localStorage.setItem(`dv:sealed-draft:one:${value.id}`, JSON.stringify({ draft: {
      chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 7, resourceId: 'C9', method: 'DELAY',
    } }));
    const view = await renderPlay(value);
    await view.findAll('button').find(item => item.text() === 'Moje akce')!.trigger('click');
    await flushPromises();
    expect(view.text()).toContain('Vyber jednu z uvolněných stop, které teď vidíš v tomto místě.');
    expect(view.findAll('button').find(item => item.text() === 'Výslovně potvrdit plán')!.attributes('disabled')).toBeDefined();
    expect(api.transport.mock.calls.some(([, options]) => options.method === 'POST')).toBe(false);
  });

  it('cleans a changed full-game draft locally without replacing the saved plan before confirmation', async () => {
    const value = play('planning', { ownPlan: { id: 'saved', actorId: 'one', chapter: 1, revision: 2,
      expectedActionRevision: 1, confirmed: true, intent: 4 } });
    const storageKey = `dv:sealed-draft:one:${value.id}`;
    const draft = { chapter: 1, expectedActionRevision: 2, confirmed: true, intent: 4, targetId: 'two',
      resourceId: 'STALE', method: 'REDIRECT', preparedIntent: 6, destination: null };
    localStorage.setItem(storageKey, JSON.stringify({ draft }));
    const view = await renderPlay(value);
    await view.findAll('button').find(item => item.text() === 'Moje akce')!.trigger('click');
    await flushPromises();
    const type = view.findAll('label').find(item => item.text().includes('Proveditelný typ akce'))!.find('select');
    expect(type.find('option[value="4"]').attributes('disabled')).toBeUndefined();
    await type.setValue('2');
    await type.trigger('change');
    await flushPromises();
    const saved = JSON.parse(localStorage.getItem(storageKey)!);
    expect(saved.draft).toMatchObject({ intent: 2, targetId: null, resourceId: null, method: null, preparedIntent: null });
    await type.setValue('4');
    await type.trigger('change');
    await flushPromises();
    expect(JSON.parse(localStorage.getItem(storageKey)!).draft).toMatchObject({ intent: 4, targetId: null, resourceId: null, method: null });
    expect(view.find('.sealed-receipt').text()).toContain('Pátrat');
    expect(api.transport.mock.calls.some(([, options]) => options.method === 'POST')).toBe(false);
  });

  it('preserves full-game Guard targets and makes REDIRECT target method-dependent', async () => {
    const value = play('planning', { choices: [
      { id: 'guard-other', label: 'Chránit postavu', benefit: 'Ochrana.', cost: 'Jeden tah.', risk: 'Dosah se ověří.',
        plan: { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 2, targetId: 'two' } },
      { id: 'sabotage', label: 'Sabotovat', benefit: 'Zásah.', cost: 'Jeden tah.', risk: 'Může selhat.',
        plan: { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 7, resourceId: 'A1', method: 'REDIRECT', targetId: 'two' } },
    ] });
    const view = await renderPlay(value);
    await view.findAll('button').find(item => item.text() === 'Moje akce')!.trigger('click');
    await flushPromises();
    await view.findAll('.sealed-choices button').find(item => item.text().includes('Chránit postavu'))!.trigger('click');
    await flushPromises();
    let target = view.findAll('label').find(item => item.text().includes('Adresát / cíl'))!.find('select');
    expect((target.element as HTMLSelectElement).value).toBe('two');
    await view.findAll('.sealed-choices button').find(item => item.text().includes('Sabotovat'))!.trigger('click');
    await flushPromises();
    const method = view.findAll('label').find(item => item.text().includes('Metoda sabotáže'))!.find('select');
    expect((method.element as HTMLSelectElement).value).toBe('REDIRECT');
    target = view.findAll('label').find(item => item.text().includes('Adresát / cíl'))!.find('select');
    expect((target.element as HTMLSelectElement).value).toBe('two');
    await method.setValue('DELAY');
    await method.trigger('change');
    await flushPromises();
    expect(view.findAll('label').some(item => item.text().includes('Adresát / cíl'))).toBe(false);
    expect(api.transport.mock.calls.some(([, options]) => options.method === 'POST')).toBe(false);
  });

  it('keeps own sabotage feedback tied to its chapter and leaves missing evidence unknown', async () => {
    const base = { chapter: 3, actor: 'one', target: null, intent: 'Sabotage', result: 'Tah se vyhodnotil', eventRef: '' };
    let view = await renderPlay(play('planning', { ownOutcomes: [{ ...base, actionObservation: { actionRef: 'action-3', chapter: 3, kind: 'DELAY_SUCCEEDED' } }] }));
    expect(view.text()).toContain('Tvá zaznamenaná zkušenost potvrzuje úspěšné zdržení');
    view.unmount(); wrapper = undefined;
    view = await renderPlay(play('planning', { ownOutcomes: [{ ...base, actionObservation: { actionRef: 'action-2', chapter: 2, kind: 'DELAY_SUCCEEDED' } }] }));
    expect(view.text()).toContain('Výsledek zdržení zatím z tvé zkušenosti neznáš');
    expect(view.text()).not.toContain('potvrzuje úspěšné zdržení');
    view.unmount(); wrapper = undefined;
    view = await renderPlay(play('planning', { ownOutcomes: [{ ...base, actionObservation: { actionRef: 'action-3', chapter: 3, kind: 'DELAY_NOT_OBSERVED' } }] }));
    expect(view.text()).toContain('Výsledek zůstává neznámý');
    expect(view.text()).not.toContain('prokázan');
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
    for (const label of ['Adresát / cíl', 'Místo']) {
      expect(labels.some(text => text.includes(label))).toBe(true);
    }
    expect(labels.some(text => text.includes('Viditelná stopa k pátrání (volitelné)'))).toBe(true);
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

  it('keeps the investigate source selector limited to currently visible sources', async () => {
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
    const source = view.findAll('label').find(item => item.text().includes('Viditelná stopa k pátrání'))!.find('select');
    expect(source.findAll('option').map(option => option.element.value)).toEqual(['Bez určení zdroje', 'A1']);
    await source.setValue('A1');
    await button('Výslovně potvrdit plán').trigger('click');
    await flushPromises();
    const request = api.transport.mock.calls.find(([url, options]) => url.endsWith('/actions') && options.method === 'POST');
    expect(JSON.parse(request![1].body).plan).toMatchObject({ intent: 4, resourceId: 'A1' });

    await button('Deník').trigger('click');
    await flushPromises();
    await button('Doložené stopy').trigger('click');
    await flushPromises();
    expect(view.findAll('.sealed-evidence-part summary').map(heading => heading.text()).some(text => text.includes('Stopa 2'))).toBe(true);
  });

  it('prepares a clue statement locally, preserves authored text, and clears it on an account change', async () => {
    const value = play();
    const view = await renderPlay(value);
    const button = (label: string) => view.findAll('button').find(item => item.text().includes(label))!;
    await button('Rozhovory').trigger('click');
    await view.get('.sealed-message-form textarea').setValue('Moje vlastní poznámka.');
    await button('Deník').trigger('click');
    await button('Doložené stopy').trigger('click');
    const requestsBefore = api.transport.mock.calls.length;
    await button('Připravit sdílení části A').trigger('click');
    await flushPromises();
    expect(api.transport.mock.calls).toHaveLength(requestsBefore);
    expect((view.get('.sealed-message-form textarea').element as HTMLTextAreaElement).value)
      .toBe('Moje vlastní poznámka.\n\nStopa A1 · řetězec 1 · část A.\nOkruh kandidátů: Host 2.');
    expect(view.text()).toContain('zatím se nic neodeslalo');
    expect(view.text()).not.toContain(marker.privateSource);
    api.setAccount({ id: 'different' });
    await flushPromises();
    if (view.find('.sealed-message-form textarea').exists()) {
      expect((view.get('.sealed-message-form textarea').element as HTMLTextAreaElement).value).toBe('');
    }
  });

  it('renders each reference from one statement without adding it to owned evidence', async () => {
    const value = play('planning', { statements: [{ id: 'shared-two', actor: 'two', public: true, recipients: [], createdAt: 1,
      body: 'Moje vlastní poznámka.\n\nStopa A1 · řetězec 1 · část A.\nOkruh kandidátů: Host 2.\n\nStopa B1 · řetězec 1 · část B.\nOkruh kandidátů: Host 1.' }] });
    const view = await renderPlay(value);
    await view.findAll('button').find(item => item.text() === 'Deník')!.trigger('click');
    await view.findAll('button').find(item => item.text() === 'Doložené stopy')!.trigger('click');
    const claims = view.findAll('.sealed-claims li');
    expect(claims).toHaveLength(2);
    expect(claims[0]!.text()).toContain('Uvádí řetězec 1, část A.');
    expect(claims[1]!.text()).toContain('Uvádí řetězec 1, část B.');
    expect(claims[0]!.text()).toContain('Stopu A1 máš i ve vlastním spisu.');
    expect(claims[1]!.text()).toContain('Tuto stopu ve vlastním spisu nemáš.');
    expect(view.get('.sealed-chain-reading').text()).toContain('Chybí B a C.');
  });

  it('distinguishes full-statement disclosures for repeated references by the same author', async () => {
    const reference = 'Stopa A1 · řetězec 1 · část A.';
    const view = await renderPlay(play('planning', { statements: [
      { id: 'first', actor: 'two', public: true, recipients: [], createdAt: 1, body: `${reference}\n${reference}` },
      { id: 'second', actor: 'two', public: true, recipients: [], createdAt: 2, body: reference },
    ] }));
    await view.findAll('button').find(item => item.text() === 'Deník')!.trigger('click');
    await view.findAll('button').find(item => item.text() === 'Doložené stopy')!.trigger('click');
    const names = view.findAll('.sealed-claim-text summary').map(summary => summary.attributes('aria-label'));
    expect(names).toHaveLength(3);
    expect(new Set(names).size).toBe(3);
    expect(names.every(name => name?.includes('Host 2, řetězec 1, část A'))).toBe(true);
    expect(view.findAll('.sealed-claim-text').every(disclosure => !disclosure.element.hasAttribute('open'))).toBe(true);
  });

  it.each([
    { intent: 2, chapter: 1 }, { intent: 10, chapter: 1 }, { intent: 3, chapter: 1 }, { intent: 10, chapter: 2 },
  ])('changes only the destination of an existing draft (intent $intent, chapter $chapter)', async ({ intent, chapter }) => {
    const value = play('planning', { chapter, routes: { ARCHIV: ['SALON'] },
      sourceSchedule: [{ family: 'C', label: 'Závěrečný záznam', location: 'SALON', releaseChapter: 3 }] });
    const draft: Plan = { chapter: 1, expectedActionRevision: 0, confirmed: true, intent, targetId: 'two',
      destination: null, defensePolicy: 1, shelteredPosition: true, resourceId: null,
      condition: intent === 10 ? { kind: 0 } : null, ...(intent === 3 ? { preparedIntent: 6 } : {}) };
    const storageKey = `dv:sealed-draft:one:${value.id}`;
    localStorage.setItem(storageKey, JSON.stringify({ draft, personalText: 'Můj rozepsaný záměr.' }));
    const view = await renderPlay(value);
    const button = (label: string) => view.findAll('button').find(item => item.text().includes(label))!;
    await button('Moje akce').trigger('click');
    const requestsBefore = api.transport.mock.calls.length;
    await button('Připravit přesun: Salon').trigger('click');
    await flushPromises();
    expect(api.transport.mock.calls).toHaveLength(requestsBefore);
    const expectedDraft = { ...draft, destination: 'SALON', ...(intent === 3 ? { targetId: null } : {}) };
    expect(JSON.parse(localStorage.getItem(storageKey)!)).toMatchObject({ draft: expectedDraft, personalText: 'Můj rozepsaný záměr.' });
    const confirmation = button('Výslovně potvrdit plán');
    if (chapter !== draft.chapter) {
      expect(confirmation.attributes('disabled')).toBeDefined();
      expect(view.text()).toContain('Tento návrh se sám nepřenese.');
    } else {
      await confirmation.trigger('click');
      await flushPromises();
      const action = api.transport.mock.calls.find(([url, options]) => url.endsWith('/actions') && options.method === 'POST');
      expect(JSON.parse(action![1].body).plan).toMatchObject({ ...expectedDraft, summary: 'Můj rozepsaný záměr.' });
    }
  });

  it('keeps Hold stationary and blocks a manually selected destination without changing the intent', async () => {
    const value = play('planning', { routes: { ARCHIV: ['SALON'] },
      sourceSchedule: [{ family: 'C', label: 'Závěrečný záznam', location: 'SALON', releaseChapter: 3 }] });
    const storageKey = `dv:sealed-draft:one:${value.id}`;
    localStorage.setItem(storageKey, JSON.stringify({ draft: { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 0 } }));
    const view = await renderPlay(value);
    await view.findAll('button').find(item => item.text() === 'Moje akce')!.trigger('click');
    expect(view.findAll('button').some(item => item.text().startsWith('Připravit přesun:'))).toBe(false);
    expect(view.text()).toContain('Při vyčkávání se nepřesouváš.');
    const confirmation = view.findAll('button').find(item => item.text() === 'Výslovně potvrdit plán')!;
    expect(confirmation.attributes('disabled')).toBeUndefined();
    await view.findAll('label').find(item => item.text().startsWith('Místo'))!.find('select').setValue('SALON');
    expect(confirmation.attributes('disabled')).toBeDefined();
    expect(view.text()).toContain('Vyčkávání neumožňuje přesun.');
    expect(JSON.parse(localStorage.getItem(storageKey)!).draft.intent).toBe(0);
    expect(api.transport.mock.calls.some(([, options]) => options.method === 'POST')).toBe(false);
  });

  it('shows a future public source route before it opens and prepares only its first adjacent move', async () => {
    const value = play('planning', {
      chapter: 1, chapters: 3, location: 'VESTIBUL', adjacentLocations: ['SALON'],
      locations: { VESTIBUL: 'Vstupní síň', SALON: 'Salon', KOMORA: 'Komora' },
      routes: { VESTIBUL: ['SALON'], SALON: ['VESTIBUL', 'KOMORA'], KOMORA: ['SALON'] },
      sourceSchedule: [{ family: 'C', label: 'Závěrečný záznam', location: 'KOMORA', releaseChapter: 3 }],
    });
    const view = await renderPlay(value);
    expect(view.text()).toContain('od kapitoly 3');
    expect(view.get('.sealed-route').text()).toContain('Vstupní síňSalonKomora');
    const button = (label: string) => view.findAll('button').find(item => item.text().includes(label))!;
    await button('Moje akce').trigger('click');
    const requestsBefore = api.transport.mock.calls.length;
    await button('Připravit přesun: Salon').trigger('click');
    await flushPromises();
    expect(api.transport.mock.calls).toHaveLength(requestsBefore);
    expect(view.get('.sealed-draft').text()).toContain('Zatím není odeslaný');
    const destination = view.findAll('label').find(item => item.text().startsWith('Místo'))!.find('select');
    expect((destination.element as HTMLSelectElement).value).toBe('SALON');
    expect(JSON.parse(localStorage.getItem(`dv:sealed-draft:one:${value.id}`)!).draft.intent).toBe(4);
  });

  it.each(['Echo', 'Unconscious', 'Sleeping'])('does not offer physical route plans to a player in state %s', async ownState => {
    const view = await renderPlay(play('planning', { ownState, routes: { ARCHIV: ['SALON'] },
      sourceSchedule: [{ family: 'C', label: 'Závěrečný záznam', location: 'SALON', releaseChapter: 3 }] }));
    expect(view.find('.sealed-exploration').exists()).toBe(false);
    await view.findAll('button').find(item => item.text() === 'Moje akce')!.trigger('click');
    expect(view.find('.sealed-exploration').exists()).toBe(false);
    expect(view.findAll('button').some(item => item.text().startsWith('Připravit přesun'))).toBe(false);
  });

  it('warns about a route that cannot fit the remaining chapter and shows safe own resolution', async () => {
    const view = await renderPlay(play('planning', {
      chapter: 3, chapters: 3, location: 'VESTIBUL',
      routes: { VESTIBUL: ['SALON'], SALON: ['KOMORA'] },
      sourceSchedule: [{ family: 'C', label: 'Závěrečný záznam', location: 'KOMORA', releaseChapter: 3 }],
      ownOutcomes: [{ chapter: 2, actor: 'one', target: null, intent: 'Investigate', result: 'Tah se vyhodnotil',
        eventRef: '', observation: 'Znovu jsi narazil na již známou stopu.' }],
    }));
    expect(view.text()).toContain('do konce nestihneš');
    expect(view.text()).toContain('Tah se vyhodnotil');
    expect(view.text()).toContain('Znovu jsi narazil na již známou stopu.');
    expect(view.text()).not.toContain('výsledek zatím neznáš');
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
