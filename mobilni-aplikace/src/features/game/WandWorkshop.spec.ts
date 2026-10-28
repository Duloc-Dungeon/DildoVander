import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
const state = vi.hoisted(() => ({ observer: null as unknown as Record<string, (...args: unknown[]) => void>, request: vi.fn(), end: vi.fn(), dispose: vi.fn(), sessionId: '' }));
vi.mock('../identity/session', async () => {
  const {ref} = await import('vue');
  return {account: ref({id:'alice',openAiConsent:true}), accountOrigin: ref('https://app.invalid'), accountRequest:state.request};
});
vi.mock('../persona-lab/voice', () => ({LabVoice: class {
  constructor(observer: typeof state.observer) { state.observer = observer; }
  async connect(ticket: () => Promise<unknown>, id: string | (() => string)) { await ticket(); state.sessionId = typeof id === 'function' ? id() : id; state.observer.phase('listening'); }
  end() { state.end(); } dispose() { state.dispose(); }
}}));
import WandWorkshop from './WandWorkshop.vue';
const wand = { wandId:'wand-id',sessionId:'session',profileId:'profile',profileRevision:1,physicalSlot:1,name:'Jantarová ozvěna',material:'Dub',core:'Jiskra',temperament:'Zvídavá',lore:'Fikční příběh.',designDescription:'Herní hůlka',affinity:'Hvězdy',weakness:'Netrpělivost',spells:[{action:'strike',name:'Úder',description:'Úder'},{action:'trick',name:'Lest',description:'Lest'},{action:'shield',name:'Štít',description:'Štít'}],combatArchetype:'balanced',combatRulesVersion:'wand-archetypes-v1'};
beforeEach(() => { state.request.mockReset(); state.end.mockReset(); state.dispose.mockReset(); state.request.mockImplementation(async (path: string) => path.endsWith('/sessions') ? {id:'b'.repeat(32)} : {url:'wss://app.invalid/live'}); });
describe('voice wand workshop', () => {
  it('uses the canonical retry operation and keeps final speech mounted until closure', async () => {
    const view = mount(WandWorkshop);
    await view.get('button').trigger('click'); await flushPromises();
    expect(state.sessionId).toBe('b'.repeat(32));
    expect(state.request.mock.calls[1][0]).toBe(`/app/v1/customer/wand/sessions/${'b'.repeat(32)}/ticket`);
    state.observer.wand(wand); await flushPromises();
    expect(view.text()).toContain('Jantarová ozvěna');
    expect(view.emitted('created')).toBeUndefined();
    expect(state.end).not.toHaveBeenCalled();
    state.observer.closed(); await flushPromises();
    expect(view.emitted('created')).toHaveLength(1);
    view.unmount(); expect(state.end).toHaveBeenCalledTimes(1);
  });
  it('stops the microphone and refreshes a saved result on explicit end', async () => {
    const view = mount(WandWorkshop, {props:{upgrade:true}});
    await view.get('button').trigger('click'); await flushPromises();
    expect(state.request.mock.calls[0][2].upgrade).toBe(true);
    state.observer.wand(wand); await flushPromises();
    await view.get('button').trigger('click');
    expect(state.end).toHaveBeenCalled(); expect(view.emitted('created')).toHaveLength(1);
    view.unmount();
  });
});
