import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';

const state = vi.hoisted(() => ({ account: null as { id: string; displayName: string; roles: string[] } | null, generation: 0, launchAllowed: false, membership: 'active' as 'active' | 'revoked' | 'unassigned' }));
vi.mock('../features/identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref(state.account);
  return {
    account,
    accountRequest: vi.fn(async (path: string) => path === '/app/v1/customer/access' ? { state: state.membership } : { allowed: state.launchAllowed }),
    offlineSession: ref(false),
    offlineSessionExpiresAt: ref(0),
    refreshIdentity: vi.fn(async () => { account.value = state.account; }),
    authenticationGenerationSnapshot: () => state.generation,
  };
});
import { installAccessGuard, waitingRoomRedirect } from './accessGuard';
import { launchAllowed, refreshLaunchAccess } from './launchAccess';
import { account, accountRequest, refreshIdentity } from '../features/identity/session';
import { notifications } from './notifications';
import { setOwnerPlayer } from './ownerMode';

function createGuardedRouter() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' }, meta: { requiresAccount: true } },
      { path: '/demo', redirect: '/' },
      { path: '/archive', component: { template: '<div />' }, meta: { requiresAccount: true } },
      { path: '/keepsake/', component: { template: '<div />' } },
      { path: '/archive/duel', component: { template: '<div />' } },
      { path: '/signin', component: { template: '<div />' } },
      { path: '/privacy', component: { template: '<div />' } },
      { path: '/support', component: { template: '<div />' } },
      { path: '/account-deletion', component: { template: '<div />' } },
      { path: '/access-revoked', component: { template: '<div />' }, meta: { requiresAccount: true } },
      { path: '/launch', component: { template: '<div />' }, meta: { requiresAccount: true } },
      { path: '/me', component: { template: '<div />' }, meta: { requiresAccount: true } },
      { path: '/testing-sessions', component: {}, meta: { requiresAccount: true, tester: true } },
      { path: '/persona-lab', component: {}, meta: { requiresAccount: true, lab: true } },
      { path: '/suitcase', component: { template: '<div />' }, meta: { requiresAccount: true, staff: true, offlineLocal: true } },
    ],
  });
  installAccessGuard(router);
  return router;
}

beforeEach(() => { vi.clearAllMocks(); state.account = null; account.value = null; state.generation = 0; state.launchAllowed = false; state.membership = 'active'; });

describe('already open evening reconciliation', () => {
  it('returns an open customer screen to waiting only after a confirmed closure', () => {
    account.value = { id: 'guest', displayName: 'Guest', roles: ['Customer'] };
    const route = { path: '/play', meta: { requiresAccount: true } };
    launchAllowed.value = null;
    expect(waitingRoomRedirect(route)).toBeNull();
    launchAllowed.value = false;
    expect(waitingRoomRedirect(route)).toEqual({ path: '/launch', replace: true });
  });
  it.each(['/me/account', '/access-revoked', '/privacy', '/support', '/launch'])('keeps %s available while closed', path => {
    account.value = { id: 'guest', displayName: 'Guest', roles: ['Customer'] };
    launchAllowed.value = false;
    expect(waitingRoomRedirect({ path, meta: { requiresAccount: true } })).toBeNull();
  });
  it.each(['Owner', 'Administrator', 'Operator', 'Viewer', 'Tester'])('preserves the %s exception', role => {
    account.value = { id: 'staff', displayName: 'Staff', roles: [role] };
    launchAllowed.value = false;
    expect(waitingRoomRedirect({ path: '/play', meta: { requiresAccount: true } })).toBeNull();
  });
});

