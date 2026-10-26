import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createRouter, createMemoryHistory } from 'vue-router';
const api = vi.hoisted(() => ({ transport: vi.fn(), setAccount: (_value: unknown) => {} }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue'); const account = ref<unknown>({ id: 'one' });
  api.setAccount = value => { account.value = value; };
  return { account, accountTransport: api.transport };
});
import PlayView from './PlayView.vue';
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
beforeEach(() => { api.transport.mockReset(); api.setAccount({ id: 'one' }); });
afterEach(() => { wrapper?.unmount(); vi.restoreAllMocks(); });
describe('mobile game interaction', () => {
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
  it('keeps Adult and Flirt unselected before accepting a challenge', async () => {
    api.transport.mockResolvedValue(Response.json(snapshot({ status: 'incoming', canAccept: true })));
    const view = await render();
    expect(view.find('input[type="checkbox"]').element).toHaveProperty('checked', false);
    expect(view.text()).not.toContain('Je mi 18+');
    await view.get('input[type="checkbox"]').setValue(true);
    const adult = view.findAll('input[type="checkbox"]')[1]; expect(adult.element).toHaveProperty('checked', false);
  });
  it('offers practice without making game requests or awarding XP', async () => {
    const view = await render('/play/practice');
    for (let index = 0; index < 7; index++) {
      await view.get('input[value="strike"]').setValue();
      await view.findAll('button').find(button => button.text() === 'Potvrdit tah')!.trigger('click');
    }
    expect(view.text()).toContain('Trénink dokončen'); expect(api.transport).not.toHaveBeenCalled();
    expect(view.text()).not.toMatch(/\+\d+ XP/);
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
