import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import SettingsView from './SettingsView.vue';
import { nextTick } from 'vue';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { account, offlineSession } from './session';
import { setOwnerPlayer } from '../../application/ownerMode';
const api = vi.hoisted(() => ({
  request: vi.fn(),
  signOut: vi.fn(),
  setAccount: (_value: {id: string; displayName: string; roles: string[]} | null) => {},
  setDeletionRequested: (_value: boolean) => {},
}));
vi.mock('./session', async () => {
  const {ref} = await import('vue');
  const account = ref<{id: string; displayName: string; roles: string[]} | null>({id: 'owner', displayName: 'Guest', roles: ['Owner']});
  const accountDeletionRequested = ref(false);
  api.setAccount = value => { account.value = value; };
  api.setDeletionRequested = value => { accountDeletionRequested.value = value; };
  return {account, accountDeletionRequested, offlineSession: ref(false), accountRequest: api.request, signOut: api.signOut, refreshIdentity: vi.fn(), authenticatePasskey: vi.fn(), deleteAccount: vi.fn(), getAccountDeletionStatus: vi.fn().mockResolvedValue(null), hasRole: () => true};
});
vi.mock('../../application/preferences', () => ({usePreferences: () => ({theme: 'lord-voldemort', themes: [{id: 'lord-voldemort', label: 'Stínový čaroděj'}, {id: 'olivander', label: 'Mistr hůlek'}]})}));
beforeEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
  setOwnerPlayer(null, false);
  offlineSession.value = false;
  let name: string | null = null;
  api.setAccount({id: 'owner', displayName: 'Guest', roles: ['Owner']});
  api.setDeletionRequested(false);
  api.signOut.mockReset(); api.request.mockReset(); api.request.mockImplementation(async (_path, method, body) => {
    if (method === 'PUT') { name = body.name; return; }
    return {sessions: [{id: 'current', current: true, installationName: 'This installation', expiresAt: 1900000000, trusted: false}], grants: [], associations: [], passkeyCount: 1, passkeys: [{id: 'owned_key', name, createdAt: '2026-10-19T10:00:00Z', isBackedUp: true}]};
  });
});
describe('credential naming settings', () => {
  it.each([
    { roles: ['Customer'], tools: [] },
    { roles: ['Viewer'], tools: ['/persona-lab'] },
    { roles: ['Operator'], tools: ['/persona-lab', '/admin/customer-sessions'] },
    { roles: ['Administrator'], tools: ['/persona-lab', '/admin/customer-sessions'] },
    { roles: ['Owner'], tools: ['/persona-lab', '/admin/customer-sessions'] },
    { roles: ['Tester'], tools: ['/testing-sessions', '/persona-lab'] },
    { roles: ['Beta', 'Tester'], tools: ['/testing-sessions', '/persona-lab'] },
  ])('shows only permitted tool links for $roles', async identity => {
    api.setAccount({ id: 'test', displayName: 'Test', roles: identity.roles });
    const wrapper = mount(SettingsView, { props: { customerSurface: true }, global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } });
    await flushPromises();
    const tools = () => wrapper.findAll('nav[aria-label="Nástroje účtu"] a').map(link => link.attributes('to'));
    expect(tools()).toEqual(identity.tools);
    offlineSession.value = true;
    await nextTick();
    expect(tools()).toEqual([]);
    offlineSession.value = false;
    api.setDeletionRequested(true);
    await nextTick();
    expect(tools()).toEqual([]);
    wrapper.unmount();
  });
  it('localizes shared account controls and keeps customer settings free of staff resources', async () => {
    api.setAccount({id: 'customer', displayName: 'Guest', roles: ['Customer']});
    const wrapper = mount(SettingsView, { props: { customerSurface: true }, global: { stubs: { AccountArchiveList: true, EveningPanel: true, RouterLink: { template: '<a><slot /></a>' } } } });
    await flushPromises();
    interfaceLanguagePreference.setLanguage('en');
    await nextTick();
    expect(wrapper.text()).toContain('Přístupové klíče');
    expect(wrapper.text()).toContain('Přihlášená zařízení');
    expect(wrapper.text()).toContain('Poslední klíč nelze odebrat. Odebrání klíče odhlásí ostatní relace účtu.');
    expect(wrapper.text()).not.toContain('Assigned resources');
    expect(wrapper.text()).not.toContain('Manage users and permissions');
    expect(wrapper.findAll('h1')).toHaveLength(0);
    wrapper.unmount();
  });

  it('uses the fenced logout flow for the current installation', async () => {
    const wrapper = mount(SettingsView, {global: {stubs: {AccountArchiveList: true, EveningPanel: true, RouterLink: true}}});
    await flushPromises(); await wrapper.findAll('button').find(button => button.text() === 'Odhlásit toto zařízení')!.trigger('click'); await flushPromises();
    expect(api.signOut).toHaveBeenCalledOnce();
    expect(api.request.mock.calls.some(call => call[0] === '/app/v1/sessions/current')).toBe(false);
    wrapper.unmount();
  });
  it('submits an owned credential name and displays persisted metadata', async () => {
    const wrapper = mount(SettingsView, {global: {stubs: {AccountArchiveList: true, EveningPanel: true, RouterLink: true}}});
    await flushPromises(); await wrapper.get('#passkey-name-0').setValue('Pracovní notebook');
    await wrapper.get('.key-name-form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/passkeys/owned_key', 'PUT', {name: 'Pracovní notebook'});
    expect(wrapper.text()).toContain('Pracovní notebook'); expect(wrapper.text()).toContain('Název přístupového klíče byl uložen.');
    wrapper.unmount();
  });

  it('shows only account security controls on the customer account surface', async () => {
    const wrapper = mount(SettingsView, { props: { customerSurface: true }, global: { stubs: { AccountArchiveList: true, EveningPanel: true, RouterLink: { template: '<a><slot /></a>' } } } });
    await flushPromises();
    expect(wrapper.text()).toContain('Přístupové klíče');
    expect(wrapper.text()).toContain('Uložit název klíče');
    expect(wrapper.text()).toContain('Přihlášená zařízení');
    expect(wrapper.text()).toContain('Odhlásit toto zařízení');
    expect(wrapper.text()).toContain('Smazání účtu');
    expect(wrapper.text()).not.toContain('Owner');
    expect(wrapper.text()).not.toContain('Přidělené prostředky');
    expect(wrapper.text()).not.toContain('Kufry propojené s účtem');
    expect(wrapper.text()).not.toContain('HTTP API v1');
    expect(wrapper.findAll('h1')).toHaveLength(0);
    wrapper.unmount();
  });

  it('offers Owner a separate administration entry from the customer account', async () => {
    setOwnerPlayer(account.value, true);
    const wrapper = mount(SettingsView, { props: { customerSurface: true }, global: { stubs: { AccountArchiveList: true, EveningPanel: true, RouterLink: { template: '<a><slot /></a>' } } } });
    await flushPromises();
    expect(wrapper.text()).toContain('Administrace večera');
    expect(wrapper.find('a[to="/settings/admin/launch"]').text()).toBe('Večer a pozvánky');
    expect(wrapper.find('a[to="/settings/admin"]').text()).toBe('Účty a oprávnění');
    wrapper.unmount();
    setOwnerPlayer(null, false);
  });

  it('shows collapsed My Data only for an eligible customer and defers its data requests', async () => {
    api.setAccount({id: 'customer-1', displayName: 'Guest', roles: ['Customer']});
    const wrapper = mount(SettingsView, { props: { customerSurface: true }, global: { stubs: { AccountArchiveList: true, EveningPanel: true, RouterLink: { template: '<a><slot /></a>' } } } });
    await flushPromises();

    expect(wrapper.text()).toContain('Moje data');
    expect(wrapper.text()).not.toContain('Moje paměti');
    expect(wrapper.text()).not.toContain('Stažení údajů účtu');
    expect(api.request.mock.calls.map(call => call[0])).toEqual(['/app/v1/account']);
    expect(wrapper.find('button[aria-controls]').attributes('aria-expanded')).toBe('false');
    wrapper.unmount();
  });

  it.each([
    ['staff', {id: 'owner', displayName: 'Staff', roles: ['Owner']}],
    ['mixed', {id: 'mixed', displayName: 'Mixed', roles: ['Customer', 'Operator']}],
  ])('does not show customer data for %s roles', async (_label, identity) => {
    api.setAccount(identity);
    const wrapper = mount(SettingsView, { props: { customerSurface: true }, global: { stubs: { AccountArchiveList: true, EveningPanel: true, RouterLink: { template: '<a><slot /></a>' } } } });
    await flushPromises();
    expect(wrapper.text()).not.toContain('Moje data');
    wrapper.unmount();
  });

  it('hides My Data when account deletion is requested', async () => {
    api.setAccount({id: 'customer-1', displayName: 'Guest', roles: ['Customer']});
    api.setDeletionRequested(true);
    const wrapper = mount(SettingsView, { props: { customerSurface: true }, global: { stubs: { AccountArchiveList: true, EveningPanel: true, RouterLink: { template: '<a><slot /></a>' } } } });
    await flushPromises();
    expect(wrapper.text()).not.toContain('Moje data');
    wrapper.unmount();
  });
});
