import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'alice', displayName: 'Alice', roles: ['Customer'], openAiConsent: false }), accountRequest: api.request };
});
import { account } from '../identity/session';
import CharacterLabelEditor from './CharacterLabelEditor.vue';

const wrappers: VueWrapper[] = [];
function render() {
  const wrapper = mount(CharacterLabelEditor, { props: { profileId: 'profile-alice', label: null } });
  wrappers.push(wrapper);
  return wrapper;
}
beforeEach(() => { api.request.mockReset(); account.value = { id: 'alice', displayName: 'Alice', roles: ['Customer'], openAiConsent: false }; });
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); });

describe('independent character label', () => {
  it('saves without AI consent or an editable character name', async () => {
    api.request.mockResolvedValue({ profile: { label: 'Petr' } });
    const wrapper = render();
    await wrapper.get('input').setValue('Petr');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/customer/character/label', 'PUT',
      { profileId: 'profile-alice', label: 'Petr', expectedLabel: null }, expect.any(AbortSignal));
    expect(wrapper.emitted('saved')).toEqual([['Petr']]);
  });

  it('rejects a late response after the signed-in account changes', async () => {
    let finish!: (value: unknown) => void;
    api.request.mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const wrapper = render();
    await wrapper.get('input').setValue('Petr');
    await wrapper.get('form').trigger('submit');
    account.value = { id: 'bob', displayName: 'Bob', roles: ['Customer'], openAiConsent: false };
    await flushPromises();
    finish({ profile: { label: 'Petr' } });
    await flushPromises();
    expect(wrapper.emitted('saved')).toBeUndefined();
  });

  it('keeps the draft and reports conflicting edits', async () => {
    api.request.mockRejectedValue({ status: 409 });
    const wrapper = render();
    await wrapper.get('input').setValue('Petr');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(wrapper.get('[role="alert"]').text()).toContain('Obnov stránku');
    expect(wrapper.get('input').element.value).toBe('Petr');
    expect(wrapper.emitted('saved')).toBeUndefined();
  });
});
