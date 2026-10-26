import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, expect, it, vi } from 'vitest';
import AdminReauthentication from './AdminReauthentication.vue';

const api = vi.hoisted(() => ({ reauthenticate: vi.fn() }));
vi.mock('./session', () => ({ authenticatePasskey: api.reauthenticate }));
beforeEach(() => { api.reauthenticate.mockReset(); });

it('keeps verification available after passkey cancellation and does not report success', async () => {
  api.reauthenticate.mockRejectedValue(new Error('Ověření bylo zrušeno.'));
  const view = mount(AdminReauthentication);
  try {
    await view.get('button').trigger('click');
    await flushPromises();
    expect(view.get('[role="alert"]').text()).toBe('Ověření bylo zrušeno.');
    expect(view.emitted('verified')).toBeUndefined();
    expect(view.get('button').attributes('disabled')).toBeUndefined();
  } finally { view.unmount(); }
});

it('prevents duplicate verification requests while an authenticator is pending', async () => {
  let complete!: () => void;
  api.reauthenticate.mockImplementation(() => new Promise<void>(resolve => { complete = resolve; }));
  const view = mount(AdminReauthentication);
  try {
    await view.get('button').trigger('click');
    await view.get('button').trigger('click');
    expect(api.reauthenticate).toHaveBeenCalledTimes(1);
    expect(view.get('button').attributes('disabled')).toBeDefined();
    complete();
    await flushPromises();
    expect(view.emitted('verified')).toHaveLength(1);
  } finally { view.unmount(); }
});
