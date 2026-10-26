<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { consumeEnrollment } from '../../application/guestClaim';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import AwField from '../../components/AwField.vue';
import LostAccessHelp from './LostAccessHelp.vue';
import brandMark from '../../assets/brand/emblem-small.webp';
import { defaultSignedInDestination } from '../customer/customerJourney';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { launchAllowed } from '../../application/launchAccess';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { setInvitationDraft, saveInvitationCharacter } from './invitationRegistration';
import { account, accountsEnabled, initialized, loading, signingOut, refreshIdentity, authenticatePasskey, signOut } from './session';
const router = useRouter();
const route = useRoute();
const enrollment = ref(!route.query.return && !signingOut.value);
const customerEnrollment = ref(false);
const enrollmentKind = ref<'invite' | 'recovery'>('invite');
const token = ref('');
const customerDisplayName = ref('');
const invitationName = ref('');
const invitationNameInput = ref<HTMLInputElement | null>(null);
const invitationCodeInput = ref<HTMLInputElement | null>(null);
const invitationUniverse = ref('');
const aiConsent = ref(false);
const busy = ref(false);
const error = ref<CustomerCopyKey | ''>('');
const language = computed({
  get: () => interfaceLanguagePreference.language.value,
  set: (value: string) => { interfaceLanguagePreference.setLanguage(value); },
});
watch(() => account.value?.id, id => interfaceLanguagePreference.setScope(id), { immediate: true });
function errorKey(value: unknown): CustomerCopyKey {
  if (value instanceof DOMException && value.name === 'NotAllowedError') return 'signin.error-cancelled';
  if (value instanceof Error && value.message === 'Přístupové klíče vyžadují HTTPS a podporovaný prohlížeč.') return 'signin.error-secure-context';
  if (value instanceof Error && value.message === 'Ověření přístupovým klíčem už probíhá.') return 'signin.error-pending';
  if (value instanceof Error && 'code' in value) {
    switch (value.code) {
      case 'accounts_not_configured': return 'signin.accounts-disabled';
      case 'invitation_expired_or_used': return 'signin.error-invitation';
      case 'ceremony_expired_or_used': return 'signin.error-ceremony';
      case 'csrf_required': return 'signin.error-page-expired';
      case 'account_origin_required': return 'signin.error-origin';
      case 'passkey_not_verified': return 'signin.error-passkey';
      case 'recent_passkey_required': return 'signin.error-recent-passkey';
      case 'same_account_passkey_required': return 'signin.error-same-account';
    }
  }
  return 'signin.error-generic';
}
onMounted(async () => {
  const suppliedCode = consumeEnrollment();
  if (suppliedCode) { enrollment.value = true; enrollmentKind.value = suppliedCode.kind; token.value = suppliedCode.token; }
  try { await refreshIdentity(); } catch (cause) { error.value = errorKey(cause); }
});
function setEnrollmentMode(kind: 'invite' | 'recovery'): void {
  enrollment.value = true;
  enrollmentKind.value = kind;
  customerEnrollment.value = false;
  token.value = '';
  aiConsent.value = false;
  error.value = '';
}
function showPasskeySignIn(): void {
  enrollment.value = false;
  customerEnrollment.value = false;
  token.value = '';
  aiConsent.value = false;
  error.value = '';
}
async function submit(): Promise<void> {
  error.value = '';
  busy.value = true;
  const focusTarget = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  try {
    const invitationRegistration = !account.value && enrollment.value && enrollmentKind.value === 'invite';
    const accountName = invitationRegistration ? invitationName.value.trim() : customerEnrollment.value ? customerDisplayName.value : '';
    if (invitationRegistration && (!accountName || !invitationUniverse.value.trim())) return;
    await authenticatePasskey(account.value ? 'reauth' : customerEnrollment.value ? 'customer-enroll' : enrollment.value ? 'enroll' : 'signin', token.value, accountName);
    token.value = '';
    if (invitationRegistration && account.value) {
      setInvitationDraft(account.value.id, accountName, invitationUniverse.value);
      if (aiConsent.value) void saveInvitationCharacter(account.value.id, true);
      await router.replace('/launch');
      return;
    }
    await router.replace(customerEnrollment.value
      ? '/me/character'
      : defaultSignedInDestination(account.value, route.query.return));
  } catch (cause) { error.value = errorKey(cause); }
  finally {
    busy.value = false;
    if (error.value) {
      await nextTick();
      const target = focusTarget instanceof HTMLElement && focusTarget !== document.body && focusTarget.isConnected
        && !(focusTarget instanceof HTMLButtonElement && focusTarget.disabled)
        ? focusTarget : invitationNameInput.value ?? invitationCodeInput.value ?? document.querySelector<HTMLElement>('button[type="submit"]');
      target?.focus();
    }
  }
}
</script>
<template>
  <section class="signin-page" aria-labelledby="sign-in-title">
    <header class="signin-masthead">
      <RouterLink to="/" class="signin-wordmark" :aria-label="customerCopy.text('home.title')"><img :src="brandMark" alt="" width="40" height="40" /><span>Wand<br />Night Stand</span></RouterLink>
      <label class="signin-language"><span class="sr-only">{{ language === 'en' ? 'Interface language' : 'Jazyk rozhraní' }}</span><select v-model="language"><option value="cs">Čeština</option><option value="en">English</option></select></label>
    </header>
    <div class="signin-content">
      <header class="signin-intro"><h1 id="sign-in-title">{{ account ? customerCopy.text('signin.title') : enrollment ? enrollmentKind === 'recovery' ? customerCopy.text('signin.recovery-title') : customerCopy.text('signin.invite-slogan') : customerCopy.text('signin.title') }}</h1></header>
      <AwNotice v-if="error" tone="danger" role="alert">{{ customerCopy.text(error) }}</AwNotice>
      <p v-if="loading" role="status">{{ customerCopy.text('signin.loading') }}</p>
      <p v-if="signingOut" role="status">{{ customerCopy.text('signin.signing-out') }}</p>
      <AwNotice v-if="initialized && !accountsEnabled" tone="info">{{ customerCopy.text('signin.accounts-disabled') }}</AwNotice>
      <section v-if="account" class="signed-in-actions"><p>{{ customerCopy.text('signin.signed-in-as', { name: account.displayName }) }}</p><AwButton variant="primary" :busy="busy" @click="submit">{{ customerCopy.text('signin.reauthenticate') }}</AwButton><AwButton v-if="enrollment" :disabled="busy || signingOut" @click="signOut">{{ customerCopy.text('signin.invite-other-account') }}</AwButton><p><RouterLink to="/settings">{{ customerCopy.text('signin.open-settings') }}</RouterLink></p></section>
      <form v-else class="identity-form" @submit.prevent="submit">
        <div v-if="customerEnrollment && launchAllowed !== true" class="identity-hint">{{ customerCopy.text('signin.customer-closed') }} <AwButton @click="customerEnrollment = false">{{ customerCopy.text('signin.back-to-signin') }}</AwButton></div>
        <template v-else-if="customerEnrollment">
          <AwField id="customer-account-name" :label="customerCopy.text('signin.customer-name')"><input id="customer-account-name" v-model="customerDisplayName" class="aw-input" maxlength="80" autocomplete="nickname" required :disabled="busy" /></AwField>
          <p class="identity-hint">{{ customerCopy.text(launchAllowed === true ? 'signin.customer-purpose' : 'signin.customer-invite-purpose') }}</p>
        </template>
        <template v-else-if="enrollment">
          <AwNotice v-if="enrollmentKind === 'recovery'" tone="warning">{{ customerCopy.text('signin.recovery-enrollment-warning') }} {{ customerCopy.text('signin.recovery-code-target') }}</AwNotice>
          <template v-if="enrollmentKind === 'invite'">
            <AwField id="account-name" :label="customerCopy.text('signin.invite-name')" :hint="customerCopy.text('signin.invite-name-hint')"><input id="account-name" aria-describedby="account-name-hint" ref="invitationNameInput" v-model="invitationName" class="aw-input" maxlength="80" autocomplete="nickname" required :disabled="busy" /></AwField>
            <AwField id="account-universe" :label="customerCopy.text('signin.invite-universe')"><input id="account-universe" v-model="invitationUniverse" class="aw-input" maxlength="160" autocomplete="off" required :disabled="busy" /></AwField>
          </template>
          <AwField id="account-invite" :label="customerCopy.text(enrollmentKind === 'recovery' ? 'signin.recovery-code-label' : 'signin.invite-code-label')" :hint="customerCopy.text('signin.token-hint')"><input id="account-invite" aria-describedby="account-invite-hint" ref="invitationCodeInput" v-model="token" class="aw-input" type="password" autocomplete="off" maxlength="256" required :disabled="busy" /></AwField>
          <fieldset v-if="enrollmentKind === 'invite'" class="consent-fieldset">
            <label><input v-model="aiConsent" type="checkbox" :disabled="busy" /> <span>{{ customerCopy.text('signin.ai-consent') }}</span></label>
            <details><summary>{{ customerCopy.text('signin.ai-consent-details') }}</summary><p>{{ customerCopy.text('signin.ai-consent-copy') }} <RouterLink to="/privacy">{{ customerCopy.text('shell.privacy') }}</RouterLink></p></details>
          </fieldset>
          <p v-if="enrollmentKind === 'invite'" class="identity-hint">{{ customerCopy.text('signin.passkey-before-dialog') }}</p>
        </template>
        <AwButton type="submit" variant="primary" :busy="busy" :disabled="!accountsEnabled || signingOut || (customerEnrollment && launchAllowed !== true)">{{ customerEnrollment ? customerCopy.text('signin.customer-create') : enrollment ? enrollmentKind === 'recovery' ? customerCopy.text('signin.recovery-title') : customerCopy.text('signin.invite-create-key') : customerCopy.text('signin.submit-signin') }}</AwButton>
        <div v-if="!customerEnrollment" class="signin-options">
          <AwButton v-if="enrollment" @click="showPasskeySignIn">{{ customerCopy.text('signin.have-passkey') }}</AwButton>
          <AwButton v-else @click="setEnrollmentMode('invite')">{{ customerCopy.text('signin.have-invite') }}</AwButton>
          <AwButton v-if="enrollmentKind === 'invite'" @click="setEnrollmentMode('recovery')">{{ customerCopy.text('signin.use-recovery') }}</AwButton>
          <AwButton v-else-if="enrollment" @click="setEnrollmentMode('invite')">{{ customerCopy.text('signin.back-to-invitation') }}</AwButton>
        </div>
        <AwButton v-if="customerEnrollment" @click="customerEnrollment = false">{{ customerCopy.text('signin.back-to-signin') }}</AwButton>
        <AwButton v-else-if="!enrollment && launchAllowed === true" @click="customerEnrollment = true; error = ''">{{ customerCopy.text('signin.create-customer') }}</AwButton>
      </form>
      <LostAccessHelp v-if="!account && !customerEnrollment" />
      <nav class="signin-help" aria-label="Nápověda"><RouterLink to="/support">{{ customerCopy.text('shell.support') }}</RouterLink><RouterLink to="/privacy">{{ customerCopy.text('shell.privacy') }}</RouterLink></nav>
    </div>
  </section>
