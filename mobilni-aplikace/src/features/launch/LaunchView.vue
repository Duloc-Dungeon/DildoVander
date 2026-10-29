<script setup lang="ts">
import PushNotifications from '../identity/PushNotifications.vue';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import AccountDeletion from '../identity/AccountDeletion.vue';
import CharacterLabelEditor from '../customer/CharacterLabelEditor.vue';
import { characterDisplayName } from '../customer/characterLabel';
import { account, accountDeletionRequested, accountRequest, signOut, signingOut } from '../identity/session';
import { clearInvitationRegistration, getInvitationRegistration, invitationRegistration, reconcileInvitationCharacter, saveInvitationCharacter, setInvitationDraft } from '../identity/invitationRegistration';
import { defaultSignedInDestination } from '../customer/customerJourney';
import { usePrivateCharacterPortrait } from '../customer/privateCharacterPortrait';
import { launchCopy as copy } from './launchCopy';
import { characterWelcomeText } from './characterWelcomeText';
import PortraitCurtain from './PortraitCurtain.vue';
import CharacterEntrance from './CharacterEntrance.vue';
import PrelaunchWand from './PrelaunchWand.vue';
import SceneShell from '../../components/scene/SceneShell.vue';
import PaperSurface from '../../components/scene/PaperSurface.vue';
import CharacterCountdown from '../../components/scene/CharacterCountdown.vue';
import { invitationVisibleMs, invitationIntroDuration, presentationForeground, selectInvitationPresentation, finishInvitationPresentation } from './launchPresentation';
import type { PrelaunchCharacterResponse as PrelaunchState } from './generatedPrelaunchCharacter';

import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { customerFixedUniverse, customerMembershipState, refreshCustomerMembership } from '../../application/launchAccess';
import { isCustomerAccount } from '../../application/access';

interface LaunchPolicy { allowed: boolean; audience: string; launchAt: number | null; serverNow: number }
const router = useRouter();
const language = computed(() => interfaceLanguagePreference.language.value);
const policy = ref<LaunchPolicy | null>(null);
const state = ref<PrelaunchState | null>(null);
const characterVerified = ref(false);
const name = ref('');
const universe = ref('');
const consent = computed(() => account.value?.openAiConsent === true);
const betaEnrollmentAvailable = ref<boolean | null>(null);
const membershipRequestMessage = ref('');
const draftProvided = ref(false);
const busy = ref(false);
const error = ref('');
const readError = ref('');
const displayedError = computed(() => error.value || readError.value);
const remaining = ref(0);
const revealed = ref(false);
const workshopOpen = ref(false);
const wandComplete = ref(false);
const receptionSteps = ['Postava', 'Portrét', 'Hůlka', 'Čekárna'];
const receptionStep = computed(() => wandComplete.value ? 3 : canPrepareWand.value ? 2 : state.value?.character ? 1 : 0);
const canPrepareWand = computed(() => isCustomerAccount(account.value)
  && customerMembershipState.value === 'active' && !!state.value?.character
  && (revealed.value && hasPortrait.value || phase.value === 'failed'));
