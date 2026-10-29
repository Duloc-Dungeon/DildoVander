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
  it('keeps YOLO result announcements and the loser task out of the opening acts', async () => {
    const value = snapshot({ mode: 'yolo', status: 'complete', result: 'loss',
      yolo: { status: 'complete', beats: ['Nástup.', 'Střet.', 'Rozsudek.'].map(text => ({ text, detailRefs: [] })), verdict: 'Eliška vítězí.' },
      partyTask: { title: 'Úkol pro poraženého', text: 'Předveď reklamu.', category: 'classic', status: 'pending' } });
    api.transport.mockResolvedValue(Response.json(value));
    const view = await render();
    expect(view.get('.game-sr[role="status"]').text()).toContain('Příběh je připraven.');
    expect(view.find('.game-task').exists()).toBe(false);
    expect(view.text()).not.toContain('Dnes slaví soupeř.');
    await view.findAll('.yolo-theatre__reading-controls button')[1]!.trigger('click');
    expect(view.find('.game-task').exists()).toBe(true);
    expect(view.text()).toContain('Eliška vítězí.');
  });
  it('announces an asynchronously completed YOLO story without revealing its winner', async () => {
    const value = snapshot({ mode: 'yolo', status: 'generating', yolo: { status: 'generating', beats: [], verdict: '' } });
    api.transport.mockImplementation(async () => Response.json(value));
    const view = await render();
    expect(view.get('.game-sr[role="status"]').text()).toBe('');
    value.status = 'complete'; value.revision++;
    value.yolo = { status: 'complete', beats: ['Nástup.', 'Střet.', 'Rozsudek.'].map(text => ({ text, detailRefs: [] })), verdict: 'Eliška vítězí.' };
    window.dispatchEvent(new Event('online'));
    await flushPromises();
    expect(view.get('.game-sr[role="status"]').text()).toContain('Příběh je připraven.');
    expect(view.text()).not.toContain('Eliška vítězí.');
  });
  it('keeps earned game modes discoverable without exposing locked modes', async () => {
    const player = { id: 'one', displayName: 'Mira', xp: 10, matchesPlayed: 1, tacticalUnlocked: false, yoloUnlocked: false };
    let tacticalMatches = 0;
    api.transport.mockImplementation(async (path: string) => Response.json(path.endsWith('/progress')
      ? { pending: [], tacticalMatches }
      : { player, opponents: [], incoming: [], active: [], history: [] }));
    const view = await render('/play');
    expect(view.find('.game-mode-links a').exists()).toBe(false);
    player.tacticalUnlocked = true;
    player.yoloUnlocked = true;
    tacticalMatches = 3;
    window.dispatchEvent(new Event('online'));
    await flushPromises();
    const destinations = view.findAll('.game-mode-links a').map(link => link.attributes('href'));
    expect(destinations).toEqual(['/play/challenge?mode=tactical', '/play/teams', '/play/challenge?mode=yolo']);
    expect(view.get('.game-start a').attributes('href')).toBe('/play/challenge');
  });

  it('keeps the owner name Olivander even when the character has another label', async () => {
    api.setAccount({ id: 'one', displayName: 'Old name', roles: ['Owner'] });
    api.request.mockResolvedValue({ hasCharacter: true, wand: { wandId: 'saved' }, tier: 1, characterDisplayName: 'Jiná postava' });
    api.transport.mockResolvedValue(Response.json(snapshot()));
    const view = await render();
    expect(view.get('.game-score--duel').text()).toContain('Olivander');
    expect(view.get('.game-score--duel').text()).not.toContain('Jiná postava');
  });
  it('shows saved rankings without a wand and exposes progress failures instead of an empty ranking', async () => {
    api.request.mockResolvedValue({ hasCharacter: true, wand: null, tier: 0 });
    const lobby = { player: { id: 'one', displayName: 'Mira', xp: 0, matchesPlayed: 0 }, opponents: [], incoming: [], active: [], history: [] };
    let fail = true;
    api.transport.mockImplementation(async (path: string) => path.endsWith('/progress')
      ? fail ? Response.json({ code: 'unavailable' }, { status: 503 }) : Response.json({ pending: [], combatRanking: [{ id: 'one', displayName: 'Mira', xp: 10, wins: 0, rank: 1 }], yoloRanking: [] })
      : Response.json(lobby));
    const view = await render('/play/ranking');
    expect(view.text()).toContain('Pořadí a postup se nepodařilo obnovit');
    expect(view.find('.game-standings').exists()).toBe(false);
    expect(view.find('a[href="/play/wand"]').exists()).toBe(false);
    fail = false;
    await view.findAll('button').find(button => button.text() === 'Obnovit pořadí')!.trigger('click');
    await flushPromises();
    expect(view.get('.game-ranking-own').text()).toContain('Mira');
    expect(view.find('[role="alert"]').exists()).toBe(false);
  });

  it('applies challenge query mode immediately when navigating within the game', async () => {
    const lobby = { player: { id: 'one', displayName: 'Mira', xp: 50, matchesPlayed: 5, tacticalUnlocked: true, yoloUnlocked: true }, opponents: [{ id: 'two', displayName: 'Eliška', xp: 50, matchesPlayed: 5 }], incoming: [], active: [], history: [] };
    api.transport.mockImplementation(async (path: string) => Response.json(path.endsWith('/progress') ? { pending: [] } : lobby));
    const view = await render('/play/challenge');
    await view.vm.$router.push('/play/challenge?mode=yolo&opponent=two');
    await flushPromises();
    expect((view.get('input[value="yolo"]').element as HTMLInputElement).checked).toBe(true);
  });

  it('requires the selected opponent to have actually unlocked tactics', async () => {
    const lobby = { player: { id: 'one', xp: 50, matchesPlayed: 5, tacticalUnlocked: true }, opponents: [{ id: 'two', displayName: 'Eliška', xp: 50, matchesPlayed: 5, tacticalUnlocked: false }], incoming: [], active: [], history: [] };
    api.transport.mockImplementation(async (path: string) => Response.json(path.endsWith('/progress') ? { pending: [] } : lobby));
    const view = await render('/play/challenge?mode=tactical&opponent=two');
    expect(view.get('input[value="tactical"]').attributes('disabled')).toBeDefined();
    expect(view.findAll('button').find(button => button.text() === 'Poslat výzvu')!.attributes('disabled')).toBeDefined();
    expect(view.text()).toContain('hůlku II');
  });

  it('shows the reduced boss energy for an exhibition attempt', async () => {
    api.transport.mockResolvedValue(Response.json(snapshot({ mode: 'boss', exhibition: true, playerEnergy: 6, opponentEnergy: 2 })));
    const view = await render();
    expect(view.text()).toContain('Baronova energie2 / 3');
    expect(view.get('[role="meter"][aria-label="Baronova energie"]').attributes('aria-valuemax')).toBe('3');
    expect(view.get('[role="meter"][aria-label="Baronova energie"]').attributes('aria-valuenow')).toBe('2');
  });

  it('updates boss lighting and energy from the server without discarding an unsubmitted choice', async () => {
    let state = snapshot({ mode: 'boss', phase: 1, playerEnergy: 5.5, opponentEnergy: 4, hint: 'První nápověda.' });
    api.transport.mockImplementation(async () => Response.json(state));
    const view = await render();
    await view.get('input[value="strike"]').setValue();
    const portrait = view.get('.boss-theatre__figure').element;
    expect(view.attributes('data-boss-phase')).toBe('1');
    state = { ...state, revision: 3, phase: 3, playerEnergy: 2.5, opponentEnergy: .5, hint: 'Nová nápověda.' };
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(view.attributes('data-boss-phase')).toBe('3');
    expect(view.get('.boss-phase').text()).toBe('Fáze 3 ze 3');
    expect(view.get('.boss-phases [aria-current="step"]').attributes('aria-label')).toBe('Fáze 3');
    expect(view.get('.boss-tactics').text()).toContain('Nová nápověda.');
    expect(view.get('[role="meter"][aria-label="Tvoje energie"]').attributes('aria-valuenow')).toBe('2.5');
    expect(view.get('input[value="strike"]').element).toHaveProperty('checked', true);
    expect(view.get('.boss-theatre__figure').element).toBe(portrait);
  });

  it('keeps a locked boss turn visible without the regular duel canvas', async () => {
    api.transport.mockResolvedValue(Response.json(snapshot({ mode: 'boss', phase: 2, ownChoiceLocked: true })));
    const view = await render();
    expect(view.find('.game-clash-stage').exists()).toBe(false);
    expect(view.get('.game-controls .game-helper').text()).toBe('Tvůj tah je uzamčený. Čekáme na soupeře.');
    expect(view.find('input[name="spell"]').exists()).toBe(false);
  });

  it.each([
    ['player', 'Tvoje výměna.'], ['opponent', 'Soupeřova výměna.'], ['draw', 'Nerozhodná výměna.'],
  ])('names a published %s round without changing the saved match result', async (winner, title) => {
    api.transport.mockResolvedValue(Response.json(snapshot({ status: 'complete', result: 'win', playerScore: 5, opponentScore: 3,
      completedRounds: 7, rounds: [{ index: 7, playerChoice: 'strike', opponentChoice: winner === 'draw' ? 'strike' : winner === 'player' ? 'trick' : 'shield', winner }] })));
    const view = await render();
    expect(view.classes()).toContain('game-duel-scene');
    expect(view.attributes('data-duel-result')).toBe('win');
    expect(view.get('.duel-round-verdict').text()).toBe(title);
    expect(view.get('.game-result h2').text()).toBe('Tohle bylo tvoje.');
    expect(view.get('.game-clash-stage').classes()).toContain('game-magic-clash--theatre');
  });

  it('does not offer a challenge to a player who is still making a wand', async () => {
    const lobby = { player: { id: 'one', xp: 20, matchesPlayed: 2 }, opponents: [{ id: 'two', displayName: 'Eliška', xp: 50, matchesPlayed: 5, hasWand: false }], incoming: [], active: [], history: [] };
    api.transport.mockImplementation(async (path: string) => Response.json(path.endsWith('/progress') ? { pending: [] } : lobby));
    const view = await render('/play/challenge');
    expect(view.get('option[value="two"]').attributes('disabled')).toBeDefined();
    expect(view.findAll('button').find(button => button.text() === 'Poslat výzvu')!.attributes('disabled')).toBeDefined();
    expect(view.text()).toContain('Ostatní si zatím připravují hůlky');
  });

  it('uses separate display labels for both sides when opening a match directly', async () => {
    api.setAccount({ id: 'one', displayName: 'Mozkomor' });
    api.request.mockResolvedValue({ hasCharacter: true, wand: { wandId: 'saved' }, tier: 1, characterDisplayName: 'Mozkomor · Petr' });
    api.transport.mockResolvedValue(Response.json(snapshot({ opponent: { id: 'two', displayName: 'Mozkomor · Jana', xp: 20, matchesPlayed: 2 } })));
    const view = await render();
    expect(view.get('.game-score--duel').text()).toContain('Mozkomor · Petr');
    expect(view.get('.game-score--duel').text()).toContain('Mozkomor · Jana');
    expect(api.transport).toHaveBeenCalledTimes(1);
  });
  it('clears a label during a match even when an older lobby remains loaded', async () => {
    api.setAccount({ id: 'one', displayName: 'Mozkomor' });
    let label: string | null = 'Mozkomor · Petr';
    api.request.mockImplementation(async () => ({ hasCharacter: true, wand: { wandId: 'saved' }, tier: 1, characterDisplayName: label }));
    api.transport.mockImplementation(async (path: string) => Response.json(path.includes('/matches/') ? snapshot()
      : path.endsWith('/progress') ? { pending: [] }
        : { player: { id: 'one', displayName: 'Mozkomor · Petr', xp: 0, matchesPlayed: 1 }, opponents: [], incoming: [], active: [], history: [] }));
    const view = await render('/play');
    await view.vm.$router.push(`/play/match/${id}`); await flushPromises();
    expect(view.get('.game-score--duel').text()).toContain('Mozkomor · Petr');
    label = null;
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(view.get('.game-score--duel').text()).not.toContain('Petr');
    expect(view.get('.game-score--duel').text()).toContain('Mozkomor');
  });
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
  it('links first-wand creation to its own scene without mounting a voice session', async () => {
    api.request.mockResolvedValue({hasCharacter:true,wand:null,tier:0});
    api.transport.mockResolvedValue(Response.json({player:{id:'one',displayName:'Mira',xp:0,matchesPlayed:0},opponents:[],active:[],incoming:[],history:[]}));
    const view = await render('/play');
    expect(view.findComponent(WandWorkshop).exists()).toBe(false);
    expect(view.get('a[href="/play/wand"]').text()).toContain('kufrovy dílny');
    expect(view.find('input[type="radio"]').exists()).toBe(false);
    api.request.mockResolvedValue({hasCharacter:true,wand:{wandId:'saved'},tier:1});
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(view.find('a[href="/play/wand"]').exists()).toBe(false);
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
  it('lets a new guest reach the captured workshop rescue without a first wand', async () => {
    api.request.mockResolvedValue({ hasCharacter: true, wand: null, tier: 0 });
    const lobby = { player: { id: 'one', displayName: 'Mira', xp: 0, matchesPlayed: 0 }, opponents: [], incoming: [], active: [], history: [], boss: { revealed: true, rescued: false, referenceXp: 20, revision: 1 } };
    api.transport.mockImplementation(async (path: string) => Response.json(path.endsWith('/boss') ? lobby.boss : path.endsWith('/progress') ? { pending: [], keeperState: 'captured' } : lobby));
    const first = await render('/play');
    expect(first.get('a[href="/play/boss"]').text()).toBe('Osvobodit dílnu');
    expect(first.find('a[href="/play/wand"]').exists()).toBe(false);
    first.unmount();
    const boss = await render('/play/boss');
    expect(boss.text()).toContain('V exhibici dostaneš neomezená kouzla');
    expect(boss.text()).toContain('6 energie proti 3');
    expect(boss.findAll('button').some(button => button.text() === 'Vyzvat Barona')).toBe(true);
    expect(boss.find('a[href="/play/wand"]').exists()).toBe(false);
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
    expect(view.find('a[href="/play/wand"]').exists()).toBe(true);
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
