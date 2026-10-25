import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import LaunchView from './LaunchView.vue';

const api = vi.hoisted(() => ({ request: vi.fn(), replace: vi.fn(), setAccount: vi.fn() }));
vi.mock('vue-router', () => ({ useRouter: () => ({ replace: api.replace }) }));
vi.mock('../identity/AccountDeletion.vue', () => ({ default: { template: '<span />' } }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref({ id: 'guest', displayName: 'Guest', roles: ['Customer'] });
  api.setAccount.mockImplementation(value => { account.value = value; });
  return { account,
    accountRequest: api.request, accountTransport: vi.fn() };
});

beforeEach(() => { vi.useFakeTimers(); api.request.mockReset(); api.replace.mockReset(); api.setAccount({ id: 'guest', displayName: 'Guest', roles: ['Customer'] }); });
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

function waitingPolicy() {
  return { allowed: false, launchAt: Date.now() + 86_400_000, serverNow: Date.now(),
    teaser: 'Pozvánka čeká.', initialLoadingMessage: 'Olivander má kocovinu.', personalizedLoadingEnabled: true };
}
function entry() { return mount(LaunchView, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } }); }

it('preserves an unsaved same-account draft when returning from another tab', async () => {
  api.request.mockImplementation(async (path: string) => path === '/app/v1/launch' ? waitingPolicy() : { character: null, artwork: null });
  const view = entry();
  try {
    await flushPromises();
    await view.get('#character-name').setValue('Noční archivář');
    await view.get('#character-universe').setValue('Město za zrcadlem');
    await view.get('input[type="checkbox"]').setValue(true);
    document.dispatchEvent(new Event('visibilitychange'));
    await flushPromises();
    expect((view.get('#character-name').element as HTMLInputElement).value).toBe('Noční archivář');
    expect((view.get('#character-universe').element as HTMLInputElement).value).toBe('Město za zrcadlem');
    expect((view.get('input[type="checkbox"]').element as HTMLInputElement).checked).toBe(true);
  } finally { view.unmount(); }
});

it('never carries another account’s AI consent or late mutation error into the new form', async () => {
  let rejectSave!: (reason: Error) => void;
  api.request.mockImplementation(async (path: string, method?: string) => {
    if (path === '/app/v1/launch') return waitingPolicy();
    if (method === 'PUT') return new Promise((_, reject) => { rejectSave = reject; });
    return { character: null, artwork: null };
  });
  const view = entry();
  try {
    await flushPromises();
    await view.get('#character-name').setValue('První host');
    await view.get('#character-universe').setValue('První svět');
    await view.get('input[type="checkbox"]').setValue(true);
    await view.get('form').trigger('submit');
    api.setAccount({ id: 'second-guest', displayName: 'Druhý host', roles: ['Customer'] });
    await flushPromises();
    expect((view.get('input[type="checkbox"]').element as HTMLInputElement).checked).toBe(false);
    expect((view.get('#character-name').element as HTMLInputElement).value).toBe('');
    expect(view.get('button[type="submit"]').attributes('disabled')).toBeDefined();
    rejectSave(new Error('Private old request failure'));
    await flushPromises();
    expect(view.text()).not.toContain('Private old request failure');
  } finally { view.unmount(); }
});

it('shows the admin opening text, then a recognized-character update without leaking final text early', async () => {
  let researched = false;
  api.request.mockImplementation(async (path: string) => path === '/app/v1/launch' ? waitingPolicy() : {
    character: { profileId: 'profile', revision: 1, name: 'Archivář', universe: 'Město za zrcadlem', status: researched ? 'ready' : 'running' },
    artwork: { status: researched ? 'running' : 'queued', loadingMessage: researched ? 'Archiváři, katalog právě odmítl evidovat tvé ego.' : null, finalMessage: 'UNRELEASED FINAL TEXT' },
  });
  const view = entry();
  try {
    await flushPromises();
    expect(view.get('.launch-status').text()).toBe('Olivander má kocovinu.');
    expect(view.text()).not.toContain('UNRELEASED FINAL TEXT');
    researched = true;
    await vi.advanceTimersByTimeAsync(5000);
    await flushPromises();
    expect(view.get('.launch-status').text()).toContain('katalog právě odmítl evidovat tvé ego');
    expect(view.text()).not.toContain('UNRELEASED FINAL TEXT');
    expect(view.get('[aria-current="step"]').text()).toBe('Mícháme barvy');
  } finally { view.unmount(); }
});
