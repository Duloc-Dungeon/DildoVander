<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { consumeEnrollment } from '../../application/guestClaim';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import AwField from '../../components/AwField.vue';
import LostAccessHelp from './LostAccessHelp.vue';
import amberWand from '../../assets/wns-v2/art/wand-amber.png';
import accessKey from '../../assets/brand/midnight/access-key.webp';
import SceneShell from '../../components/scene/SceneShell.vue';
import PaperSurface from '../../components/scene/PaperSurface.vue';
import { defaultSignedInDestination } from '../customer/customerJourney';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { launchAllowed } from '../../application/launchAccess';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { setInvitationDraft, saveInvitationCharacter } from './invitationRegistration';
import { account, accountRequest, accountsEnabled, initialized, loading, signingOut, refreshIdentity, authenticatePasskey, signOut } from './session';
const router = useRouter();
const route = useRoute();
const protectedReturn = !!route.query.return && route.query.return !== '/';
const enrollment = ref(!protectedReturn && !signingOut.value);
const arrival = ref(!protectedReturn && !signingOut.value);
const linkedInvitation = ref(false);
const linkedEnrollment = ref<{ kind: 'invite' | 'recovery'; token: string } | null>(null);
const betaEntry = ref(enrollment.value);
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
const enrollmentUnavailable = ref(false);
const betaClosedError = ref(false);
const betaChangedError = ref(false);
const recoveryHint = ref(false);
const pendingBetaDraft = ref<{ name: string; universe: string } | null>(null);
const aiConsent = ref(false);
const busy = ref(false);
const error = ref<CustomerCopyKey | ''>('');
const language = computed(() => interfaceLanguagePreference.language.value);
watch(() => account.value?.id, id => interfaceLanguagePreference.setScope(id), { immediate: true });
function errorKey(value: unknown): CustomerCopyKey {
  if (value instanceof DOMException && value.name === 'NotAllowedError') return 'signin.error-cancelled';
  if (value instanceof Error && value.message === 'Přístupové klíče vyžadují HTTPS a podporovaný prohlížeč.') return 'signin.error-secure-context';
  if (value instanceof Error && value.message === 'Ověření přístupovým klíčem už probíhá.') return 'signin.error-pending';
  if (value instanceof Error && 'code' in value) {
    switch (value.code) {
      case 'ai_consent_required': return 'signin.ai-consent-required';
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
  linkedEnrollment.value = suppliedCode;
  if (suppliedCode) { arrival.value = false; linkedInvitation.value = true; enrollment.value = true; betaEntry.value = false; enrollmentKind.value = suppliedCode.kind; token.value = suppliedCode.token; }
  try {
    await refreshIdentity();
    if (account.value && !suppliedCode && !protectedReturn) {
      await router.replace(defaultSignedInDestination(account.value));
      return;
    }
    if (!suppliedCode && betaEntry.value) await refreshDefaultEnrollment();
  } catch (cause) { error.value = errorKey(cause); }
});
async function refreshDefaultEnrollment(): Promise<void> {
  if (account.value) return;
  enrollmentLoading.value = true;
  enrollmentUnavailable.value = false;
  try {
    defaultEnrollment.value = await accountRequest<DefaultEnrollment>('/app/v1/enrollment/default');
    if (defaultEnrollment.value.fixedUniverse) invitationUniverse.value = defaultEnrollment.value.fixedUniverse;
  } catch {
    defaultEnrollment.value = null;
    enrollmentUnavailable.value = true;
  } finally {
    enrollmentLoading.value = false;
  }
}
function beginFromArrival(): void {
  arrival.value = false;
  enrollment.value = true;
  customerEnrollment.value = false;
  aiConsent.value = false;
  error.value = '';
  betaClosedError.value = false;
  betaChangedError.value = false;
  const supplied = linkedEnrollment.value;
  linkedInvitation.value = !!supplied;
  enrollmentKind.value = supplied?.kind ?? 'invite';
  betaEntry.value = !supplied;
  token.value = supplied?.token ?? '';
  if (!supplied) void refreshDefaultEnrollment();
}
function setEnrollmentMode(kind: 'invite' | 'recovery'): void {
  arrival.value = false;
  linkedInvitation.value = false;
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
  arrival.value = false;
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
    if (!account.value && (customerEnrollment.value || enrollment.value && enrollmentKind.value === 'invite') && !aiConsent.value) { error.value = 'signin.ai-consent-required'; return; }
    if ((invitationRegistration || betaRegistration) && (!enrollmentName || !invitationUniverse.value.trim())) return;
    if (betaRegistration && !defaultEnrollment.value?.available) {
      await refreshDefaultEnrollment();
      if (!defaultEnrollment.value?.available) return;
    }
    if (betaRegistration) pendingBetaDraft.value = { name: enrollmentName, universe: invitationUniverse.value };
    await authenticatePasskey(account.value ? 'reauth' : betaRegistration || customerEnrollment.value ? 'customer-enroll' : enrollment.value ? 'enroll' : 'signin', token.value, enrollmentName, aiConsent.value);
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
  <SceneShell class="signin-scene">
  <section class="signin-page" :class="{ 'signin-page--arrival': arrival, 'signin-page--paper': !arrival && enrollment }" aria-labelledby="sign-in-title">
    <header class="signin-masthead">
      <RouterLink to="/" class="signin-wordmark" :aria-label="customerCopy.text('home.title')"><span>Wand<br />Night Stand</span></RouterLink>
      <span class="signin-language" aria-label="Jazyk rozhraní">Čeština</span>
    </header>
    <div v-if="arrival && !account" class="arrival-content">
      <div class="arrival-art" aria-hidden="true"><img :src="amberWand" alt="" width="1254" height="1254" fetchpriority="high" /></div>
      <h1 id="sign-in-title">{{ language === 'en' ? 'Enter on your own reputation.' : 'Vstup na vlastní pověst.' }}</h1>
      <p>Ollivander Industries™ tě vítá v mobilní pobočce. Fronty jsme zrušili. Posuzování zákazníků zůstává.</p>
      <AwButton variant="primary" @click="beginFromArrival">{{ language === 'en' ? 'Begin' : 'Začít' }}</AwButton>
      <AwButton @click="showPasskeySignIn">{{ language === 'en' ? 'I already have access' : 'Už mám přístup' }}</AwButton>
      <nav class="signin-help" aria-label="Nápověda"><RouterLink to="/support">{{ customerCopy.text('shell.support') }}</RouterLink><RouterLink to="/privacy">{{ customerCopy.text('shell.privacy') }}</RouterLink></nav>
    </div>
    <component :is="enrollment ? PaperSurface : 'div'" v-else class="signin-content">
      <div v-if="!enrollment && !account" class="signin-access-key" aria-hidden="true"><img :src="accessKey" alt="" width="696" height="430" /></div>
      <header class="signin-intro"><h1 id="sign-in-title">{{ account ? customerCopy.text('signin.title') : enrollment ? enrollmentKind === 'recovery' ? customerCopy.text('signin.recovery-title') : (language === 'en' ? 'What shall we call you?' : 'Jak ti budou říkat?') : (language === 'en' ? 'Back on the scene.' : 'Zpátky na scénu.') }}</h1></header>
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
            <AwNotice v-else-if="betaEntry && enrollmentUnavailable" tone="warning" role="alert">{{ language === 'en' ? 'We could not check invitation availability. Check your connection and try again.' : 'Možnost přijetí pozvánky se nepodařilo ověřit. Zkontroluj připojení a zkus to znovu.' }} <AwButton @click="refreshDefaultEnrollment">{{ language === 'en' ? 'Try again' : 'Zkusit znovu' }}</AwButton></AwNotice>
            <AwNotice v-else-if="betaEntry && ((defaultEnrollment?.available === false && !token) || betaClosedError)" tone="info">{{ language === 'en' ? 'Enrollment for new participants is currently closed.' : 'Přijímání nových účastníků je nyní uzavřené.' }}</AwNotice>
            <AwNotice v-else-if="betaChangedError" tone="warning">{{ language === 'en' ? 'The beta group or its rules changed. Review the current settings and continue again.' : 'Beta skupina nebo její pravidla se změnily. Zkontroluj aktuální podmínky a spusť vstup znovu.' }}</AwNotice>
            <AwField id="account-name" :label="customerCopy.text('signin.invite-name')" :hint="customerCopy.text('signin.invite-name-hint')"><input id="account-name" aria-describedby="account-name-hint" ref="invitationNameInput" v-model="invitationName" class="aw-input" maxlength="80" autocomplete="nickname" required :disabled="busy" /></AwField>
            <div v-if="betaEntry && defaultEnrollment?.fixedUniverse" class="fixed-universe"><span>{{ customerCopy.text('signin.invite-universe') }}</span><p id="account-universe">{{ defaultEnrollment.fixedUniverse }}</p><small>{{ language === 'en' ? 'The invitation sets this universe.' : 'Univerzum je určené pozvánkou.' }}</small></div>
            <AwField v-else id="account-universe" :label="customerCopy.text('signin.invite-universe')"><input id="account-universe" v-model="invitationUniverse" class="aw-input" maxlength="160" autocomplete="off" required :disabled="busy" /></AwField>
          </template>
          <AwField v-if="(!betaEntry || enrollmentKind === 'recovery') && !linkedInvitation" id="account-invite" :label="customerCopy.text(enrollmentKind === 'recovery' ? 'signin.recovery-code-label' : 'signin.invite-code-label')" :hint="customerCopy.text('signin.token-hint')"><input id="account-invite" aria-describedby="account-invite-hint" ref="invitationCodeInput" v-model="token" class="aw-input" type="password" autocomplete="off" maxlength="256" required :disabled="busy" /></AwField>

          <p v-if="enrollmentKind === 'invite'" class="identity-hint">{{ customerCopy.text('signin.passkey-before-dialog') }}</p>
        </template>
          <fieldset v-if="customerEnrollment || enrollment && enrollmentKind === 'invite'" class="consent-fieldset">
            <label><input v-model="aiConsent" type="checkbox" required :disabled="busy" /> <span>{{ language === 'en' ? 'I agree to use OpenAI to create my character and personal experience.' : 'Souhlasím se zpracováním údajů postavy a zvuku rozhovoru službou OpenAI pro vytvoření osobního zážitku.' }}</span></label>
            <p class="identity-hint">{{ language === 'en' ? 'Without this consent, we will not create your character.' : 'Bez tohoto souhlasu registraci nelze dokončit.' }}</p>
            <details><summary>{{ customerCopy.text('signin.ai-consent-details') }}</summary><p>{{ customerCopy.text('signin.ai-consent-copy') }} <RouterLink to="/privacy">{{ customerCopy.text('shell.privacy') }}</RouterLink></p></details>
          </fieldset>
        <AwButton type="submit" variant="primary" :busy="busy" :disabled="!accountsEnabled || signingOut || ((customerEnrollment || enrollment && enrollmentKind === 'invite') && !aiConsent) || (betaEntry && (enrollmentLoading || !defaultEnrollment?.available)) || (customerEnrollment && launchAllowed !== true)">{{ customerEnrollment ? customerCopy.text('signin.customer-create') : enrollment ? enrollmentKind === 'recovery' ? customerCopy.text('signin.recovery-title') : betaEntry ? (language === 'en' ? 'Accept invitation' : 'Přijmout pozvánku') : customerCopy.text('signin.invite-create-key') : customerCopy.text('signin.submit-signin') }}</AwButton>
        <p v-if="!enrollment && !customerEnrollment" class="identity-hint">{{ language === 'en' ? 'This opens system verification. Confirm with your device; no account password is needed.' : 'Otevře se systémové ověření. Potvrď ho na svém zařízení; heslo k účtu nepotřebuješ.' }}</p>
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
      <AwButton v-if="!account" class="signin-back" @click="arrival = true; error = ''">{{ language === 'en' ? 'Back' : 'Zpět' }}</AwButton>
    </component>
  </section>
  </SceneShell>
</template>
<style scoped>
.signin-page { width: 100%; max-width: 30rem; margin-inline: auto; padding-inline: max(clamp(16px, calc(5.71vw - 2.28px), 20px), env(safe-area-inset-left, 0px)) max(clamp(16px, calc(5.71vw - 2.28px), 20px), env(safe-area-inset-right, 0px)); padding-block: max(20px, env(safe-area-inset-top, 0px)) max(32px, env(safe-area-inset-bottom, 0px)); }
.signin-masthead { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 1rem; margin-bottom: 24px; }
.signin-wordmark { min-height: 44px; display: flex; align-items: center; gap: .65rem; color: var(--aw-ivory); text-decoration: none; font-family: var(--aw-font-display); font-size: 1.45rem; font-weight: 500; line-height: .9; letter-spacing: -.025em; }
.signin-wordmark img { flex: 0 0 auto; width: 40px; height: 40px; }
.signin-wordmark span { white-space: nowrap; }
.signin-language { display: inline-flex; align-items: center; min-height: 44px; padding: .5rem; border: 1px solid var(--aw-border-subtle); border-radius: 8px; color: var(--aw-ivory); background: var(--aw-surface); font-size: .875rem; }
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
.signin-page { max-width: 34rem; min-height: 100dvh; }
.signin-masthead { margin-bottom: 1.25rem; }
.signin-wordmark img { display: none; }
.signin-wordmark { font-size: 1.35rem; }
.arrival-content { display: grid; gap: 1rem; padding-top: 1rem; }
.arrival-invitation { display: block; width: 115%; height: auto; max-width: none; margin-left: -7.5%; filter: drop-shadow(0 1rem 1rem #0008); }
.arrival-content h1 { font-family: var(--aw-font-display); font-size: clamp(2.7rem, 10vw, 3.5rem); font-weight: 500; line-height: .98; margin: .5rem 0 0; }
.arrival-content p { font-family: var(--aw-font-display); font-size: 1.35rem; line-height: 1.25; margin: 0 0 .6rem; }
.arrival-content > .aw-button { width: 100%; min-height: 48px; }
.arrival-content > .aw-button--secondary { border-color: transparent; background: transparent; text-decoration: underline; }
.signin-access-key { display: block; width: 100%; height: auto; margin-bottom: -1rem; }
.signin-page:not(.signin-page--paper) .signin-intro { text-align: center; }
.signin-content { gap: 1.25rem; }
.signin-content.paper-surface { border-radius: 2px; }
.signin-page--paper .signin-intro h1 { font-size: clamp(2rem, 8vw, 2.8rem); }
.signin-options .aw-button { padding: .35rem .5rem; border: 0; background: transparent; text-decoration: underline; }
.fixed-universe p { margin: .35rem 0; font-family: var(--aw-font-display); font-size: 1.4rem; }
.fixed-universe small { color: var(--aw-muted); }
.signin-back { justify-self: start; min-height: 44px; }
@media (max-height: 550px) { .signin-page { padding-top: .5rem; } .signin-masthead { margin-bottom: .5rem; } .signin-access-key { max-height: 10rem; object-fit: contain; } }
@media (min-width: 600px) { .signin-page { padding-block: 32px; } }
.signin-scene { --wns-display: var(--wns-font-family); }
.signin-page { max-width: 38rem; color: var(--wns-text, #f5f5f0); }
.signin-masthead { margin-bottom: clamp(2rem, 8vh, 5rem); }
.signin-wordmark { font-family: var(--wns-display); font-weight: 800; font-size: 1.15rem; letter-spacing: -.055em; line-height: .98; }
.signin-language { color: var(--wns-muted, #b2b9b6); background: transparent; border: 0; }
.arrival-content { gap: 1.25rem; padding-top: 0; }
.arrival-art { height: clamp(14rem, 32vh, 21rem); display: grid; place-items: center; border-radius: var(--wns-radius-hero); background: var(--wns-amber); }
.arrival-art img { display: block; width: 100%; height: 100%; object-fit: contain; padding: 12%; }
.arrival-content h1 { font: 800 clamp(2.7rem, 11vw, 4rem)/.98 var(--wns-display); letter-spacing: -.07em; }
.arrival-content p { max-width: 32rem; color: var(--wns-muted, #b2b9b6); font: 500 1rem/1.55 var(--wns-display); }
.arrival-content > .aw-button:first-of-type, .identity-form > .aw-button--primary { min-height: 56px; border-radius: 999px; background: var(--wns-mint, #b8f3d4); color: #111513; font-weight: 750; }
.signin-content { gap: 1.25rem; padding: clamp(1.25rem, 5vw, 2rem); border-radius: 28px !important; background: var(--wns-surface, #191b1b); border-color: var(--wns-line, #343939); }
.signin-intro h1 { font: 800 clamp(2.3rem, 9vw, 3.4rem)/1 var(--wns-display); letter-spacing: -.065em; }
.signin-access-key { height: clamp(8rem, 28vh, 15rem); display: grid; place-items: center; margin: 0; border-radius: 22px; background: var(--wns-violet, #c8b9fc); color: #15121d; }
.signin-access-key img { display: block; width: 100%; height: 100%; object-fit: contain; padding: 12%; }
.identity-hint, .signin-help { color: var(--wns-muted, #b2b9b6); }
.identity-form :deep(.aw-input) { color: var(--wns-text, #f5f5f0); background: var(--wns-surface-raised, #222626); border-color: var(--wns-line, #343939); border-radius: 14px; }
.fixed-universe { color: var(--wns-text, #f5f5f0); }
.fixed-universe p { font-family: var(--wns-display); }
.consent-fieldset { padding: 1rem; border: 1px solid var(--wns-line, #343939); border-radius: 18px; background: #111313; }
.consent-fieldset label { line-height: 1.45; }
.consent-fieldset input { accent-color: var(--wns-mint, #b8f3d4); }
.signin-options { gap: .25rem .75rem; }
.signin-options .aw-button { min-height: 44px; padding-inline: .5rem; color: var(--wns-muted, #b2b9b6); }
.signin-back { color: var(--wns-muted, #b2b9b6); }
.signin-content :deep(.lost-access-help) { color: var(--wns-text, #f5f5f0); }
.signin-page :deep(.aw-button--secondary) { color: var(--wns-text, #f5f5f0); border-color: var(--wns-line, #343939); border-radius: 999px; background: transparent; }
.signin-page :deep(:focus-visible) { outline: 2px solid var(--wns-mint, #b8f3d4); outline-offset: 4px; }
</style>
