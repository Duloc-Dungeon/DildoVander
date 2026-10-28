import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { ref } from 'vue';
import LaunchView from './LaunchView.vue';
import { getInvitationRegistration, invitationRegistration, setInvitationDraft } from '../identity/invitationRegistration';
import { beginInvitationPresentation, presentationForeground, selectInvitationPresentation } from './launchPresentation';
import { customerFixedUniverse, customerMembershipState } from '../../application/launchAccess';

const api = vi.hoisted(() => ({ request: vi.fn(), replace: vi.fn(), setAccount: vi.fn(), portrait: vi.fn() }));
vi.mock('vue-router', () => ({ useRouter: () => ({ replace: api.replace }) }));
vi.mock('../identity/AccountDeletion.vue', () => ({ default: { template: '<span />' } }));
vi.mock('./PortraitCurtain.vue', () => ({ default: { name: 'PortraitCurtain', props: ['image'], template: '<div class="portrait-stage" />' } }));
vi.mock('./PrelaunchWand.vue', () => ({ default: { name: 'PrelaunchWand', emits: ['open-change'], template: '<section><button @click="$emit(\'open-change\', true)">Vytvořit hůlku</button><button @click="$emit(\'open-change\', false)">Zpět k odpočtu</button></section>' } }));
vi.mock('../customer/privateCharacterPortrait', () => ({ usePrivateCharacterPortrait: () => api.portrait() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref({ id: 'guest', displayName: 'Guest', roles: ['Customer'], openAiConsent: true });
  api.setAccount.mockImplementation(value => { account.value = value; });
  return { account, accountDeletionRequested: ref(false), accountRequest: api.request, accountTransport: vi.fn() };
});
let image: ReturnType<typeof ref<string>>;
let failed: ReturnType<typeof ref<boolean>>;
let persisted = false;
let allowed = false;
let revokedAudience = false;
let artworkStatus = 'queued';
let artworkSource: { url: string; title: string; author: string; license: string; licenseUrl: string } | null = null;
let memberState: 'active' | 'unassigned' | 'revoked' = 'active';
let memberUniverse: string | null = null;
const character = { profileId: 'profile', revision: 1, name: 'Raven', universe: 'Fiction', status: 'queued' };
function entry() { return mount(LaunchView, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } }); }
function savedCreation(status: 'saving' | 'uncertain' | 'confirmed' = 'confirmed') {
  const draft = setInvitationDraft('guest', 'Raven', 'Fiction');
  invitationRegistration.value = { ...draft, status, writeStarted: true, ...(status === 'confirmed' ? { profileId: 'profile', revision: 1 } : {}) };
  beginInvitationPresentation('guest', draft.creationId);
}
beforeEach(() => {
  vi.useFakeTimers(); localStorage.clear(); sessionStorage.clear(); invitationRegistration.value = null;
  selectInvitationPresentation(); presentationForeground.value = true;
  api.request.mockReset(); api.replace.mockReset(); api.setAccount({ id: 'guest', displayName: 'Guest', roles: ['Customer'], openAiConsent: true });
  image = ref(''); failed = ref(false);
  api.portrait.mockReturnValue({ url: image, failed, pending: ref(false), clear: vi.fn(() => { image.value = ''; }), retry: vi.fn(), fail: vi.fn() });
  persisted = false; allowed = false; revokedAudience = false; artworkStatus = 'queued'; artworkSource = null;
  character.status = 'queued';
  memberState = 'active'; memberUniverse = null; customerMembershipState.value = null; customerFixedUniverse.value = null;
  api.request.mockImplementation(async (path: string, method?: string) => {
    if (path === '/app/v1/launch') return { allowed, audience: revokedAudience ? 'revoked' : 'beta', launchAt: Date.now() + 86400000, serverNow: Date.now() };
    if (path === '/app/v1/customer/access') return { state: memberState, accessRevision: 1, fixedUniverse: memberUniverse };
    if (path === '/app/v1/enrollment/default') return { available: true };
    if (path === '/app/v1/enrollment/default/admit' && method === 'POST') { memberState = 'active'; memberUniverse = 'Astronomy'; return {}; }
    if (method === 'PUT') { persisted = true; throw new Error('Response lost'); }
    return { character: persisted ? character : null, artwork: persisted ? { status: artworkStatus, finalMessage: 'PRIVATE FINAL', ...(artworkSource ? { source: artworkSource } : {}) } : null };
  });
});
it('offers wand creation only after revealing the saved character', async () => {
  persisted = true; artworkStatus = 'ready';
  const view = entry();
  try {
    await flushPromises(); image.value = 'blob:portrait'; await flushPromises();
    expect(view.findComponent({ name: 'PrelaunchWand' }).exists()).toBe(false);
    view.findComponent({ name: 'PortraitCurtain' }).vm.$emit('revealed', true); await flushPromises();
    expect(view.findComponent({ name: 'PrelaunchWand' }).exists()).toBe(true);
    await view.findComponent({ name: 'PrelaunchWand' }).get('button').trigger('click');
    expect(view.get('.launch-card').attributes('style')).toContain('display: none');
    expect(api.request.mock.calls.some(call => call[1] === 'POST')).toBe(false);
  } finally { view.unmount(); }
});
it('keeps an open workshop through night opening, then presents the night when the guest returns', async () => {
  persisted = true; artworkStatus = 'ready';
  const view = entry();
  try {
    await flushPromises(); image.value = 'blob:portrait'; await flushPromises();
    view.findComponent({ name: 'PortraitCurtain' }).vm.$emit('revealed', true); await flushPromises();
    await view.findComponent({ name: 'PrelaunchWand' }).get('button').trigger('click');
    allowed = true; await vi.advanceTimersByTimeAsync(15000); await flushPromises();
    expect(view.find('.group-opening').exists()).toBe(false);
    expect(api.replace).not.toHaveBeenCalled();
    view.findComponent({ name: 'PrelaunchWand' }).vm.$emit('open-change', false); await flushPromises();
    expect(view.find('.group-opening').exists()).toBe(true);
  } finally { view.unmount(); }
});
it('allows the saved character to proceed when its portrait fails', async () => {
  persisted = true; artworkStatus = 'failed';
  const view = entry();
  try { await flushPromises(); expect(view.findComponent({ name: 'PrelaunchWand' }).exists()).toBe(true); }
  finally { view.unmount(); }
});
it('removes the workshop when admission is revoked', async () => {
  persisted = true; artworkStatus = 'failed';
  const view = entry();
  try {
    await flushPromises(); expect(view.findComponent({ name: 'PrelaunchWand' }).exists()).toBe(true);
    revokedAudience = true; await vi.advanceTimersByTimeAsync(15000); await flushPromises();
    expect(view.findComponent({ name: 'PrelaunchWand' }).exists()).toBe(false);
  } finally { view.unmount(); }
});
it('shows safe source attribution for a fallback portrait and omits it for generated artwork', async () => {
  persisted = true; artworkStatus = 'ready'; artworkSource = {
    url: 'https://commons.wikimedia.org/wiki/File:Portrait.jpg', title: '<em>Portrét</em>', author: 'Autor', license: 'CC BY-SA 4.0',
    licenseUrl: 'https://creativecommons.org/licenses/by-sa/4.0/'
  };
  const view = entry();
  try {
    await flushPromises();
    image.value = 'blob:authorised-decoded';
    await flushPromises();
    view.findComponent({ name: 'PortraitCurtain' }).vm.$emit('revealed', true);
    await flushPromises();
    expect(view.text()).toContain('Obrázek z internetu');
    expect(view.text()).toContain('<em>Portrét</em>');
    expect(view.find('.launch-attribution a').attributes('href')).toBe(artworkSource.url);
    expect(view.find('.launch-attribution a').attributes('rel')).toBe('noopener noreferrer');
    expect(view.find('.launch-attribution').html()).not.toContain('<em>Portrét</em>');
  } finally { view.unmount(); }
});
afterEach(() => { selectInvitationPresentation(); vi.useRealTimers(); });
it('reconciles a lost save response without a second registration or PUT', async () => {
  const view = entry();
  try {
    await flushPromises();
    await view.get('#character-name').setValue('Raven'); await view.get('#character-universe').setValue('Fiction');
    await view.get('form').trigger('submit'); await flushPromises();
    expect(view.text()).toContain('Raven'); expect(view.text()).toContain('Do otevření');
    expect(view.text()).not.toContain('Response lost'); expect(view.find('#character-name').exists()).toBe(false);
    expect(api.request.mock.calls.filter(([, method]) => method === 'PUT')).toHaveLength(1);
  } finally { view.unmount(); }
});
it('does not mark an independently queued portrait as failed when research failed', async () => {
  persisted = true; character.status = 'failed'; artworkStatus = 'queued';
  const view = entry();
  try {
    await flushPromises();
    expect(view.find('[role="alert"]').exists()).toBe(false);
    image.value = 'blob:prepared-without-research';
    await flushPromises();
    expect(view.find('[role="alert"]').exists()).toBe(false);
    expect(view.findComponent({ name: 'PortraitCurtain' }).props('image')).toBe('blob:prepared-without-research');
  } finally { view.unmount(); }
});
it('A: starts the reveal at decoded image time 4s, cancelling the remaining introduction', async () => {
  persisted = true; savedCreation(); const view = entry();
  try {
    await flushPromises(); await vi.advanceTimersByTimeAsync(4000);
    expect(view.text()).not.toContain('Postava je uložená.');
    image.value = 'blob:authorised-decoded'; await flushPromises();
    expect(view.findComponent({ name: 'PortraitCurtain' }).props('image')).toBe('blob:authorised-decoded');
    expect(view.get('.launch-status').text()).toBe('');
  } finally { view.unmount(); }
});
it('B: confirms at 8 visible seconds when the image takes 30 seconds', async () => {
  persisted = true; savedCreation(); const view = entry();
  try {
    await flushPromises(); await vi.advanceTimersByTimeAsync(7999);
    expect(view.get('.launch-status').text()).toBe('Chvíli strpení…');
    await vi.advanceTimersByTimeAsync(101); expect(view.get('.launch-status').text()).toBe('Postava je uložená. Můžeš se vrátit později.');
    await vi.advanceTimersByTimeAsync(21900); image.value = 'blob:decoded'; await flushPromises();
    expect(view.get('.launch-status').text()).toBe('');
  } finally { view.unmount(); }
});
it('C: eight seconds cannot turn an uncertain write into success', async () => {
  savedCreation('uncertain'); const view = entry();
  try { await flushPromises(); await vi.advanceTimersByTimeAsync(9000); expect(view.get('.launch-status').text()).toContain('Ověřujeme'); expect(view.text()).not.toContain('Postava je uložená.'); }
  finally { view.unmount(); }
});
it('D: preparation and decode errors appear immediately without waiting for the introduction', async () => {
  persisted = true; savedCreation(); const view = entry();
  try { await flushPromises(); await vi.advanceTimersByTimeAsync(2000); failed.value = true; await flushPromises(); expect(view.get('[role="alert"]').text()).toContain('Přípravu se nepodařilo'); }
  finally { view.unmount(); }
});
it('E: pauses at 3 seconds and restores only five seconds on remount', async () => {
  persisted = true; savedCreation(); let view = entry();
  await flushPromises(); await vi.advanceTimersByTimeAsync(3000); view.unmount();
  await vi.advanceTimersByTimeAsync(100000); view = entry();
  try { await flushPromises(); await vi.advanceTimersByTimeAsync(4900); expect(view.get('.launch-status').text()).toBe('Chvíli strpení…'); await vi.advanceTimersByTimeAsync(200); expect(view.text()).toContain('Postava je uložená.'); }
  finally { view.unmount(); }
});
it('J: an account draft waits for explicit creation and does not ask for registration consent again', async () => {
  setInvitationDraft('guest', 'Raven', 'Fiction'); const view = entry();
  try { await flushPromises(); expect(view.text()).toContain('Přístup je připravený.'); expect(view.find('#character-name').exists()).toBe(false); expect(view.find('input[type="checkbox"]').exists()).toBe(false); expect(api.request.mock.calls.some(([, method]) => method === 'PUT')).toBe(false); expect(getInvitationRegistration('guest')?.status).toBe('draft'); }
  finally { view.unmount(); }
});
it('K: an already seen server-authorized opening sends returning guests straight to their destination', async () => {
  persisted = true; savedCreation(); allowed = true; sessionStorage.setItem('wand.group-opening-seen.v1.guest', 'seen'); const view = entry();
  try { await flushPromises(); expect(api.replace).toHaveBeenCalledWith('/me/character'); }
  finally { view.unmount(); }
});
it.each(['Owner', 'Viewer'])('keeps the authorized %s account on its existing operational destination', async role => {
  allowed = true;
  api.setAccount({ id: 'staff', displayName: 'Staff', roles: [role] });
  const view = entry();
  try {
    await flushPromises();
    expect(api.replace).toHaveBeenCalledWith('/settings');
    expect(view.find('.group-opening').exists()).toBe(false);
    expect(api.request.mock.calls.some(([path]) => path === '/app/v1/prelaunch/character')).toBe(false);
  } finally { view.unmount(); }
});
it('lets an existing unassigned customer explicitly request beta admission before loading character data', async () => {
  memberState = 'unassigned';
  customerMembershipState.value = 'unassigned';
  const view = entry();
  try {
    await flushPromises();
    expect(view.text()).toContain('Požádat o přístup do bety');
    expect(api.request).not.toHaveBeenCalledWith('/app/v1/enrollment/default/admit', 'POST', {});
    expect(api.request).not.toHaveBeenCalledWith('/app/v1/prelaunch/character', 'GET', undefined, expect.anything());
    await view.get('button').trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/enrollment/default/admit', 'POST', {});
    expect(api.request).toHaveBeenCalledWith('/app/v1/customer/access');
    expect(memberState).toBe('active');
  } finally { view.unmount(); customerMembershipState.value = null; }
});
it('immediately signals revoked membership returned by an idle launch-policy refresh', async () => {
  revokedAudience = true;
  customerMembershipState.value = 'active';
  const view = entry();
  try {
    await flushPromises();
    expect(customerMembershipState.value).toBe('revoked');
  } finally { view.unmount(); customerMembershipState.value = null; }
});
it('replaces a recovered draft universe with the persisted group universe after explicit admission', async () => {
  memberState = 'unassigned';
  customerMembershipState.value = 'unassigned';
  setInvitationDraft('guest', 'Raven', 'Old universe');
  const view = entry();
  try {
    await flushPromises();
    await view.get('button').trigger('click');
    await flushPromises();
    expect(customerFixedUniverse.value).toBe('Astronomy');
    expect(getInvitationRegistration('guest')?.universe).toBe('Astronomy');
    expect(view.text()).toContain('Přístup je připravený.');
  } finally { view.unmount(); customerMembershipState.value = null; customerFixedUniverse.value = null; }
});
it('old characters get no introduction and public loading ignores host and research text', async () => {
  persisted = true; const view = entry();
  try { await flushPromises(); expect(view.get('.launch-status').text()).toContain('Postava je uložená.'); expect(view.text()).toContain('Opona má trému.'); expect(view.text()).not.toContain('PRIVATE FINAL'); expect(view.text()).not.toContain('Od pořadatele'); }
  finally { view.unmount(); }
});
it('preserves an unfinished same-account draft and clears it from another account’s view', async () => {
  const view = entry();
  try {
    await flushPromises(); await view.get('#character-name').setValue('Noční archivář'); await view.get('#character-universe').setValue('Město');
    expect(getInvitationRegistration('guest')?.name).toBe('Noční archivář');
    api.setAccount({ id: 'other', displayName: 'Other', roles: ['Customer'], openAiConsent: true }); await flushPromises();
    expect(view.get<HTMLInputElement>('#character-name').element.value).toBe(''); expect(view.text()).not.toContain('Noční archivář');
  } finally { view.unmount(); }
});

