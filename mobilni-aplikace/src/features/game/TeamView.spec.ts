import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';

const api = vi.hoisted(() => ({
  gameRequest: vi.fn(),
  accountRequest: vi.fn(),
  setAccount: (_value: unknown) => {},
}));

vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref<unknown>({ id: 'one' });
  api.setAccount = value => { account.value = value; };
  return { account, accountRequest: api.accountRequest };
});

vi.mock('./game', async () => ({
  actions: [
    { id: 'strike', name: 'Úder', beats: 'Přeruší lest' },
    { id: 'trick', name: 'Lest', beats: 'Obejde štít' },
    { id: 'shield', name: 'Štít', beats: 'Zastaví úder' },
  ],
  actionName: (choice?: string) => choice ?? '—',
  practiceRound: (await vi.importActual<typeof import('./game')>('./game')).practiceRound,
  gameRequest: api.gameRequest,
  gameErrorText: () => 'Obnov stav.',
  GameError: class GameError extends Error { constructor(public status: number) { super(); } },
  operation: () => 'operation-id',
}));

import TeamView from './TeamView.vue';

const matchId = 'team-one';
const epoch = Date.parse('2026-10-29T12:00:00Z');
const lobby = {
  player: { id: 'one', displayName: 'Mira', xp: 50, matchesPlayed: 1 },
  opponents: [
    { id: 'two', displayName: 'Karel', xp: 50, matchesPlayed: 1 },
    { id: 'three', displayName: 'Eliška', xp: 50, matchesPlayed: 1 },
    { id: 'four', displayName: 'Pavel', xp: 50, matchesPlayed: 1 },
  ],
};
const teamMatch = (patch: Record<string, unknown> = {}) => ({
  id: matchId,
  status: 'halftime',
  revision: 5,
  teamMembers: ['one', 'two'],
  opponents: ['three', 'four'],
  acceptedBy: ['one', 'two', 'three', 'four'],
  rounds: [],
  teamScore: 2,
  opponentScore: 2,
  completedRounds: 4,
  ownSparks: 1,
  ownInventory: { strike: 2, trick: 2, shield: 2 },
  ownChoiceLocked: false,
  allFourLocked: [],
  halftimeEndsAt: Math.floor(epoch / 1000) + 3,
  xpAwarded: 0,
  sparkBalances: { one: 1, two: 1, three: 1, four: 1 },
  ownTransferLocked: false,
  onTeamA: true,
  ...patch,
});

let wrapper: VueWrapper | undefined;
let currentMatch: ReturnType<typeof teamMatch>;

async function render() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/play/team/:id', component: TeamView },
      { path: '/play/teams', component: TeamView },
      { path: '/:pathMatch(.*)*', component: TeamView },
    ],
  });
  await router.push(`/play/team/${matchId}`);
  wrapper = mount(TeamView, { global: { plugins: [router], stubs: { GameClash: true } } });
  await flushPromises();
  return wrapper;
}

beforeEach(() => {
  api.accountRequest.mockResolvedValue({ wand: null });
  vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
  vi.setSystemTime(epoch);
  currentMatch = teamMatch();
  api.setAccount({ id: 'one' });
  api.gameRequest.mockReset();
  api.gameRequest.mockImplementation(async (path: string) => path === '' ? lobby : structuredClone(currentMatch));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.useRealTimers();
});

