import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import CustomerSealedCaseRewards from './CustomerSealedCaseRewards.vue';
import { account, offlineSession, signingOut } from '../identity/session';

const request = vi.hoisted(() => vi.fn());
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return {
    account: ref({ id: 'account-a', displayName: 'A', roles: [] }),
    accountDeletionRequested: ref(false), offlineSession: ref(false), signingOut: ref(false), accountRequest: request,
  };
});

const ownerCase = `sc_${'1'.repeat(32)}`;
const anotherCase = `sc_${'2'.repeat(32)}`;
const page = (items: unknown[]) => ({ schemaVersion: 1, items });
const mounted: ReturnType<typeof mount>[] = [];
const originalOnline = Object.getOwnPropertyDescriptor(window.navigator, 'onLine');
function setOnline(value: boolean): void {
  Object.defineProperty(window.navigator, 'onLine', { configurable: true, get: () => value });
}

function setup(locale: 'cs' | 'en' = 'cs') {
  const wrapper = mount(CustomerSealedCaseRewards, { props: { locale } });
  mounted.push(wrapper);
  return wrapper;
}

beforeEach(() => {
  vi.resetAllMocks();
  setOnline(true);
  account.value = { id: 'account-a', displayName: 'Account A', roles: [] };
  offlineSession.value = false;
  signingOut.value = false;
  request.mockResolvedValue(page([
    { caseId: ownerCase, releasedAt: '2026-10-22T12:00:00Z', team: 'Guardian', confirmedPlans: 3, successfulPlans: 2 },
    { caseId: anotherCase, releasedAt: '2026-10-21T12:00:00Z', team: 'Conspirator', confirmedPlans: 2, successfulPlans: 1 },
  ]));
});

afterEach(() => {
  for (const wrapper of mounted.splice(0).reverse()) wrapper.unmount();
  if (originalOnline) Object.defineProperty(window.navigator, 'onLine', originalOnline);
  else Reflect.deleteProperty(window.navigator, 'onLine');
});

describe('CustomerSealedCaseRewards', () => {
  it('loads released case keepsakes and renders only each participant role and outcome counts', async () => {
    const wrapper = setup();
    await flushPromises();

    expect(request).toHaveBeenCalledWith('/app/v1/sealed-cases/rewards?scenarioMetadata=true', 'GET', undefined, expect.any(AbortSignal));
    const rows = wrapper.findAll('.case-rewards__item');
    expect(rows).toHaveLength(2);
    expect(rows[0].text()).toContain('Strážce');
    expect(rows[0].text()).toContain('Potvrzené hlavní plány: 3 · Provedené zásahy: 2');
    expect(rows[1].text()).toContain('Spiklenec');
    expect(rows[0].text()).toContain('Případ posledního zvonu');
    expect(wrapper.text()).not.toContain(ownerCase);
  });

  it('uses the saved scenario title and team for each different case', async () => {
    request.mockResolvedValueOnce(page([
      { caseId: ownerCase, releasedAt: '2026-10-29T12:00:00Z', team: 'Guardian', confirmedPlans: 3, successfulPlans: 2,
        scenario: { id: 'HP_SMRTIJEDI_BYSTROZOROVE_V1', title: 'Smrtijedi a bystrozorové: Případ zamčeného letaxu', teamLabel: 'Bystrozorové' } },
      { caseId: anotherCase, releasedAt: '2026-10-29T11:00:00Z', team: 'Conspirator', confirmedPlans: 2, successfulPlans: 1,
        scenario: { id: 'HP_SPOLECENSTVI_KRBU_V1', title: 'Společenství vlastníků krbu', teamLabel: 'Přednostní cestující' } },
    ]));
    const wrapper = setup();
    await flushPromises();
    const rows = wrapper.findAll('.case-rewards__item');
    expect(rows[0].find('h3').text()).toBe('Smrtijedi a bystrozorové: Případ zamčeného letaxu');
    expect(rows[0].find('.case-rewards__badge').text()).toBe('Bystrozorové');
    expect(rows[1].find('h3').text()).toBe('Společenství vlastníků krbu');
    expect(rows[1].find('.case-rewards__badge').text()).toBe('Přednostní cestující');
    expect(wrapper.text()).not.toContain('Případ posledního zvonu');
  });

  it.each([
    { id: 'HP_SPOLECENSTVI_KRBU_V1', title: 'Společenství vlastníků krbu', teamLabel: 'Sousedé', privateNotes: 'PRIVATE_CANARY' },
    { id: 'HP_SPOLECENSTVI_KRBU_V1', title: ' ', teamLabel: 'Sousedé' },
    null,
  ])('rejects malformed or expanded scenario metadata', async scenario => {
    request.mockResolvedValueOnce(page([
      { caseId: ownerCase, releasedAt: '2026-10-29T12:00:00Z', team: 'Guardian', confirmedPlans: 1, successfulPlans: 1, scenario },
    ]));
    const wrapper = setup();
    await flushPromises();
    expect(wrapper.findAll('.case-rewards__item')).toHaveLength(0);
    expect(wrapper.text()).toContain('Tvoje stopy v případech jsou dočasně nedostupné.');
    expect(wrapper.text()).not.toContain('PRIVATE_CANARY');
  });

  it('rejects contradictory reward counts and keeps Czech copy with the legacy locale prop', async () => {
    request.mockResolvedValueOnce(page([
      { caseId: ownerCase, releasedAt: '2026-10-22T12:00:00Z', team: 'Guardian', confirmedPlans: 1, successfulPlans: 2 },
    ]));
    const failed = setup();
    await flushPromises();
    expect(failed.text()).toContain('Tvoje stopy v případech jsou dočasně nedostupné.');
    expect(failed.findAll('.case-rewards__item')).toHaveLength(0);
    failed.unmount();

    request.mockResolvedValueOnce(page([]));
    const empty = setup('en');
    await flushPromises();
    expect(empty.text()).toContain('Zatím tu nejsou žádné uzavřené případy uložené k tomuto účtu.');
    empty.unmount();
  });

  it('aborts stale case rewards when the signed-in account changes', async () => {
    let finishOld!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => { finishOld = resolve; }));
    const wrapper = setup();
    const oldSignal = request.mock.calls[0][3] as AbortSignal;

    request.mockResolvedValueOnce(page([
      { caseId: anotherCase, releasedAt: '2026-10-22T12:00:00Z', team: 'Conspirator', confirmedPlans: 1, successfulPlans: 1 },
    ]));
    account.value = { id: 'account-b', displayName: 'Account B', roles: [] };
    await flushPromises();
    finishOld(page([
      { caseId: ownerCase, releasedAt: '2026-10-22T12:00:00Z', team: 'Guardian', confirmedPlans: 3, successfulPlans: 3 },
    ]));
    await flushPromises();

    expect(oldSignal.aborted).toBe(true);
    expect(wrapper.findAll('.case-rewards__item')).toHaveLength(1);
    expect(wrapper.find('.case-rewards__item')!.text()).toContain('Spiklenec');
  });
});
