import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import { nextTick } from 'vue';
import SignInView from './SignInView.vue';
import { account } from './session';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { launchAllowed } from '../../application/launchAccess';
import { clearInvitationRegistration, getInvitationRegistration } from './invitationRegistration';

const api = vi.hoisted(() => ({
  authenticate: vi.fn(),
  refreshIdentity: vi.fn(),
  signOut: vi.fn(),
  request: vi.fn(),
  enrollment: null as { kind: 'invite' | 'recovery'; token: string } | null,
}));

vi.mock('./session', async () => {
  const { ref } = await import('vue');
  return {
    account: ref(null),
    accountsEnabled: ref(true),
    accountRequest: api.request,
    accountDeletionRequested: ref(false),
    initialized: ref(true),
    loading: ref(false),
    signingOut: ref(false),
    refreshIdentity: api.refreshIdentity,
    authenticatePasskey: api.authenticate,
    signOut: api.signOut,
  };
});
vi.mock('../../application/guestClaim', () => ({
  consumeEnrollment: () => api.enrollment,
}));

function createSignInRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' } },
      { path: '/signin', component: { template: '<div />' } },
      { path: '/privacy', component: { template: '<div />' } },
      { path: '/launch', component: { template: '<div />' } },
      { path: '/settings', component: { template: '<div />' } },
      { path: '/support', component: { template: '<div />' } },
      { path: '/suitcase', component: { template: '<div />' } },
      { path: '/me/character', component: { template: '<div />' } },
      { path: '/keepsake/:pathMatch(.*)*', component: { template: '<div />' } },
    ],
  });
}

async function mountSignIn(returnPath?: string, startInInvite = false) {
  const router = createSignInRouter();
  await router.push({ path: '/signin', query: returnPath ? { return: returnPath } : {} });
  const wrapper = mount(SignInView, { attachTo: document.body, global: { plugins: [router] } });
  await flushPromises();
  if (!api.enrollment && !startInInvite) {
    await wrapper.findAll('button').find(button => button.text() === 'Už mám přístupový klíč')?.trigger('click');
    await flushPromises();
  }
  return { router, wrapper };
}

beforeEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
  account.value = null;
  launchAllowed.value = true;
  api.authenticate.mockReset();
  api.refreshIdentity.mockReset();
  api.signOut.mockReset();
  api.request.mockReset().mockResolvedValue({ available: true, fixedUniverse: null });
  api.enrollment = null;
  clearInvitationRegistration('new-invitee');
});

describe('single-group beta enrollment', () => {
  it('offers in-app enrollment without an invitation code and keeps the name for the account', async () => {
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'new-beta', displayName: 'Hermione Granger', roles: ['Customer'] };
    });
    const { router, wrapper } = await mountSignIn(undefined, true);

    expect(api.request).toHaveBeenCalledWith('/app/v1/enrollment/default');
    expect(wrapper.find('#account-invite').exists()).toBe(false);
    await wrapper.get('#account-name').setValue('Hermione Granger');
    await wrapper.get('#account-universe').setValue('Kouzelnický svět');
    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(api.authenticate).toHaveBeenCalledWith('customer-enroll', '', 'Hermione Granger');
    expect(router.currentRoute.value.path).toBe('/launch');
    wrapper.unmount();
  });

  it('fails closed when the server says beta enrollment is closed', async () => {
    api.request.mockResolvedValue({ available: false });
    const { wrapper } = await mountSignIn(undefined, true);
    expect(wrapper.text()).toContain('Přijímání nových účastníků je nyní uzavřené.');
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined();
    expect(wrapper.find('#account-invite').exists()).toBe(false);
    wrapper.unmount();
  });

  it('uses the server fixed universe as read only', async () => {
    api.request.mockResolvedValue({ available: true, fixedUniverse: 'Za zrcadlem' });
    const { wrapper } = await mountSignIn(undefined, true);
    expect((wrapper.get('#account-universe').element as HTMLInputElement).value).toBe('Za zrcadlem');
    expect(wrapper.get('#account-universe').attributes('readonly')).toBeDefined();
    wrapper.unmount();
  });
});

