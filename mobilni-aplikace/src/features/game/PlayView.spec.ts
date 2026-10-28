import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createRouter, createMemoryHistory } from 'vue-router';
const api = vi.hoisted(() => ({ transport: vi.fn(), request: vi.fn(), setAccount: (_value: unknown) => {} }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue'); const account = ref<unknown>({ id: 'one' });
  api.setAccount = value => { account.value = value; };
  return { account, accountOrigin: ref('https://app.invalid'), accountTransport: api.transport, accountRequest: api.request };
});
import PlayView from './PlayView.vue';
import WandWorkshop from './WandWorkshop.vue';
import { practiceRound, type Match } from './game';
const id = 'a'.repeat(32);
const snapshot = (patch: Partial<Match> = {}): Match => ({ id, mode: 'quick', status: 'active', revision: 2,
  opponent: { id: 'two', displayName: 'Eliška', xp: 20, matchesPlayed: 2 }, rounds: [],
  completedRounds: 0, totalRounds: 7, block: 1, playerScore: 0, opponentScore: 0,
  playerSparks: 2, opponentSparks: 2, ownChoiceLocked: false, opponentChoiceLocked: false,
  tasksEnabled: false, adultConsent: false, flirtConsent: false, createdAt: 1, xpAwarded: 0, ...patch });
