import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import PrelaunchWand from './PrelaunchWand.vue';

const api = vi.hoisted(() => ({ load: vi.fn() }));
vi.mock('../game/wandWorkshop', () => ({ loadWorkshop: api.load }));
vi.mock('../game/WandWorkshop.vue', () => ({ default: {
  name: 'WandWorkshop', props: { prelaunch: Boolean }, emits: ['saved', 'exit'],
  template: '<div><h1 id="workshop-title" tabindex="-1">Kufrova dílna</h1><button @click="$emit(\'saved\')">Uložit</button><button @click="$emit(\'exit\')">Zpět</button></div>',
} }));
let view: VueWrapper | undefined;
beforeEach(() => { api.load.mockReset().mockResolvedValue({ eligible: true, profile: { profileId: 'profile', revision: 1 }, session: null }); });
afterEach(() => { view?.unmount(); view = undefined; });
async function render() { view = mount(PrelaunchWand); await flushPromises(); return view; }

it('only reads until the guest chooses to create a wand', async () => {
  const wrapper = await render();
  expect(wrapper.text()).toContain('Můžeš odejít a vrátit se později.');
  expect(wrapper.findComponent({ name: 'WandWorkshop' }).exists()).toBe(false);
  expect(api.load).toHaveBeenCalledOnce();
  await wrapper.get('button').trigger('click');
  expect(wrapper.findComponent({ name: 'WandWorkshop' }).props('prelaunch')).toBe(true);
  expect(wrapper.emitted('open-change')).toEqual([[true]]);
});
it('returns from the saved wand to waiting and can show it again', async () => {
  const wrapper = await render();
  await wrapper.get('button').trigger('click');
  await wrapper.findAll('button')[0]!.trigger('click');
  await wrapper.findAll('button')[1]!.trigger('click');
  await flushPromises();
  expect(wrapper.text()).toContain('Hůlka je uložená. Ollivander Industries™ děkuje za důvěru. Považuje ji za odvážnou.');
  expect(wrapper.text()).toContain('Můžeš odejít a vrátit se později.');
  expect(wrapper.get('button').text()).toBe('Prohlédnout hůlku');
  expect(wrapper.emitted('open-change')).toEqual([[true], [false]]);
});
it.each(['asking', 'generating', 'complete'])('restores a %s workshop with reads after returning to the app', async status => {
  api.load.mockResolvedValue({ eligible: true, session: { status, wand: status === 'complete' ? { wandId: 'saved' } : null } });
  const wrapper = await render();
  expect(wrapper.get('button').text()).toBe(status === 'complete' ? 'Prohlédnout hůlku' : 'Pokračovat v tvorbě hůlky');
  expect(wrapper.findComponent({ name: 'WandWorkshop' }).exists()).toBe(false);
});
it('offers a read retry after a failed state request', async () => {
  api.load.mockRejectedValueOnce(new Error('offline'));
  const wrapper = await render();
  expect(wrapper.text()).toContain('Dílnu se nepodařilo načíst.');
  await wrapper.get('button').trigger('click'); await flushPromises();
  expect(wrapper.get('button').text()).toBe('Vytvořit hůlku');
});
it('aborts its request when the account, character or admission scope is removed', async () => {
  const wrapper = await render();
  const signal = api.load.mock.calls[0]![0] as AbortSignal;
  wrapper.unmount(); view = undefined;
  expect(signal.aborted).toBe(true);
});
