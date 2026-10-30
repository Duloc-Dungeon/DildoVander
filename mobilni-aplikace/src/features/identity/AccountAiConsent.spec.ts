import { beforeEach, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import AccountAiConsent from './AccountAiConsent.vue';
vi.mock('./AccountDeletion.vue', () => ({ default: { template: '<div class="account-deletion">Smazat účet</div>' } }));
const api = vi.hoisted(() => ({ request: vi.fn(), refresh: vi.fn(), signOut: vi.fn() }));
vi.mock('./session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'legacy-a', openAiConsent: false }), accountRequest: api.request,
    refreshIdentity: api.refresh, signOut: api.signOut };
});
import { account } from './session';
const options = { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } };
beforeEach(() => { api.request.mockReset().mockResolvedValue(undefined); api.refresh.mockReset(); account.value = { id: 'legacy-a', displayName: 'Legacy', roles: [], openAiConsent: false }; });
it('requires an explicit choice and persists before refreshing identity', async () => {
  const view = mount(AccountAiConsent, options);
  await view.get('form').trigger('submit');
  expect(api.request).not.toHaveBeenCalled();
  await view.get('input').setValue(true);
  await view.get('form').trigger('submit');
  await flushPromises();
  expect(api.request).toHaveBeenCalledExactlyOnceWith('/app/v1/account/ai-consent', 'POST', { openAiConsent: true, consentVersion: 3 });
  expect(view.text()).toContain('OpenAI a ElevenLabs');
  expect(api.refresh).toHaveBeenCalledOnce();
  view.unmount();
});
it('resets the choice on account switch and does not carry consent or an error to another account', async () => {
  let reject!: (cause: Error) => void;
  api.request.mockImplementation(() => new Promise((_resolve, failure) => { reject = failure; }));
  const view = mount(AccountAiConsent, options);
  await view.get('input').setValue(true);
  await view.get('form').trigger('submit');
  account.value = { id: 'legacy-b', displayName: 'Other', roles: [], openAiConsent: false };
  reject(new Error('previous account'));
  await flushPromises();
  expect((view.get('input').element as HTMLInputElement).checked).toBe(false);
  expect(view.find('[role="alert"]').exists()).toBe(false);
  expect(api.refresh).not.toHaveBeenCalled();
  view.unmount();
});
it('keeps failure recoverable without treating the choice as saved', async () => {
  api.request.mockRejectedValueOnce(new Error('offline'));
  const view = mount(AccountAiConsent, options);
  await view.get('input').setValue(true);
  await view.get('form').trigger('submit');
  await flushPromises();
  expect(view.get('[role="alert"]').text()).toContain('nepodařilo uložit');
  expect(api.refresh).not.toHaveBeenCalled();
  await view.get('form').trigger('submit');
  await flushPromises();
  expect(api.refresh).toHaveBeenCalledOnce();
  view.unmount();
});
