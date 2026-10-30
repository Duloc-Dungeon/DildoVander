import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { account } from '../features/identity/session';
import { setOwnerPlayer } from './ownerMode';
import { router } from './router';

let roles: string[] | null;
let membership: 'active' | 'unassigned' | 'revoked';
let allowed: boolean;

const protectedScreens = router.getRoutes().filter(route => route.meta.requiresAccount
  && router.resolve(route.path.replace(':id', 'AbC123')).name === route.name);
const toolPaths = ['/testing-sessions', '/persona-lab', '/settings', '/settings/admin',
  '/settings/admin/launch', '/settings/admin/characters', '/admin/customer-sessions'];

beforeEach(async () => {
  roles = null;
  account.value = null;
  membership = 'active';
  allowed = true;
  setOwnerPlayer(null, false);
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const path = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, 'https://test.invalid').pathname;
    if (path === '/app/v1/session') return Response.json({ enabled: true, account: roles ? { id: 'synthetic', displayName: 'Test', roles, openAiConsent: true } : null, csrfToken: 'synthetic' });
    if (path === '/app/v1/customer/access') return Response.json({ state: membership });
    if (path === '/app/v1/launch') return Response.json({ allowed });
    throw new Error(`Unexpected request: ${path}`);
  }));
  await router.push('/signin');
});

afterEach(async () => {
  await router.push('/signin');
  account.value = null;
  vi.unstubAllGlobals();
});

describe('every protected screen', () => {
  it.each(protectedScreens)('requires sign-in for $path', async screen => {
    await router.push(screen.path.replace(':id', 'AbC123'));
    expect(router.currentRoute.value.path).toBe('/signin');
  });

  it.each([
    { role: 'Customer', tools: [] },
    { role: 'Beta', tools: [] },
    { role: 'Viewer', tools: ['/persona-lab', '/settings'] },
    { role: 'Operator', tools: ['/persona-lab', '/settings', '/admin/customer-sessions'] },
    { role: 'Administrator', tools: toolPaths.filter(path => path !== '/testing-sessions') },
    { role: 'Owner', tools: toolPaths.filter(path => path !== '/testing-sessions') },
    { role: 'Tester', tools: ['/testing-sessions', '/persona-lab', '/settings'] },
  ])('enforces $role access across the screen inventory', async identity => {
    roles = [identity.role];
    for (const screen of protectedScreens) {
      const path = screen.path.replace(':id', 'AbC123');
      await router.push(path);
      const shouldEnter = !toolPaths.includes(path) || identity.tools.includes(path);
      expect(router.currentRoute.value.path === path, `${identity.role}: ${path}`).toBe(shouldEnter);
    }
  });

  it.each(['unassigned', 'revoked'] as const)('preserves mixed Tester access with %s membership', async state => {
    roles = ['Beta', 'Tester'];
    membership = state;
    for (const path of ['/testing-sessions', '/persona-lab', '/me/account', '/play/wand']) {
      await router.push(path);
      expect(router.currentRoute.value.path).toBe(path);
    }
    await router.push('/settings/admin');
    expect(router.currentRoute.value.path).toBe('/me/character');
  });

  it.each(['unassigned', 'revoked'] as const)('keeps account and help accessible with %s membership and a closed evening', async state => {
    roles = ['Customer'];
    membership = state;
    allowed = false;
    for (const path of ['/ME/ACCOUNT/', '/SUPPORT/', '/PRIVACY/', '/ACCOUNT-DELETION/']) {
      await router.push(path);
      expect(router.currentRoute.value.path).toBe(path.toLowerCase().replace(/\/$/, ''));
    }
    await router.push('/play');
    expect(router.currentRoute.value.path).toBe(state === 'revoked' ? '/access-revoked' : '/launch');
  });
});