const groupOpening = ref(false);
const groupPresenting = ref(false);
const groupBusy = ref(false);
const entranceRetryAttempted = ref(false);
const entranceRetrying = ref(false);
const formFocused = ref(false);
let characterRead = 0;
let policyRead = 0;
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
  if (portrait.failed.value || state.value?.artwork?.status === 'failed') return 'failed';
  if (hasPortrait.value) return 'ready';
  return state.value?.character ? 'preparing' : 'entry';
});
const actualPreparation = computed(() => writing.value || ['queued', 'pending', 'running'].includes(state.value?.artwork?.status ?? ''));
const statusMessage = computed(() => {
  if (displayedError.value || phase.value === 'failed') return '';
  if (registration.value?.status === 'error') return registration.value.error === 'existing_character' ? copy.value.conflict : copy.value.saveFailed;
  if (uncertain.value) return copy.value.verifying;
  if (writing.value) return copy.value.saving;
  if (account.value && !characterVerified.value) return copy.value.accountLoading;
  if (!state.value?.character) return '';
  if (hasPortrait.value) return '';
  if (registration.value?.status === 'confirmed' && registration.value.writeStarted && invitationVisibleMs.value < invitationIntroDuration) return copy.value.waiting;
  return copy.value.saved;
});
const finalMessage = computed(() => characterWelcomeText(state.value?.artwork?.finalMessage) || copy.value.finalFallback);
const receptionJoke = computed(() => {
  const artwork = state.value?.artwork;
  const welcomeJoke = characterWelcomeText(artwork?.welcome?.joke);
  const loadingJoke = characterWelcomeText(artwork?.loadingMessage);
  if (canPrepareWand.value) return welcomeJoke || loadingJoke || finalMessage.value;
  return loadingJoke || welcomeJoke
    || 'Postava dostane vlastní tvář. Zrcadlo si zatím vyjednává právo na jiný názor.';
});
const receptionStatus = computed(() => {
  if (displayedError.value || phase.value === 'failed') return '';
  if (writing.value || uncertain.value || !state.value?.character) return statusMessage.value;
  if (statusMessage.value === copy.value.waiting) return statusMessage.value;
  if (wandComplete.value) return 'Postava i hůlka jsou připravené. Tady počkáš na otevření večera.';
  if (canPrepareWand.value) return 'Postava je připravená. Teď můžeš do dílny pro hůlku.';
  if (hasPortrait.value) return '';
  return actualPreparation.value || portrait.pending.value
    ? 'Postava je uložená. Ještě připravujeme její portrét.'
    : 'Postava je uložená. Portrét zatím není připravený.';
});
const portraitSource = computed(() => {
  const source = state.value?.artwork?.source;
  if (!source) return null;
  try {
    const url = new URL(source.url);
    const licenseUrl = new URL(source.licenseUrl);
    if (url.protocol !== 'https:' || url.hostname !== 'commons.wikimedia.org' || url.port || url.username || url.password || !url.pathname.startsWith('/wiki/File:')
      || licenseUrl.protocol !== 'https:' || licenseUrl.port || licenseUrl.username || licenseUrl.password
      || !(licenseUrl.hostname === 'commons.wikimedia.org' && licenseUrl.pathname.startsWith('/wiki/File:')
        || licenseUrl.hostname === 'creativecommons.org' && licenseUrl.pathname.startsWith('/licenses/'))) return null;
    return source;
  } catch {
    return null;
  }
});
const timeParts = computed(() => {
  const total = Math.max(0, Math.floor(remaining.value / 1000));
  return [Math.floor(total / 86400), Math.floor(total / 3600) % 24, Math.floor(total / 60) % 60, total % 60];
});
const launchTimeLabel = computed(() => {
  if (policy.value?.launchAt == null) return '';
  return new Intl.DateTimeFormat(language.value === 'en' ? 'en-GB' : 'cs-CZ', {
    dateStyle: 'long',
    timeStyle: 'short',
  }).format(policy.value.launchAt);
});
watch(() => account.value?.id, id => {
  accountEpoch++;
  policyRead++;
  policy.value = null;
  remaining.value = 0;
  lastLaunchCheck = Number.NEGATIVE_INFINITY;
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = undefined;
  groupOpening.value = false;
  groupPresenting.value = false;
  groupBusy.value = false;
  entranceRetryAttempted.value = false;
  entranceRetrying.value = false;
  characterRead++;
  portrait.clear();
  state.value = null;
  characterVerified.value = false;
  busy.value = false;
  error.value = '';
  revealed.value = false;
  interfaceLanguagePreference.setScope(id);
  const draft = id ? getInvitationRegistration(id) : null;
  name.value = draft?.name ?? '';
  universe.value = draft?.universe ?? '';
  draftProvided.value = !!draft?.name && !!draft?.universe;
  selectInvitationPresentation(id, draft?.writeStarted ? draft.creationId : undefined);
  readError.value = '';
  if (controller && id) void retryRead();
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
watch(characterIdentity, () => { revealed.value = false; workshopOpen.value = false; wandComplete.value = false; }, { flush: 'sync' });
watch(canPrepareWand, available => { if (!available) workshopOpen.value = false; }, { flush: 'sync' });
function onWorkshopOpen(open: boolean): void {
  workshopOpen.value = open;
  if (!open) void refreshPolicy().catch(() => {});
}
function rememberDraft(): void {
  if (account.value && !state.value?.character && !writing.value && !uncertain.value) setInvitationDraft(account.value.id, name.value, universe.value);
}
function formFocusOut(event: FocusEvent): void {
  const form = event.currentTarget;
  if (form instanceof HTMLElement && event.relatedTarget instanceof Node && form.contains(event.relatedTarget)) return;
  formFocused.value = false;
}
async function refreshPolicy(): Promise<void> {
  const owner = account.value?.id;
  const currentRead = ++policyRead;
  const value = await accountRequest<LaunchPolicy>('/app/v1/launch');
  if (owner !== account.value?.id || currentRead !== policyRead || controller?.signal.aborted) return;
  if (!Number.isFinite(value.serverNow) || typeof value.allowed !== 'boolean'
    || value.launchAt != null && !Number.isFinite(value.launchAt)) throw new Error(copy.value.readFailed);
  serverTimeAnchor = value.serverNow;
  monotonicAnchor = performance.now();
  policy.value = value;
  if (value.audience === 'revoked') {
    groupOpening.value = false;
    if (pollTimer) { clearTimeout(pollTimer); pollTimer = undefined; }
    window.dispatchEvent(new Event('customer-access-revoked'));
    return;
  }
  if (!value.allowed) {
    const wasOpening = groupOpening.value;
    groupOpening.value = false;
    groupPresenting.value = false;
    // A pause may arrive before the opening scene ever fetched a character.
    // Reconcile the waiting scene with reads, never a second enrollment/save.
    if (wasOpening && account.value && customerMembershipState.value === 'active') {
      try { await refreshCharacter(controller?.signal); beginPolling(); }
      catch { if (owner === account.value?.id) readError.value = copy.value.readFailed; }
    }
  }
  updateCountdown();
  // Server permission always wins over local presentation, including staff/testers.
  if (value.allowed && account.value && !isCustomerAccount(account.value)) {
    // Operational accounts keep their established destination. The consumer
    // group-opening presentation must not become a staff onboarding detour.
    await router.replace(defaultSignedInDestination(account.value));
    return;
  }
  if (value.allowed && account.value && customerMembershipState.value === 'active' && !workshopOpen.value) {
    const seenKey = `wand.group-opening-seen.v1.${account.value.id}`;
    let alreadySeen = false;
    try { alreadySeen = sessionStorage.getItem(seenKey) === 'seen'; } catch { /* Static presentation remains safe without storage. */ }
    if (alreadySeen) await router.replace(defaultSignedInDestination(account.value));
    else groupOpening.value = true;
  }
}
async function enterGroup(continueToApp = false): Promise<void> {
  const owner = account.value;
  if (!owner || groupBusy.value || !policy.value?.allowed || (isCustomerAccount(owner) && customerMembershipState.value !== 'active')) return;
  const epoch = accountEpoch;
  groupBusy.value = true;
  error.value = '';
  try {
    await refreshPolicy();
    if (epoch !== accountEpoch || !policy.value?.allowed || account.value?.id !== owner.id || customerMembershipState.value !== 'active') return;
    if (!continueToApp) {
      entranceRetryAttempted.value = false;
      groupPresenting.value = true;
      void refreshCharacter(controller?.signal)
        .catch(() => { if (epoch === accountEpoch) readError.value = copy.value.readFailed; })
        .then(() => { if (epoch === accountEpoch) beginPolling(); });
      return;
    }
    const failure = await router.replace(defaultSignedInDestination(owner));
    if (epoch !== accountEpoch) return;
    if (failure) { error.value = copy.value.readFailed; return; }
    if (account.value?.id === owner.id) {
      try { sessionStorage.setItem(`wand.group-opening-seen.v1.${owner.id}`, 'seen'); } catch { /* Restricted storage can safely replay the introduction. */ }
    }
  }
  catch { if (epoch === accountEpoch) error.value = copy.value.readFailed; }
  finally { if (epoch === accountEpoch) groupBusy.value = false; }
}
async function retryEntrance(): Promise<void> {
  if (entranceRetryAttempted.value || entranceRetrying.value) return;
  const epoch = accountEpoch;
  entranceRetryAttempted.value = true;
  entranceRetrying.value = true;
  try {
    await retryRead();
    if (epoch !== accountEpoch || controller?.signal.aborted || readError.value) return;
    if (state.value?.artwork?.status === 'failed' && consent.value) await retryPortrait();
    else if (state.value?.artwork?.status === 'ready' && portrait.failed.value) await portrait.retry();
  } catch {
    if (epoch === accountEpoch) readError.value = copy.value.readFailed;
  } finally { if (epoch === accountEpoch) entranceRetrying.value = false; }
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
  readError.value = '';
  if (!value.character) {
    if (previous || registration.value?.status === 'confirmed') {
      clearInvitationRegistration(owner);
      name.value = ''; universe.value = ''; draftProvided.value = false;
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
    await accountRequest('/app/v1/prelaunch/character/retry', 'POST', { expectedRevision: existing.revision, expectedProfileId: existing.profileId });
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
    await accountRequest('/app/v1/prelaunch/character/retry', 'POST', {
      expectedProfileId: state.value?.character?.profileId, expectedRevision: state.value?.character?.revision,
    });
    if (epoch === accountEpoch) { await refreshCharacter(controller?.signal); beginPolling(); }
  } catch { if (epoch === accountEpoch) error.value = copy.value.failed; }
  finally { if (epoch === accountEpoch) busy.value = false; }
}
async function retryRead(): Promise<void> {
  if (busy.value) return;
  const epoch = accountEpoch;
  busy.value = true; error.value = ''; readError.value = '';
  try {
    if (isCustomerAccount(account.value)) await refreshCustomerMembership();
    if (epoch !== accountEpoch || controller?.signal.aborted) return;
    await refreshPolicy();
    if (epoch !== accountEpoch || controller?.signal.aborted) return;
    if (isCustomerAccount(account.value) && customerMembershipState.value !== 'active') {
      if (customerMembershipState.value === 'unassigned') await refreshBetaEnrollment();
      else if (customerMembershipState.value === null) readError.value = copy.value.readFailed;
      return;
    }
    if (!policy.value?.allowed || groupPresenting.value) await refreshCharacter(controller?.signal);
    beginPolling();
  }
  catch { if (epoch === accountEpoch && !controller?.signal.aborted) readError.value = copy.value.readFailed; }
  finally { if (epoch === accountEpoch) busy.value = false; }
}
async function leaveAccount(): Promise<void> {
  if (signingOut.value) return;
  try {
    await signOut();
    await router.replace('/signin');
  } catch {
    await router.replace({ path: '/signin', query: { logout: 'unconfirmed' } });
  }
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
  const entranceWaiting = groupPresenting.value && !hasPortrait.value && !portrait.failed.value && state.value?.artwork?.status !== 'failed';
  if (!actualPreparation.value && !uncertain.value && !entranceWaiting) return;
  pollTimer = setTimeout(async () => {
    pollTimer = undefined;
    const epoch = accountEpoch;
    try { await refreshCharacter(controller?.signal); }
    catch { if (epoch === accountEpoch && !controller?.signal.aborted) readError.value = copy.value.readFailed; }
    beginPolling();
  }, 5000);
}
function onOffline(): void {
  readError.value = copy.value.readFailed;
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = undefined;
}
function onOnline(): void { void retryRead(); }
function onVisibility(): void {
  if (presentationForeground.value) {
    void retryRead();
    startPolicyPolling();
  } else {
    if (pollTimer) clearTimeout(pollTimer); pollTimer = undefined;
    if (policyTimer) clearInterval(policyTimer); policyTimer = undefined;
  }
}
function startPolicyPolling(): void {
  if (!policyTimer && presentationForeground.value) policyTimer = setInterval(() => {
    if (readError.value) void retryRead();
    else void refreshPolicy().catch(() => {});
  }, 15_000);
}
watch(presentationForeground, onVisibility);
onMounted(async () => {
  controller = new AbortController();
  window.addEventListener('offline', onOffline);
  window.addEventListener('online', onOnline);
  await retryRead();
  if (controller.signal.aborted) return;
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
  <SceneShell v-if="groupOpening" class="launch-page group-opening" :labelled-by="groupPresenting ? 'entrance-title' : 'group-opening-title'" :active="presentationForeground">
    <CharacterEntrance v-if="groupPresenting" :character="state?.character ?? null" :artwork="state?.artwork ?? null" :image="portrait.url.value" :failed="portrait.failed.value" :read-failed="!!displayedError" :active="presentationForeground" :busy="groupBusy" :retrying="entranceRetrying" :retry-attempted="entranceRetryAttempted" @continue="enterGroup(true)" @retry="retryEntrance" @failed="portrait.fail()">
      <AwNotice v-if="displayedError" tone="danger" role="alert">{{ displayedError }}</AwNotice>
      <p v-if="portraitSource && hasPortrait" class="launch-attribution">Obrázek z internetu: <a :href="portraitSource.url" target="_blank" rel="noopener noreferrer">{{ portraitSource.title }}</a> · {{ portraitSource.author }} · <a :href="portraitSource.licenseUrl" target="_blank" rel="noopener noreferrer">{{ portraitSource.license }}</a></p>
    </CharacterEntrance>
    <template v-else>
      <div class="group-opening-art" data-scene-enter aria-hidden="true"><span>W</span></div>
      <section class="group-opening-copy">
      <h1 id="group-opening-title">{{ language === 'en' ? 'The evening begins.' : 'Večer začíná.' }}</h1>
      <AwNotice v-if="displayedError" tone="danger" role="alert">{{ displayedError }} <button class="launch-retry" :disabled="groupBusy" @click="enterGroup(false)">{{ copy.retry }}</button></AwNotice>
      <AwButton variant="primary" :busy="groupBusy" :disabled="groupBusy" @click="enterGroup(false)">Vstoupit</AwButton>
    </section>
    </template>
    <footer class="launch-footer"><AwButton v-if="account" :busy="signingOut" :disabled="signingOut" @click="leaveAccount">{{ copy.signOut }}</AwButton><details class="launch-account-options"><summary>{{ copy.account }}</summary><PushNotifications v-if="account && !accountDeletionRequested" /><AccountDeletion /></details><nav :aria-label="copy.account"><RouterLink to="/privacy">{{ copy.privacy }}</RouterLink><RouterLink to="/support">{{ copy.support }}</RouterLink></nav></footer>
  </SceneShell>
  <SceneShell v-else class="launch-page" :class="{ 'keyboard-priority': formFocused }" :labelled-by="workshopOpen ? 'prelaunch-wand-title' : 'launch-title'">
    <header class="launch-masthead">
      <RouterLink to="/" class="launch-wordmark" :aria-label="copy.home"><span>Wand<br />Night Stand</span></RouterLink>
      <AwButton v-if="account" class="launch-signout" :busy="signingOut" :disabled="signingOut" @click="leaveAccount">{{ copy.signOut }}</AwButton>
    </header>
    <section v-if="requiresBetaAdmission" class="launch-card beta-admission-request">
      <PaperSurface class="launch-copy">
        <h1 id="launch-title">{{ language === 'en' ? 'Request beta access' : 'Požádat o přístup do bety' }}</h1>
        <p>{{ language === 'en' ? 'Your account is ready. You can join the current beta group while it is accepting participants.' : 'Tvůj účet je připravený. Do současné beta skupiny můžeš vstoupit, dokud přijímá účastníky.' }}</p>
        <AwNotice v-if="betaEnrollmentAvailable === false" tone="info">{{ language === 'en' ? 'Enrollment for new participants is currently closed.' : 'Přijímání nových účastníků je nyní uzavřené.' }}</AwNotice>
        <p v-else-if="betaEnrollmentAvailable === null" role="status">{{ language === 'en' ? 'We could not check the current beta access rules.' : 'Aktuální pravidla vstupu do bety se nepodařilo ověřit.' }}</p>
        <AwNotice v-if="displayedError" tone="danger" role="alert">{{ displayedError }} <button class="launch-retry" :disabled="busy" @click="retryRead">{{ copy.retry }}</button></AwNotice>
        <p v-if="membershipRequestMessage" role="status">{{ membershipRequestMessage }}</p>
        <AwButton v-if="betaEnrollmentAvailable === true" variant="primary" :busy="busy" :disabled="busy" @click="requestBetaAdmission">{{ language === 'en' ? 'Join the beta group' : 'Vstoupit do beta skupiny' }}</AwButton>
      </PaperSurface>
    </section>
    <template v-else>
    <ol v-if="writing || state?.character" v-show="!workshopOpen" class="reception-steps" aria-label="Příprava na večer">
      <li v-for="(step, index) in receptionSteps" :key="step" :class="{ 'is-current': index === receptionStep, 'is-done': index < receptionStep }" :aria-current="index === receptionStep ? 'step' : undefined">
        <span class="reception-step-mark" aria-hidden="true" />
        <span>{{ step }}<small v-if="index === 1 && phase === 'failed'">Bez portrétu</small><span v-if="index < receptionStep" class="sr-only">{{ index === 1 && phase === 'failed' ? ' — přeskočeno' : ' — hotovo' }}</span></span>
      </li>
    </ol>
    <PushNotifications v-if="wandComplete && !workshopOpen" context="waiting" class="launch-push-offer" />
    <PrelaunchWand v-if="canPrepareWand" :key="characterIdentity" @open-change="onWorkshopOpen" @complete-change="wandComplete = $event" />
    <section v-show="!workshopOpen" class="launch-card" :class="{ 'launch-card--reception': writing || state?.character }">
      <PaperSurface class="launch-copy">
        <h1 v-if="!state?.character" id="launch-title">{{ readError && account ? copy.waitingTitle : writing ? 'Tvoje postava se připravuje.' : draftProvided ? name : copy.title }}</h1>
        <AwNotice v-if="displayedError" tone="danger" role="alert">{{ displayedError }} <button class="launch-retry" :disabled="busy" @click="retryRead">{{ copy.retry }}</button></AwNotice>
        <AwNotice v-if="phase === 'failed'" tone="warning" role="alert">{{ copy.failed }} <button class="launch-retry" :disabled="busy || portrait.pending.value" @click="retryPortrait">{{ copy.retry }}</button></AwNotice>
        <template v-if="!account"><RouterLink class="aw-button aw-button--primary" to="/signin">{{ copy.invitation }}</RouterLink></template>
        <form v-else-if="characterVerified && !state?.character && !writing" class="launch-form" @submit.prevent="saveCharacter" @focusin="formFocused = true" @focusout="formFocusOut">
          <p v-if="draftProvided && !uncertain">{{ copy.accountReady }}</p>
          <template v-if="!draftProvided">
            <AwField id="character-name" :label="copy.name" :hint="copy.nameHint"><input id="character-name" aria-describedby="character-name-hint" v-model="name" class="aw-input" maxlength="80" autocomplete="nickname" required @input="rememberDraft" /></AwField>
            <div v-if="customerFixedUniverse" class="fixed-universe"><span>{{ copy.universe }}</span><strong>{{ customerFixedUniverse }}</strong><p>{{ language === 'en' ? 'Set by your invitation group.' : 'Určeno skupinou, která tě pozvala.' }}</p></div>
            <AwField v-else id="character-universe" :label="copy.universe" :hint="copy.universeHint"><input id="character-universe" aria-describedby="character-universe-hint" v-model="universe" class="aw-input" maxlength="160" autocomplete="off" required @input="rememberDraft" /></AwField>
          </template>
          <AwButton type="submit" variant="primary" :busy="busy" :disabled="!consent">{{ copy.portrait }}</AwButton>
        </form>
        <form v-else-if="state?.character && !state.artwork" class="launch-form" @submit.prevent="prepareExistingPortrait" @focusin="formFocused = true" @focusout="formFocusOut">
          <p>{{ copy.missingPortrait }}</p>
          <AwButton type="submit" variant="primary" :busy="busy" :disabled="!consent">{{ copy.portrait }}</AwButton>
        </form>
        <header v-if="state?.character" class="reception-identity">
          <h1 id="launch-title">{{ characterDisplayName(state.character.name, state.character.label) }}</h1>
          <p>{{ state.character.universe }}</p>
        </header>
        <div v-if="writing || state?.character" class="reception-introduction" :class="{ 'launch-final': canPrepareWand }">
          <p class="launch-status" role="status" aria-atomic="true">{{ receptionStatus }}</p>
          <p class="launch-joke">{{ receptionJoke }}</p>
          <template v-if="canPrepareWand && state?.artwork?.welcome">
            <p v-if="characterWelcomeText(state.artwork.welcome.identity)" class="launch-identity">{{ characterWelcomeText(state.artwork.welcome.identity) }}</p>
            <p v-if="characterWelcomeText(state.artwork.welcome.teaser)" class="launch-teaser">{{ characterWelcomeText(state.artwork.welcome.teaser) }}</p>
          </template>
          <p v-if="portraitSource && revealed && hasPortrait" class="launch-attribution">Obrázek z internetu (upraveno pro portrét): <a :href="portraitSource.url" target="_blank" rel="noopener noreferrer">{{ portraitSource.title }}</a> — {{ portraitSource.author }}, <a :href="portraitSource.licenseUrl" target="_blank" rel="noopener noreferrer">{{ portraitSource.license }}</a></p>
        </div>
      </PaperSurface>
      <PortraitCurtain v-if="!readError || state?.character || writing" class="launch-art" :image="portrait.url.value" :identity="characterIdentity" :alt="copy.portraitAlt" :preparing="actualPreparation" :unavailable="phase === 'failed'" @revealed="revealed = $event" @failed="portrait.fail()" />
      <p v-if="!writing && !state?.character" class="launch-text launch-status" role="status">{{ statusMessage }}</p>
    </section>
    <section v-if="state?.character || account && policy && !policy.allowed && !characterVerified" v-show="!workshopOpen" class="reception-waiting" aria-label="Čekání na večer">
      <CharacterCountdown class="countdown" heading-as="h2" :name="wandComplete ? 'Vítej v čekárně.' : 'Večer se chystá.'" :universe="''" :values="timeParts" :units="[copy.days, copy.hours, copy.minutes, copy.seconds]" :title="copy.countdown" :label="copy.countdownLabel" :unscheduled="policy?.launchAt == null || remaining <= 0 ? copy.unscheduled : undefined" />
      <div class="reception-options">
        <p v-if="launchTimeLabel" class="launch-time">Večer se otevře {{ launchTimeLabel }}.</p>
        <p v-if="wandComplete" class="reception-seat">Čekárna uznává tvoji pověst. Na pořadí to vliv nemá.</p>
        <details v-if="account && !accountDeletionRequested" class="launch-notifications"><summary>Nastavit oznámení pro toto zařízení</summary><PushNotifications /></details>
        <details v-if="state?.character" class="launch-label-options"><summary>Rozlišovací označení (nepovinné)</summary><CharacterLabelEditor :profile-id="state.character.profileId" :label="state.character.label" @saved="state.character.label = $event" /></details>
      </div>
    </section>
    </template>
    <footer class="launch-footer"><details v-if="account" class="launch-account-options"><summary>{{ copy.account }}</summary><AccountDeletion /></details><nav :aria-label="copy.account"><RouterLink to="/privacy">{{ copy.privacy }}</RouterLink><RouterLink to="/support">{{ copy.support }}</RouterLink></nav></footer>
  </SceneShell>
</template>

<style scoped>
.launch-page { min-height: 100dvh; padding: max(1rem, var(--safe-area-inset-top, env(safe-area-inset-top, 0px))) max(1rem, env(safe-area-inset-right, 0px)) max(1rem, env(safe-area-inset-bottom, 0px)) max(1rem, env(safe-area-inset-left, 0px)); color: var(--aw-ivory); }
.launch-masthead { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 1rem; padding-block: .5rem 2rem; }
.launch-wordmark { min-height: 44px; display: flex; align-items: center; gap: .8rem; color: var(--aw-ivory); text-decoration: none; font-family: var(--aw-font-display); font-size: 1.8rem; font-weight: 500; line-height: .9; letter-spacing: -.025em; }
.launch-wordmark img { display: none; }
.launch-wordmark span { white-space: nowrap; }
.launch-language { display: inline-flex; align-items: center; font: inherit; font-size: .875rem; color: var(--aw-game-paper, var(--aw-ivory)); background: rgb(3 24 22 / .72); border: 1px solid var(--aw-game-brass, var(--aw-border-subtle)); border-radius: 8px; min-height: 44px; padding: .5rem; }
.launch-card { width: min(100%, 42rem); margin: 0 auto; display: flex; flex-direction: column; gap: 1rem; align-items: stretch; }
.launch-art { order: 2; width: 100%; }
.launch-copy { order: 1; min-width: 0; }
.launch-copy > .aw-notice { margin-block-start: 1rem; }
.launch-copy :deep(h1) { font-family: var(--aw-font-display); font-size: clamp(2rem, 8vw, 3.5rem); font-weight: 500; line-height: 1.05; letter-spacing: -.025em; margin: 0; overflow-wrap: anywhere; color: var(--aw-game-ink, #2a2019); }
.launch-status { color: var(--aw-game-muted-ink, #625340); line-height: 1.5; min-height: 1.5em; margin-block: .5rem; font-size: .875rem; }
.launch-text { order: 3; min-width: 0; padding-top: .25rem; align-self: stretch; text-align: center; }
.launch-text p { overflow-wrap: anywhere; margin: 0; }
.launch-text .launch-status { color: var(--aw-game-muted); }
.launch-final { display: grid; gap: 1rem; }
.launch-joke, .launch-generic { color: var(--aw-ivory); font-size: 1.125rem; line-height: 1.5; }
.launch-joke { font-family: var(--aw-font-display); font-size: clamp(1.6rem, 6.5vw, 2rem); line-height: 1.3; font-weight: 500; }
.launch-identity { font-family: var(--aw-font-display); font-size: 1.2rem; line-height: 1.6; }
.launch-teaser { color: var(--aw-muted); font-size: .875rem; line-height: 1.6; }
.launch-attribution { margin-top: 1.25rem; color: var(--wns-muted, #b2b9b6); font-size: .8rem; line-height: 1.5; overflow-wrap: anywhere; }
.launch-attribution a { min-height: 44px; display: inline-flex; align-items: center; color: inherit; text-decoration: underline; text-underline-offset: .15em; }
.launch-form { display: grid; gap: 1rem; margin-block: 1rem 1.5rem; }
.launch-form p { margin: 0; }
.launch-form .aw-input { min-height: 48px; font-size: 1rem; }
.fixed-universe { display: grid; gap: .25rem; padding-block: .25rem; color: var(--aw-game-ink, #2a2019); }
.fixed-universe span { font-size: .875rem; font-weight: 600; }
.fixed-universe strong { font: 500 1.2rem/1.3 var(--aw-font-display); overflow-wrap: anywhere; }
.fixed-universe p { margin: 0; color: var(--aw-game-muted-ink, #625340); font-size: .875rem; }
.launch-consent { display: flex; gap: .75rem; align-items: start; min-height: 44px; color: var(--aw-game-ink, #2a2019); font-size: .875rem; line-height: 1.6; cursor: pointer; }
.launch-consent input { flex-shrink: 0; width: 24px; height: 24px; margin: .1rem 0 0; accent-color: var(--aw-accent); }
.launch-data-details { color: var(--aw-game-muted-ink, #625340); font-size: .875rem; line-height: 1.6; }
.launch-data-details summary { min-height: 44px; padding-block: .6rem; cursor: pointer; }
.launch-data-details a { display: inline-flex; min-height: 44px; align-items: center; }
.countdown { margin-block: 0; color: var(--wns-text, #f5f5f0); }
.launch-retry { display: block; margin-block-start: .5rem; color: inherit; text-decoration: underline; text-underline-offset: .2em; background: none; border: 0; cursor: pointer; min-height: 44px; padding: .5rem; font: inherit; }
.aw-button { display: inline-flex; align-items: center; justify-content: center; min-height: 48px; padding: .85rem 1.25rem; border-radius: 8px; text-decoration: none; }
.aw-button--primary { color: var(--aw-on-accent); background: var(--aw-accent); }
.launch-footer { margin-top: 1.5rem; display: flex; flex-wrap: wrap; align-items: start; justify-content: space-between; gap: 1rem; color: var(--aw-muted); font-size: .875rem; }
.launch-footer nav { display: flex; flex-wrap: wrap; column-gap: 1rem; }
.launch-footer a { display: inline-flex; align-items: center; min-height: 44px; color: var(--aw-muted); }
.launch-account-options { max-width: 100%; }
.launch-account-options[open] { flex-basis: 100%; width: 100%; }
.launch-account-options summary { cursor: pointer; min-height: 44px; padding-block: .65rem; }
.launch-label-options { margin-top: 1rem; }
.launch-label-options summary { cursor: pointer; min-height: 44px; padding-block: .65rem; }
.launch-label-options[open] :deep(form) { margin-top: .75rem; }
.launch-notifications summary { cursor: pointer; min-height: 44px; padding-block: .65rem; }
.launch-notifications[open] { margin-block: .75rem; }
.group-opening { display: flex; flex-direction: column; align-items: center; overflow: hidden; }
.group-opening-art { display: block; width: min(100%, 36rem); max-height: 58dvh; object-fit: contain; margin: 0 auto; }
.group-opening-copy { position: relative; width: min(100% - 2rem, 30rem); display: grid; justify-items: center; gap: 1rem; padding: 1rem 2rem max(1rem, env(safe-area-inset-bottom)); text-align: center; }
.group-opening-copy h1 { margin: 0; font: 500 clamp(2.5rem, 10vw, 4.5rem)/1.05 var(--aw-font-display); color: var(--aw-ivory); text-shadow: 0 .2rem 1rem #000; }
.group-opening-copy :deep(.aw-button) { width: min(100%, 22rem); }
.group-opening > .launch-footer { width: min(100%, 42rem); margin-top: auto; }
.launch-skip { min-height: 44px; padding: .5rem; color: var(--aw-ivory); background: transparent; border: 0; text-decoration: underline; text-underline-offset: .2em; font: inherit; cursor: pointer; }
:focus-visible { outline: 2px solid var(--aw-info); outline-offset: 3px; }
@media (max-width: 760px) {
  .launch-page { padding-inline: max(16px, env(safe-area-inset-left, 0px)) max(16px, env(safe-area-inset-right, 0px)); }
  .launch-masthead { padding-block: 0 1.5rem; }
  .launch-wordmark { font-size: 1.55rem; }
  .launch-wordmark img { width: 40px; height: 40px; }
  .launch-card { gap: .85rem; }
  .launch-copy { max-width: none; }
  .launch-copy :deep(h1) { font-size: 2.25rem; line-height: 1.1; }
  .launch-art { flex-shrink: 0; }
  .keyboard-priority .launch-art { display: none; }
  .launch-footer { flex-wrap: wrap; }
}
@media (max-width: 340px) { .launch-page { padding-inline: max(16px, env(safe-area-inset-left, 0px)) max(16px, env(safe-area-inset-right, 0px)); } }
.launch-page { --wns-display: var(--wns-font-family); background: var(--wns-bg, #080909); color: var(--wns-text, #f5f5f0); }
.launch-masthead { width: min(100%, 62rem); margin-inline: auto; padding-block: .5rem clamp(1.25rem, 5vh, 3rem); }
.launch-wordmark { font: 800 1.2rem/.95 var(--wns-display); letter-spacing: -.06em; }
.launch-language { border: 0; border-radius: 999px; background: transparent; color: var(--wns-muted, #b2b9b6); }
.launch-card { max-width: 62rem; gap: 1.25rem; }
.launch-copy { color: var(--wns-text, #f5f5f0); }
.launch-copy :deep(h1) { color: var(--wns-text, #f5f5f0); font: 800 clamp(2.25rem, 8vw, 3.5rem)/1 var(--wns-display); letter-spacing: -.065em; }
.launch-status, .launch-text .launch-status { color: var(--wns-muted, #b2b9b6); }
.launch-final { text-align: left; }
.launch-joke { color: var(--wns-text, #f5f5f0); font: 700 clamp(1.5rem, 6vw, 2.5rem)/1.35 var(--wns-display); letter-spacing: -.035em; }
.launch-identity { color: var(--wns-muted, #b2b9b6); font: 500 1rem/1.55 var(--wns-display); }
.launch-teaser, .launch-generic { color: var(--wns-muted, #b2b9b6); }
.launch-art { overflow: visible; border-radius: 30px; background: var(--wns-violet, #c8b9fc); }
.launch-form { gap: 1.25rem; }
.launch-form .aw-input { border-radius: 14px; color: var(--wns-text, #f5f5f0); background: var(--wns-surface-raised, #222626); border-color: var(--wns-line, #343939); }
.fixed-universe, .launch-consent, .launch-data-details { color: var(--wns-text, #f5f5f0); }
.fixed-universe p, .launch-data-details { color: var(--wns-muted, #b2b9b6); }
.launch-consent input { accent-color: var(--wns-mint, #b8f3d4); }
.launch-form :deep(.aw-button--primary), .beta-admission-request :deep(.aw-button--primary) { min-height: 56px; border-radius: 999px; color: #111513; background: var(--wns-mint, #b8f3d4); font-weight: 750; }
.launch-footer { max-width: 62rem; margin-inline: auto; color: var(--wns-muted, #b2b9b6); }
.launch-footer a { color: var(--wns-muted, #b2b9b6); }
.group-opening-art { display: grid; place-items: center; flex: 1; min-height: 36vh; width: min(100%, 36rem); max-height: 58dvh; border-radius: 32px; background: var(--wns-amber, #ffc662); color: #111513; }
.group-opening-art span { font: 800 clamp(14rem, 60vw, 28rem)/.8 var(--wns-display); letter-spacing: -.1em; }
.group-opening-copy h1 { color: var(--wns-text, #f5f5f0); text-shadow: none; font-family: var(--wns-display); font-weight: 800; letter-spacing: -.06em; }
.launch-page :deep(:focus-visible) { outline: 2px solid var(--wns-mint, #b8f3d4); outline-offset: 4px; }
.launch-page h1[tabindex='-1']:focus { outline: none; }
@media (min-width: 760px) { .launch-card { display: grid; grid-template-columns: minmax(0, 1fr) minmax(18rem, .85fr); align-items: center; gap: 2rem; } .launch-copy { order: 1; } .launch-art { order: 2; } .launch-text { grid-column: 1 / -1; } }
@media (max-width: 759px) { .launch-copy { border-radius: 24px; } .launch-art { max-width: min(100%, 44.8vh); margin-inline: auto; } }
.reception-steps { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); width: min(100%, 62rem); margin: 0 auto 24px; padding: 0; list-style: none; }
.reception-steps li { display: flex; gap: 8px; align-items: baseline; min-width: 0; padding: 12px 4px 0; border-top: 2px solid var(--wns-line); color: var(--wns-muted); font-size: clamp(.75rem, 2.8vw, .9rem); }
.reception-step-mark { flex: 0 0 6px; width: 6px; height: 6px; border-radius: 50%; background: currentColor; }
.reception-steps li > span:last-child { min-width: 0; overflow-wrap: anywhere; }
.reception-steps .is-current { border-color: var(--wns-amber); color: var(--wns-amber); font-weight: 750; }
.reception-steps .is-done { border-color: var(--wns-mint); color: var(--wns-mint); }
.reception-steps small { display: block; font-size: .75rem; font-weight: 500; }
.launch-card--reception { width: min(100%, 62rem); margin-block: 32px; }
.launch-card--reception .launch-copy { background: transparent; border: 0; padding: 0; border-radius: 0; }
.launch-card--reception .launch-art { width: min(100%, 300px); align-self: center; }
.reception-identity h1 { text-wrap: balance; }
.reception-identity > p { margin: 8px 0 28px; color: var(--wns-muted); }
.reception-introduction { display: grid; gap: 20px; }
.reception-introduction p { margin: 0; overflow-wrap: anywhere; }
.reception-introduction .launch-status { color: var(--wns-mint); max-width: 48ch; }
.reception-introduction .launch-joke { max-width: 28ch; font-size: clamp(1.4rem, 5vw, 2.1rem); line-height: 1.4; font-weight: 650; letter-spacing: -.02em; }
.reception-introduction .launch-identity { color: var(--wns-muted); font-size: 1rem; }
.reception-waiting { display: grid; gap: 28px; width: min(100%, 62rem); margin: 40px auto 0; padding-top: 28px; border-top: 1px solid var(--wns-line); }
.launch-push-offer { width: min(100%, 62rem); margin: 24px auto; }
.reception-waiting .countdown { text-align: left; }
.reception-waiting :deep(.character-countdown-identity p) { display: none; }
.reception-waiting :deep(.character-countdown-title) { margin-top: 16px; color: var(--wns-muted); }
.reception-waiting :deep(.character-countdown-grid) { max-width: 320px; }
.reception-options { min-width: 0; color: var(--wns-muted); font-size: .875rem; }
.reception-options p { margin: 0 0 16px; }
.reception-options .reception-seat { color: var(--wns-text); max-width: 42ch; line-height: 1.5; }
.reception-options summary { cursor: pointer; min-height: 44px; padding-block: 12px; }
.launch-page ::selection { background: var(--wns-violet); color: var(--wns-on-violet); }
@media (min-width: 760px) { .launch-card--reception { grid-template-columns: minmax(0, 1fr) minmax(240px, .7fr); gap: 64px; } .reception-waiting { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 64px; } }
</style>
