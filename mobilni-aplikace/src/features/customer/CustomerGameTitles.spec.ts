import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import CustomerGameTitles from './CustomerGameTitles.vue';
import { account, offlineSession, signingOut } from '../identity/session';

const request = vi.hoisted(() => vi.fn());
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return {
    account: ref({ id: 'account-a', displayName: 'A', roles: [] }),
    accountDeletionRequested: ref(false), offlineSession: ref(false), signingOut: ref(false), accountRequest: request,
  };
});

const mounted: ReturnType<typeof mount>[] = [];
const originalOnline = Object.getOwnPropertyDescriptor(window.navigator, 'onLine');
function setOnline(value: boolean): void {
  Object.defineProperty(window.navigator, 'onLine', { configurable: true, get: () => value });
}

function setup(locale: 'cs' | 'en' = 'cs') {
  const wrapper = mount(CustomerGameTitles, { props: { locale } });
  mounted.push(wrapper);
  return wrapper;
}

beforeEach(() => {
  vi.resetAllMocks();
  setOnline(true);
  account.value = { id: 'account-a', displayName: 'Account A', roles: [] };
  offlineSession.value = false;
  signingOut.value = false;
  request.mockResolvedValue({ titles: ['Oběť odborníka', 'Čtenář myšlenek'] });
});

afterEach(() => {
  for (const wrapper of mounted.splice(0).reverse()) wrapper.unmount();
  if (originalOnline) Object.defineProperty(window.navigator, 'onLine', originalOnline);
  else Reflect.deleteProperty(window.navigator, 'onLine');
});

describe('CustomerGameTitles', () => {
  it('loads account titles and displays each as a badge', async () => {
    const wrapper = setup();
    await flushPromises();

    expect(request).toHaveBeenCalledWith('/app/v1/game/progress', 'GET', undefined, expect.any(AbortSignal));
    expect(wrapper.findAll('.game-titles__badge').map(badge => badge.text())).toEqual(['Oběť odborníka', 'Čtenář myšlenek']);
  });

  it('rejects malformed title data and keeps Czech copy with the legacy English locale prop', async () => {
    request.mockResolvedValueOnce({ titles: ['valid', 42] });
    const failed = setup();
    await flushPromises();
    expect(failed.text()).toContain('Tvoje tituly jsou dočasně nedostupné.');
    expect(failed.findAll('.game-titles__badge')).toHaveLength(0);
    failed.unmount();

    request.mockResolvedValueOnce({ titles: [] });
    const empty = setup('en');
    await flushPromises();
    expect(empty.text()).toContain('Tvé tituly');
    expect(empty.text()).toContain('Zatím sis žádný titul neodnesl.');
    empty.unmount();
  });

  it('aborts stale titles when the signed-in account changes', async () => {
    let finishOld!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => { finishOld = resolve; }));
    const wrapper = setup();
    const oldSignal = request.mock.calls[0][3] as AbortSignal;

    request.mockResolvedValueOnce({ titles: ['Nový titul'] });
    account.value = { id: 'account-b', displayName: 'Account B', roles: [] };
    await flushPromises();
    finishOld({ titles: ['Titul starého účtu'] });
    await flushPromises();

    expect(oldSignal.aborted).toBe(true);
    expect(wrapper.findAll('.game-titles__badge').map(badge => badge.text())).toEqual(['Nový titul']);
  });
});
