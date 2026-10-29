import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { nextTick, type Ref } from 'vue';
import ChronicleMatchReveal from './ChronicleMatchReveal.vue';

const api = vi.hoisted(() => ({
  account: null as Ref<{ id: string } | null> | null,
  offline: null as Ref<boolean> | null,
  signingOut: null as Ref<boolean> | null,
  deleting: null as Ref<boolean> | null,
  source: vi.fn(),
  reveal: vi.fn(),
}));

vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  api.account = ref({ id: 'alice' });
  api.offline = ref(false);
  api.signingOut = ref(false);
  api.deleting = ref(false);
  return {
    account: api.account, offlineSession: api.offline, signingOut: api.signingOut,
    accountDeletionRequested: api.deleting,
  };
});

vi.mock('./chronicle', () => ({
  ChronicleClient: class {
    source(...args: unknown[]) { return api.source(...args); }
    reveal(...args: unknown[]) { return api.reveal(...args); }
  },
  chronicleCanRead: () => Boolean(api.account?.value && !api.offline?.value && !api.signingOut?.value && !api.deleting?.value && navigator.onLine),
  isChronicleEvent: (item: { revealed?: boolean }) => item.revealed === true,
}));

const aliceEvent = {
  id: '15ca3a2a-8612-4d66-8fc4-8e65a64c9712', sourceKind: 'duel', sourceId: '1'.repeat(32),
  title: 'Alenčina vzpomínka', summary: 'Její příběh.', participants: [], story: [], decisions: [],
  awards: [{ id: 'alice-award', title: 'Alenčin odznak' }], illustrationState: 'ready', revealed: true,
};
const lockedEvent = { id: aliceEvent.id, sourceKind: 'duel', sourceId: aliceEvent.sourceId, revealed: false };

function mountCard(props: Partial<{ matchId: string; mode: string; status: string; verdictVisible: boolean }> = {}) {
  return mount(ChronicleMatchReveal, {
    props: { matchId: aliceEvent.sourceId, mode: 'duel', status: 'complete', verdictVisible: true, ...props },
    global: { stubs: { RouterLink: true, AwButton: { template: '<button @click="$emit(\'click\')"><slot /></button>' } } },
  });
}

beforeEach(() => {
  vi.useRealTimers();
  api.account!.value = { id: 'alice' };
  api.offline!.value = false;
  api.signingOut!.value = false;
  api.deleting!.value = false;
  api.source.mockReset();
  api.reveal.mockReset();
  api.reveal.mockResolvedValue(aliceEvent);
});

afterEach(() => { vi.useRealTimers(); });

describe('ChronicleMatchReveal', () => {
  it('does not request a YOLO source until its verdict is visible', async () => {
    api.source.mockResolvedValue(lockedEvent);
    const wrapper = mountCard({ mode: 'yolo', verdictVisible: false });
    expect(api.source).not.toHaveBeenCalled();
    await wrapper.setProps({ verdictVisible: true });
    await flushPromises();
    expect(api.source).toHaveBeenCalledOnce();
    expect(api.reveal).toHaveBeenCalledOnce();
    wrapper.unmount();
  });

  it('discards an Alice response that arrives after switching to Bob', async () => {
    let resolveAlice!: (value: typeof aliceEvent) => void;
    api.source.mockImplementationOnce(() => new Promise(resolve => { resolveAlice = resolve; }))
      .mockImplementation(() => new Promise(() => {}));
    const wrapper = mountCard();
    api.account!.value = { id: 'bob' };
    await nextTick();
    expect(wrapper.text()).not.toContain('Alenčina vzpomínka');
    resolveAlice(aliceEvent);
    await flushPromises();
    expect(wrapper.text()).not.toContain('Alenčina vzpomínka');
    expect(wrapper.text()).not.toContain('Alenčin odznak');
    expect(api.reveal).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('clears a mounted previous-owner award synchronously on account switch', async () => {
    api.source.mockResolvedValueOnce(aliceEvent).mockImplementation(() => new Promise(() => {}));
    const wrapper = mountCard();
    await flushPromises();
    expect(wrapper.text()).toContain('Alenčin odznak');
    api.account!.value = { id: 'bob' };
    await nextTick();
    expect(wrapper.text()).not.toContain('Alenčina vzpomínka');
    expect(wrapper.text()).not.toContain('Alenčin odznak');
    wrapper.unmount();
  });

  it('retries source 404s on a bounded schedule and never repeats after delivery', async () => {
    vi.useFakeTimers();
    api.source.mockRejectedValueOnce(Object.assign(new Error('not delivered'), { status: 404 }))
      .mockRejectedValueOnce(Object.assign(new Error('not delivered'), { status: 404 }))
      .mockResolvedValue(lockedEvent);
    const wrapper = mountCard();
    await flushPromises();
    expect(api.source).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(499);
    expect(api.source).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(1);
    await flushPromises();
    expect(api.source).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(1000);
    await flushPromises();
    expect(api.source).toHaveBeenCalledTimes(3);
    expect(api.reveal).toHaveBeenCalledOnce();
    expect(wrapper.text()).toContain('Alenčina vzpomínka');
    await vi.advanceTimersByTimeAsync(10_000);
    expect(api.source).toHaveBeenCalledTimes(3);
    expect(api.reveal).toHaveBeenCalledOnce();
    wrapper.unmount();
  });

  it.each(['account switch', 'unmount'])('cancels scheduled source retries on %s', async action => {
    vi.useFakeTimers();
    api.source.mockRejectedValueOnce(Object.assign(new Error('not delivered'), { status: 404 }))
      .mockImplementation(() => new Promise(() => {}));
    const wrapper = mountCard();
    await flushPromises();
    expect(api.source).toHaveBeenCalledOnce();
    if (action === 'account switch') {
      api.account!.value = { id: 'bob' };
      await flushPromises();
    }
    else wrapper.unmount();
    await flushPromises();
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(7000);
    expect(api.source).toHaveBeenCalledTimes(action === 'account switch' ? 2 : 1);
    expect(api.reveal).not.toHaveBeenCalled();
    if (action !== 'unmount') wrapper.unmount();
  });

  it('does not retry non-404 source failures', async () => {
    vi.useFakeTimers();
    api.source.mockRejectedValue(Object.assign(new Error('server unavailable'), { status: 503 }));
    const wrapper = mountCard();
    await flushPromises();
    expect(wrapper.text()).toContain('Výsledek zůstává uložený');
    await vi.advanceTimersByTimeAsync(7000);
    expect(api.source).toHaveBeenCalledOnce();
    wrapper.unmount();
  });
});
