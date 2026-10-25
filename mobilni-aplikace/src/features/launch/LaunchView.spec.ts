import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import LaunchView from './LaunchView.vue';

const api = vi.hoisted(() => ({ request: vi.fn(), replace: vi.fn() }));
vi.mock('vue-router', () => ({ useRouter: () => ({ replace: api.replace }) }));
vi.mock('../identity/AccountDeletion.vue', () => ({ default: { template: '<span />' } }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'guest', displayName: 'Guest', roles: ['Customer'] }),
    accountRequest: api.request, accountTransport: vi.fn() };
});

beforeEach(() => { vi.useFakeTimers(); api.request.mockReset(); api.replace.mockReset(); });
afterEach(() => { vi.useRealTimers(); });

it('recovers a committed character when the save response is lost instead of repeating creation', async () => {
  let persisted = false;
  api.request.mockImplementation(async (path: string, method?: string) => {
    if (path === '/app/v1/launch') return { allowed: false, launchAt: Date.now() + 86_400_000,
      serverNow: Date.now(), teaser: 'Hvězdy čekají.', initialLoadingMessage: 'Rovnáme hvězdy.', personalizedLoadingEnabled: false };
    if (method === 'PUT') { persisted = true; throw new Error('Response lost'); }
    return persisted ? { character: { profileId: 'profile', revision: 1, name: 'Raven', universe: 'Fiction', status: 'queued' },
      artwork: { status: 'queued' } } : { character: null, artwork: null };
  });
  const view = mount(LaunchView, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } });
  try {
    await flushPromises();
    await view.get('#character-name').setValue('Raven');
    await view.get('#character-universe').setValue('Fiction');
    await view.get('input[type="checkbox"]').setValue(true);
    await view.get('form').trigger('submit');
    await flushPromises();
    expect(view.text()).toContain('Raven');
    expect(view.text()).toContain('Každé kouzlo má svůj čas');
    expect(view.text()).not.toContain('Response lost');
    expect(view.find('form').exists()).toBe(false);
    expect(api.request.mock.calls.filter(([, method]) => method === 'PUT')).toHaveLength(1);
  } finally { view.unmount(); }
});
