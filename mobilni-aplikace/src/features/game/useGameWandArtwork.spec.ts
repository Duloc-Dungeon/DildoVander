import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h, ref, type Ref } from 'vue';

const api = vi.hoisted(() => ({
  request: vi.fn(), transport: vi.fn(),
  account: null as Ref<{ id: string } | null> | null,
}));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref<{ id: string } | null>(null);
  api.account = account;
  return { account, accountRequest: api.request, accountTransport: api.transport };
});
import { mount, type VueWrapper } from '@vue/test-utils';
import { useGameWandArtwork } from './useGameWandArtwork';

const wandId = 'night-wand';
const locator = '11111111-1111-4111-8111-111111111111';
const firstAsset = '22222222-2222-4222-8222-222222222222';
const secondAsset = '33333333-3333-4333-8333-333333333333';
const spells = ['strike', 'trick', 'shield'].map(action => ({ action, name: `Kouzlo ${action}`, description: 'Syntetická ukázka.' }));
function character(assetId: string | null = firstAsset, artOverrides: Record<string, unknown> = {}) {
  return {
    generatedWand: {
      wandId, physicalSlot: 2, name: 'Měsíční ozvěna', material: 'Tis', core: 'Křemen',
      temperament: 'Klidná', lore: 'Fiktivní návrh.', designDescription: 'Stříbrný hrot.',
      affinity: 'Měsíční světlo', weakness: 'Zatmění', spells,
    },
    artwork: assetId ? { locator, assetId, status: 'ready', version: 1, synthetic: true, ...artOverrides } : null,
  };
}
const defer = <T,>() => { let resolve!: (value: T) => void; const promise = new Promise<T>(done => resolve = done); return { promise, resolve }; };
let state: ReturnType<typeof useGameWandArtwork> | undefined;
let wrapper: VueWrapper | undefined;
const host = defineComponent({ setup() { state = useGameWandArtwork(); return () => h('div'); } });
let assetNumber = 0;

beforeEach(() => {
  vi.resetAllMocks();
  api.account!.value = { id: 'account-a' };
  api.request.mockImplementation(async () => character());
  api.transport.mockImplementation(async () => new Response(new Blob(['approved synthetic art'], { type: 'image/png' })));
  assetNumber = 0;
  vi.spyOn(URL, 'createObjectURL').mockImplementation(() => `blob:wand-${++assetNumber}`);
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
  wrapper = mount(host);
});

afterEach(() => { wrapper?.unmount(); wrapper = undefined; state = undefined; vi.restoreAllMocks(); });

describe('authenticated game wand illustration', () => {
  it('loads only the exact current wand and uses the owner-checked PNG route', async () => {
    await state!.refresh(wandId);
    expect(api.request).toHaveBeenCalledWith('/app/v1/customer/character', 'GET', undefined, expect.any(AbortSignal));
    expect(api.transport).toHaveBeenCalledWith(
      `/app/v1/archive/owned/${locator}/assets/${firstAsset}`,
      expect.objectContaining({ method: 'GET', redirect: 'error' }),
    );
    expect(URL.createObjectURL).toHaveBeenCalled();
    expect(state!.url.value).toBe('blob:wand-1');
    expect(state!.name.value).toBe('Měsíční ozvěna');
    expect(state!.synthetic.value).toBe(true);
  });

  it('rechecks metadata without downloading an unchanged asset', async () => {
    await state!.refresh(wandId);
    await state!.refresh(wandId);
    expect(api.request).toHaveBeenCalledTimes(2);
    expect(api.transport).toHaveBeenCalledTimes(1);
    expect(URL.createObjectURL).toHaveBeenCalledTimes(1);
  });

  it('releases the old image when the canonical asset changes', async () => {
    await state!.refresh(wandId);
    api.request.mockResolvedValueOnce(character(secondAsset));
    await state!.refresh(wandId);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:wand-1');
    expect(state!.url.value).toBe('blob:wand-2');
    expect(api.transport).toHaveBeenLastCalledWith(
      `/app/v1/archive/owned/${locator}/assets/${secondAsset}`,
      expect.objectContaining({ method: 'GET' }),
    );
  });

  it('clears on absent, not-ready, or mismatched generated wand metadata', async () => {
    await state!.refresh(wandId);
    api.request.mockResolvedValueOnce(character(null));
    await state!.refresh(wandId);
    expect(state!.url.value).toBe('');
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:wand-1');

    api.request.mockResolvedValueOnce(character(firstAsset, { status: 'queued' }));
    await state!.refresh(wandId);
    expect(state!.url.value).toBe('');
    expect(api.transport).toHaveBeenCalledTimes(1);

    api.request.mockResolvedValueOnce({ ...character(), generatedWand: { ...character().generatedWand, wandId: 'other-wand' } });
    await state!.refresh(wandId);
    expect(state!.name.value).toBe('');
    expect(state!.synthetic.value).toBe(false);
  });

  it('rejects stale metadata after the account changes', async () => {
    const response = defer<ReturnType<typeof character>>();
    api.request.mockReturnValueOnce(response.promise);
    const pending = state!.refresh(wandId);
    api.account!.value = { id: 'account-b' };
    response.resolve(character());
    await pending;
    expect(api.transport).not.toHaveBeenCalled();
    expect(state!.url.value).toBe('');
  });

  it('does not publish a late private image after the wand scope is cleared', async () => {
    const response = defer<Response>();
    api.transport.mockReturnValueOnce(response.promise);
    const pendingImage = state!.refresh(wandId);
    await vi.waitFor(() => expect(api.transport).toHaveBeenCalledTimes(1));
    state!.clear();
    response.resolve(new Response(new Blob(['late private art'], { type: 'image/png' })));
    await pendingImage;
    expect(state!.url.value).toBe('');
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });

  it('coalesces concurrent metadata reads for the same account and wand', async () => {
    const response = defer<ReturnType<typeof character>>();
    api.request.mockReturnValueOnce(response.promise);
    const first = state!.refresh(wandId);
    const second = state!.refresh(wandId);
    expect(api.request).toHaveBeenCalledTimes(1);
    response.resolve(character());
    await Promise.all([first, second]);
    expect(api.transport).toHaveBeenCalledTimes(1);
  });

  it('drops an unsupported media response and clears its synthetic marker', async () => {
    api.transport.mockResolvedValueOnce(new Response('<svg/>', { headers: { 'Content-Type': 'image/svg+xml' } }));
    await state!.refresh(wandId);
    expect(state!.url.value).toBe('');
    expect(state!.synthetic.value).toBe(false);
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });
});