describe('launch access navigation', () => {
  it.each(['logout', 'account-switch', 'role-loss', 'reauthentication'])('rechecks permissions after %s during launch verification', async change => {
    state.account = { id: 'operator', displayName: 'Operator', roles: ['Operator'] };
    state.launchAllowed = true;
    let finishPolicy!: (value: { allowed: boolean }) => void;
    let started!: () => void;
    const policyStarted = new Promise<void>(resolve => { started = resolve; });
    vi.mocked(accountRequest).mockImplementationOnce(() => {
      started();
      return new Promise(resolve => { finishPolicy = resolve; });
    });
    const router = createGuardedRouter();
    const navigating = router.push('/persona-lab');
    await policyStarted;
    state.generation++;
    if (change === 'logout') state.account = null;
    if (change === 'account-switch') state.account = { id: 'new-operator', displayName: 'Other', roles: ['Operator'] };
    if (change === 'role-loss') state.account = { id: 'operator', displayName: 'Guest', roles: ['Customer'] };
    account.value = state.account;
    finishPolicy({ allowed: false });
    await navigating;
    expect(router.currentRoute.value.path).toBe(change === 'logout' ? '/signin' : change === 'role-loss' ? '/me/character' : '/persona-lab');
    if (change === 'account-switch' || change === 'reauthentication') expect(refreshIdentity).toHaveBeenCalledTimes(2);
  });

  it('shows a recoverable sign-in screen when initial session verification fails', async () => {
    state.account = { id: 'operator', displayName: 'Operator', roles: ['Operator'] };
    account.value = state.account;
    vi.mocked(refreshIdentity).mockRejectedValueOnce(new Error('network unavailable'));
    const router = createGuardedRouter();
    await router.push('/persona-lab?mode=voice');
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.query.return).toBe('/persona-lab?mode=voice');
    expect(account.value?.id).toBe('operator');
  });

  it.each(['unassigned', 'revoked'] as const)('allows mixed testers into testing tools with %s customer membership', async membership => {
    state.account = { id: 'tester', displayName: 'Tester', roles: ['Beta', 'Tester'] };
    state.membership = membership;
    state.launchAllowed = true;
    const router = createGuardedRouter();
    for (const path of ['/testing-sessions', '/persona-lab']) {
      await router.push(path);
      expect(router.currentRoute.value.path).toBe(path);
    }
  });

  it.each([null, {}, { allowed: 'false' }, { allowed: 1 }])('fails closed on malformed launch policy %j', async policy => {
    vi.mocked(accountRequest).mockResolvedValueOnce(policy as never);
    const router = createGuardedRouter();
    await router.push('/archive/duel');
    expect(router.currentRoute.value.path).toBe('/signin');

    state.account = { id: 'operator', displayName: 'Operator', roles: ['Operator'] };
    vi.mocked(accountRequest).mockResolvedValueOnce(policy as never);
    await router.push('/persona-lab');
    expect(router.currentRoute.value.path).toBe('/launch');
  });

  it('does not admit a screen using a launch response superseded by a newer closure', async () => {
    state.account = { id: 'operator', displayName: 'Operator', roles: ['Operator'] };
    let finishPolicy!: (value: { allowed: boolean }) => void;
    let started!: () => void;
    const policyStarted = new Promise<void>(resolve => { started = resolve; });
    vi.mocked(accountRequest).mockImplementationOnce(() => {
      started();
      return new Promise(resolve => { finishPolicy = resolve; });
    });
    const router = createGuardedRouter();
    const navigating = router.push('/persona-lab');
    await policyStarted;
    await refreshLaunchAccess();
    finishPolicy({ allowed: true });
    await navigating;
    expect(router.currentRoute.value.path).toBe('/launch');
    expect(launchAllowed.value).toBe(false);
  });

  it('allows the owner player to prepare before launch without a customer membership', async () => {
    state.account = { id: 'owner', displayName: 'Owner', roles: ['Owner'] };
    setOwnerPlayer(state.account, true);
    state.membership = 'unassigned';
    const router = createGuardedRouter();
    await router.push('/me');
    expect(router.currentRoute.value.path).toBe('/me');
    expect(account.value?.roles).toEqual(['Owner']);
    setOwnerPlayer(null, false);
  });
  it('cancels a transition on a connection failure and permits a verified retry without signing out', async () => {
    state.account = { id: 'guest', displayName: 'Guest', roles: ['Customer'] };
    state.launchAllowed = true;
    const router = createGuardedRouter();
    await router.push('/me');
    vi.mocked(refreshIdentity).mockRejectedValueOnce(new TypeError('Network unavailable'));
    await router.push('/');
    expect(router.currentRoute.value.path).toBe('/me');
    expect(account.value?.id).toBe('guest');
    expect(notifications.value.at(-1)?.message).toContain('Zkontrolujte připojení');
    await router.push('/');
    expect(router.currentRoute.value.path).toBe('/');
  });

  it('requires sign-in when session verification explicitly removes the account', async () => {
    state.account = { id: 'guest', displayName: 'Guest', roles: ['Customer'] };
    state.launchAllowed = true;
    const router = createGuardedRouter();
    await router.push('/me');
    state.account = null;
    await router.push('/me?retry=1');
    expect(router.currentRoute.value.path).toBe('/signin');
  });
  it('sends signed-out visitors away from feature routes to invitation authentication', async () => {
    const router = createGuardedRouter();
    await router.push('/');
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.query.return).toBe('/');
  });

  it.each(['/', '/demo', '/archive'])('requires sign-in at %s even after production launch', async path => {
    state.launchAllowed = true;
    const router = createGuardedRouter();
    await router.push(path);
    expect(router.currentRoute.value.path).toBe('/signin');
  });

  it.each(['/keepsake/', '/archive/duel'])('preserves guest capability entry at %s after launch', async path => {
    state.launchAllowed = true;
    const router = createGuardedRouter();
    await router.push(path);
    expect(router.currentRoute.value.path).toBe(path);
  });

  it('still requires authentication for account routes after production launch', async () => {
    state.launchAllowed = true;
    const router = createGuardedRouter();
    await router.push('/me');
    expect(router.currentRoute.value.path).toBe('/signin');
  });

  it('holds regular accounts in the launch experience until the server allows access', async () => {
    state.account = { id: 'guest', displayName: 'Guest', roles: ['Customer'] };
    const router = createGuardedRouter();
    await router.push('/me');
    expect(router.currentRoute.value.path).toBe('/launch');
  });

  it('keeps privileged users on their selected feature when server policy allows them', async () => {
    state.account = { id: 'operator', displayName: 'Operator', roles: ['Operator'] };
    state.launchAllowed = true;
    const router = createGuardedRouter();
    await router.push('/me');
    expect(router.currentRoute.value.path).toBe('/me');
  });

  it('keeps privacy, support, and account deletion reachable without an account', async () => {
    const router = createGuardedRouter();
    for (const path of ['/privacy', '/support', '/account-deletion']) {
      await router.push(path);
      expect(router.currentRoute.value.path).toBe(path);
    }
  });

  it('routes a revoked customer to the access status page even after global launch unlock', async () => {
    state.account = { id: 'guest', displayName: 'Guest', roles: ['Customer'] };
    state.membership = 'revoked';
    state.launchAllowed = true;
    const router = createGuardedRouter();
    await router.push('/me');
    expect(router.currentRoute.value.path).toBe('/access-revoked');
  });
});
