import { ref } from 'vue';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('./session', () => ({ account: ref({ id: 'admin', roles: ['Administrator'] }), accountRequest: api.request,
  hasRole: (role: string) => account.value?.roles.includes(role) || account.value?.roles.includes('Owner') }));
import { account } from './session';
import ActorVoicesPanel from './ActorVoicesPanel.vue';

let view: VueWrapper | undefined;
const initial = { revision: 2, kufrVoiceId: 'KufrVoice1', baronVoiceId: 'BaronVoice2' };
beforeEach(() => {
  account.value = { id: 'admin', displayName: 'Správce', roles: ['Administrator'] };
  api.request.mockReset().mockResolvedValue(initial);
});
afterEach(() => { view?.unmount(); view = undefined; });
async function render() {
  view = mount(ActorVoicesPanel, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } });
  await flushPromises(); return view;
}

describe('independent actor voice administration', () => {
  it('loads separate voices and saves a revision without making a speech request', async () => {
    const panel = await render();
    expect((panel.get('#kufr-voice-id').element as HTMLInputElement).value).toBe('KufrVoice1');
    expect((panel.get('#baron-voice-id').element as HTMLInputElement).value).toBe('BaronVoice2');
    await panel.get('#kufr-voice-id').setValue('U48DQ1c9SVmD2BVCSiHL');
    api.request.mockResolvedValueOnce({ ...initial, revision: 3, kufrVoiceId: 'U48DQ1c9SVmD2BVCSiHL' });
    await panel.get('form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenLastCalledWith('/app/v1/admin/actor-voices', 'PUT', {
      expectedRevision: 2, kufrVoiceId: 'U48DQ1c9SVmD2BVCSiHL', baronVoiceId: 'BaronVoice2',
    }, expect.any(AbortSignal), expect.any(String));
    expect(panel.get('[role="status"]').text()).toContain('Hlasy jsou uložené');
    expect(api.request).toHaveBeenCalledTimes(2);
  });

  it('preserves a conflicting draft until explicit reload', async () => {
    const panel = await render();
    await panel.get('#baron-voice-id').setValue('NewBaron3');
    api.request.mockRejectedValueOnce(Object.assign(new Error('stale'), { status: 409 }));
    await panel.get('form').trigger('submit'); await flushPromises();
    expect((panel.get('#baron-voice-id').element as HTMLInputElement).value).toBe('NewBaron3');
    expect(panel.get('[role="alert"]').text()).toContain('Tvoje úpravy zůstaly');
    expect(panel.get('button[type="submit"]').attributes('disabled')).toBeDefined();
    api.request.mockResolvedValueOnce({ ...initial, revision: 4, baronVoiceId: 'RemoteBaron4' });
    await panel.findAll('button').find(button => button.text() === 'Zahodit úpravy a načíst uložené hlasy')!.trigger('click');
    await flushPromises();
    expect((panel.get('#baron-voice-id').element as HTMLInputElement).value).toBe('RemoteBaron4');
  });

  it('rejects invalid voice IDs locally', async () => {
    const panel = await render();
    await panel.get('#kufr-voice-id').setValue('../untrusted');
    await panel.get('form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenCalledOnce();
  });

  it('clears controls and ignores a late save response after an account change', async () => {
    const panel = await render();
    await panel.get('#baron-voice-id').setValue('NewBaron3');
    let complete!: (value: unknown) => void;
    api.request.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
    await panel.get('form').trigger('submit');
    account.value = { id: 'customer', displayName: 'Host', roles: ['Customer'] };
    await flushPromises(); complete({ ...initial, revision: 3, baronVoiceId: 'NewBaron3' }); await flushPromises();
    expect(panel.find('form').exists()).toBe(false);
    expect(panel.find('[role="status"]').exists()).toBe(false);
    expect(api.request).toHaveBeenCalledTimes(2);
  });
});