describe('team duel halftime', () => {
  it.each([
    [true, 0, 1, 'strike', 'shield', 'opponent'], [true, 1, 1, 'trick', 'strike', 'opponent'],
    [false, 0, 1, 'shield', 'strike', 'player'], [false, 1, 1, 'strike', 'trick', 'player'],
    [true, 0, 5, 'strike', 'strike', 'draw'], [true, 1, 5, 'trick', 'shield', 'player'],
    [false, 0, 5, 'shield', 'trick', 'opponent'], [false, 1, 5, 'strike', 'strike', 'draw'],
  ] as const)('shows the correct published pair for side A=%s slot %s round %s', async (onTeamA, position, roundIndex, ownChoice, otherChoice, winner) => {
    const ownTeam = onTeamA ? ['one', 'two'] : ['three', 'four'];
    const otherTeam = onTeamA ? ['three', 'four'] : ['one', 'two'];
    const user = ownTeam[position];
    const people = [lobby.player, ...lobby.opponents];
    const ownPlayer = people.find(player => player.id === user)!;
    const currentLobby = { ...lobby, player: ownPlayer, opponents: people.filter(player => player.id !== user) };
    api.setAccount({ id: user });
    // The server serializes B choices by pairing, rather than canonical player slot.
    const publishedB = roundIndex <= 4 ? ['shield', 'strike'] : ['strike', 'shield'];
    currentMatch = teamMatch({ status: 'active', onTeamA, teamMembers: ownTeam, opponents: otherTeam, completedRounds: roundIndex,
      opponentWands: Object.fromEntries(otherTeam.map(user => [user, { wandName: `Hůlka hráče ${user}` }])),
      rounds: [{ index: roundIndex, choiceA0: 'strike', choiceA1: 'trick', choiceB0: publishedB[0], choiceB1: publishedB[1], teamAPoints: 2, teamBPoints: 3 }] });
    api.gameRequest.mockImplementation(async (path: string) => path === '' ? currentLobby : structuredClone(currentMatch));
    const view = await render();
    const round = view.getComponent({ name: 'GameClash' }).props('round');
    expect(round).toMatchObject({ index: roundIndex, playerChoice: ownChoice, opponentChoice: otherChoice });
    expect(round.winner).toBe(winner);
    expect(round).not.toHaveProperty('playerPoints');
    const pairedOpponent = otherTeam[roundIndex <= 4 ? position : 1 - position];
    expect(view.getComponent({ name: 'GameClash' }).props('opponentWandName')).toBe(`Hůlka hráče ${pairedOpponent}`);
    expect(view.get('.game-reveal').text()).toContain(`${ownChoice} proti ${otherChoice}`);
    expect(view.get('.team-round-points').text()).toContain(onTeamA ? 'Tvůj tým +2 · Soupeři +3' : 'Tvůj tým +3 · Soupeři +2');
  });

  it('maps canonical A/B readiness into the B viewer roster without revealing a private choice', async () => {
    api.setAccount({ id: 'three' });
    currentMatch = teamMatch({ status: 'active', onTeamA: false, teamMembers: ['three', 'four'], opponents: ['one', 'two'],
      allFourLocked: [true, false, false, true], ownChoiceLocked: false, rounds: [] });
    const people = [lobby.player, ...lobby.opponents];
    const currentLobby = { ...lobby, player: people.find(player => player.id === 'three')!, opponents: people.filter(player => player.id !== 'three') };
    api.gameRequest.mockImplementation(async (path: string) => path === '' ? currentLobby : structuredClone(currentMatch));
    const view = await render();
    expect(view.findAll('.team-player').map(player => player.attributes('data-ready'))).toEqual(['false', 'true', 'true', 'false']);
    expect(view.get('.team-readiness').text()).toBe('Potvrzené tahy: 2 ze 4.');
    expect(view.getComponent({ name: 'GameClash' }).props('round')).toBeUndefined();
    await view.get('input[value="strike"]').setValue();
    currentMatch = teamMatch({ ...currentMatch, revision: 6, allFourLocked: [true, true, false, true] });
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(view.get('.team-readiness').text()).toBe('Potvrzené tahy: 3 ze 4.');
    expect(view.get('input[value="strike"]').element).toHaveProperty('checked', true);
    expect(view.getComponent({ name: 'GameClash' }).props('round')).toBeUndefined();
  });

  it('does not report an invented readiness count when remote states are missing', async () => {
    currentMatch = teamMatch({ status: 'active', allFourLocked: [] });
    const view = await render();
    expect(view.get('.team-readiness').text()).toBe('Čekáme na stav hráčů.');
    expect(view.text()).toContain('Stav neznámý');
  });

  it('derives remaining seconds from the server deadline and stays expired after a late poll', async () => {
    const view = await render();
    expect(view.get('[role="timer"]').text()).toBe('3 s');
    expect(view.get('[role="timer"]').attributes('aria-live')).toBe('off');

    await vi.advanceTimersByTimeAsync(1000);
    expect(view.text()).toContain('2 s');

    window.dispatchEvent(new Event('online'));
    await flushPromises();
    expect(view.text()).toContain('2 s');

    await vi.advanceTimersByTimeAsync(2000);
    await flushPromises();
    expect(view.text()).toContain('Přestávka skončila');
    const transferButtons = view.findAll('button').filter(button => ['Předat 1 jiskru', 'Nic nepředávat'].includes(button.text()));
    expect(transferButtons).toHaveLength(0);
    expect(api.gameRequest.mock.calls.filter(([path]) => path === `/teams/${matchId}`).length).toBeGreaterThan(1);
    expect(api.gameRequest.mock.calls.some(([path]) => path === `/teams/${matchId}/transfer`)).toBe(false);
    const pollsAfterExpiry = api.gameRequest.mock.calls.filter(([path]) => path === `/teams/${matchId}`).length;
    await vi.advanceTimersByTimeAsync(9000);
    await flushPromises();
    expect(api.gameRequest.mock.calls.filter(([path]) => path === `/teams/${matchId}`).length - pollsAfterExpiry).toBe(3);
  });

  it('disables halftime choices when the server omits its deadline', async () => {
    currentMatch = teamMatch({ halftimeEndsAt: undefined });
    const view = await render();

    expect(view.text()).toContain('čas neznámý');
    expect(view.text()).toContain('Čekáme na potvrzení serveru');
    expect(view.findAll('button').some(button => ['Předat 1 jiskru', 'Nic nepředávat'].includes(button.text()))).toBe(false);
    expect(view.text()).toContain('Pokud nic nevybereš, nabídneš nulu.');
  });

  it('does not send a transfer when the deadline passes before the next clock tick', async () => {
    const view = await render();
    const transfer = view.findAll('button').find(button => button.text() === 'Předat 1 jiskru')!;
    vi.setSystemTime(epoch + 4000);
    await transfer.trigger('click');
    await flushPromises();

    expect(api.gameRequest.mock.calls.some(([path]) => path === `/teams/${matchId}/transfer`)).toBe(false);
    expect(view.get('[role="timer"]').text()).toBe('0 s');
    expect(view.text()).toContain('Přestávka skončila');
  });

  it('clears polling and countdown timers when the account is invalidated', async () => {
    await render();
    const requestsBeforeInvalidation = api.gameRequest.mock.calls.length;

    api.setAccount({ id: 'two' });
    await flushPromises();
    await vi.advanceTimersByTimeAsync(5000);
    await flushPromises();

    expect(api.gameRequest).toHaveBeenCalledTimes(requestsBeforeInvalidation);
    expect(wrapper?.text()).not.toContain('Přestávka ·');
  });

  it('clears timers when the view unmounts', async () => {
    const view = await render();
    const requestsBeforeUnmount = api.gameRequest.mock.calls.length;
    view.unmount();
    wrapper = undefined;

    await vi.advanceTimersByTimeAsync(5000);
    expect(api.gameRequest).toHaveBeenCalledTimes(requestsBeforeUnmount);
  });
});
