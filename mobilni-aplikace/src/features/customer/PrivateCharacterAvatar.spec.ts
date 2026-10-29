import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import PrivateCharacterAvatar from './PrivateCharacterAvatar.vue';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'owner', openAiConsent: true }), accountRequest: api.request };
});
vi.mock('./privateCharacterPortrait', async () => {
  const { computed, ref } = await import('vue');
  return { usePrivateCharacterPortrait: (identity: () => { profileId: string; revision: number } | null) => ({
    url: computed(() => { const value = identity(); return value ? `blob:${value.profileId}-${value.revision}` : ''; }),
    pending: ref(false), failed: ref(false), retry: vi.fn(), fail: vi.fn(),
  }) };
});
const mounted: ReturnType<typeof mount>[] = [];
const state = (profileId = 'profile-a') => ({ character: { profileId, revision: 1 }, artwork: { status: 'ready', finalMessage: `Hotovo pro ${profileId}.` } });
beforeEach(() => { api.request.mockReset(); });
afterEach(() => { mounted.splice(0).forEach(view => view.unmount()); });
function render() { const view = mount(PrivateCharacterAvatar, { props: { profileId: 'profile-a', revision: 1, name: 'Alžběta' } }); mounted.push(view); return view; }

it('hides serialized missing welcome fields in an existing portrait', async () => {
  api.request.mockResolvedValue({ ...state(), artwork: { status: 'ready', welcome: { joke: 'null', identity: ' NULL ', teaser: 'undefined' } } });
  const view = render();
  await flushPromises();
  expect(view.find('.private-character-avatar__joke').exists()).toBe(false);
  expect(view.text()).not.toMatch(/null|undefined/i);
});

it('reuses the existing private portrait with reads only and keeps it separate from wand artwork', async () => {
  api.request.mockResolvedValue(state());
  const view = render();
  await flushPromises();
  expect(view.get('img').attributes('src')).toBe('blob:profile-a-1');
  expect(view.text()).toContain('Hotovo pro profile-a.');
  expect(view.text()).toContain('nikam nezveřejňuje automaticky');
  expect(api.request.mock.calls).toHaveLength(1);
  expect(api.request.mock.calls[0].slice(0, 2)).toEqual(['/app/v1/prelaunch/character', 'GET']);
});

it('does not show another profile’s ready image when an older metadata response arrives late', async () => {
  let release!: (value: ReturnType<typeof state>) => void;
  api.request.mockReturnValueOnce(new Promise(resolve => { release = resolve; })).mockResolvedValueOnce(state('profile-b'));
  const view = render();
  await view.setProps({ profileId: 'profile-b', name: 'Beáta' });
  await flushPromises();
  expect(view.get('img').attributes('src')).toBe('blob:profile-b-1');
  release(state());
  await flushPromises();
  expect(view.get('img').attributes('src')).toBe('blob:profile-b-1');
  expect(view.text()).not.toContain('Hotovo pro profile-a.');
});

it('offers explicit preparation without creating an image when an existing character has no portrait', async () => {
  api.request.mockResolvedValue({ character: { profileId: 'profile-a', revision: 1 }, artwork: null });
  const view = render();
  await flushPromises();
  expect(view.find('figure').exists()).toBe(true);
  expect(view.get('button').text()).toBe('Připravit portrét');
  expect(api.request.mock.calls.every(([, method]) => method === 'GET')).toBe(true);
});

it('actually retries generation for a failed portrait and preserves its generated joke', async () => {
  api.request.mockResolvedValueOnce({ character: { profileId: 'profile-a', revision: 1 }, artwork: {
    status: 'failed', welcome: { joke: 'Sebevědomí máš na světovládu. Pro dnešek ti svěříme židli.', identity: '', teaser: '' },
  } }).mockResolvedValueOnce({}).mockResolvedValueOnce(state());
  const view = render();
  await flushPromises();
  expect(view.text()).toContain('Pro dnešek ti svěříme židli.');
  await view.get('button').trigger('click');
  await flushPromises();
  expect(api.request.mock.calls[1].slice(0, 3)).toEqual(['/app/v1/prelaunch/character/retry', 'POST', {
    expectedProfileId: 'profile-a', expectedRevision: 1,
  }]);
  expect(view.get('img').attributes('src')).toBe('blob:profile-a-1');
});

it('does not display a previous revision’s joke after changing character', async () => {
  api.request.mockResolvedValueOnce({ ...state(), artwork: { status: 'failed', welcome: { joke: 'Vtip staré postavy.', identity: '', teaser: '' } } })
    .mockResolvedValueOnce(state('profile-b'));
  const view = render();
  await flushPromises();
  expect(view.text()).toContain('Vtip staré postavy.');
  await view.setProps({ profileId: 'profile-b', name: 'Beáta' });
  await flushPromises();
  expect(view.text()).not.toContain('Vtip staré postavy.');
});