it('reconciles a dropped enrollment response and saves the draft to the committed account', async () => {
  api.authenticate.mockRejectedValueOnce(Object.assign(new Error('response lost after commit'), { code: 'customer_enrollment_completion_uncertain' }));
  api.refreshIdentity.mockResolvedValue(undefined);
  const { router, wrapper } = await mountSignIn(undefined, true);
  try {
    api.refreshIdentity.mockImplementation(async () => {
      account.value = { id: 'beta-committed', displayName: 'Hermione Granger', roles: ['Customer'] };
    });
    await wrapper.get('#account-name').setValue('Hermione Granger');
    await wrapper.get('#account-universe').setValue('Kouzelnický svět');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(api.refreshIdentity).toHaveBeenCalledTimes(2);
    expect(getInvitationRegistration('beta-committed')).toMatchObject({ name: 'Hermione Granger', universe: 'Kouzelnický svět' });
    expect(router.currentRoute.value.path).toBe('/launch');
    expect(api.authenticate).toHaveBeenCalledTimes(1);
  } finally {
    wrapper.unmount();
    account.value = null;
    clearInvitationRegistration('beta-committed');
  }
});

it('requires passkey sign-in after an unconfirmed enrollment without repeating account creation', async () => {
  api.authenticate.mockRejectedValueOnce(Object.assign(new Error('response lost'), { code: 'customer_enrollment_completion_uncertain' }));
  api.refreshIdentity.mockResolvedValue(undefined);
  const { router, wrapper } = await mountSignIn(undefined, true);
  try {
    await wrapper.get('#account-name').setValue('Hermione Granger');
    await wrapper.get('#account-universe').setValue('Kouzelnický svět');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(wrapper.text()).toContain('Pokračuj přihlášením svým přístupovým klíčem');
    expect(wrapper.find('#account-name').exists()).toBe(false);
    api.authenticate.mockImplementationOnce(async () => {
      account.value = { id: 'beta-signed-in', displayName: 'Hermione Granger', roles: ['Customer'] };
    });
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(api.authenticate).toHaveBeenLastCalledWith('signin', '', '');
    expect(api.authenticate).toHaveBeenCalledTimes(2);
    expect(getInvitationRegistration('beta-signed-in')).toMatchObject({ name: 'Hermione Granger', universe: 'Kouzelnický svět' });
    expect(router.currentRoute.value.path).toBe('/launch');
  } finally {
    wrapper.unmount();
    account.value = null;
    clearInvitationRegistration('beta-signed-in');
  }
});

it('keeps the beta form in place when passkey creation is cancelled', async () => {
  api.authenticate.mockRejectedValueOnce(new DOMException('cancelled', 'NotAllowedError'));
  const { wrapper } = await mountSignIn(undefined, true);
  try {
    await wrapper.get('#account-name').setValue('Hermione Granger');
    await wrapper.get('#account-universe').setValue('Kouzelnický svět');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(wrapper.find('#account-name').exists()).toBe(true);
    expect(wrapper.find('#account-universe').exists()).toBe(true);
    expect(wrapper.text()).not.toContain('Pokračuj přihlášením svým přístupovým klíčem');
    expect(api.authenticate).toHaveBeenCalledTimes(1);
  } finally { wrapper.unmount(); }
});

it('keeps the beta form in place when the passkey options request fails', async () => {
  api.authenticate.mockRejectedValueOnce(new TypeError('network unavailable before credential creation'));
  const { wrapper } = await mountSignIn(undefined, true);
  try {
    await wrapper.get('#account-name').setValue('Hermione Granger');
    await wrapper.get('#account-universe').setValue('Kouzelnický svět');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(wrapper.find('#account-name').exists()).toBe(true);
    expect(wrapper.find('#account-universe').exists()).toBe(true);
    expect(wrapper.text()).not.toContain('Pokračuj přihlášením svým přístupovým klíčem');
    expect(api.refreshIdentity).toHaveBeenCalledTimes(1);
  } finally { wrapper.unmount(); }
});

