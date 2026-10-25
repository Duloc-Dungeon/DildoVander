import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';

const state = vi.hoisted(() => ({ account: null as { id: string; displayName: string; roles: string[] } | null, launchAllowed: false }));
vi.mock('../features/identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref(state.account);
  return {
    account,
    accountRequest: vi.fn(async () => ({ allowed: state.launchAllowed })),
    offlineSession: ref(false),
    offlineSessionExpiresAt: ref(0),
    refreshIdentity: vi.fn(async () => { account.value = state.account; }),
  };
});
import { installAccessGuard } from './accessGuard';

function createGuardedRouter() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' } },
      { path: '/signin', component: { template: '<div />' } },
      { path: '/privacy', component: { template: '<div />' } },
      { path: '/support', component: { template: '<div />' } },
      { path: '/account-deletion', component: { template: '<div />' } },
      { path: '/launch', component: { template: '<div />' }, meta: { requiresAccount: true } },
      { path: '/me', component: { template: '<div />' }, meta: { requiresAccount: true } },
      { path: '/suitcase', component: { template: '<div />' }, meta: { requiresAccount: true, staff: true, offlineLocal: true } },
    ],
  });
  installAccessGuard(router);
  return router;
}

beforeEach(() => { state.account = null; state.launchAllowed = false; });

describe('launch access navigation', () => {
  it('sends signed-out visitors away from feature routes to invitation authentication', async () => {
    const router = createGuardedRouter();
    await router.push('/');
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.query.return).toBe('/');
  });

  it('preserves public feature routes for signed-out visitors after production launch', async () => {
    state.launchAllowed = true;
    const router = createGuardedRouter();
    await router.push('/');
    expect(router.currentRoute.value.path).toBe('/');
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
});
