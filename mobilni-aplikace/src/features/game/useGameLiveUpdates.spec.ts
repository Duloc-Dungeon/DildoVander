import { afterEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, ref } from 'vue';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const live = vi.hoisted(() => ({ changed: () => {}, stop: vi.fn() }));
vi.mock('./gameLive', () => ({ connectGameLive: (changed: () => void) => { live.changed = changed; return live.stop; } }));
vi.mock('../identity/session', async () => ({ account: (await import('vue')).ref({ id: 'one' } as { id: string } | null) }));
import { account } from '../identity/session';
import { useGameLiveUpdates } from './useGameLiveUpdates';

let wrapper: VueWrapper | undefined;
function render(refresh: () => Promise<void>, blocked = ref(false)) {
  account.value = { id: 'one' } as typeof account.value;
  wrapper = mount(defineComponent({ setup() { useGameLiveUpdates(refresh, () => blocked.value); return () => null; } }));
}
afterEach(() => { wrapper?.unmount(); vi.clearAllMocks(); vi.restoreAllMocks(); });

describe('game live invalidations', () => {
  it('coalesces notifications during a command and during an in-flight snapshot', async () => {
    const busy = ref(true);
    let finish!: () => void;
    const refresh = vi.fn().mockImplementationOnce(() => new Promise<void>(resolve => { finish = resolve; })).mockResolvedValue(undefined);
    render(refresh, busy);
    live.changed(); live.changed();
    expect(refresh).not.toHaveBeenCalled();
    busy.value = false;
    await flushPromises();
    expect(refresh).toHaveBeenCalledTimes(1);
    live.changed(); live.changed();
    expect(refresh).toHaveBeenCalledTimes(1);
    finish();
    await flushPromises();
    expect(refresh).toHaveBeenCalledTimes(2);
  });

  it('defers hidden updates until visible and stops on logout and unmount', async () => {
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);
    const refresh = vi.fn().mockResolvedValue(undefined);
    render(refresh);
    live.changed();
    await flushPromises();
    expect(refresh).not.toHaveBeenCalled();
    hidden.mockReturnValue(false);
    document.dispatchEvent(new Event('visibilitychange'));
    await flushPromises();
    expect(refresh).toHaveBeenCalledTimes(1);
    account.value = null;
    expect(live.stop).toHaveBeenCalled();
    live.changed();
    await flushPromises();
    expect(refresh).toHaveBeenCalledTimes(1);
    wrapper!.unmount(); wrapper = undefined;
    live.changed();
    await flushPromises();
    expect(refresh).toHaveBeenCalledTimes(1);
  });
});
