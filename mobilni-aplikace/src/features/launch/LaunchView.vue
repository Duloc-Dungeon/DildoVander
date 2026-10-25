<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import AccountDeletion from '../identity/AccountDeletion.vue';
import { account, accountRequest, accountTransport } from '../identity/session';
import { launchCopy as copy } from './launchCopy';
import curtain from '../../assets/brand/invitation-curtain.webp';
import curtainSmall from '../../assets/brand/invitation-curtain-small.webp';
import brandMark from '../../assets/brand/mark.svg';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';

watch(() => account.value?.id, id => interfaceLanguagePreference.setScope(id), { immediate: true });
const language = computed({
  get: () => interfaceLanguagePreference.language.value,
  set: (value: string) => { interfaceLanguagePreference.setLanguage(value); },
});

interface LaunchPolicy {
  allowed: boolean;
  audience: 'beta' | 'production' | 'privileged';
  launchAt: number | null;
  serverNow: number;
  teaser: string;
  initialLoadingMessage: string;
  personalizedLoadingEnabled: boolean;
}
interface PrelaunchState {
  character: { profileId: string; revision: number; name: string; universe: string; status: string } | null;
  artwork: { status: string; loadingMessage?: string | null; url?: string | null; errorCode?: string | null } | null;
}
const router = useRouter();
const policy = ref<LaunchPolicy | null>(null);
const state = ref<PrelaunchState | null>(null);
const name = ref('');
const universe = ref('');
const consent = ref(false);
const busy = ref(false);
const error = ref('');
const remaining = ref(0);
const portraitReady = ref(false);
const portraitUrl = ref('');
const portraitRevision = ref<number | null>(null);
let timer: ReturnType<typeof setInterval> | undefined;
let policyTimer: ReturnType<typeof setInterval> | undefined;
let pollTimer: ReturnType<typeof setTimeout> | undefined;
let controller: AbortController | undefined;
let serverTimeAnchor = 0;
let monotonicAnchor = 0;
let lastLaunchCheck = 0;

const timeParts = computed(() => {
  const total = Math.max(0, Math.floor(remaining.value / 1000));
  return [Math.floor(total / 86400), Math.floor(total / 3600) % 24, Math.floor(total / 60) % 60, total % 60];
});
const hasPortrait = computed(() => state.value?.artwork?.status === 'ready' && portraitReady.value);
const loadingMessage = computed(() => (policy.value?.personalizedLoadingEnabled ? state.value?.artwork?.loadingMessage : null)
  || policy.value?.initialLoadingMessage || copy.value.loading);

