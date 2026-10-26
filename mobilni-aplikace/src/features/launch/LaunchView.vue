<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import AccountDeletion from '../identity/AccountDeletion.vue';
import { account, accountRequest } from '../identity/session';
import { clearInvitationRegistration, getInvitationRegistration, invitationRegistration, reconcileInvitationCharacter, saveInvitationCharacter, setInvitationDraft } from '../identity/invitationRegistration';
import { defaultSignedInDestination } from '../customer/customerJourney';
import { usePrivateCharacterPortrait } from '../customer/privateCharacterPortrait';
import { launchCopy as copy } from './launchCopy';
import PortraitCurtain from './PortraitCurtain.vue';
import CharacterReview from './CharacterReview.vue';
import { invitationVisibleMs, invitationIntroDuration, presentationForeground, selectInvitationPresentation, finishInvitationPresentation } from './launchPresentation';
import type { PrelaunchCharacterResponse as PrelaunchState } from './generatedPrelaunchCharacter';
import brandMark from '../../assets/brand/emblem-small.webp';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { customerFixedUniverse, customerMembershipState, refreshCustomerMembership } from '../../application/launchAccess';
import { isCustomerAccount } from '../../application/access';

interface LaunchPolicy { allowed: boolean; audience: string; launchAt: number | null; serverNow: number }
const router = useRouter();
const language = computed({ get: () => interfaceLanguagePreference.language.value, set: (value: string) => { interfaceLanguagePreference.setLanguage(value); } });
const policy = ref<LaunchPolicy | null>(null);
const state = ref<PrelaunchState | null>(null);
const characterVerified = ref(false);
const name = ref('');
const universe = ref('');
const consent = ref(false);
const betaEnrollmentAvailable = ref<boolean | null>(null);
const membershipRequestMessage = ref('');
const draftProvided = ref(false);
const busy = ref(false);
const error = ref('');
const remaining = ref(0);
const revealed = ref(false);
let characterRead = 0;
let accountEpoch = 0;
let controller: AbortController | undefined;
let timer: ReturnType<typeof setInterval> | undefined;
let policyTimer: ReturnType<typeof setInterval> | undefined;
let pollTimer: ReturnType<typeof setTimeout> | undefined;
let serverTimeAnchor = 0;
let monotonicAnchor = 0;
let lastLaunchCheck = Number.NEGATIVE_INFINITY;
const registration = computed(() => invitationRegistration.value?.owner === account.value?.id ? invitationRegistration.value : null);
const requiresBetaAdmission = computed(() => isCustomerAccount(account.value) && customerMembershipState.value === 'unassigned');
const writing = computed(() => registration.value?.status === 'saving');
const uncertain = computed(() => registration.value?.status === 'uncertain');
const hasPortrait = computed(() => !!portrait.url.value);
const characterIdentity = computed(() => {
  const character = state.value?.character;
  return account.value && character ? JSON.stringify([account.value.id, character.profileId, character.revision]) : '';
});
const portrait = usePrivateCharacterPortrait(() => {
  const character = state.value?.character;
  return account.value && character && state.value?.artwork?.status === 'ready'
    ? { accountId: account.value.id, profileId: character.profileId, revision: character.revision } : null;
});
const phase = computed(() => {
  if (portrait.failed.value || state.value?.artwork?.status === 'failed' || state.value?.character?.status === 'failed') return 'failed';
  if (hasPortrait.value) return 'ready';
  return state.value?.character ? 'preparing' : 'entry';
});
const actualPreparation = computed(() => writing.value || ['queued', 'running'].includes(state.value?.character?.status ?? '') || ['queued', 'pending', 'running'].includes(state.value?.artwork?.status ?? ''));
const statusMessage = computed(() => {
  if (error.value || phase.value === 'failed') return '';
  if (registration.value?.status === 'error') return registration.value.error === 'existing_character' ? copy.value.conflict : copy.value.saveFailed;
  if (uncertain.value) return copy.value.verifying;
  if (writing.value) return copy.value.saving;
  if (account.value && !characterVerified.value) return copy.value.accountLoading;
  if (!state.value?.character) return '';
  if (hasPortrait.value) return '';
  if (registration.value?.status === 'confirmed' && registration.value.writeStarted && invitationVisibleMs.value < invitationIntroDuration) return copy.value.waiting;
  return copy.value.saved;
});
const finalMessage = computed(() => state.value?.artwork?.finalMessage || copy.value.finalFallback);
const timeParts = computed(() => {
  const total = Math.max(0, Math.floor(remaining.value / 1000));
  return [Math.floor(total / 86400), Math.floor(total / 3600) % 24, Math.floor(total / 60) % 60, total % 60];
});
watch(() => account.value?.id, id => {
  accountEpoch++;
  characterRead++;
  portrait.clear();
  state.value = null;
  characterVerified.value = false;
  consent.value = false;
  busy.value = false;
  error.value = '';
  revealed.value = false;
  interfaceLanguagePreference.setScope(id);
  const draft = id ? getInvitationRegistration(id) : null;
  name.value = draft?.name ?? '';
  universe.value = draft?.universe ?? '';
  draftProvided.value = !!draft?.name && !!draft?.universe;
  selectInvitationPresentation(id, draft?.writeStarted ? draft.creationId : undefined);
  if (controller && id) void refreshCharacter(controller.signal).then(beginPolling).catch(() => {});
}, { immediate: true, flush: 'sync' });
watch(customerFixedUniverse, fixed => {
  if (!fixed || state.value?.character) return;
  universe.value = fixed;
  const owner = account.value?.id;
  if (owner && name.value.trim()) setInvitationDraft(owner, name.value, fixed);
});
watch(() => registration.value?.status, status => {
  if (registration.value?.writeStarted && (status === 'saving' || status === 'confirmed' || status === 'uncertain')) selectInvitationPresentation(account.value?.id, registration.value?.creationId);
  if (status === 'confirmed' && controller) void refreshCharacter(controller.signal).then(beginPolling).catch(() => {});
});
watch(hasPortrait, ready => { if (ready) finishInvitationPresentation(); });
watch(() => state.value?.character?.profileId, () => { consent.value = false; revealed.value = false; }, { flush: 'sync' });
function rememberDraft(): void {
  if (account.value && !state.value?.character && !writing.value && !uncertain.value) setInvitationDraft(account.value.id, name.value, universe.value);
}
async function refreshPolicy(): Promise<void> {
  const owner = account.value?.id;
  const value = await accountRequest<LaunchPolicy>('/app/v1/launch');
  if (owner !== account.value?.id || controller?.signal.aborted) return;
  if (typeof value.serverNow !== 'number' || typeof value.allowed !== 'boolean') throw new Error(copy.value.readFailed);
  serverTimeAnchor = value.serverNow;
  monotonicAnchor = performance.now();
  policy.value = value;
  if (value.audience === 'revoked') {
    if (pollTimer) { clearTimeout(pollTimer); pollTimer = undefined; }
    window.dispatchEvent(new Event('customer-access-revoked'));
    return;
  }
  updateCountdown();
  // Server permission always wins over local presentation, including staff/testers.
  if (value.allowed && account.value && (!isCustomerAccount(account.value) || customerMembershipState.value === 'active')) {
    await router.replace(defaultSignedInDestination(account.value));
  }
}
async function refreshBetaEnrollment(): Promise<void> {
  try {
    const result = await accountRequest<{ available: boolean }>('/app/v1/enrollment/default');
    betaEnrollmentAvailable.value = result.available === true;
  } catch { betaEnrollmentAvailable.value = null; }
}
async function refreshCharacter(signal?: AbortSignal): Promise<void> {
  const owner = account.value?.id;
  if (!owner) return;
  const currentRead = ++characterRead;
  const value = await accountRequest<PrelaunchState>('/app/v1/prelaunch/character', 'GET', undefined, signal);
  if (owner !== account.value?.id || currentRead !== characterRead || signal?.aborted) return;
  const previous = state.value?.character;
  state.value = value;
  characterVerified.value = true;
  if (!value.character) {
    if (previous || registration.value?.status === 'confirmed') {
      clearInvitationRegistration(owner);
      name.value = ''; universe.value = ''; consent.value = false; draftProvided.value = false;
      portrait.clear(); selectInvitationPresentation();
    }
    return;
  }
  reconcileInvitationCharacter(owner, value.character);
  name.value = value.character.name;
  universe.value = value.character.universe;
}
function updateCountdown(): void {
  const now = performance.now();
  remaining.value = policy.value?.launchAt == null ? 0 : Math.max(0, policy.value.launchAt - (serverTimeAnchor + now - monotonicAnchor));
  if (remaining.value === 0 && policy.value?.launchAt != null && now - lastLaunchCheck > 15_000) {
    lastLaunchCheck = now; void refreshPolicy().catch(() => {});
  }
}
async function saveCharacter(): Promise<void> {
  if (busy.value || writing.value || !account.value || !consent.value || !name.value.trim() || !universe.value.trim()) return;
  const owner = account.value.id;
  const epoch = accountEpoch;
  busy.value = true; error.value = '';
  setInvitationDraft(owner, name.value, universe.value);
  try {
    await saveInvitationCharacter(owner, true);
    if (epoch !== accountEpoch) return;
    await refreshCharacter(controller?.signal);
    beginPolling();
  } catch { if (epoch === accountEpoch) error.value = copy.value.saveFailed; }
  finally { if (epoch === accountEpoch) busy.value = false; }
}
async function prepareExistingPortrait(): Promise<void> {
  const existing = state.value?.character;
  if (!existing || state.value?.artwork || !consent.value || busy.value) return;
  const epoch = accountEpoch;
  busy.value = true; error.value = '';
  try {
    await accountRequest('/app/v1/prelaunch/character', 'PUT', { expectedRevision: existing.revision, expectedProfileId: existing.profileId, name: existing.name, universe: existing.universe, openAiConsent: true });
    if (epoch !== accountEpoch) return;
    await refreshCharacter(controller?.signal); beginPolling();
  } catch {
    if (epoch !== accountEpoch) return;
    error.value = copy.value.readFailed;
    try { await refreshCharacter(controller?.signal); } catch { /* Preserve recovery. */ }
  } finally { if (epoch === accountEpoch) busy.value = false; }
}
async function retryPortrait(): Promise<void> {
  if (!account.value || busy.value) return;
  const epoch = accountEpoch;
  busy.value = true; error.value = '';
  try {
    if (state.value?.artwork?.status === 'ready') { await portrait.retry(); return; }
    await accountRequest('/app/v1/prelaunch/character/retry', 'POST', {});
    if (epoch === accountEpoch) { await refreshCharacter(controller?.signal); beginPolling(); }
  } catch { if (epoch === accountEpoch) error.value = copy.value.failed; }
  finally { if (epoch === accountEpoch) busy.value = false; }
}
async function retryRead(): Promise<void> {
  if (busy.value) return;
  const epoch = accountEpoch;
  busy.value = true; error.value = '';
  try { await refreshPolicy(); await refreshCharacter(controller?.signal); beginPolling(); }
  catch { if (epoch === accountEpoch) error.value = copy.value.readFailed; }
  finally { if (epoch === accountEpoch) busy.value = false; }
}
async function requestBetaAdmission(): Promise<void> {
  if (busy.value || !requiresBetaAdmission.value || betaEnrollmentAvailable.value !== true) return;
  const owner = account.value?.id;
  if (!owner) return;
  busy.value = true;
  error.value = '';
  membershipRequestMessage.value = '';
  try {
    await accountRequest('/app/v1/enrollment/default/admit', 'POST', {});
    const membership = await refreshCustomerMembership();
    if (owner !== account.value?.id) return;
    if (membership === 'active') {
      membershipRequestMessage.value = language.value === 'en' ? 'Your beta access is ready.' : 'Přístup do bety je připravený.';
      await refreshPolicy();
      await refreshCharacter(controller?.signal);
      beginPolling();
    } else {
      membershipRequestMessage.value = language.value === 'en' ? 'Your access request is being checked. Refresh the status before trying again.' : 'Žádost o přístup se ověřuje. Před dalším pokusem zkontroluj stav.';
    }
  } catch (cause) {
    if (owner !== account.value?.id) return;
    const code = cause && typeof cause === 'object' && 'code' in cause ? cause.code : '';
    const membership = await refreshCustomerMembership();
    if (membership === 'active') {
      membershipRequestMessage.value = language.value === 'en' ? 'Your beta access is ready.' : 'Přístup do bety je připravený.';
      await refreshPolicy();
      await refreshCharacter(controller?.signal);
      beginPolling();
    } else if (code === 'beta_enrollment_closed' || code === 'beta_enrollment_unavailable') {
      await refreshBetaEnrollment();
      membershipRequestMessage.value = language.value === 'en'
        ? 'Enrollment for new participants is currently closed.'
        : 'Přijímání nových účastníků je nyní uzavřené.';
    } else {
      error.value = language.value === 'en'
        ? 'We could not confirm your beta access. Check the account status before retrying.'
        : 'Přístup do bety se nepodařilo potvrdit. Před opakováním ověř stav účtu.';
    }
  } finally { busy.value = false; }
}
function beginPolling(): void {
  if (!account.value || !presentationForeground.value || navigator.onLine === false || pollTimer || controller?.signal.aborted) return;
  if (!actualPreparation.value && !uncertain.value) return;
  pollTimer = setTimeout(async () => {
    pollTimer = undefined;
    try { await refreshCharacter(controller?.signal); }
    catch { error.value = copy.value.readFailed; }
    beginPolling();
  }, 5000);
}
function onOffline(): void {
  error.value = copy.value.readFailed;
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = undefined;
}
function onOnline(): void { void retryRead(); }
function onVisibility(): void {
  if (presentationForeground.value) {
    void refreshPolicy().catch(() => {});
    if (isCustomerAccount(account.value)) {
      void refreshCustomerMembership().then(async membership => {
        if (membership === 'active') await refreshCharacter(controller?.signal).then(beginPolling);
        else if (membership === 'unassigned') await refreshBetaEnrollment();
      }).catch(() => {});
    } else {
      void refreshCharacter(controller?.signal).then(beginPolling).catch(() => {});
    }
    startPolicyPolling();
  } else {
    if (pollTimer) clearTimeout(pollTimer); pollTimer = undefined;
    if (policyTimer) clearInterval(policyTimer); policyTimer = undefined;
  }
}
function startPolicyPolling(): void {
  if (!policyTimer && presentationForeground.value) policyTimer = setInterval(() => { void refreshPolicy().catch(() => {}); }, 15_000);
}
watch(presentationForeground, onVisibility);
onMounted(async () => {
  controller = new AbortController();
  window.addEventListener('offline', onOffline);
  window.addEventListener('online', onOnline);
  try {
    if (isCustomerAccount(account.value) && customerMembershipState.value === null) {
      await refreshCustomerMembership();
    }
    await refreshPolicy();
    if (isCustomerAccount(account.value) && customerMembershipState.value !== 'active') {
      if (customerMembershipState.value === 'unassigned') await refreshBetaEnrollment();
      else if (customerMembershipState.value === null) error.value = copy.value.readFailed;
    } else {
      await refreshCharacter(controller.signal);
      beginPolling();
    }
  }
  catch { error.value = copy.value.readFailed; }
  timer = setInterval(updateCountdown, 1000);
  startPolicyPolling();
});
onBeforeUnmount(() => {
  controller?.abort();
  window.removeEventListener('offline', onOffline);
  window.removeEventListener('online', onOnline);
  if (timer) clearInterval(timer);
  if (policyTimer) clearInterval(policyTimer);
  if (pollTimer) clearTimeout(pollTimer);
  selectInvitationPresentation();
  characterRead++;
});
</script>