it('keeps the beta form and refreshes its state after a known closed response', async () => {
  api.authenticate.mockRejectedValueOnce(Object.assign(new Error('closed'), { code: 'beta_enrollment_closed' }));
  api.request.mockResolvedValue({ available: true, fixedUniverse: null });
  const { wrapper } = await mountSignIn(undefined, true);
  try {
    await wrapper.get('#account-name').setValue('Hermione Granger');
    await wrapper.get('#account-universe').setValue('Kouzelnický svět');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(wrapper.find('#account-name').exists()).toBe(true);
    expect(wrapper.find('#account-universe').exists()).toBe(true);
    expect(wrapper.text()).toContain('Přijímání nových účastníků je nyní uzavřené.');
    expect(wrapper.text()).not.toContain('Pokračuj přihlášením svým přístupovým klíčem');
    expect(api.refreshIdentity).toHaveBeenCalledTimes(1);
  } finally { wrapper.unmount(); }
});

describe('sign-in destinations', () => {
  it('hides customer feature descriptions and public signup while launch access is unknown', async () => {
    launchAllowed.value = null;
    const { wrapper } = await mountSignIn();
    expect(wrapper.text()).not.toMatch(/Vyzkoušet příběh|Zákaznický účet vám uloží|Vytvořit zákaznický účet/i);
    expect(wrapper.text()).not.toContain('Vytvořit zákaznický účet');
    wrapper.unmount();
  });
  it.each([
    ['invitation_expired_or_used', 'Pozvánka už není platná.', 'This invitation has expired or was already used.'],
    ['ceremony_expired_or_used', 'Ověření vypršelo.', 'Verification expired.'],
    ['csrf_required', 'Platnost stránky vypršela.', 'This page has expired.'],
    ['account_origin_required', 'Tato adresa není povolená', 'This address is not allowed'],
    ['passkey_not_verified', 'Přístupový klíč se nepodařilo ověřit.', 'The passkey could not be verified.'],
    ['recent_passkey_required', 'Nejprve znovu ověřte', 'Verify your identity with a passkey again.'],
    ['same_account_passkey_required', 'Vyberte přístupový klíč', 'Choose a passkey for the currently signed-in account.'],
    ['accounts_not_configured', 'Přihlášení je dostupné až po konfiguraci', 'Sign-in is available after an administrator configures'],
  ])('preserves safe localized guidance for %s without displaying server details', async (code, czech, english) => {
    api.authenticate.mockRejectedValueOnce(Object.assign(new Error('private-server-detail-token'), { code }));
    const { wrapper } = await mountSignIn();
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(wrapper.get('[role="alert"]').text()).toContain(czech);
    interfaceLanguagePreference.setLanguage('en');
    await nextTick();
    expect(wrapper.get('[role="alert"]').text()).toContain(english);
    expect(wrapper.text()).not.toContain('private-server-detail-token');
    wrapper.unmount();
  });

  it('localizes sign-in and shows only a safe generic error for unrecognized failures', async () => {
    api.authenticate.mockRejectedValueOnce(new Error('private-server-detail-token'));
    const { wrapper } = await mountSignIn();
    interfaceLanguagePreference.setLanguage('en');
    await nextTick();
    expect(wrapper.get('h1').text()).toBe('Sign in');
    expect(wrapper.get('button[type="submit"]').text()).toBe('Sign in with passkey');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(wrapper.text()).toContain('Sign-in or the account change could not be completed.');
    expect(wrapper.text()).not.toContain('private-server-detail-token');
    expect(wrapper.get('details summary').text()).toBe('Lost access?');
    wrapper.unmount();
  });

  it('shows expandable lost-access guidance only on unauthenticated sign-in without making requests', async () => {
    const { router, wrapper } = await mountSignIn();
    const disclosure = wrapper.get('details');
    expect(disclosure.get('summary').text()).toBe('Ztratili jste přístup?');
    expect(disclosure.attributes('open')).toBeUndefined();
    await disclosure.get('summary').trigger('click');
    expect(disclosure.attributes('open')).toBeDefined();
    expect(wrapper.get('a[href="/support"]').text()).toBe('kontaktujte podporu');
    expect(api.authenticate).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('Zobrazované jméno ani odkaz na památku vlastnictví účtu neprokazují.');
    expect(router.currentRoute.value.path).toBe('/signin');
    wrapper.unmount();
  });

  it('opens the invitation form by default and switches to recovery without carrying its code', async () => {
    const { wrapper } = await mountSignIn(undefined, true);
    expect(wrapper.find('#account-invite').exists()).toBe(false);
    await wrapper.findAll('button').find(button => button.text() === 'Mám pozvánku nebo kód obnovy')!.trigger('click');
    expect(wrapper.find('#account-name').exists()).toBe(true);
    expect(wrapper.find('#account-universe').exists()).toBe(true);
    expect(wrapper.find('#account-invite').exists()).toBe(true);
    expect(wrapper.find('select#enrollment-kind').exists()).toBe(false);
    expect(wrapper.find('h1').text()).toBe('Vstup na vlastní pověst.');
    await wrapper.get('#account-invite').setValue('invitation-secret');
    await wrapper.get('input[type="checkbox"]').setValue(true);
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit přístup')!.trigger('click');
    expect(wrapper.find('#account-name').exists()).toBe(false);
    expect(wrapper.find('#account-universe').exists()).toBe(false);
    expect((wrapper.get('#account-invite').element as HTMLInputElement).value).toBe('');
    expect(wrapper.text()).toContain('nahradí všechny původní klíče');
    expect(wrapper.find('a[href="/support"]').exists()).toBe(true);
    expect(wrapper.find('a[href="/privacy"]').exists()).toBe(true);
    await wrapper.findAll('button').find(button => button.text() === 'Zpět k pozvánce')!.trigger('click');
    expect((wrapper.get('input[type="checkbox"]').element as HTMLInputElement).checked).toBe(false);
    wrapper.unmount();
  });

  it('keeps recovery enrollment behavior and defaults a staff account to settings', async () => {
    api.enrollment = { kind: 'recovery', token: 'private-recovery-code' };
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'staff', displayName: 'Obsluha', roles: ['Operator'] };
    });
    const { router, wrapper } = await mountSignIn();

    expect(wrapper.get('h1').text()).toBe('Obnovit přístup');
    expect(wrapper.find('#account-name').exists()).toBe(false);
    expect(wrapper.text()).toContain('nahradí všechny původní klíče');
    expect(wrapper.text()).toContain('oprávnění zůstanou zachovány');
    expect(wrapper.find('.consent-fieldset details').exists()).toBe(false);
    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(api.authenticate).toHaveBeenCalledWith('enroll', 'private-recovery-code', '');
    expect(router.currentRoute.value.path).toBe('/settings');
    wrapper.unmount();
  });

  it('collects one shared name and universe, keeps AI consent optional, and preserves recovery separation', async () => {
    api.enrollment = { kind: 'invite', token: 'private-invitation-code' };
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'new-invitee', displayName: 'Luna Lovegood', roles: ['Customer'] };
    });
    const { router, wrapper } = await mountSignIn(undefined, true);

    expect(wrapper.get('h1').text()).toBe('Vstup na vlastní pověst.');
    expect(wrapper.find('details').exists()).toBe(true);
    expect(wrapper.get('details summary').text()).toBe('Soukromí');
    expect(wrapper.get('#account-invite').attributes('type')).toBe('password');
    expect(wrapper.text()).toContain('Kód získáte soukromě od vlastníka.');
    expect(wrapper.get('#account-name').attributes('maxlength')).toBe('80');
    expect(wrapper.get('#account-universe').attributes('maxlength')).toBe('160');
    expect((wrapper.get('input[type="checkbox"]').element as HTMLInputElement).required).toBe(false);
    await wrapper.get('#account-name').setValue('  Luna Lovegood  ');
    await wrapper.get('#account-universe').setValue('  Kouzelnický svět  ');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(api.authenticate).toHaveBeenCalledWith('enroll', 'private-invitation-code', 'Luna Lovegood');
    expect(getInvitationRegistration('new-invitee')).toMatchObject({ name: 'Luna Lovegood', universe: 'Kouzelnický svět', status: 'draft' });
    expect(router.currentRoute.value.path).toBe('/launch');
    wrapper.unmount();
  });

  it('retains entered invitation data when the passkey dialog is canceled', async () => {
    api.enrollment = { kind: 'invite', token: 'private-invitation-code' };
    api.authenticate.mockRejectedValueOnce(new DOMException('Canceled', 'NotAllowedError'));
    const { router, wrapper } = await mountSignIn();
    await wrapper.get('#account-name').setValue('Luna');
    await wrapper.get('#account-universe').setValue('Kouzelnický svět');
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect((wrapper.get('#account-name').element as HTMLInputElement).value).toBe('Luna');
    expect((wrapper.get('#account-universe').element as HTMLInputElement).value).toBe('Kouzelnický svět');
    expect((wrapper.get('#account-invite').element as HTMLInputElement).value).toBe('private-invitation-code');
    expect(wrapper.get('[role="alert"]').text()).toContain('Ověření bylo zrušeno nebo vypršelo.');
    expect(document.activeElement).toBe(wrapper.get('#account-name').element);
    expect(router.currentRoute.value.path).toBe('/signin');
    wrapper.unmount();
  });

  it('lets an authenticated visitor switch accounts while preserving the invitation token', async () => {
    api.enrollment = { kind: 'invite', token: 'private-invitation-code' };
    account.value = { id: 'existing', displayName: 'Existing', roles: ['Customer'] };
    api.signOut.mockImplementation(async () => { account.value = null; });
    const { wrapper } = await mountSignIn();
    await wrapper.findAll('button').find(button => button.text().includes('pozvánkou'))!.trigger('click'); await flushPromises();
    expect(api.signOut).toHaveBeenCalledOnce();
    expect(wrapper.find('#account-invite').exists()).toBe(true);
    expect((wrapper.get('#account-invite').element as HTMLInputElement).value).toBe('private-invitation-code');
    wrapper.unmount();
  });

  it('defaults an authenticated customer to the character page', async () => {
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'customer', displayName: 'Zákazník', roles: ['Customer'] };
    });
    const { router, wrapper } = await mountSignIn();

    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(api.authenticate).toHaveBeenCalledWith('signin', '', '');
    expect(router.currentRoute.value.path).toBe('/me/character');
    wrapper.unmount();
  });

  it('preserves the authorized account keepsake return after customer sign-in', async () => {
    const keepsake = '/keepsake/?account=1&k=e513a7ce-1f2a-4320-9ca2-463fb179b0f4';
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'customer', displayName: 'Zákazník', roles: ['Customer'] };
    });
    const { router, wrapper } = await mountSignIn(keepsake);

    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(router.currentRoute.value.fullPath).toBe(keepsake);
    wrapper.unmount();
  });

  it('falls back to the customer default for an invalid return and keeps mixed roles on staff routing', async () => {
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'customer', displayName: 'Zákazník', roles: ['Customer'] };
    });
    const invalidReturn = await mountSignIn('//outside.example');
    await invalidReturn.wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(invalidReturn.router.currentRoute.value.path).toBe('/me/character');
    invalidReturn.wrapper.unmount();

    account.value = null;
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'mixed', displayName: 'Obsluha', roles: ['Customer', 'Operator'] };
    });
    const mixedRole = await mountSignIn();
    await mixedRole.wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(mixedRole.router.currentRoute.value.path).toBe('/settings');
    mixedRole.wrapper.unmount();
  });

  it('keeps customer account creation on the character page', async () => {
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'new-customer', displayName: 'Nový zákazník', roles: ['Customer'] };
    });
    const keepsake = '/keepsake/?account=1&k=e513a7ce-1f2a-4320-9ca2-463fb179b0f4';
    const { router, wrapper } = await mountSignIn(keepsake);
    expect(wrapper.find('details').exists()).toBe(true);
    await wrapper.findAll('button').find(button => button.text() === 'Vytvořit zákaznický účet')!.trigger('click');
    await flushPromises();
    expect(wrapper.find('.consent-fieldset details').exists()).toBe(false);
    await wrapper.get('#customer-account-name').setValue('Nový zákazník');
    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(api.authenticate).toHaveBeenCalledWith('customer-enroll', '', 'Nový zákazník');
    expect(router.currentRoute.value.path).toBe('/me/character');
    wrapper.unmount();
  });
});

it('reauthenticates an existing account from the default invitation screen without hidden fields', async () => {
  account.value = { id: 'existing', displayName: 'Existing', roles: ['Customer'] };
  const { wrapper } = await mountSignIn(undefined, true);
  try {
    await wrapper.get('button').trigger('click');
    await flushPromises();
    expect(api.authenticate).toHaveBeenCalledWith('reauth', '', '');
  } finally { wrapper.unmount(); }
});