async function refreshPolicy(): Promise<void> {
  const value = await accountRequest<LaunchPolicy>('/app/v1/launch');
  if (typeof value.serverNow !== 'number' || typeof value.allowed !== 'boolean') throw new Error('Stav pozvánky se nepodařilo ověřit.');
  serverTimeAnchor = value.serverNow;
  monotonicAnchor = performance.now();
  policy.value = value;
  updateCountdown();
  if (value.allowed && account.value) await router.replace('/me/character');
}
async function refreshCharacter(signal?: AbortSignal): Promise<void> {
  if (!account.value) return;
  const value = await accountRequest<PrelaunchState>('/app/v1/prelaunch/character', 'GET', undefined, signal);
  state.value = value;
  if (!value.character) return;
  name.value = value.character.name;
  universe.value = value.character.universe;
  if (value.artwork?.status === 'ready') await loadPortrait(value.character.revision, signal);
  else clearPortrait();
}
function clearPortrait(): void {
  if (portraitUrl.value.startsWith('blob:')) URL.revokeObjectURL(portraitUrl.value);
  portraitUrl.value = '';
  portraitRevision.value = null;
  portraitReady.value = false;
}
async function loadPortrait(revision: number, signal?: AbortSignal): Promise<void> {
  if (portraitRevision.value === revision && portraitUrl.value) return;
  clearPortrait();
  portraitRevision.value = revision;
  const response = await accountTransport(`/app/v1/prelaunch/character/portrait?revision=${revision}`, {
    headers: new Headers({ Accept: 'image/png, image/jpeg, image/webp' }), signal, redirect: 'error',
  });
  if (!response.ok) throw new Error('Portrét se zatím nepodařilo zobrazit.');
  const image = await response.blob();
  if (!['image/png', 'image/jpeg', 'image/webp'].includes(image.type) || image.size === 0) throw new Error('Server zatím nepředal hotový portrét.');
  portraitUrl.value = URL.createObjectURL(image);
}
function updateCountdown(): void {
  const serverNow = serverTimeAnchor + (performance.now() - monotonicAnchor);
  remaining.value = policy.value?.launchAt == null ? 0 : Math.max(0, policy.value.launchAt - serverNow);
  const monotonicNow = performance.now();
  if (remaining.value === 0 && policy.value?.launchAt != null && monotonicNow - lastLaunchCheck > 15_000) {
    lastLaunchCheck = monotonicNow;
    void refreshPolicy().catch(() => {});
  }
}
async function saveCharacter(): Promise<void> {
  if (busy.value || !account.value || !consent.value || !name.value.trim() || !universe.value.trim()) return;
  busy.value = true;
  error.value = '';
  const expected = state.value?.character;
  try {
    await accountRequest('/app/v1/prelaunch/character', 'PUT', {
      expectedRevision: expected?.revision ?? 0,
      ...(expected ? { expectedProfileId: expected.profileId } : {}),
      name: name.value.trim(), universe: universe.value.trim(), openAiConsent: true,
    });
    await refreshCharacter(controller?.signal);
    beginPolling();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Tvoji dva údaje se nepodařilo uložit.';
    try {
      // The save may have committed before its response was lost, or on another device.
      await refreshCharacter(controller?.signal);
      if (state.value?.character) { error.value = ''; beginPolling(); }
    } catch { /* Preserve the original error and entered fields while disconnected. */ }
  } finally { busy.value = false; }
}
async function prepareExistingPortrait(): Promise<void> {
  const existing = state.value?.character;
  if (!existing || state.value?.artwork || !consent.value || busy.value) return;
  busy.value = true;
  error.value = '';
  try {
    await accountRequest('/app/v1/prelaunch/character', 'PUT', {
      expectedRevision: existing.revision,
      expectedProfileId: existing.profileId,
      name: existing.name,
      universe: existing.universe,
      openAiConsent: true,
    });
    await refreshCharacter(controller?.signal);
    beginPolling();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Přípravu portrétu se nepodařilo spustit.';
  } finally { busy.value = false; }
}
async function retryPortrait(): Promise<void> {
  if (!account.value || busy.value) return;
  busy.value = true;
  error.value = '';
  try {
    if (state.value?.artwork?.status === 'ready' && state.value.character) {
      clearPortrait();
      await loadPortrait(state.value.character.revision, controller?.signal);
      error.value = '';
      return;
    }
    await accountRequest('/app/v1/prelaunch/character/retry', 'POST', {});
    beginPolling();
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Nový pokus se nepodařilo spustit.'; }
  finally { busy.value = false; }
}
function beginPolling(): void {
  if (!account.value || document.hidden || pollTimer) return;
  pollTimer = setTimeout(async () => {
    pollTimer = undefined;
    if (!controller?.signal.aborted) {
      try { await refreshCharacter(controller?.signal); }
      catch { /* A brief connection failure leaves the countdown intact. */ }
      if (['queued', 'pending', 'running'].includes(state.value?.artwork?.status ?? '')) beginPolling();
    }
  }, 5000);
}
function onVisibility(): void {
  if (!document.hidden) {
    void refreshPolicy().catch(() => {});
    void refreshCharacter(controller?.signal).then(beginPolling).catch(() => {});
    startPolicyPolling();
  } else {
    if (pollTimer) { clearTimeout(pollTimer); pollTimer = undefined; }
    if (policyTimer) { clearInterval(policyTimer); policyTimer = undefined; }
  }
}
function startPolicyPolling(): void {
  if (policyTimer || document.hidden) return;
  policyTimer = setInterval(() => { void refreshPolicy().catch(() => {}); }, 15_000);
}
onMounted(async () => {
  controller = new AbortController();
  try {
    await refreshPolicy();
    await refreshCharacter(controller.signal);
    if (['queued', 'pending', 'running'].includes(state.value?.artwork?.status ?? '')) beginPolling();
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Teď se nepodařilo ověřit pozvánku.'; }
  timer = setInterval(updateCountdown, 1000);
  document.addEventListener('visibilitychange', onVisibility);
  startPolicyPolling();
});
onBeforeUnmount(() => {
  controller?.abort();
  if (timer) clearInterval(timer);
  if (policyTimer) clearInterval(policyTimer);
  if (pollTimer) clearTimeout(pollTimer);
  document.removeEventListener('visibilitychange', onVisibility);
  clearPortrait();
});
</script>

<template>
  <section class="launch-page" aria-labelledby="launch-title">
    <header class="launch-masthead">
      <RouterLink to="/" class="launch-wordmark" :aria-label="copy.home"><img :src="brandMark" alt="" width="48" height="48" /><span>Wand<br />Night Stand</span></RouterLink>
      <div class="launch-masthead-actions"><label class="launch-language"><span class="sr-only">{{ language === 'en' ? 'Interface language' : 'Jazyk rozhraní' }}</span><select v-model="language"><option value="cs">Čeština</option><option value="en">English</option></select></label><RouterLink to="/support" class="launch-help">{{ copy.support }}</RouterLink></div>
    </header>
    <section class="launch-card">
      <div class="launch-art" :class="{ 'is-ready': hasPortrait }">
        <picture :aria-hidden="true"><source media="(max-width: 760px)" :srcset="curtainSmall" /><img :src="curtain" alt="" width="960" height="1280" fetchpriority="high" /></picture>
        <img v-if="portraitUrl" class="launch-portrait" :src="portraitUrl" :alt="copy.portraitAlt" @load="portraitReady = true" @error="clearPortrait(); error = copy.failed" />
      </div>
      <div class="launch-copy">
        <h1 id="launch-title">{{ copy.title }}<span v-if="state?.character">, {{ state.character.name }}</span>.</h1>
        <p v-if="language === 'en' && policy?.teaser && (!account || state?.character)" class="launch-host-note">{{ copy.hostNote }}</p>
        <template v-if="!account">
          <p class="launch-teaser">{{ policy?.teaser || copy.teaser }}</p>
          <RouterLink class="aw-button aw-button--primary" to="/signin">{{ copy.invitation }} <span aria-hidden="true">→</span></RouterLink>
        </template>
        <template v-else-if="!state?.character">
          <p class="launch-teaser">{{ copy.introduction }}</p>
          <form class="launch-form" @submit.prevent="saveCharacter">
            <AwField id="character-name" :label="copy.name" :hint="copy.nameHint"><input id="character-name" v-model="name" class="aw-input" maxlength="100" autocomplete="off" required /></AwField>
            <AwField id="character-universe" :label="copy.universe" :hint="copy.universeHint"><input id="character-universe" v-model="universe" class="aw-input" maxlength="160" autocomplete="off" required /></AwField>
            <label class="launch-consent"><input v-model="consent" type="checkbox" required /><span>{{ copy.consent }} <RouterLink to="/privacy">{{ copy.privacy }}</RouterLink></span></label>
            <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
            <AwButton type="submit" :busy="busy" :disabled="!consent">{{ copy.portrait }}</AwButton>
          </form>
        </template>
        <template v-else>
          <p class="launch-teaser">{{ policy?.teaser || copy.teaser }}</p>
          <p v-if="!hasPortrait" class="launch-status" role="status">{{ loadingMessage }}</p>
          <form v-if="state?.character && !state.artwork" class="launch-form" @submit.prevent="prepareExistingPortrait">
            <p>{{ copy.missingPortrait }}</p>
            <label class="launch-consent"><input v-model="consent" type="checkbox" /><span>{{ copy.consent }}</span></label>
            <AwButton type="submit" :busy="busy" :disabled="!consent">{{ copy.portrait }}</AwButton>
          </form>
          <AwNotice v-if="error || state?.artwork?.status === 'failed'" tone="warning" role="alert">{{ error || copy.failed }} <button class="launch-retry" type="button" :disabled="busy" @click="retryPortrait">{{ copy.retry }}</button></AwNotice>
          <section v-if="policy?.launchAt" class="countdown" :aria-label="copy.countdownLabel">
            <p>{{ copy.countdown }}</p>
            <div class="countdown-grid" aria-live="off"><div><strong>{{ timeParts[0] }}</strong><span>{{ copy.days }}</span></div><div><strong>{{ String(timeParts[1]).padStart(2,'0') }}</strong><span>{{ copy.hours }}</span></div><div><strong>{{ String(timeParts[2]).padStart(2,'0') }}</strong><span>{{ copy.minutes }}</span></div><div><strong>{{ String(timeParts[3]).padStart(2,'0') }}</strong><span>{{ copy.seconds }}</span></div></div>
            <p>{{ copy.soon }}</p>
          </section>
          <p v-else class="countdown-wait">{{ copy.unscheduled }}</p>
          <p class="launch-saved">{{ copy.saved }}</p>
        </template>
        <AwNotice v-if="error && !state?.character" tone="warning" role="alert">{{ error }} <button class="launch-retry" type="button" @click="refreshPolicy">{{ copy.retry }}</button></AwNotice>
      </div>
    </section>
    <footer class="launch-footer">
      <p class="launch-footnote">{{ copy.footnote }}</p>
      <nav :aria-label="copy.account"><RouterLink to="/privacy">{{ copy.privacy }}</RouterLink><RouterLink to="/support">{{ copy.support }}</RouterLink></nav>
    </footer>
    <details v-if="account" class="launch-account-options"><summary>{{ copy.account }}</summary><AccountDeletion /><RouterLink to="/privacy">{{ copy.privacy }}</RouterLink></details>
  </section>
</template>

<style scoped>
.launch-page { max-width: 88rem; min-height: 100dvh; display: grid; grid-template-rows: auto 1fr auto auto; margin: 0 auto; padding: max(1.5rem, env(safe-area-inset-top)) max(1.5rem, env(safe-area-inset-right)) max(1rem, env(safe-area-inset-bottom)) max(1.5rem, env(safe-area-inset-left)); color: var(--aw-ivory); }
.launch-masthead { display: flex; align-items: center; justify-content: space-between; gap: 1rem; padding-block: .5rem 2rem; }
.launch-wordmark { display: flex; align-items: center; gap: .8rem; color: var(--aw-ivory); text-decoration: none; font-family: var(--aw-font-display); font-size: 1.8rem; font-weight: 500; line-height: .86; letter-spacing: -.025em; }
.launch-wordmark img { flex-shrink: 0; }
.launch-wordmark span { white-space: nowrap; }
.launch-help { color: var(--aw-muted); font-size: .85rem; min-height: 44px; display: inline-flex; align-items: center; padding: .5rem; }
.launch-masthead-actions { display: flex; align-items: center; gap: .5rem; }
.launch-language select { font-size: .85rem; color: var(--aw-ivory); background: var(--aw-surface); border: 1px solid var(--aw-border-subtle); border-radius: 8px; min-height: 44px; max-width: 7rem; padding: .5rem; }
.launch-card { display: grid; grid-template-columns: minmax(0, .95fr) minmax(0, 1.05fr); align-items: center; gap: clamp(2rem, 6vw, 7rem); }
.launch-art { position: relative; aspect-ratio: 3/4; max-height: 48rem; overflow: hidden; background: var(--aw-surface); border-radius: 12px; }
.launch-art picture, .launch-art picture img { display: block; width: 100%; height: 100%; object-fit: cover; }
.launch-portrait { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; opacity: 0; transition: opacity .6s ease-out; }
.launch-art.is-ready .launch-portrait { opacity: 1; }
.launch-copy { max-width: 35rem; padding-block: 1rem; min-width: 0; }
.launch-copy h1 { font-family: var(--aw-font-display); font-size: clamp(2.75rem, 5.8vw, 5.75rem); font-weight: 500; line-height: .98; letter-spacing: -.025em; margin: 0 0 1.5rem; overflow-wrap: anywhere; }
.launch-teaser { color: var(--aw-muted); font-size: 1.05rem; line-height: 1.75; max-width: 46ch; margin-block: 0 1.5rem; }
.launch-host-note { color: var(--aw-muted); font-size: .75rem; line-height: 1.5; margin-block: 0 .5rem; }
.launch-status { color: var(--aw-link); line-height: 1.65; padding-block: 1rem; border-block: 1px solid var(--aw-border-subtle); }
.launch-form { display: grid; gap: 1.1rem; margin-top: 1.5rem; }
.launch-consent { display: flex; gap: .5rem; align-items: start; color: var(--aw-muted); font-size: .82rem; line-height: 1.65; cursor: pointer; }
.launch-consent input { flex-shrink: 0; width: 24px; height: 24px; margin: .1rem .3rem 0 0; accent-color: var(--aw-accent); }
.launch-consent a { color: var(--aw-ivory); }
.countdown { margin-top: 2rem; }
.countdown > p:first-child { color: var(--aw-ivory); }
.countdown-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: .75rem; padding-block: .5rem 1rem; border-bottom: 1px solid var(--aw-border-subtle); }
.countdown-grid > div { display: grid; gap: .25rem; }
.countdown-grid strong { font-family: var(--aw-font-ui); font-weight: 500; font-variant-numeric: lining-nums tabular-nums; font-size: clamp(2rem, 4vw, 3.2rem); letter-spacing: -.03em; line-height: 1.1; }
.countdown-grid span, .countdown > p:last-child, .countdown-wait, .launch-saved { color: var(--aw-muted); font-size: .8rem; line-height: 1.6; }
.launch-saved { max-width: 45ch; margin-top: 1.5rem; }
.launch-retry { color: inherit; text-decoration: underline; text-underline-offset: .2em; background: none; border: 0; cursor: pointer; min-height: 44px; padding: .5rem; }
.aw-button { display: inline-flex; align-items: center; justify-content: center; gap: 1.5rem; min-height: 48px; padding: .85rem 1.25rem; border-radius: 8px; text-decoration: none; }
.aw-button--primary { color: var(--aw-on-accent); background: var(--aw-accent); }
.launch-footer { margin-top: 2rem; display: flex; align-items: center; justify-content: space-between; gap: 1rem; border-top: 1px solid var(--aw-border-subtle); padding-top: .75rem; color: var(--aw-muted); font-size: .75rem; }
.launch-footer nav { display: flex; flex-wrap: wrap; column-gap: 1.25rem; }
.launch-footer a { display: inline-flex; align-items: center; min-height: 44px; color: var(--aw-muted); }
.launch-footnote { max-width: 45ch; }
.launch-account-options { margin-top: .5rem; color: var(--aw-muted); font-size: .8rem; }
.launch-account-options summary { display: list-item; cursor: pointer; min-height: 44px; padding-block: .75rem; }
.launch-account-options > a { display: inline-block; padding-block: .75rem; }
@media (prefers-reduced-motion: reduce) { .launch-portrait { transition: none; } }
@media (min-width: 1600px) { .launch-page { padding-top: 3rem; } }
@media (max-width: 760px) {
  .launch-page { padding-inline: max(1.25rem, env(safe-area-inset-left)) max(1.25rem, env(safe-area-inset-right)); }
  .launch-masthead { padding-block: 0 1.5rem; }
  .launch-wordmark { font-size: 1.55rem; }
  .launch-wordmark img { width: 40px; height: 40px; }
  .launch-card { grid-template-columns: 1fr; gap: 1.75rem; }
  .launch-art { aspect-ratio: 5/3; }
  .launch-art picture img { object-position: 50% 65%; }
  .launch-art.is-ready { aspect-ratio: 4/3; }
  .launch-copy { padding: 0; max-width: none; }
  .launch-copy h1 { font-size: clamp(2.75rem, 11vw, 4.5rem); }
  .launch-footer { align-items: start; flex-direction: column; gap: .25rem; }
}
@media (max-width: 420px) { .launch-masthead .launch-help { display: none; } }
</style>
