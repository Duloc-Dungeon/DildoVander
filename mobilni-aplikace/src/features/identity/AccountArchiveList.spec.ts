import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { account, offlineSession, signingOut } from './session';
import AccountArchiveList from './AccountArchiveList.vue';

const request = vi.hoisted(() => vi.fn());
vi.mock('./session', async () => {
  const { ref } = await import('vue');
  return {
    account: ref({ id: 'account-a', displayName: 'A', roles: [] }),
    accountDeletionRequested: ref(false), offlineSession: ref(false), signingOut: ref(false), accountRequest: request,
  };
});

const ownerLocator = '11111111-1111-4111-8111-111111111111';
const linkedLocator = '22222222-2222-4222-8222-222222222222';
const olderLocator = '33333333-3333-4333-8333-333333333333';
const ready = { state: 'ready', reason: null, canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: true };
const owner = {
  locator: ownerLocator, linkedAt: 1_761_200_000, officialName: 'Moje památka', storyTitle: 'Večerní příběh', hasPortrait: true,
  accessKind: 'originalOwner', canManage: true, canReadPrivate: true, canUnlink: false, readiness: ready,
  characterName: 'Eliška', eveningAt: 1_761_100_000,
};
const linked = {
  locator: linkedLocator, linkedAt: 1_761_200_010, officialName: null, storyTitle: null, hasPortrait: false,
  accessKind: 'linkedViewer', canManage: false, canReadPrivate: false, canUnlink: true,
  readiness: { ...ready, state: 'processing', reason: 'reconciliation_required', hasStory: false, hasArtwork: false },
  characterName: null, eveningAt: null,
};
const page = (items: unknown[] = [owner, linked], nextCursor: string | null = null) => ({ schemaVersion: 1, items, nextCursor });
const mounted: ReturnType<typeof mount>[] = [];
const originalOnline = Object.getOwnPropertyDescriptor(window.navigator, 'onLine');
const originalHidden = Object.getOwnPropertyDescriptor(document, 'hidden');

function setOnline(value: boolean): void {
  Object.defineProperty(window.navigator, 'onLine', { configurable: true, get: () => value });
}
function setHidden(value: boolean): void {
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => value });
}
function indexedOwner(index: number) {
  const id = String(index + 1).padStart(8, '0');
  const suffix = String(index + 1).padStart(12, '0');
  return { ...owner, locator: `${id}-1111-4111-8111-${suffix}`, linkedAt: owner.linkedAt - index, officialName: `Výsledek ${index + 1}` };
}

function setup(locale: 'cs' | 'en' = 'cs') {
  const wrapper = mount(AccountArchiveList, {
    props: { locale },
    global: { stubs: { RouterLink: { props: ['to'], template: '<a :data-to="JSON.stringify(to)"><slot /></a>' } } },
  });
  mounted.push(wrapper);
  return wrapper;
}

beforeEach(() => {
  vi.useRealTimers();
  setHidden(false);
  vi.resetAllMocks();
  setOnline(true);
  account.value = { id: 'account-a', displayName: 'Account A', roles: [] };
  offlineSession.value = false;
  signingOut.value = false;
  request.mockResolvedValue(page());
});

afterEach(() => {
  for (const wrapper of mounted.splice(0).reverse()) wrapper.unmount();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  if (originalOnline) Object.defineProperty(window.navigator, 'onLine', originalOnline);
  else Reflect.deleteProperty(window.navigator, 'onLine');
  if (originalHidden) Object.defineProperty(document, 'hidden', originalHidden);
  else Reflect.deleteProperty(document, 'hidden');
  vi.useRealTimers();
});

