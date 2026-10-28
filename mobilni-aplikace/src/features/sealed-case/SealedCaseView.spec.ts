import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
const api = vi.hoisted(() => ({ transport: vi.fn(), setAccount: (_value: unknown) => {} }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref<unknown>({ id: 'one' });
  api.setAccount = value => { account.value = value; };
  return { account, accountTransport: api.transport };
});
import SealedCaseView from './SealedCaseView.vue';

let wrapper: VueWrapper | undefined;
async function render() {
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/play/pod-peceti/:id?', component: SealedCaseView },
  ] });
  await router.push('/play/pod-peceti');
  wrapper = mount(SealedCaseView, { global: { plugins: [router] } });
  await flushPromises();
  return { view: wrapper, router };
}
beforeEach(() => { api.transport.mockReset(); api.setAccount({ id: 'one' }); vi.useFakeTimers(); });
afterEach(() => { wrapper?.unmount(); vi.useRealTimers(); });

describe('sealed case discovery lifecycle', () => {
  it('ignores a previous account response when discovering the current case', async () => {
    let finish!: (response: Response) => void;
    api.transport.mockImplementationOnce(() => new Promise<Response>(resolve => { finish = resolve; }))
      .mockResolvedValue(Response.json({ id: null }));
    const { router } = await render();
    api.setAccount({ id: 'two' });
    await flushPromises();
    finish(Response.json({ id: `sc_${'1'.repeat(32)}` }));
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/play/pod-peceti');
    expect(api.transport).toHaveBeenCalledTimes(2);
  });

  it('does not restart polling when an outstanding request settles after leaving', async () => {
    let finish!: (response: Response) => void;
    api.transport.mockImplementation(() => new Promise<Response>(resolve => { finish = resolve; }));
    const { view } = await render();
    view.unmount(); wrapper = undefined;
    finish(Response.json({ id: null }));
    await flushPromises();
    await vi.advanceTimersByTimeAsync(15000);
    expect(api.transport).toHaveBeenCalledTimes(1);
  });

  it('clears a discovery failure after a successful refresh with no current case', async () => {
    api.transport.mockResolvedValueOnce(Response.json({ code: 'unavailable' }, { status: 503 }))
      .mockResolvedValue(Response.json({ id: null }));
    const { view } = await render();
    expect(view.find('[role="alert"]').exists()).toBe(true);
    await view.findAll('button').find(button => button.text() === 'Obnovit stav')!.trigger('click');
    await flushPromises();
    expect(view.find('[role="alert"]').exists()).toBe(false);
    expect(view.text()).toContain('Založit případ');
  });
});
