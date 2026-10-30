<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { consumeEnrollment } from '../../application/guestClaim';
import AwButton from '../../components/AwButton.vue';
import ApplicationHeaderActions from '../../application/ApplicationHeaderActions.vue';
import AwNotice from '../../components/AwNotice.vue';
import AwField from '../../components/AwField.vue';
import LostAccessHelp from './LostAccessHelp.vue';
import amberWand from '../../assets/wns-v2/art/wand-amber.png';
import accessKey from '../../assets/brand/midnight/access-key.webp';
import SceneShell from '../../components/scene/SceneShell.vue';
import { defaultSignedInDestination } from '../customer/customerJourney';
import { isCustomerAccount } from '../../application/access';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { launchAllowed } from '../../application/launchAccess';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { setInvitationDraft, saveInvitationCharacter } from './invitationRegistration';
import { account, accountRequest, accountsEnabled, initialized, loading, signingOut, refreshIdentity, authenticatePasskey, signOut } from './session';
const router = useRouter();
const route = useRoute();
const logoutUnconfirmed = computed(() => route.query.logout === 'unconfirmed');
const protectedReturn = !!route.query.return && route.query.return !== '/';
const enrollment = ref(!protectedReturn && !signingOut.value && !logoutUnconfirmed.value);
const arrival = ref(!protectedReturn && !signingOut.value && !logoutUnconfirmed.value);
const linkedInvitation = ref(false);
const linkedEnrollment = ref<{ kind: 'invite'; token: string } | null>(null);
const betaEntry = ref(enrollment.value);
const customerEnrollment = ref(false);
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
const sessionUnavailable = ref(false);
const sessionRefreshing = ref(false);
const stepHeading = ref<HTMLHeadingElement | null>(null);
const language = computed(() => interfaceLanguagePreference.language.value);
watch(() => account.value?.id, id => interfaceLanguagePreference.setScope(id), { immediate: true });
// These steps share a route, so route navigation cannot announce the new view.
watch([arrival, enrollment, betaEntry, customerEnrollment], () => {
  stepHeading.value?.focus();
}, { flush: 'post' });
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
  if (logoutUnconfirmed.value) return;
  const suppliedCode = consumeEnrollment();
  linkedEnrollment.value = suppliedCode;
  if (suppliedCode) { arrival.value = false; linkedInvitation.value = true; enrollment.value = true; betaEntry.value = false; token.value = suppliedCode.token; }
  await retryIdentity();
});
async function retryIdentity(): Promise<void> {
  if (sessionRefreshing.value || busy.value) return;
  sessionRefreshing.value = true;
  error.value = '';
  try {
    await refreshIdentity();
    sessionUnavailable.value = false;
    if (account.value && !linkedEnrollment.value && !protectedReturn && !logoutUnconfirmed.value) {
      await router.replace(defaultSignedInDestination(account.value));
      return;
    }
    if (!linkedEnrollment.value && betaEntry.value) await refreshDefaultEnrollment();
  } catch (cause) {
    sessionUnavailable.value = true;
    error.value = errorKey(cause);
  } finally { sessionRefreshing.value = false; }
}
async function leaveAccount(continueInvitation = false): Promise<void> {
  if (busy.value || signingOut.value) return;
  busy.value = true;
  error.value = '';
  try {
    await signOut();
    sessionUnavailable.value = false;
    if (continueInvitation) return;
    arrival.value = false;
    enrollment.value = false;
    await router.replace('/signin');
  } catch {
    await router.replace({ path: '/signin', query: { logout: 'unconfirmed' } });
  } finally { busy.value = false; }
}
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
  betaEntry.value = !supplied;
  token.value = supplied?.token ?? '';
  if (!supplied) void refreshDefaultEnrollment();
}
function setEnrollmentMode(): void {
  arrival.value = false;
  linkedInvitation.value = false;
  enrollment.value = true;
  betaEntry.value = false;
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
  const returningAccount = !!account.value;
  try {
    const betaRegistration = !account.value && enrollment.value && betaEntry.value;
    const invitationRegistration = !account.value && enrollment.value && !betaEntry.value;
    const accountName = invitationRegistration ? invitationName.value.trim() : customerEnrollment.value ? customerDisplayName.value : '';
    const enrollmentName = betaRegistration ? invitationName.value.trim() : accountName;
    if (!account.value && (customerEnrollment.value || enrollment.value) && !aiConsent.value) { error.value = 'signin.ai-consent-required'; return; }
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
    let destination = customerEnrollment.value
      ? '/me/character'
      : defaultSignedInDestination(account.value, route.query.return);
    if (!returningAccount && isCustomerAccount(account.value) && !account.value?.roles.includes('Owner')
      && ['/me/character', '/me/account'].includes(destination)) {
      try {
        const policy = await accountRequest<{ allowed: boolean }>('/app/v1/launch');
        launchAllowed.value = policy.allowed === true;
        if (policy.allowed === false) destination = '/launch';
      } catch { /* Account access remains available when launch status cannot be checked. */ }
    }
    await router.replace(destination);
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
  <section class="signin-page" aria-labelledby="sign-in-title">
    <ApplicationHeaderActions>
      <AwButton v-if="arrival && !account" class="signin-code-entry" @click="setEnrollmentMode()">{{ customerCopy.text('signin.have-invite') }}</AwButton>
    </ApplicationHeaderActions>
    <div v-if="arrival && !account && !logoutUnconfirmed && !sessionUnavailable" class="arrival-content">
      <div class="signin-art signin-art--amber" aria-hidden="true"><img :src="amberWand" alt="" width="1254" height="1254" fetchpriority="high" /></div>
      <h1 id="sign-in-title" ref="stepHeading" tabindex="-1">{{ language === 'en' ? 'Enter on your own reputation.' : 'Vstup na vlastní pověst.' }}</h1>
      <div class="arrival-copy">
        <p>Vyber si fiktivní postavu pro společný večer. Pak si vytvoříš účet a přístupový klíč pro návrat.</p>
        <p class="identity-hint">Ollivander Industries™ tě vítá v mobilní pobočce. U našich hůlek je vlastní rozum samozřejmostí. U zákazníků příjemným překvapením.</p>
      </div>
      <AwButton variant="primary" @click="beginFromArrival">{{ language === 'en' ? 'Begin' : 'Začít' }}</AwButton>
      <AwButton @click="showPasskeySignIn">{{ language === 'en' ? 'I already have access' : 'Už mám přístup' }}</AwButton>
      <nav class="signin-help" aria-label="Nápověda"><RouterLink to="/support">{{ customerCopy.text('shell.support') }}</RouterLink><RouterLink to="/privacy">{{ customerCopy.text('shell.privacy') }}</RouterLink></nav>
    </div>
    <div v-else class="signin-content">
      <div v-if="!enrollment && !account && !customerEnrollment" class="signin-art signin-art--violet" aria-hidden="true"><img :src="accessKey" alt="" width="696" height="430" /></div>
      <header class="signin-intro">
        <h1 id="sign-in-title" ref="stepHeading" tabindex="-1">{{ account ? customerCopy.text('signin.title') : enrollment || customerEnrollment ? (language === 'en' ? 'What shall we call you?' : 'Jak ti budou říkat?') : (language === 'en' ? 'Back on the scene.' : 'Zpátky na scénu.') }}</h1>
        <p v-if="!account && enrollment" class="identity-hint">Vyber si postavu pro společný večer. Přijetím pozvánky si založíš účet pro svůj osobní zážitek.</p>
      </header>
      <nav v-if="!account && !customerEnrollment" class="signin-options" aria-label="Způsob vstupu">
        <AwButton v-if="!enrollment || betaEntry" :disabled="busy || signingOut" @click="setEnrollmentMode()">{{ customerCopy.text('signin.have-invite') }}</AwButton>
        <AwButton v-if="enrollment" :disabled="busy || signingOut" @click="showPasskeySignIn">{{ customerCopy.text('signin.have-passkey') }}</AwButton>
      </nav>
      <AwNotice v-if="error && !sessionUnavailable" tone="danger" role="alert">{{ customerCopy.text(error) }}</AwNotice>
      <AwNotice v-if="sessionUnavailable" tone="danger" role="alert">{{ customerCopy.text('signin.session-unavailable') }} <AwButton :busy="sessionRefreshing" :disabled="busy || signingOut || sessionRefreshing" @click="retryIdentity">{{ customerCopy.text('signin.retry-session') }}</AwButton></AwNotice>
      <AwNotice v-if="logoutUnconfirmed" tone="danger" role="alert">{{ customerCopy.text('signin.logout-unconfirmed') }}</AwNotice>
      <AwButton v-if="account || logoutUnconfirmed" :busy="busy || signingOut" :disabled="busy || signingOut" @click="leaveAccount()">{{ customerCopy.text('signin.sign-out') }}</AwButton>
      <AwNotice v-if="recoveryHint" tone="warning" role="status">{{ language === 'en' ? 'We could not confirm whether account creation finished. Sign in with your existing passkey to continue; your name and universe are kept on this screen.' : 'Nepodařilo se ověřit, zda se vytvoření účtu dokončilo. Pokračuj přihlášením svým přístupovým klíčem; jméno a univerzum zůstávají na této obrazovce.' }}</AwNotice>
      <p v-if="loading" role="status">{{ customerCopy.text('signin.loading') }}</p>
      <p v-if="signingOut" role="status">{{ customerCopy.text('signin.signing-out') }}</p>
      <AwNotice v-if="initialized && !accountsEnabled" tone="info">{{ customerCopy.text('signin.accounts-disabled') }}</AwNotice>
      <section v-if="account" class="signed-in-actions"><p>{{ customerCopy.text('signin.signed-in-as', { name: account.displayName }) }}</p><AwButton variant="primary" :busy="busy" @click="submit">{{ customerCopy.text('signin.reauthenticate') }}</AwButton><AwButton v-if="enrollment" :disabled="busy || signingOut" @click="leaveAccount(true)">{{ customerCopy.text('signin.invite-other-account') }}</AwButton><p><RouterLink to="/settings">{{ customerCopy.text('signin.open-settings') }}</RouterLink></p></section>
      <form v-else class="identity-form" @submit.prevent="submit">
        <div v-if="customerEnrollment && launchAllowed !== true" class="identity-hint">{{ customerCopy.text('signin.customer-closed') }} <AwButton @click="customerEnrollment = false">{{ customerCopy.text('signin.back-to-signin') }}</AwButton></div>
        <template v-else-if="customerEnrollment">
          <AwField id="customer-account-name" :label="customerCopy.text('signin.customer-name')"><input id="customer-account-name" v-model="customerDisplayName" class="aw-input" maxlength="80" autocomplete="nickname" required :disabled="busy" /></AwField>
          <p class="identity-hint">{{ customerCopy.text(launchAllowed === true ? 'signin.customer-purpose' : 'signin.customer-invite-purpose') }}</p>
        </template>
        <template v-else-if="enrollment">
            <p v-if="enrollmentLoading" role="status">{{ language === 'en' ? 'Checking beta access…' : 'Ověřuji možnost vstupu do bety…' }}</p>
            <AwNotice v-else-if="betaEntry && enrollmentUnavailable" tone="warning" role="alert">{{ language === 'en' ? 'We could not check invitation availability. Check your connection and try again.' : 'Možnost přijetí pozvánky se nepodařilo ověřit. Zkontroluj připojení a zkus to znovu.' }} <AwButton @click="refreshDefaultEnrollment">{{ language === 'en' ? 'Try again' : 'Zkusit znovu' }}</AwButton></AwNotice>
            <AwNotice v-else-if="betaEntry && ((defaultEnrollment?.available === false && !token) || betaClosedError)" tone="info">{{ language === 'en' ? 'Enrollment for new participants is currently closed.' : 'Přijímání nových účastníků je nyní uzavřené.' }}</AwNotice>
            <AwNotice v-else-if="betaChangedError" tone="warning">{{ language === 'en' ? 'The beta group or its rules changed. Review the current settings and continue again.' : 'Beta skupina nebo její pravidla se změnily. Zkontroluj aktuální podmínky a spusť vstup znovu.' }}</AwNotice>
            <AwField id="account-name" :label="customerCopy.text('signin.invite-name')" :hint="customerCopy.text('signin.invite-name-hint')"><input id="account-name" aria-describedby="account-name-hint" ref="invitationNameInput" v-model="invitationName" class="aw-input" placeholder="Např. Hermiona Granger" maxlength="80" autocomplete="nickname" required :disabled="busy" /></AwField>
            <div v-if="betaEntry && defaultEnrollment?.fixedUniverse" class="fixed-universe"><span>{{ customerCopy.text('signin.invite-universe') }}</span><p id="account-universe">{{ defaultEnrollment.fixedUniverse }}</p><small>{{ language === 'en' ? 'The invitation sets this universe.' : 'Univerzum je určené pozvánkou.' }}</small></div>
            <AwField v-else id="account-universe" :label="customerCopy.text('signin.invite-universe')" hint="Svět, ze kterého tvoje postava pochází."><input id="account-universe" aria-describedby="account-universe-hint" v-model="invitationUniverse" class="aw-input" placeholder="Např. Harry Potter" maxlength="160" autocomplete="off" required :disabled="busy" /></AwField>
          <AwField v-if="!betaEntry && !linkedInvitation" id="account-invite" :label="customerCopy.text('signin.invite-code-label')" :hint="customerCopy.text('signin.token-hint')"><input id="account-invite" aria-describedby="account-invite-hint" ref="invitationCodeInput" v-model="token" class="aw-input" type="password" autocomplete="off" maxlength="256" required :disabled="busy" /></AwField>

        </template>
          <fieldset v-if="customerEnrollment || enrollment" class="consent-fieldset">
            <legend>Souhlas s osobním zážitkem</legend>
            <label><input v-model="aiConsent" type="checkbox" required :disabled="busy" /> <span>{{ language === 'en' ? 'I agree to processing my character details and conversation audio and text by OpenAI and ElevenLabs for my personal experience and voice conversation.' : 'Souhlasím se zpracováním údajů postavy, zvuku a přepisu rozhovoru službami OpenAI a ElevenLabs pro osobní zážitek a hlasový rozhovor.' }}</span></label>
            <p class="identity-hint">{{ language === 'en' ? 'Without this consent, we will not create your character.' : 'Bez tohoto souhlasu registraci nelze dokončit.' }}</p>
            <details><summary>{{ customerCopy.text('signin.ai-consent-details') }}</summary><p>{{ customerCopy.text('signin.ai-consent-copy') }} <RouterLink to="/privacy">{{ customerCopy.text('shell.privacy') }}</RouterLink></p></details>
          </fieldset>
        <p v-if="customerEnrollment || enrollment" class="identity-hint">{{ customerCopy.text('signin.passkey-before-dialog') }}</p>
        <AwButton type="submit" variant="primary" :busy="busy" :disabled="!accountsEnabled || signingOut || ((customerEnrollment || enrollment) && !aiConsent) || (betaEntry && (enrollmentLoading || !defaultEnrollment?.available)) || (customerEnrollment && launchAllowed !== true)">{{ customerEnrollment ? customerCopy.text('signin.customer-create') : enrollment ? betaEntry ? (language === 'en' ? 'Accept invitation' : 'Přijmout pozvánku') : customerCopy.text('signin.invite-create-key') : customerCopy.text('signin.submit-signin') }}</AwButton>
        <p v-if="!enrollment && !customerEnrollment" class="identity-hint">{{ language === 'en' ? 'This opens system verification. Confirm with your device; no account password is needed.' : 'Otevře se systémové ověření. Potvrď ho na svém zařízení; heslo k účtu nepotřebuješ.' }}</p>
        <AwButton v-if="customerEnrollment" @click="customerEnrollment = false">{{ customerCopy.text('signin.back-to-signin') }}</AwButton>
        <AwButton v-else-if="!enrollment && launchAllowed === true" @click="customerEnrollment = true; error = ''">{{ customerCopy.text('signin.create-customer') }}</AwButton>
      </form>
      <LostAccessHelp v-if="!account && !customerEnrollment" />
      <nav class="signin-help" aria-label="Nápověda"><RouterLink to="/support">{{ customerCopy.text('shell.support') }}</RouterLink><RouterLink to="/privacy">{{ customerCopy.text('shell.privacy') }}</RouterLink></nav>
      <AwButton v-if="!account" class="signin-back" @click="arrival = true; error = ''">{{ language === 'en' ? 'Back' : 'Zpět' }}</AwButton>
    </div>
  </section>
  </SceneShell>
</template>
<style scoped>
.signin-page {
  width: 100%;
  max-width: 34rem;
  min-height: var(--application-content-height, 100dvh);
  margin-inline: auto;
  padding-inline: max(24px, var(--safe-area-inset-left, env(safe-area-inset-left, 0px))) max(24px, var(--safe-area-inset-right, env(safe-area-inset-right, 0px)));
  padding-block: 24px max(32px, var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px)));
  color: var(--wns-text);
}
.arrival-content, .signin-content { display: grid; gap: 24px; min-width: 0; }
.arrival-copy, .signin-intro { display: grid; gap: 12px; }
.arrival-copy p { margin: 0; }
.signin-page h1 { margin: 0; font: var(--wns-weight-heading) clamp(2rem, 8.5vw, 3rem)/1.12 var(--wns-font-family); letter-spacing: -.04em; text-align: start; text-wrap: balance; }
.arrival-content > p { margin: 0; color: var(--wns-muted); }
.signin-art { display: grid; grid-template: minmax(0, 1fr) / minmax(0, 1fr); place-items: center; width: 100%; height: clamp(12rem, 30vh, 18rem); overflow: hidden; border-radius: var(--wns-radius-hero); }
.signin-art--amber { background: var(--wns-amber); }
.signin-art--violet { background: var(--wns-violet); }
.signin-art img { display: block; min-width: 0; min-height: 0; width: 100%; height: 100%; object-fit: contain; padding: 12%; }
.identity-form, .signed-in-actions { display: grid; gap: 20px; min-width: 0; }
.signin-page :deep(.aw-button) { min-height: 48px; border-radius: var(--wns-radius-button); white-space: normal; }
.arrival-content > .aw-button, .identity-form > .aw-button--primary { width: 100%; min-height: 56px; }
.signin-page :deep(.aw-button--secondary) { color: var(--wns-text); border-color: var(--wns-line); background: transparent; }
.signin-page :deep(.aw-input) { min-height: 48px; font-size: 1rem; color: var(--wns-text); background: var(--wns-input-bg); border-color: var(--wns-input-border); border-radius: var(--wns-radius-field); }
.identity-hint { margin: 0; color: var(--wns-muted); font-size: .9rem; }
.consent-fieldset { display: grid; gap: 12px; min-width: 0; margin: 0; padding: 0; border: 0; }
.consent-fieldset legend { padding: 0; margin-bottom: 12px; font-weight: var(--wns-weight-label); }
.consent-fieldset label { display: flex; align-items: flex-start; gap: 12px; min-height: 44px; line-height: 1.55; }
.consent-fieldset input { flex: 0 0 24px; width: 24px; height: 24px; margin: .15rem 0 0; accent-color: var(--wns-mint); }
.consent-fieldset summary { min-height: 44px; padding-block: 10px; cursor: pointer; }
.consent-fieldset details p { margin: 8px 0 0; }
.fixed-universe p { margin: 4px 0; font-weight: var(--wns-weight-label); }
.fixed-universe small { color: var(--wns-muted); }
.signin-options { display: flex; flex-wrap: wrap; gap: 4px 16px; }
.signin-options .aw-button, .signin-code-entry { justify-content: flex-start; padding-inline: 0; border: 0; background: transparent; text-align: start; text-decoration: underline; text-underline-offset: .2em; }
.signin-code-entry { max-width: 11rem; }
.signin-help { display: flex; flex-wrap: wrap; gap: 8px 24px; }
.signin-help a { display: inline-flex; align-items: center; min-height: 44px; }
.signin-back { justify-self: start; }
.signed-in-actions p { margin: 0; }
.signin-content :deep(.lost-access-help) { padding: 0; border: 0; background: transparent; box-shadow: none; }
.signin-content :deep(.lost-access-help summary) { min-height: 44px; padding-block: 10px; }
.signin-page :deep(:focus-visible) { outline: 2px solid var(--wns-focus-ring); outline-offset: 4px; }
.signin-page h1[tabindex='-1']:focus { outline: none; }
@media (max-width: 359px) { .signin-page { padding-inline: max(16px, var(--safe-area-inset-left, env(safe-area-inset-left, 0px))) max(16px, var(--safe-area-inset-right, env(safe-area-inset-right, 0px))); } }
@media (max-height: 550px) { .signin-art { height: 10rem; } }
@media (min-width: 600px) { .signin-page { padding-block: 32px; } }
</style>
