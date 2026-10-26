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
import { account, accountRequest, accountsEnabled, initialized, loading, signingOut, refreshIdentity, authenticatePasskey, signOut } from './session';
const router = useRouter();
const route = useRoute();
const enrollment = ref(!route.query.return && !signingOut.value);
const betaEntry = ref(true);
const customerEnrollment = ref(false);
const enrollmentKind = ref<'invite' | 'recovery'>('invite');
const token = ref('');
const customerDisplayName = ref('');
const invitationName = ref('');
const invitationNameInput = ref<HTMLInputElement | null>(null);
const invitationCodeInput = ref<HTMLInputElement | null>(null);
const invitationUniverse = ref('');
interface DefaultEnrollment { available: boolean; groupId?: string; groupName?: string; fixedUniverse?: string | null; universe?: string | null }
const defaultEnrollment = ref<DefaultEnrollment | null>(null);
const enrollmentLoading = ref(false);
const betaClosedError = ref(false);
const betaChangedError = ref(false);
const recoveryHint = ref(false);
const pendingBetaDraft = ref<{ name: string; universe: string } | null>(null);
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
  if (suppliedCode) { enrollment.value = true; betaEntry.value = false; enrollmentKind.value = suppliedCode.kind; token.value = suppliedCode.token; }
  try {
    await refreshIdentity();
    if (!suppliedCode) await refreshDefaultEnrollment();
  } catch (cause) { error.value = errorKey(cause); }
});
async function refreshDefaultEnrollment(): Promise<void> {
  if (account.value) return;
  enrollmentLoading.value = true;
  try {
    defaultEnrollment.value = await accountRequest<DefaultEnrollment>('/app/v1/enrollment/default');
    if (defaultEnrollment.value.fixedUniverse) invitationUniverse.value = defaultEnrollment.value.fixedUniverse;
  } catch {
    defaultEnrollment.value = null;
  } finally {
    enrollmentLoading.value = false;
  }
}
function setEnrollmentMode(kind: 'invite' | 'recovery'): void {
  enrollment.value = true;
  betaEntry.value = false;
  enrollmentKind.value = kind;
  customerEnrollment.value = false;
  token.value = '';
  aiConsent.value = false;
  error.value = '';
  betaClosedError.value = false;
  betaChangedError.value = false;
}
function showPasskeySignIn(): void {
  enrollment.value = false;
  betaEntry.value = false;
  customerEnrollment.value = false;
  token.value = '';
  aiConsent.value = false;
  error.value = '';
  betaClosedError.value = false;
  betaChangedError.value = false;
}
async function submit(): Promise<void> {
  error.value = '';
  busy.value = true;
  const focusTarget = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  try {
    const betaRegistration = !account.value && enrollment.value && enrollmentKind.value === 'invite' && betaEntry.value;
    const invitationRegistration = !account.value && enrollment.value && enrollmentKind.value === 'invite' && !betaEntry.value;
    const accountName = invitationRegistration ? invitationName.value.trim() : customerEnrollment.value ? customerDisplayName.value : '';
    const enrollmentName = betaRegistration ? invitationName.value.trim() : accountName;
    if ((invitationRegistration || betaRegistration) && (!enrollmentName || !invitationUniverse.value.trim())) return;
    if (betaRegistration && !defaultEnrollment.value?.available) {
      await refreshDefaultEnrollment();
      if (!defaultEnrollment.value?.available) return;
    }
    if (betaRegistration) pendingBetaDraft.value = { name: enrollmentName, universe: invitationUniverse.value };
    await authenticatePasskey(account.value ? 'reauth' : betaRegistration || customerEnrollment.value ? 'customer-enroll' : enrollment.value ? 'enroll' : 'signin', token.value, enrollmentName);
    token.value = '';
    if ((invitationRegistration || betaRegistration) && account.value) {
      const draft = pendingBetaDraft.value ?? { name: enrollmentName, universe: invitationUniverse.value };
      setInvitationDraft(account.value.id, draft.name, draft.universe);
      pendingBetaDraft.value = null;
      recoveryHint.value = false;
      if (aiConsent.value) void saveInvitationCharacter(account.value.id, true);
      await router.replace('/launch');
      return;
    }
    if (pendingBetaDraft.value && account.value) {
      setInvitationDraft(account.value.id, pendingBetaDraft.value.name, pendingBetaDraft.value.universe);
      pendingBetaDraft.value = null;
      recoveryHint.value = false;
      if (aiConsent.value) void saveInvitationCharacter(account.value.id, true);
      await router.replace('/launch');
      return;
    }
    await router.replace(customerEnrollment.value
      ? '/me/character'
      : defaultSignedInDestination(account.value, route.query.return));
  } catch (cause) {
    const uncertainCompletion = cause && typeof cause === 'object' && 'code' in cause && cause.code === 'customer_enrollment_completion_uncertain';
    if (pendingBetaDraft.value && uncertainCompletion) {
      // Enrollment may have committed even if its response was lost. Reconcile the
      // session before offering another account-creation ceremony.
      try { await refreshIdentity(); } catch { /* Keep the draft in component memory. */ }
      if (account.value) {
        setInvitationDraft(account.value.id, pendingBetaDraft.value.name, pendingBetaDraft.value.universe);
        pendingBetaDraft.value = null;
        token.value = '';
        recoveryHint.value = false;
        if (aiConsent.value) void saveInvitationCharacter(account.value.id, true);
        await router.replace('/launch');
        return;
      }
      enrollment.value = false;
      betaEntry.value = false;
      customerEnrollment.value = false;
      token.value = '';
      recoveryHint.value = true;
      return;
    }
    pendingBetaDraft.value = null;
    const code = cause && typeof cause === 'object' && 'code' in cause ? cause.code : '';
    betaClosedError.value = code === 'beta_enrollment_closed' || code === 'beta_enrollment_unavailable';
    betaChangedError.value = code === 'beta_enrollment_closed_or_changed';
    if (betaClosedError.value || betaChangedError.value) await refreshDefaultEnrollment();
    else error.value = errorKey(cause);
  }
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
      <AwNotice v-if="recoveryHint" tone="warning" role="status">{{ language === 'en' ? 'We could not confirm whether account creation finished. Sign in with your existing passkey to continue; your name and universe are kept on this screen.' : 'Nepodařilo se ověřit, zda se vytvoření účtu dokončilo. Pokračuj přihlášením svým přístupovým klíčem; jméno a univerzum zůstávají na této obrazovce.' }}</AwNotice>
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
            <p v-if="enrollmentLoading" role="status">{{ language === 'en' ? 'Checking beta access…' : 'Ověřuji možnost vstupu do bety…' }}</p>
            <AwNotice v-else-if="betaEntry && ((!defaultEnrollment?.available && !token) || betaClosedError)" tone="info">{{ language === 'en' ? 'Enrollment for new participants is currently closed.' : 'Přijímání nových účastníků je nyní uzavřené.' }}</AwNotice>
            <AwNotice v-else-if="betaChangedError" tone="warning">{{ language === 'en' ? 'The beta group or its rules changed. Review the current settings and continue again.' : 'Beta skupina nebo její pravidla se změnily. Zkontroluj aktuální podmínky a spusť vstup znovu.' }}</AwNotice>
            <AwField id="account-name" :label="customerCopy.text('signin.invite-name')" :hint="customerCopy.text('signin.invite-name-hint')"><input id="account-name" aria-describedby="account-name-hint" ref="invitationNameInput" v-model="invitationName" class="aw-input" maxlength="80" autocomplete="nickname" required :disabled="busy" /></AwField>
            <AwField id="account-universe" :label="customerCopy.text('signin.invite-universe')" :hint="betaEntry && defaultEnrollment?.fixedUniverse ? (language === 'en' ? 'The invitation sets this universe.' : 'Univerzum je určené pozvánkou.') : undefined"><input id="account-universe" v-model="invitationUniverse" class="aw-input" maxlength="160" autocomplete="off" required :readonly="betaEntry && !!defaultEnrollment?.fixedUniverse" :disabled="busy" /></AwField>
          </template>
          <AwField v-if="!betaEntry || enrollmentKind === 'recovery'" id="account-invite" :label="customerCopy.text(enrollmentKind === 'recovery' ? 'signin.recovery-code-label' : 'signin.invite-code-label')" :hint="customerCopy.text('signin.token-hint')"><input id="account-invite" aria-describedby="account-invite-hint" ref="invitationCodeInput" v-model="token" class="aw-input" type="password" autocomplete="off" maxlength="256" required :disabled="busy" /></AwField>
          <fieldset v-if="enrollmentKind === 'invite'" class="consent-fieldset">
            <label><input v-model="aiConsent" type="checkbox" :disabled="busy" /> <span>{{ betaEntry ? (language === 'en' ? 'I agree to use OpenAI to create my character and personal experience.' : 'Souhlasím s použitím OpenAI pro vytvoření své postavy a osobního zážitku.') : customerCopy.text('signin.ai-consent') }}</span></label>
            <p v-if="betaEntry" class="identity-hint">{{ language === 'en' ? 'Without this consent, we will not create your character.' : 'Bez tohoto souhlasu postavu nevytvoříme.' }}</p>
            <details><summary>{{ customerCopy.text('signin.ai-consent-details') }}</summary><p>{{ customerCopy.text('signin.ai-consent-copy') }} <RouterLink to="/privacy">{{ customerCopy.text('shell.privacy') }}</RouterLink></p></details>
          </fieldset>
          <p v-if="enrollmentKind === 'invite'" class="identity-hint">{{ customerCopy.text('signin.passkey-before-dialog') }}</p>
        </template>
        <AwButton type="submit" variant="primary" :busy="busy" :disabled="!accountsEnabled || signingOut || enrollmentLoading || (betaEntry && !defaultEnrollment?.available) || (customerEnrollment && launchAllowed !== true)">{{ customerEnrollment ? customerCopy.text('signin.customer-create') : enrollment ? enrollmentKind === 'recovery' ? customerCopy.text('signin.recovery-title') : betaEntry ? (language === 'en' ? 'Continue' : 'Pokračovat') : customerCopy.text('signin.invite-create-key') : customerCopy.text('signin.submit-signin') }}</AwButton>
        <div v-if="!customerEnrollment" class="signin-options">
          <AwButton v-if="enrollment" @click="showPasskeySignIn">{{ customerCopy.text('signin.have-passkey') }}</AwButton>
          <AwButton v-if="betaEntry" @click="setEnrollmentMode('invite')">{{ customerCopy.text('signin.have-invite') }}</AwButton>
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