<template>
  <section class="launch-page" aria-labelledby="launch-title">
    <header class="launch-masthead">
      <RouterLink to="/" class="launch-wordmark" :aria-label="copy.home"><img :src="brandMark" alt="" width="48" height="48" /><span>Wand<br />Night Stand</span></RouterLink>
      <label class="launch-language"><span class="sr-only">{{ language === 'en' ? 'Interface language' : 'Jazyk rozhraní' }}</span><select v-model="language"><option value="cs">Čeština</option><option value="en">English</option></select></label>
    </header>
    <section v-if="requiresBetaAdmission" class="launch-card beta-admission-request">
      <div class="launch-copy">
        <h1 id="launch-title">{{ language === 'en' ? 'Request beta access' : 'Požádat o přístup do bety' }}</h1>
        <p>{{ language === 'en' ? 'Your account is ready. You can join the current beta group while it is accepting participants.' : 'Tvůj účet je připravený. Do současné beta skupiny můžeš vstoupit, dokud přijímá účastníky.' }}</p>
        <AwNotice v-if="betaEnrollmentAvailable === false" tone="info">{{ language === 'en' ? 'Enrollment for new participants is currently closed.' : 'Přijímání nových účastníků je nyní uzavřené.' }}</AwNotice>
        <p v-else-if="betaEnrollmentAvailable === null" role="status">{{ language === 'en' ? 'We could not check the current beta access rules.' : 'Aktuální pravidla vstupu do bety se nepodařilo ověřit.' }}</p>
        <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
        <p v-if="membershipRequestMessage" role="status">{{ membershipRequestMessage }}</p>
        <AwButton v-if="betaEnrollmentAvailable === true" :busy="busy" :disabled="busy" @click="requestBetaAdmission">{{ language === 'en' ? 'Join the beta group' : 'Vstoupit do beta skupiny' }}</AwButton>
      </div>
    </section>
    <section v-else class="launch-card">
      <div class="launch-copy">
        <h1 id="launch-title">{{ state?.character?.name || (draftProvided ? name : copy.title) }}</h1>
        <p v-if="state?.character || draftProvided" class="launch-universe">{{ state?.character?.universe || universe }}</p>
        <p class="launch-status" role="status" aria-atomic="true">{{ statusMessage }}</p>
        <AwNotice v-if="error" tone="danger" role="alert">{{ error }} <button class="launch-retry" :disabled="busy" @click="retryRead">{{ copy.retry }}</button></AwNotice>
        <AwNotice v-if="phase === 'failed'" tone="warning" role="alert">{{ copy.failed }} <button class="launch-retry" :disabled="busy || portrait.pending.value" @click="retryPortrait">{{ copy.retry }}</button></AwNotice>
        <template v-if="!account"><RouterLink class="aw-button aw-button--primary" to="/signin">{{ copy.invitation }}</RouterLink></template>
        <form v-else-if="characterVerified && !state?.character && !writing" class="launch-form" @submit.prevent="saveCharacter">
          <p v-if="draftProvided && !uncertain">{{ copy.accountReady }}</p>
          <template v-if="!draftProvided">
            <AwField id="character-name" :label="copy.name" :hint="copy.nameHint"><input id="character-name" aria-describedby="character-name-hint" v-model="name" class="aw-input" maxlength="80" autocomplete="nickname" required @input="rememberDraft" /></AwField>
            <AwField id="character-universe" :label="copy.universe" :hint="customerFixedUniverse ? (language === 'en' ? 'This universe is fixed for your beta group.' : 'Univerzum je pevně určené pro tvoji beta skupinu.') : copy.universeHint"><input id="character-universe" aria-describedby="character-universe-hint" v-model="universe" class="aw-input" maxlength="160" autocomplete="off" required :readonly="!!customerFixedUniverse" @input="rememberDraft" /></AwField>
          </template>
          <label class="launch-consent"><input v-model="consent" type="checkbox" /><span>{{ copy.consent }}</span></label>
          <details class="launch-data-details"><summary>{{ copy.privacyDetailsTitle }}</summary><p>{{ copy.privacyDetails }}</p><RouterLink to="/privacy">{{ copy.privacy }}</RouterLink></details>
          <AwButton type="submit" :busy="busy" :disabled="!consent">{{ copy.portrait }}</AwButton>
        </form>
        <form v-else-if="state?.character && !state.artwork" class="launch-form" @submit.prevent="prepareExistingPortrait">
          <p>{{ copy.missingPortrait }}</p>
          <label class="launch-consent"><input v-model="consent" type="checkbox" /><span>{{ copy.consent }}</span></label>
          <details class="launch-data-details"><summary>{{ copy.privacyDetailsTitle }}</summary><p>{{ copy.privacyDetails }}</p><RouterLink to="/privacy">{{ copy.privacy }}</RouterLink></details>
          <AwButton type="submit" :busy="busy" :disabled="!consent">{{ copy.portrait }}</AwButton>
        </form>
        <CharacterReview v-if="state?.character" :profile-id="state.character.profileId" :revision="state.character.revision" :status="state.character.status" @saved="retryRead" />
        <p v-if="state?.artwork?.errorCode" role="alert">{{ language === 'en' ? 'Error code' : 'Kód chyby' }}: {{ state.artwork.errorCode }}</p>
        <section class="countdown" :aria-label="copy.countdownLabel">
          <p>{{ copy.countdown }}</p>
          <div v-if="policy?.launchAt != null" class="countdown-grid" aria-live="off"><div v-for="(part, index) in timeParts" :key="index"><strong>{{ index ? String(part).padStart(2, '0') : part }}</strong><span>{{ [copy.days, copy.hours, copy.minutes, copy.seconds][index] }}</span></div></div>
          <p v-else class="countdown-wait">{{ copy.unscheduled }}</p>
        </section>
      </div>
      <PortraitCurtain class="launch-art" :image="portrait.url.value" :identity="characterIdentity" :alt="copy.portraitAlt" :preparing="actualPreparation" @revealed="revealed = $event" @failed="portrait.fail()" />
      <div class="launch-text">
        <div v-if="revealed && hasPortrait" class="launch-final">
          <template v-if="state?.artwork?.welcome"><p class="launch-joke">{{ state.artwork.welcome.joke }}</p><p v-if="state.artwork.welcome.identity" class="launch-identity">{{ state.artwork.welcome.identity }}</p><p v-if="state.artwork.welcome.teaser" class="launch-teaser">{{ state.artwork.welcome.teaser }}</p></template>
          <p v-else class="launch-joke">{{ finalMessage }}</p>
        </div>
        <p v-else class="launch-generic">{{ copy.loading }}</p>
      </div>
    </section>
    <footer class="launch-footer"><details v-if="account" class="launch-account-options"><summary>{{ copy.account }}</summary><AccountDeletion /></details><nav :aria-label="copy.account"><RouterLink to="/privacy">{{ copy.privacy }}</RouterLink><RouterLink to="/support">{{ copy.support }}</RouterLink></nav></footer>
  </section>
