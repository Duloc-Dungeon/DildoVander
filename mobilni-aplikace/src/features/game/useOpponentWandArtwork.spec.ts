import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h, type Ref } from 'vue';
import { mount, type VueWrapper } from '@vue/test-utils';
const api = vi.hoisted(() => ({ transport: vi.fn(), account: null as Ref<{ id: string } | null> | null }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue'); api.account = ref<{ id: string } | null>({ id: 'one' });
  return { account: api.account, accountTransport: api.transport };
});
import { useOpponentWandArtwork } from './useOpponentWandArtwork';
import type { Match } from './game';
const id = 'a'.repeat(32);
const path = `/app/v1/game/matches/${id}/opponent/wand-artwork?revision=${'b'.repeat(64)}`;
const match = (url: string | null = path) => ({ id, mode: 'quick', opponent: { id: 'two', wandArtworkUrl: url } }) as Match;
let wrapper: VueWrapper;
let state: ReturnType<typeof useOpponentWandArtwork>;
const host = defineComponent({ setup() { state = useOpponentWandArtwork(); return () => h('div'); } });
beforeEach(() => {
  vi.resetAllMocks(); api.account!.value = { id: 'one' };
  api.transport.mockResolvedValue(new Response(new Blob(['image'], { type: 'image/png' })));
  vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:opponent');
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
  wrapper = mount(host);
});
afterEach(() => { wrapper.unmount(); vi.restoreAllMocks(); });
describe('approved match opponent artwork', () => {
  it('loads only the exact match and revision route and caches unchanged metadata', async () => {
    await state.refresh(match()); await state.refresh(match());
    expect(api.transport).toHaveBeenCalledTimes(1);
    expect(api.transport).toHaveBeenCalledWith(path, expect.objectContaining({ method: 'GET', cache: 'no-store', redirect: 'error' }));
    expect(state.url.value).toBe('blob:opponent');
  });
  it('clears a withdrawn reference and refetches a changed revision', async () => {
    await state.refresh(match());
    await state.refresh(match(null));
    expect(state.url.value).toBe(''); expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:opponent');
    api.transport.mockResolvedValue(new Response(new Blob(['new'], { type: 'image/png' })));
    await state.refresh(match(path.replace('b'.repeat(64), 'c'.repeat(64))));
    expect(api.transport).toHaveBeenCalledTimes(2);
  });
  it.each(['https://foreign.invalid/image.png', path.replace(id, 'd'.repeat(32)), path + '&account=other', path.replace('?revision=', '?x='), path + '/extra', path + '\n'])('rejects an arbitrary image location: %s', async invalid => {
    await state.refresh(match(invalid));
    expect(api.transport).not.toHaveBeenCalled(); expect(state.url.value).toBe('');
  });
  it('fences a late response after account change', async () => {
    let resolve!: (response: Response) => void;
    api.transport.mockReturnValue(new Promise<Response>(done => { resolve = done; }));
    const pending = state.refresh(match()); api.account!.value = { id: 'other' };
    resolve(new Response(new Blob(['image'], { type: 'image/png' }))); await pending;
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });
  it.each([403, 404])('retains the fallback when authorization returns %s', async status => {
    api.transport.mockResolvedValue(new Response('{}', { status })); await state.refresh(match());
    expect(state.url.value).toBe('');
  });
  it('rejects non-PNG media', async () => {
    api.transport.mockResolvedValue(new Response('<html />', { headers: { 'Content-Type': 'text/html' } }));
    await state.refresh(match()); expect(URL.createObjectURL).not.toHaveBeenCalled();
  });
});