describe('account archive collection', () => {
  it('puts the newest server-ordered keepsake first and progressively discloses access details', async () => {
    const permission = vi.fn();
    vi.stubGlobal('Notification', { requestPermission: permission });
    const wrapper = setup();
    await flushPromises();
    const cards = wrapper.findAll('.collection-card');
    expect(cards).toHaveLength(2);
    expect(cards[0].text()).toContain('Naposledy uložená památka');
    expect(cards[0].get('h2').text()).toBe('Večerní příběh');
    expect(wrapper.attributes('aria-label')).toBe('Uložené památky');
    expect(cards[0].element.compareDocumentPosition(wrapper.get('.collection-tools').element) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(wrapper.get<HTMLDetailsElement>('.collection-tools').element.open).toBe(false);
    expect(cards[0].get('.collection-actions a, .collection-card__actions a').text()).toBe('Otevřít památku');
    expect(cards[0].get('.collection-actions a, .collection-card__actions a').element).toBe(cards[0].get('a').element);
    expect(cards[0].get('details.collection-item-details').attributes('open')).toBeUndefined();
    expect(cards[0].text()).toContain('Moje památka');
    await cards[0].get('details.collection-item-details > summary').trigger('click');
    expect(cards[0].text()).toContain('Tvoje památka');
    expect(cards[0].text()).toContain('Eliška');
    expect(cards[0].get('.collection-status-pill').attributes('data-state')).toBe('ready');
    expect(cards[0].get('.collection-status-pill').text()).toBe('Připraveno');
    expect(cards[0].text()).toContain('Ilustrace je k dispozici');
    expect(cards[0].findAll('button').some(button => button.text() === 'Odpojit od účtu')).toBe(false);
    await cards[1].get('details.collection-item-details > summary').trigger('click');
    expect(cards[1].text()).toContain('Propojeno s účtem · pouze ke čtení');
    expect(cards[1].get('.collection-status-pill').attributes('data-state')).toBe('processing');
    expect(cards[1].get('.collection-status-pill').text()).toBe('Probíhá');
    expect(cards[1].findAll('button').some(button => button.text() === 'Odpojit od účtu')).toBe(true);
    expect(JSON.parse(cards[0].get('a').attributes('data-to')!)).toEqual({ path: '/keepsake/', query: { account: '1', k: ownerLocator } });
    expect(cards[0].get('time').attributes('datetime')).toBe(new Date(owner.linkedAt * 1000).toISOString());
    expect(request).toHaveBeenCalledWith('/app/v1/archive/collection?limit=25&access=all', 'GET', undefined, expect.any(AbortSignal));
    expect(permission).not.toHaveBeenCalled();
  });

  it('shows truthful partial and failed states with available pieces and Czech copy for the legacy locale prop', async () => {
    request.mockResolvedValueOnce(page([
      { ...owner, readiness: { state: 'partial', reason: 'generation_failed', canRetry: true, shouldPoll: false, hasStory: true, hasArtwork: false } },
      { ...linked, readiness: { state: 'failed', reason: 'generation_failed', canRetry: false, shouldPoll: false, hasStory: false, hasArtwork: false } },
    ]));
    const wrapper = setup('en');
    await flushPromises();
    expect(wrapper.text()).toContain('Částečně dostupné');
    expect(wrapper.text()).toContain('Generování selhalo. Dostupný obsah ověříš otevřením památky.');
    expect(wrapper.text()).toContain('Příběh je k dispozici');
    expect(wrapper.text()).toContain('Nepodařilo se dokončit');
    expect(wrapper.findAll('.collection-card').length).toBe(2);
    expect(wrapper.findAll('button').some(button => button.text() === 'Odpojit od účtu')).toBe(true);
  });

  it('hides search controls for a truly empty collection and points to games and rankings', async () => {
    request.mockResolvedValueOnce(page([], null));
    const wrapper = setup();
    await flushPromises();
    expect(wrapper.text()).toContain('Zatím tu nemáš žádné uložené památky.');
    expect(wrapper.find('.collection-search').exists()).toBe(false);
    expect(wrapper.find('.collection-tools').exists()).toBe(true);
    expect(wrapper.findAll('button').some(button => button.text() === 'Obnovit památky')).toBe(true);
    const destinations = wrapper.findAll('a').map(link => JSON.parse(link.attributes('data-to')!));
    expect(destinations).toContain('/play');
    expect(destinations).toContain('/play/ranking');
  });

  it('can discover newly saved keepsakes by refreshing an empty collection', async () => {
    request.mockResolvedValueOnce(page([], null)).mockResolvedValueOnce(page([owner], null));
    const wrapper = setup();
    await flushPromises();
    expect(wrapper.find('.collection-card').exists()).toBe(false);
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit památky')!.trigger('click');
    await flushPromises();
    expect(wrapper.get('.collection-card h2').text()).toBe('Večerní příběh');
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(2);
  });

  it('distinguishes an empty first visit from empty search results and submits bounded trimmed filters', async () => {
    request.mockResolvedValueOnce(page([owner], null)).mockResolvedValueOnce(page([], null));
    const wrapper = setup();
    await flushPromises();
    expect(wrapper.find('.collection-search').exists()).toBe(true);

    await wrapper.get('input[type="search"]').setValue('   hůlka    ');
    await wrapper.get('select').setValue('linked');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(wrapper.text()).toContain('Těmto filtrům žádné památky neodpovídají.');
    expect(wrapper.find('.collection-search').exists()).toBe(true);
    expect(wrapper.find('.collection-tools').attributes('open')).toBe('');
    expect(request.mock.calls.filter(call => call[1] === 'GET').at(-1)?.[0]).toBe('/app/v1/archive/collection?limit=25&access=linked&q=h%C5%AFlka');
    expect(wrapper.find('.collection-card__latest').exists()).toBe(false);
  });

  it('keeps applied search controls and values when a filtered refresh fails', async () => {
    request.mockResolvedValueOnce(page([owner], null));
    const wrapper = setup();
    await flushPromises();
    await wrapper.get('input[type="search"]').setValue('hůlka');
    await wrapper.get('select').setValue('owned');
    request.mockRejectedValueOnce(new Error('temporary outage'));
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(wrapper.text()).toContain('Památky se nepodařilo načíst.');
    expect(wrapper.get('input[type="search"]').element).toHaveProperty('value', 'hůlka');
    expect(wrapper.get('select').element).toHaveProperty('value', 'owned');
    expect(wrapper.find('.collection-search').exists()).toBe(true);
    expect(wrapper.find('.collection-tools').attributes('open')).toBe('');
  });

  it('keeps cursor paging tied to the active filters and deduplicates an item repeated across pages', async () => {
    request.mockResolvedValueOnce(page([owner], null))
      .mockResolvedValueOnce(page([linked], 'cursor+/opaque'))
      .mockResolvedValueOnce(page([linked, { ...owner, locator: olderLocator }], null));
    const wrapper = setup();
    await flushPromises();
    await wrapper.get('input[type="search"]').setValue(' night ');
    await wrapper.get('select').setValue('linked');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Načíst další')!.trigger('click');
    await flushPromises();
    expect(request.mock.calls.at(-1)?.[0]).toBe('/app/v1/archive/collection?limit=25&access=linked&q=night&cursor=cursor%2B%2Fopaque');
    expect(wrapper.findAll('.collection-card')).toHaveLength(2);
    expect(wrapper.text()).toContain('Při prohlížení starších památek se automatické obnovování pozastaví.');
  });

  it('fails closed for contradictory capabilities and only unlinks a linked-viewer association after confirmation', async () => {
    request.mockResolvedValueOnce(page([
      { ...owner, canUnlink: true }, { ...linked, canManage: true },
    ]));
    const confirm = vi.fn(() => true);
    vi.stubGlobal('confirm', confirm);
    const wrapper = setup();
    await flushPromises();
    for (const card of wrapper.findAll('.collection-card')) {
      expect(card.text()).toContain('Podrobnosti přístupu nelze ověřit; odpojení není dostupné.');
      expect(card.findAll('button').some(button => button.text() === 'Odpojit od účtu')).toBe(false);
    }

    request.mockReset().mockResolvedValueOnce(page([linked])).mockResolvedValueOnce(undefined).mockResolvedValueOnce(page());
    const fresh = setup();
    await flushPromises();
    await fresh.find('.collection-card').find('button').trigger('click');
    await flushPromises();
    expect(confirm).toHaveBeenCalledWith('Odebrat toto propojení pouze ke čtení z účtu? Původní památka ani její uložené soubory se tím nesmažou.');
    expect(request.mock.calls.some(([path, method]) => path === `/app/v1/archive/associations/${linkedLocator}` && method === 'DELETE')).toBe(true);
    expect(request.mock.calls.some(([, method]) => method === 'POST')).toBe(false);
  });

  it('aborts late collection reads on account changes and does not expose the old account response', async () => {
    let finishAccountA!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishAccountA = resolve));
    request.mockResolvedValueOnce(page([linked]));
    const wrapper = setup();
    const signal = request.mock.calls[0][3] as AbortSignal;
    account.value = { id: 'account-b', displayName: 'Account B', roles: [] };
    expect(signal.aborted).toBe(true);
    await nextTick();
    finishAccountA(page([owner]));
    await flushPromises();
    expect(wrapper.text()).not.toContain('Moje památka');
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(2);
  });

  it('clears rows and aborts requests immediately offline and reloads once on reconnect', async () => {
    let finishOfflineRead!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishOfflineRead = resolve));
    const wrapper = setup();
    const signal = request.mock.calls[0][3] as AbortSignal;
    await wrapper.get('input[type="search"]').setValue('soukromá postava');
    await wrapper.get('select').setValue('owned');
    setOnline(false);
    window.dispatchEvent(new Event('offline'));
    expect(signal.aborted).toBe(true);
    finishOfflineRead(page([owner]));
    await flushPromises();
    expect(wrapper.find('.collection-card').exists()).toBe(false);
    expect(wrapper.text()).toContain('Tvoje památky nejsou offline dostupné.');

    request.mockResolvedValueOnce(page([owner]));
    setOnline(true);
    window.dispatchEvent(new Event('online'));
    await flushPromises();
    expect(wrapper.findAll('.collection-card')).toHaveLength(1);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(2);
    expect(request.mock.calls.at(-1)?.[0]).toBe('/app/v1/archive/collection?limit=25&access=all');
    expect((wrapper.get('input[type="search"]').element as HTMLInputElement).value).toBe('');
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('all');
  });

  it('bounds processing refreshes to twelve and stops polling while cursor pages are expanded', async () => {
    vi.useFakeTimers();
    request.mockImplementation(() => Promise.resolve(page([{
      ...owner, readiness: { ...ready, state: 'processing', reason: 'reconciliation_required', shouldPoll: true },
    }], 'older')));
    const wrapper = setup();
    await flushPromises();
    await vi.advanceTimersByTimeAsync(12 * 5000);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(13);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(13);

    request.mockResolvedValueOnce(page([{ ...owner, locator: olderLocator }], null));
    await wrapper.findAll('button').find(button => button.text() === 'Načíst další')!.trigger('click');
    await flushPromises();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(14);
  });

  it('limits appended collection rows to 200 items', async () => {
    request.mockImplementation(() => {
      const getCount = request.mock.calls.filter(call => call[1] === 'GET').length;
      const start = (getCount - 1) * 25;
      const rows = Array.from({ length: 25 }, (_, index) => indexedOwner(start + index));
      return Promise.resolve(page(rows, `cursor-${getCount}`));
    });
    const wrapper = setup();
    await flushPromises();
    for (let pageIndex = 0; pageIndex < 7; pageIndex++) {
      await wrapper.findAll('button').find(button => button.text() === 'Načíst další')!.trigger('click');
      await flushPromises();
    }
    expect(wrapper.findAll('.collection-card')).toHaveLength(200);
    expect(wrapper.text()).toContain('Zobrazuje se nejvýše 200 památek.');
    expect(wrapper.findAll('button').some(button => button.text() === 'Načíst další')).toBe(false);
  });

  it('pauses in-flight polling while hidden and revalidates when the page becomes visible', async () => {
    vi.useFakeTimers();
    request.mockImplementation(() => Promise.resolve(page([{
      ...owner, readiness: { ...ready, state: 'processing', reason: 'reconciliation_required', shouldPoll: true },
    }], null)));
    const wrapper = setup();
    await flushPromises();
    await vi.advanceTimersByTimeAsync(5000);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(2);

    setHidden(true);
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(60_000);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(2);

    setHidden(false);
    document.dispatchEvent(new Event('visibilitychange'));
    await flushPromises();
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(3);
    wrapper.unmount();
  });

  it('aborts stale filtered reads and ignores them after the newer filter result', async () => {
    let finishOld!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishOld = resolve));
    const wrapper = setup();
    const signal = request.mock.calls[0][3] as AbortSignal;
    await wrapper.get('input[type="search"]').setValue('new');
    request.mockResolvedValueOnce(page([{ ...owner, officialName: 'Nový výsledek' }], null));
    await wrapper.get('form').trigger('submit');
    expect(signal.aborted).toBe(true);
    request.mockResolvedValueOnce(page([{ ...owner, officialName: 'Nový výsledek' }], null));
    await flushPromises();
    finishOld(page([owner]));
    await flushPromises();
    expect(wrapper.text()).toContain('Nový výsledek');
    expect(wrapper.text()).not.toContain('Moje památka');
  });

  it('clears a private search and access filter on account A to B to A transitions without replaying the query', async () => {
    let finishAccountASearch!: (value: unknown) => void;
    request.mockResolvedValueOnce(page([owner]));
    const wrapper = setup();
    await flushPromises();

    await wrapper.get('input[type="search"]').setValue('Eliška');
    await wrapper.get('select').setValue('owned');
    request.mockReturnValueOnce(new Promise(resolve => finishAccountASearch = resolve));
    await wrapper.get('form').trigger('submit');
    const privateSearchCall = request.mock.calls.at(-1)!;
    const privateSearchSignal = privateSearchCall[3] as AbortSignal;
    expect(privateSearchCall[0]).toBe('/app/v1/archive/collection?limit=25&access=owned&q=Eli%C5%A1ka');

    request.mockResolvedValueOnce(page([linked]));
    account.value = { id: 'account-b', displayName: 'Account B', roles: [] };
    expect(privateSearchSignal.aborted).toBe(true);
    await nextTick();
    expect((wrapper.get('input[type="search"]').element as HTMLInputElement).value).toBe('');
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('all');
    expect(request.mock.calls.at(-1)?.[0]).toBe('/app/v1/archive/collection?limit=25&access=all');

    finishAccountASearch(page([owner]));
    await flushPromises();
    expect(wrapper.text()).not.toContain('Moje památka');
    expect((wrapper.get('input[type="search"]').element as HTMLInputElement).value).toBe('');

    request.mockResolvedValueOnce(page([owner]));
    account.value = { id: 'account-a', displayName: 'Account A', roles: [] };
    await flushPromises();
    expect(request.mock.calls.at(-1)?.[0]).toBe('/app/v1/archive/collection?limit=25&access=all');
    expect((wrapper.get('input[type="search"]').element as HTMLInputElement).value).toBe('');

    await wrapper.get('input[type="search"]').setValue('jméno zůstane soukromé');
    await wrapper.get('select').setValue('linked');
    signingOut.value = true;
    await nextTick();
    expect(wrapper.text()).toContain('Památky nejsou dostupné, dokud se mění přihlášení.');
    request.mockResolvedValueOnce(page([owner]));
    signingOut.value = false;
    await flushPromises();
    expect((wrapper.get('input[type="search"]').element as HTMLInputElement).value).toBe('');
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('all');
    expect(request.mock.calls.at(-1)?.[0]).toBe('/app/v1/archive/collection?limit=25&access=all');
  });

  it('clears private rows and pending work on sign-out and shows a translated unavailable notice', async () => {
    const wrapper = setup('en');
    await flushPromises();
    signingOut.value = true;
    await nextTick();
    expect(wrapper.findAll('.collection-card')).toHaveLength(0);
    expect(wrapper.text()).toContain('Památky nejsou dostupné, dokud se mění přihlášení.');
    expect(request.mock.calls[0][3].aborted).toBe(true);
  });
});