</template>

<style scoped>
.launch-page { max-width: 88rem; min-height: 100dvh; margin: 0 auto; padding: max(1.5rem, var(--safe-area-inset-top, env(safe-area-inset-top, 0px))) max(1.5rem, env(safe-area-inset-right, 0px)) max(1rem, env(safe-area-inset-bottom, 0px)) max(1.5rem, env(safe-area-inset-left, 0px)); color: var(--aw-ivory); }
.launch-masthead { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 1rem; padding-block: .5rem 2rem; }
.launch-wordmark { min-height: 44px; display: flex; align-items: center; gap: .8rem; color: var(--aw-ivory); text-decoration: none; font-family: var(--aw-font-display); font-size: 1.8rem; font-weight: 500; line-height: .9; letter-spacing: -.025em; }
.launch-wordmark img { flex-shrink: 0; }
.launch-wordmark span { white-space: nowrap; }
.launch-language select { font: inherit; font-size: .875rem; color: var(--aw-ivory); background: var(--aw-surface); border: 1px solid var(--aw-border-subtle); border-radius: 8px; min-height: 44px; max-width: 7rem; padding: .5rem; }
.launch-card { display: grid; grid-template-columns: minmax(0, .95fr) minmax(0, 1.05fr); grid-template-areas: 'art copy' 'art text'; align-items: center; column-gap: clamp(2rem, 6vw, 7rem); }
.launch-art { grid-area: art; }
.launch-copy { grid-area: copy; min-width: 0; max-width: 35rem; }
.launch-copy h1 { font-family: var(--aw-font-display); font-size: clamp(2.75rem, 5.8vw, 5.75rem); font-weight: 500; line-height: 1.05; letter-spacing: -.025em; margin: 0; overflow-wrap: anywhere; }
.launch-universe { color: var(--aw-muted); margin-block: .5rem 1rem; overflow-wrap: anywhere; }
.launch-status { color: var(--aw-link); line-height: 1.5; min-height: 3em; margin-block: .5rem; font-size: .875rem; }
.launch-text { grid-area: text; min-width: 0; padding-top: 1.5rem; align-self: start; }
.launch-text p { overflow-wrap: anywhere; margin: 0; }
.launch-final { display: grid; gap: 1rem; }
.launch-joke, .launch-generic { color: var(--aw-ivory); font-size: 1.125rem; line-height: 1.5; }
.launch-identity { font-size: 1rem; line-height: 1.6; }
.launch-teaser { color: var(--aw-muted); font-size: .875rem; line-height: 1.6; }
.launch-form { display: grid; gap: 1rem; margin-block: 1rem 1.5rem; }
.launch-form p { margin: 0; }
.launch-form .aw-input { min-height: 48px; font-size: 1rem; }
.launch-consent { display: flex; gap: .75rem; align-items: start; min-height: 44px; color: var(--aw-ivory); font-size: .875rem; line-height: 1.6; cursor: pointer; }
.launch-consent input { flex-shrink: 0; width: 24px; height: 24px; margin: .1rem 0 0; accent-color: var(--aw-accent); }
.launch-data-details { color: var(--aw-muted); font-size: .875rem; line-height: 1.6; }
.launch-data-details summary { min-height: 44px; padding-block: .6rem; cursor: pointer; }
.launch-data-details a { display: inline-flex; min-height: 44px; align-items: center; }
.countdown { margin-block: 1.5rem 0; }
.countdown > p:first-child { margin: 0 0 .75rem; }
.countdown-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: .5rem; padding-bottom: 1.5rem; }
.countdown-grid > div { display: grid; gap: .25rem; min-width: 0; }
.countdown-grid strong { font-weight: 500; font-variant-numeric: lining-nums tabular-nums; font-size: clamp(1.75rem, 4vw, 3.2rem); letter-spacing: -.03em; line-height: 1.1; overflow-wrap: anywhere; }
.countdown-grid span, .countdown-wait { color: var(--aw-muted); font-size: .875rem; line-height: 1.6; }
.countdown-wait { margin-block: 0 1.5rem; }
.launch-retry { color: inherit; text-decoration: underline; text-underline-offset: .2em; background: none; border: 0; cursor: pointer; min-height: 44px; padding: .5rem; font: inherit; }
.aw-button { display: inline-flex; align-items: center; justify-content: center; min-height: 48px; padding: .85rem 1.25rem; border-radius: 8px; text-decoration: none; }
.aw-button--primary { color: var(--aw-on-accent); background: var(--aw-accent); }
.launch-footer { margin-top: 1.5rem; display: flex; align-items: start; justify-content: space-between; gap: 1rem; color: var(--aw-muted); font-size: .875rem; }
.launch-footer nav { display: flex; flex-wrap: wrap; column-gap: 1rem; }
.launch-footer a { display: inline-flex; align-items: center; min-height: 44px; color: var(--aw-muted); }
.launch-account-options { max-width: 100%; }
.launch-account-options summary { cursor: pointer; min-height: 44px; padding-block: .65rem; }
:focus-visible { outline: 2px solid var(--aw-info); outline-offset: 3px; }
@media (max-width: 760px) {
  .launch-page { padding-inline: max(20px, env(safe-area-inset-left, 0px)) max(20px, env(safe-area-inset-right, 0px)); }
  .launch-masthead { padding-block: 0 1.5rem; }
  .launch-wordmark { font-size: 1.55rem; }
  .launch-wordmark img { width: 40px; height: 40px; }
  .launch-card { display: flex; flex-direction: column; gap: 0; align-items: stretch; }
  .launch-copy { max-width: none; }
  .launch-copy h1 { font-size: 2.25rem; line-height: 1.1; }
  .launch-art { flex-shrink: 0; }
  .launch-footer { flex-wrap: wrap; }
}
@media (max-width: 340px) { .launch-page { padding-inline: max(16px, env(safe-area-inset-left, 0px)) max(16px, env(safe-area-inset-right, 0px)); } }
</style>