</template>
<style scoped>
.signin-page { width: 100%; max-width: 30rem; margin-inline: auto; padding-inline: max(clamp(16px, calc(5.71vw - 2.28px), 20px), env(safe-area-inset-left, 0px)) max(clamp(16px, calc(5.71vw - 2.28px), 20px), env(safe-area-inset-right, 0px)); padding-block: max(20px, env(safe-area-inset-top, 0px)) max(32px, env(safe-area-inset-bottom, 0px)); }
.signin-masthead { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 1rem; margin-bottom: 24px; }
.signin-wordmark { min-height: 44px; display: flex; align-items: center; gap: .65rem; color: var(--aw-ivory); text-decoration: none; font-family: var(--aw-font-display); font-size: 1.45rem; font-weight: 500; line-height: .9; letter-spacing: -.025em; }
.signin-wordmark img { flex: 0 0 auto; width: 40px; height: 40px; }
.signin-wordmark span { white-space: nowrap; }
.signin-language select { max-width: 7rem; min-height: 44px; padding: .5rem; border: 1px solid var(--aw-border-subtle); border-radius: 8px; color: var(--aw-ivory); background: var(--aw-surface); font: inherit; font-size: .875rem; }
.signin-content { display: grid; gap: 24px; }
.signin-intro h1 { margin: 0; font-family: var(--aw-font-display); font-size: 36px; font-weight: 500; line-height: 1.05; }
.signin-intro p { margin: 8px 0 0; color: var(--aw-muted); }
.identity-form { display: grid; gap: 16px; }
.identity-form > .aw-button { min-height: 48px; }
.consent-fieldset summary { min-height: 44px; padding-block: .65rem; cursor: pointer; }
.identity-form :deep(.aw-input) { min-height: 48px; font-size: 16px; }
.identity-hint { margin: 0; color: var(--aw-muted); font-size: .9rem; }
.signin-options { display: flex; flex-wrap: wrap; gap: 8px 12px; }
.consent-fieldset { border: 0; padding: 0; margin: 0; display: grid; gap: .5rem; }
.consent-fieldset label { min-height: 44px; display: flex; align-items: flex-start; gap: .6rem; }
.consent-fieldset input { flex: 0 0 20px; width: 20px; height: 20px; margin-top: .2rem; }
.consent-fieldset details p { margin: .5rem 0 0; }
.signed-in-actions { display: grid; gap: 16px; }
.signed-in-actions p { margin: 0; }
.signin-help a { display: inline-flex; align-items: center; min-height: 44px; }
.signin-content :deep(.lost-access-help summary) { min-height: 44px; padding-block: .65rem; }
.signin-help { display: flex; gap: 1rem; flex-wrap: wrap; }
.signin-content :deep(.lost-access-help) { padding: 0; border: 0; background: transparent; box-shadow: none; }
@media (min-width: 600px) { .signin-page { padding-block: 32px; } }
</style>
