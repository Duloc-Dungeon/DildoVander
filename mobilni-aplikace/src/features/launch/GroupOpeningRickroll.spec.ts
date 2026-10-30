import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, afterEach, expect, it, vi } from 'vitest';
import GroupOpeningRickroll from './GroupOpeningRickroll.vue';

const { state, route, owner, membership, policy } = await vi.hoisted(async () => {
  const { reactive, ref } = await import('vue');
  return {
    state: { media: vi.fn() }, route: reactive({ path: '/play' }), owner: ref({ id: 'guest' }),
    membership: ref('active'), policy: ref({ allowed: true, launchAt: 1000, openedAt: 2000, serverNow: 3000 }),
  };
});
vi.mock('vue-router', () => ({ useRoute: () => route }));
vi.mock('../identity/session', () => ({ account: owner }));
vi.mock('../../application/launchAccess', () => ({ customerMembershipState: membership, launchPolicySnapshot: policy }));
vi.mock('../media/rickrollMedia', () => ({ readRickrollVideo: (...args: unknown[]) => state.media(...args) }));
vi.mock('../media/RickrollVideo.vue', () => ({ default: { props: ['active', 'src'], emits: ['dismiss'], template: '<button v-if="active" @click="$emit(\'dismiss\')">Done</button>' } }));
beforeEach(() => {
  sessionStorage.clear();
  route.path = '/play'; owner.value = { id: 'guest' }; membership.value = 'active';
  policy.value = { allowed: true, launchAt: 1000, openedAt: 2000, serverNow: 3000 };
  state.media.mockReset().mockResolvedValue('/app/v1/launch/rickroll.mp4');
  vi.spyOn(HTMLDialogElement.prototype, 'showModal').mockImplementation(function (this: HTMLDialogElement) { this.open = true; });
  vi.spyOn(HTMLDialogElement.prototype, 'close').mockImplementation(function (this: HTMLDialogElement) { this.open = false; });
});
afterEach(() => vi.restoreAllMocks());
it('covers direct arrivals and remembers completion for this account and opening', async () => {
  let view = mount(GroupOpeningRickroll);
  try {
    await flushPromises();
    expect(view.get('dialog').element.open).toBe(true);
    await view.get('button').trigger('click');
    await flushPromises();
    expect(view.get('dialog').element.open).toBe(false);
    view.unmount(); view = mount(GroupOpeningRickroll);
    await flushPromises();
    expect(view.find('button').exists()).toBe(false);
    owner.value = { id: 'another' };
    await flushPromises();
    expect(view.get('dialog').element.open).toBe(true);
  } finally { view.unmount(); }
});
it.each(['expired', 'waiting', 'revoked', 'launch'])('does not cover %s views', async mode => {
  if (mode === 'expired') policy.value.serverNow = 902000;
  if (mode === 'waiting') policy.value.allowed = false;
  if (mode === 'revoked') membership.value = 'revoked';
  if (mode === 'launch') route.path = '/launch';
  const view = mount(GroupOpeningRickroll);
  try { await flushPromises(); expect(state.media).not.toHaveBeenCalled(); }
  finally { view.unmount(); }
});
it('discards media fetched for the previous account', async () => {
  let resolve!: (value: string) => void;
  state.media.mockImplementationOnce(() => new Promise<string>(done => { resolve = done; }));
  const view = mount(GroupOpeningRickroll);
  try {
    membership.value = 'revoked';
    await flushPromises();
    resolve('/app/v1/launch/rickroll.mp4');
    await flushPromises();
    expect(view.find('button').exists()).toBe(false);
  } finally { view.unmount(); }
});