let wrapper: VueWrapper | undefined;
async function render(path = `/play/match/${id}`) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/play/match/:id', component: PlayView }, { path: '/:pathMatch(.*)*', component: PlayView }] });
  await router.push(path); wrapper = mount(PlayView, { global: { plugins: [router] } }); await flushPromises(); return wrapper;
}
beforeEach(() => { api.transport.mockReset(); api.request.mockReset(); api.request.mockResolvedValue({hasCharacter: true, wand: {wandId: 'saved'}, tier: 1}); api.setAccount({ id: 'one', openAiConsent: true }); });
afterEach(() => { wrapper?.unmount(); vi.restoreAllMocks(); });
describe('mobile game interaction', () => {
  it('presents real server scores, identities, round count and stock using the responsive table art', async () => {
    api.setAccount({ id: 'one', displayName: 'Vojta' });
    api.transport.mockResolvedValue(Response.json(snapshot({ mode: 'tactical', playerScore: 2, opponentScore: 3,
      completedRounds: 4, totalRounds: 8, playerInventory: { strike: 2, trick: 1, shield: 0 } })));
    const view = await render();
    expect(view.classes()).toContain('responsive-table');
    expect(view.get('.game-score--duel').text()).toContain('Vojta');
    expect(view.get('.game-score--duel').text()).toContain('Eliška');
    expect(view.findAll('.game-score--duel strong').map(score => score.text())).toEqual(['2', '3']);
    expect(view.findAll('.game-round-dots > span')).toHaveLength(8);
    expect(view.findAll('.game-round-dots > .done')).toHaveLength(4);
    expect(view.get('input[value="shield"]').attributes('disabled')).toBeDefined();
    expect(view.get('input[value="trick"]').element.closest('label')?.textContent).toContain('1 použití');
    expect(view.findAll('.game-spell-token')).toHaveLength(3);
    expect(view.get('.game-spell-token').attributes('src')).toContain('token-strike.webp');
    expect(view.find('img[src*="mira"]').exists()).toBe(false);
    await view.get('input[value="trick"]').setValue();
    expect(api.transport).toHaveBeenCalledTimes(1);
    expect(view.findAll('.game-score--duel strong').map(score => score.text())).toEqual(['2', '3']);
  });

  it('keeps the original seven-round practice contract and explains the real cyclic rules', async () => {
    const view = await render('/play/practice');
    expect(view.findAll('.game-round-dots > span')).toHaveLength(7);
    expect(view.get('.game-header').text()).toContain('Trénink · bez XP');
    expect(view.get('.game-rules').text()).toContain('Úder přeruší lest');
    expect(view.find('.game-spark').exists()).toBe(false);
    expect(api.transport).not.toHaveBeenCalled();
  });

  it('selects locally and commits action and spark together only after confirmation', async () => {
    api.transport.mockImplementation(async (_path: string, init: RequestInit) => Response.json(snapshot(init.method === 'POST' ? { revision: 3, ownChoiceLocked: true } : {})));
    const view = await render();
    await view.get('input[value="trick"]').setValue();
    await view.get('.game-spark input').setValue(true);
    expect(api.transport).toHaveBeenCalledTimes(1);
    await view.findAll('button').find(button => button.text() === 'Potvrdit tah')!.trigger('click'); await flushPromises();
    expect(JSON.parse(api.transport.mock.calls[1][1].body)).toMatchObject({ expectedRevision: 2, choice: 'trick', sparkBid: 1 });
    expect(view.text()).toContain('Tvůj tah je uzamčený');
    expect(view.find('input[value="trick"]').exists()).toBe(false);
  });
  it('preserves an uncertain submission, announces the failure and disables a second commit', async () => {
    api.transport.mockResolvedValueOnce(Response.json(snapshot())).mockRejectedValue(new TypeError('offline'));
    const view = await render(); await view.get('input[value="shield"]').setValue();
    await view.findAll('button').find(button => button.text() === 'Potvrdit tah')!.trigger('click'); await flushPromises();
    expect(view.get('[role="alert"]').text()).toContain('Spojení se přerušilo');
    expect(view.findAll('button').find(button => button.text() === 'Potvrdit tah')!.attributes('disabled')).toBeDefined();
  });
  it('fences a late response on account change', async () => {
    let finish!: (response: Response) => void;
    api.transport.mockReturnValueOnce(new Promise(resolve => { finish = resolve; })).mockRejectedValue(new Error('signed out'));
    const view = await render(); api.setAccount(null); finish(Response.json(snapshot())); await flushPromises();
    expect(view.text()).not.toContain('Eliška');
  });
  it('does not let an older poll unlock a successfully committed choice', async () => {
    let finishPoll!: (response: Response) => void;
    api.transport.mockImplementation(async (_path: string, init: RequestInit) => {
      if (init.method === 'POST') return Response.json(snapshot({ revision: 3, ownChoiceLocked: true }));
      if (api.transport.mock.calls.length === 1) return Response.json(snapshot());
      return new Promise<Response>(resolve => { finishPoll = resolve; });
    });
    const view = await render();
    window.dispatchEvent(new Event('online')); await flushPromises();
    await view.get('input[value="strike"]').setValue();
    await view.findAll('button').find(button => button.text() === 'Potvrdit tah')!.trigger('click'); await flushPromises();
    finishPoll(Response.json(snapshot({ revision: 2, ownChoiceLocked: false }))); await flushPromises();
    expect(view.text()).toContain('Tvůj tah je uzamčený');
    expect(view.find('input[value="strike"]').exists()).toBe(false);
  });
  it('accepts a challenge without asking for per-match task or content consent', async () => {
    api.transport.mockResolvedValue(Response.json(snapshot({ status: 'incoming', canAccept: true })));
    const view = await render();
    expect(view.find('input[type="checkbox"]').exists()).toBe(false);
    expect(view.text()).not.toContain('Je mi 18+');
    expect(view.text()).toContain('Po prohře se zobrazí úkol');
    await view.findAll('button').find(button => button.text() === 'Přijmout výzvu')!.trigger('click');
    await flushPromises();
    const body = JSON.parse(api.transport.mock.calls[1][1].body);
    expect(body).toMatchObject({ expectedRevision: 2, useLoadouts: false });
    expect(body).not.toHaveProperty('tasksEnabled');
    expect(body).not.toHaveProperty('adultConsent');
    expect(body).not.toHaveProperty('flirtConsent');
  });
  it('keeps the active first-wand voice mounted when polling discovers its saved wand', async () => {
    api.request.mockResolvedValue({hasCharacter:true,wand:null,tier:0});
    api.transport.mockResolvedValue(Response.json({player:{id:'one',displayName:'Mira',xp:0,matchesPlayed:0},opponents:[],active:[],incoming:[],history:[]}));
    const view = await render('/play');
    const workshop = view.findComponent(WandWorkshop);
    const workshopElement = workshop.element;
    expect(workshop.exists()).toBe(true); expect(view.find('input[type="radio"]').exists()).toBe(false);
    workshop.vm.$emit('active-change', true); await flushPromises();
    api.request.mockResolvedValue({hasCharacter:true,wand:{wandId:'saved'},tier:1});
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(view.findComponent(WandWorkshop).element).toBe(workshopElement);
  });
  it('offers practice without making game requests or awarding XP', async () => {
    vi.spyOn(crypto, 'getRandomValues').mockImplementation(array => { (array as Uint32Array).fill(0); return array; });
    const view = await render('/play/practice');
    for (let index = 0; index < 7; index++) {
      await view.get('input[value="strike"]').setValue();
      await view.findAll('button').find(button => button.text() === 'Potvrdit tah')!.trigger('click');
    }
    expect(view.text()).toContain('Trénink dokončen'); expect(api.transport).not.toHaveBeenCalled();
    expect(view.text()).not.toMatch(/\+\d+ XP/);
  });

  it('allows confirmation of a saved task without granting gameplay to a player without a wand', async () => {
    api.request.mockResolvedValue({ hasCharacter: true, wand: null, tier: 0 });
    const saved = snapshot({ status: 'complete', result: 'loss', partyTask: {
      title: 'Uložený úkol', text: 'Předveď reklamu.', category: 'classic', status: 'pending',
    } });
    api.transport.mockImplementation(async (_path: string, init: RequestInit) => Response.json(init.method === 'POST'
      ? { ...saved, revision: 3, partyTask: { ...saved.partyTask!, status: 'done' } } : saved));
    const view = await render();
    expect(view.findComponent(WandWorkshop).exists()).toBe(false);
    expect(view.get('.game-task').text()).toContain('Uložený úkol');
    expect(view.find('input[value="strike"]').exists()).toBe(false);
    await view.get('input[value="done"]').setValue();
    await view.findAll('button').find(button => button.text() === 'Potvrdit výsledek úkolu')!.trigger('click');
    await flushPromises();
    expect(api.transport.mock.calls[1][0]).toContain(`/matches/${id}/task`);
    expect(view.get('.game-task').text()).toContain('Uloženo: úkol splněn.');
  });

  it('shows pending saved tasks alongside the workshop without a wand', async () => {
    api.request.mockResolvedValue({ hasCharacter: true, wand: null, tier: 0 });
    api.transport.mockResolvedValue(Response.json({
      player: { id: 'one', displayName: 'Mira', xp: 0, matchesPlayed: 1 }, opponents: [], incoming: [], active: [],
      history: [snapshot({ status: 'complete', result: 'loss', partyTask: { title: 'Čekající úkol', text: 'Předveď reklamu.', category: 'classic', status: 'pending' } })],
    }));
    const view = await render('/play');
    expect(view.findComponent(WandWorkshop).exists()).toBe(true);
    expect(view.text()).toContain('Úkoly k potvrzení');
    expect(view.findAll('a').find(link => link.text().includes('Potvrdit výsledek'))!.attributes('href')).toBe(`/play/match/${id}`);
    expect(view.find('input[value="strike"]').exists()).toBe(false);
  });

  it.each(['player', 'opponent'])('ends practice once %s wins four exchanges', async winner => {
    vi.spyOn(crypto, 'getRandomValues').mockImplementation(array => { (array as Uint32Array).fill(winner === 'player' ? 1 : 2); return array; });
    const view = await render('/play/practice');
    for (let index = 0; index < 4; index++) {
      await view.get('input[value="strike"]').setValue();
      await view.findAll('button').find(button => button.text() === 'Potvrdit tah')!.trigger('click');
    }
    expect(view.text()).toContain('Trénink dokončen');
    expect(view.findAll('.game-round-dots > .done')).toHaveLength(4);
    expect(view.find('input[value="strike"]').exists()).toBe(false);
    expect(api.transport).not.toHaveBeenCalled();
  });

  it.each(['done', 'skip'] as const)('requires explicit confirmation and retains saved task outcome %s after reload', async outcome => {
    let saved = snapshot({ status: 'complete', completedRounds: 4, playerScore: 0, opponentScore: 4,
      result: 'loss', tasksEnabled: true, adultConsent: true, flirtConsent: true,
      partyTask: { title: 'Reklama po půlnoci', text: 'Předveď reklamu na svou hůlku.', category: 'adult', status: 'pending' } });
    api.transport.mockImplementation(async (_path: string, init: RequestInit) => {
      if (init.method === 'POST') saved = { ...saved, revision: 3, partyTask: { ...saved.partyTask!, status: outcome === 'done' ? 'done' : 'skipped' } };
      return Response.json(saved);
    });
    const view = await render();
    expect(view.get('.game-task').text()).toContain('Reklama po půlnoci');
    expect(view.text()).not.toContain('Odvolat souhlas');
    expect(view.text()).not.toContain('Jiný úkol');
    expect(view.findAll('a').some(link => link.text() === 'Odveta')).toBe(false);
    const confirm = view.findAll('button').find(button => button.text() === 'Potvrdit výsledek úkolu')!;
    expect(confirm.attributes('disabled')).toBeDefined();
    await view.get(`input[value="${outcome}"]`).setValue();
    expect(api.transport).toHaveBeenCalledTimes(1);
    await confirm.trigger('click'); await flushPromises();
    expect(JSON.parse(api.transport.mock.calls[1][1].body)).toMatchObject({ expectedRevision: 2, taskAction: outcome });
    const expected = outcome === 'done' ? 'Uloženo: úkol splněn.' : 'Uloženo: úkol nesplněn.';
    expect(view.get('.game-task').text()).toContain(expected);
    expect(view.findAll('a').some(link => link.text() === 'Odveta')).toBe(true);
    view.unmount();
    const reloaded = await render();
    expect(reloaded.get('.game-task').text()).toContain(expected);
    expect(reloaded.find('.game-task-outcome').exists()).toBe(false);
  });

  it('clears a selected task outcome when navigating directly to another match', async () => {
    const secondId = 'b'.repeat(32);
    api.transport.mockImplementation(async (path: string) => Response.json(snapshot({
      id: path.includes(secondId) ? secondId : id, status: 'complete', result: 'loss',
      partyTask: { title: path.includes(secondId) ? 'Druhý úkol' : 'První úkol', text: 'Předveď reklamu.', category: 'classic', status: 'pending' },
    })));
    const view = await render();
    await view.get('input[value="done"]').setValue();
    await view.vm.$router.push(`/play/match/${secondId}`);
    await flushPromises();
    expect(view.get('.game-task').text()).toContain('Druhý úkol');
    expect(view.get('input[value="done"]').element).toHaveProperty('checked', false);
    expect(view.findAll('button').find(button => button.text() === 'Potvrdit výsledek úkolu')!.attributes('disabled')).toBeDefined();
    expect(api.transport.mock.calls.every(([, init]) => init.method === 'GET')).toBe(true);
  });

  it('keeps an unacknowledged task discoverable after returning to the lobby', async () => {
    api.transport.mockImplementation(async (path: string) => Response.json(path.endsWith('/progress') ? { pending: [] } : {
      player: { id: 'one', displayName: 'Mira', xp: 0, matchesPlayed: 1 }, opponents: [], incoming: [], active: [],
      history: [snapshot({ status: 'complete', result: 'loss', partyTask: { title: 'Čekající úkol', text: 'Předveď reklamu.', category: 'classic', status: 'pending' } })],
    }));
    const view = await render('/play');
    expect(view.text()).toContain('Úkoly k potvrzení');
    const taskLink = view.findAll('a').find(link => link.text().includes('Potvrdit výsledek'))!;
    expect(taskLink.attributes('href')).toBe(`/play/match/${id}`);
  });
});
it('explains all nine cyclic practice outcomes including consumed ties', () => {
  for (const left of ['strike', 'trick', 'shield'] as const) for (const right of ['strike', 'trick', 'shield'] as const) {
    const round = practiceRound(left, right);
    const inverse = practiceRound(right, left);
    expect(round.winner === 'draw').toBe(left === right);
    if (left !== right) expect(round.winner).not.toBe(inverse.winner);
  }
  expect(practiceRound('strike', 'trick').winner).toBe('player');
  expect(practiceRound('trick', 'shield').winner).toBe('player');
  expect(practiceRound('shield', 'strike').winner).toBe('player');
});