it('a matching existing profile without a local PUT never starts or stalls in an introduction', async () => {
  persisted = true; setInvitationDraft('guest', 'Raven', 'Fiction'); const view = entry();
  try { await flushPromises(); expect(view.get('.launch-status').text()).toContain('Postava je uložená.'); expect(getInvitationRegistration('guest')?.writeStarted).toBe(false); }
  finally { view.unmount(); }
});
it('announces offline immediately during the presentation and reconnects with reads only', async () => {
  persisted = true; savedCreation(); const view = entry();
  try {
    await flushPromises(); await vi.advanceTimersByTimeAsync(2000); window.dispatchEvent(new Event('offline')); await flushPromises();
    expect(view.get('[role="alert"]').text()).toContain('Zkontroluj připojení');
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(view.find('[role="alert"]').exists()).toBe(false);
    expect(api.request.mock.calls.some(([,method])=>method==='PUT')).toBe(false);
  } finally { view.unmount(); }
});

it('K: entering starts the character presentation and only continuing completes entry', async () => {
  const deadline=Date.now()+4000;
  persisted=true; savedCreation();
  const original=api.request.getMockImplementation()!;
  api.request.mockImplementation(async(path:string,method?:string)=>path==='/app/v1/launch'
    ? {allowed,launchAt:deadline,serverNow:Date.now()} : original(path,method));
  const view=entry();
  try {
    await flushPromises(); await vi.advanceTimersByTimeAsync(3000); expect(api.replace).not.toHaveBeenCalled();
    allowed=true; await vi.advanceTimersByTimeAsync(1000); await flushPromises();
    expect(api.replace).not.toHaveBeenCalled();
    expect(view.text()).toContain('Večer začíná.');
    await view.get('.group-opening-copy button').trigger('click'); await flushPromises();
    expect(view.find('[data-entrance]').exists()).toBe(true);
    expect(api.replace).not.toHaveBeenCalled();
    expect(sessionStorage.getItem('wand.group-opening-seen.v1.guest')).toBeNull();
    expect(view.text()).not.toContain('Přeskočit animaci');
    expect(view.find('.entrance-continue').exists()).toBe(false);
    await vi.advanceTimersByTimeAsync(10000); await flushPromises();
    await view.get('.entrance-continue').trigger('click'); await flushPromises();
    expect(api.replace).toHaveBeenCalledWith('/me/character');
    expect(sessionStorage.getItem('wand.group-opening-seen.v1.guest')).toBe('seen');
  } finally {view.unmount();}
});
it('polls for a not-yet-available character during the entrance without generating anything again', async () => {
  allowed = true;
  const view = entry();
  try {
    await flushPromises();
    await view.get('.group-opening-copy button').trigger('click'); await flushPromises();
    expect(view.text()).toContain('Slavná osoba se ještě hledá');
    persisted = true;
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    expect(view.findComponent({ name: 'CharacterEntrance' }).props('character')?.name).toBe('Raven');
    expect(api.request.mock.calls.filter(([path]) => path === '/app/v1/prelaunch/character').length).toBeGreaterThanOrEqual(2);
    expect(api.request.mock.calls.some(([, method]) => method && method !== 'GET')).toBe(false);
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    await view.get('.entrance-continue').trigger('click'); await flushPromises();
    expect(api.replace).toHaveBeenCalledWith('/me/character');
  } finally { view.unmount(); }
});
it('does not mark a cancelled entry as seen and removes the presentation immediately on account change', async () => {
  allowed = true; persisted = true;
  const view = entry();
  try {
    await flushPromises();
    await view.get('.group-opening-copy button').trigger('click'); await flushPromises();
    api.replace.mockResolvedValueOnce({ type: 4 });
    await vi.advanceTimersByTimeAsync(10000); await flushPromises();
    await view.get('.entrance-continue').trigger('click'); await flushPromises();
    expect(sessionStorage.getItem('wand.group-opening-seen.v1.guest')).toBeNull();
    api.setAccount(null); await flushPromises();
    expect(view.find('[data-entrance]').exists()).toBe(false);
    expect(view.text()).not.toContain('Raven');
  } finally { view.unmount(); }
});
it('rechecks server authorization when entering and closes the scene if access was revoked', async () => {
  allowed = true;
  const view = entry();
  try {
    await flushPromises();
    expect(view.text()).toContain('Večer začíná.');
    allowed = false;
    await view.get('.group-opening-copy button').trigger('click');
    await flushPromises();
    expect(api.replace).not.toHaveBeenCalled();
    expect(view.text()).not.toContain('Večer začíná.');
  } finally { view.unmount(); }
});
it('loads the waiting scene when access is paused after the initial group opening', async () => {
  allowed = true;
  persisted = true;
  const view = entry();
  try {
    await flushPromises();
    expect(view.text()).toContain('Večer začíná.');
    expect(api.request.mock.calls.filter(([path]) => path === '/app/v1/prelaunch/character')).toHaveLength(0);
    allowed = false;
    await vi.advanceTimersByTimeAsync(15000);
    await flushPromises();
    expect(view.text()).not.toContain('Večer začíná.');
    expect(view.text()).toContain('Raven');
    expect(view.find('.countdown').exists()).toBe(true);
    expect(api.request.mock.calls.some(([, method]) => method === 'PUT')).toBe(false);
  } finally { view.unmount(); }
});
it('a zero local countdown cannot grant entry when the server still denies it', async () => {
  const deadline=Date.now()+1000;
  api.request.mockImplementation(async(path:string)=>path==='/app/v1/launch'
    ? {allowed:false,launchAt:deadline,serverNow:Date.now()} : {character:null,artwork:null});
  const view=entry();
  try {await flushPromises();await vi.advanceTimersByTimeAsync(2000);expect(api.replace).not.toHaveBeenCalled();}
  finally {view.unmount();}
});
it('shows Soon before the owner confirms a countdown', async () => {
  persisted = true;
  const original = api.request.getMockImplementation()!;
  api.request.mockImplementation(async (path: string, method?: string) => path === '/app/v1/launch'
    ? { allowed: false, audience: 'beta', launchAt: null, serverNow: Date.now() } : original(path, method));
  const view = entry();
  try {
    await flushPromises();
    expect(view.get('.character-countdown-wait').text()).toBe('Brzy');
    expect(view.find('.character-countdown-grid').exists()).toBe(false);
  } finally { view.unmount(); }
});
it('shows Soon after a non-force countdown expires until the server opens the night', async () => {
  persisted = true;
  const original = api.request.getMockImplementation()!;
  api.request.mockImplementation(async (path: string, method?: string) => path === '/app/v1/launch'
    ? { allowed: false, audience: 'beta', launchAt: Date.now() - 1000, serverNow: Date.now() } : original(path, method));
  const view = entry();
  try {
    await flushPromises();
    expect(view.get('.character-countdown-wait').text()).toBe('Brzy');
    expect(view.find('.character-countdown-grid').exists()).toBe(false);
    expect(api.replace).not.toHaveBeenCalled();
  } finally { view.unmount(); }
});
