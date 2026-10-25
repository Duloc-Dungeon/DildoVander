<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import AccountDeletion from '../identity/AccountDeletion.vue';
import { account, accountRequest, accountTransport } from '../identity/session';

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
  || policy.value?.initialLoadingMessage || 'Někdo tu právě rovná hvězdy podle abecedy.');

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
  <main class="launch-page" aria-labelledby="launch-title">
    <section class="launch-card">
      <div class="launch-art" :class="{ 'is-ready': hasPortrait }" aria-label="Portrét připravované postavy">
        <img v-if="portraitUrl" :src="portraitUrl" alt="Portrét tvé postavy" @load="portraitReady = true" @error="clearPortrait(); error = 'Portrét se nepodařilo načíst.'" />
        <svg v-else viewBox="0 0 380 460" role="img" aria-label="Tajemná noční obloha s rozzářenou hvězdou">
          <defs><radialGradient id="halo"><stop stop-color="#b8c0ff" stop-opacity=".8"/><stop offset="1" stop-color="#342b68" stop-opacity="0"/></radialGradient><linearGradient id="night" x2="0" y2="1"><stop stop-color="#35316f"/><stop offset="1" stop-color="#10131d"/></linearGradient></defs>
          <rect width="380" height="460" rx="190" fill="url(#night)"/><circle cx="190" cy="190" r="150" fill="url(#halo)"/><path d="M60 340Q170 270 320 350v110H60z" fill="#171a26"/><path d="m190 100 12 58 58 12-58 12-12 58-12-58-58-12 58-12z" fill="#f5e9c8"/><circle cx="102" cy="135" r="3" fill="white"/><circle cx="285" cy="220" r="2" fill="white"/><circle cx="125" cy="260" r="2" fill="white"/><path d="M85 310q105-75 210 0" fill="none" stroke="#b8c0ff" stroke-opacity=".55" stroke-width="2"/></svg>
        <span v-if="!hasPortrait" class="launch-orbit" aria-hidden="true"></span>
      </div>
      <div class="launch-copy">
        <p class="eyebrow">Pozvánka do neznáma</p>
        <h1 id="launch-title">Něco se chystá<span v-if="state?.character">, {{ state.character.name }}</span>.</h1>
        <template v-if="!account">
          <p class="launch-teaser">{{ policy?.teaser || 'Někde se právě přepisuje pořadí hvězd. Tvoje místo už je připravené.' }}</p>
          <RouterLink class="aw-button aw-button--primary" to="/signin">Mám pozvánku</RouterLink>
        </template>
        <template v-else-if="!state?.character">
          <p class="launch-teaser">Začneme jménem a světem, ze kterého přicházíš. Zbytek se začne skládat samo.</p>
          <form class="launch-form" @submit.prevent="saveCharacter">
            <AwField id="character-name" label="Jméno postavy"><input id="character-name" v-model="name" class="aw-input" maxlength="100" autocomplete="off" required /></AwField>
            <AwField id="character-universe" label="Její svět"><input id="character-universe" v-model="universe" class="aw-input" maxlength="160" autocomplete="off" required /></AwField>
            <label class="launch-consent"><input v-model="consent" type="checkbox" required /><span>Souhlasím s odesláním fiktivního jména a univerza poskytovatelům OpenAI pro obohacení postavy a vytvoření její ilustrace. <RouterLink to="/privacy">Ochrana soukromí</RouterLink></span></label>
            <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
            <AwButton type="submit" :busy="busy" :disabled="!consent">Připravit můj portrét</AwButton>
          </form>
        </template>
        <template v-else>
          <p class="launch-teaser">{{ policy?.teaser || 'Někde se právě přepisuje pořadí hvězd. Tvoje místo už je připravené.' }}</p>
          <p v-if="!hasPortrait" class="launch-status" role="status">{{ loadingMessage }}</p>
          <form v-if="state?.character && !state.artwork" class="launch-form" @submit.prevent="prepareExistingPortrait">
            <p>Tvá uložená postava zatím nemá portrét. Jeho příprava odešle její fiktivní jméno a univerzum poskytovatelům OpenAI.</p>
            <label class="launch-consent"><input v-model="consent" type="checkbox" /> Souhlasím s odesláním jména a univerza této fiktivní postavy poskytovatelům OpenAI pro obohacení a vytvoření portrétu.</label>
            <AwButton type="submit" :busy="busy" :disabled="!consent">Připravit můj portrét</AwButton>
          </form>
          <AwNotice v-if="error || state?.artwork?.status === 'failed'" tone="warning" role="alert">{{ error || 'Portrét se zatím nepodařilo připravit.' }} <button class="launch-retry" type="button" @click="retryPortrait">Zkusit znovu</button></AwNotice>
          <section v-if="policy?.launchAt" class="countdown" aria-label="Čas do otevření">
            <p class="eyebrow">Každé kouzlo má svůj čas</p>
            <div class="countdown-grid" aria-live="off"><div><strong>{{ timeParts[0] }}</strong><span>dní</span></div><div><strong>{{ String(timeParts[1]).padStart(2,'0') }}</strong><span>hodin</span></div><div><strong>{{ String(timeParts[2]).padStart(2,'0') }}</strong><span>minut</span></div><div><strong>{{ String(timeParts[3]).padStart(2,'0') }}</strong><span>sekund</span></div></div>
            <p>Tvůj okamžik se blíží.</p>
          </section>
          <p v-else class="countdown-wait">Tvůj okamžik se blíží. Ještě se ladí poslední hvězda.</p>
        </template>
        <AwNotice v-if="error && !state?.character" tone="warning" role="alert">{{ error }} <button class="launch-retry" type="button" @click="refreshPolicy">Zkusit znovu</button></AwNotice>
      </div>
    </section>
    <details v-if="account" class="launch-account-options"><summary>Soukromí a účet</summary><AccountDeletion /><RouterLink to="/privacy">Ochrana soukromí</RouterLink></details>
    <p class="launch-footnote">Některé věci je lepší nechat chvíli pod pokličkou.</p>
  </main>
</template>

<style scoped>
.launch-page{max-width:76rem;margin:0 auto;padding:clamp(1rem,4vw,3.5rem);display:grid;align-content:center;min-height:calc(100vh - 12rem);color:var(--aw-ivory)}
.launch-card{display:grid;grid-template-columns:minmax(17rem,.85fr) minmax(18rem,1.15fr);align-items:center;gap:clamp(2rem,7vw,6rem);padding:clamp(1.25rem,5vw,4rem);border:1px solid var(--aw-border-subtle);border-radius:1.4rem;background:linear-gradient(140deg,var(--aw-surface),color-mix(in srgb,var(--aw-void) 90%,var(--aw-accent) 10%));box-shadow:0 24px 90px #0005}
.launch-art{position:relative;aspect-ratio:4/4.7;max-height:34rem;overflow:hidden;border-radius:48% 48% 43% 43%;background:#171827;box-shadow:0 0 5rem #7770;margin:auto;width:100%}.launch-art>svg,.launch-art>img{width:100%;height:100%;object-fit:cover;display:block}.launch-art>img{opacity:0;transition:opacity .7s ease}.launch-art.is-ready>img{opacity:1}.launch-orbit{position:absolute;inset:8%;border:1px solid #b8c0ff55;border-radius:50%;animation:orbit 14s linear infinite;pointer-events:none}.launch-orbit:after{content:"";position:absolute;top:8%;left:18%;width:.45rem;height:.45rem;border-radius:50%;background:#eee6ca;box-shadow:0 0 1rem #fff}
.launch-copy{max-width:38rem}.launch-copy h1{font-size:clamp(2.2rem,5vw,4.3rem);letter-spacing:-.04em;line-height:1.04;margin:.35rem 0 1.25rem}.launch-teaser{color:var(--aw-muted);font-size:1.05rem;line-height:1.75;max-width:55ch}.launch-status{margin:1.4rem 0;color:var(--aw-link);font-style:italic;min-height:1.6rem}.launch-form{display:grid;gap:1rem;margin-top:1.5rem}.launch-consent{display:flex;gap:.75rem;align-items:start;color:var(--aw-muted);font-size:.82rem;line-height:1.55}.launch-consent input{margin-top:.25rem;accent-color:var(--aw-accent)}.launch-consent a{color:var(--aw-ivory)}.countdown{margin-top:2rem;padding-top:1.1rem;border-top:1px solid var(--aw-border-subtle)}.countdown-grid{display:grid;grid-template-columns:repeat(4,minmax(3.5rem,1fr));gap:.75rem;margin:1rem 0}.countdown-grid>div{display:grid;gap:.25rem}.countdown-grid strong{font-variant-numeric:tabular-nums;font-size:clamp(1.8rem,4vw,3rem);letter-spacing:-.06em}.countdown-grid span,.countdown>p:last-child,.countdown-wait,.launch-footnote{color:var(--aw-muted);font-size:.8rem}.launch-footnote{text-align:center;margin:1.5rem 0}.launch-retry{margin-left:.5rem;color:inherit;text-decoration:underline;background:none;border:0;cursor:pointer}.aw-button{display:inline-flex;align-items:center;justify-content:center;min-height:44px;padding:.7rem 1.2rem;border-radius:.45rem;text-decoration:none}.aw-button--primary{color:var(--aw-on-accent);background:var(--aw-accent)}
.launch-account-options{margin:1rem auto;max-width:60rem;color:var(--aw-muted);font-size:.8rem}.launch-account-options summary{cursor:pointer;padding:.5rem}.launch-account-options>a{display:inline-block;margin:.5rem;color:var(--aw-ivory)}
@keyframes orbit{to{transform:rotate(360deg)}}@media(prefers-reduced-motion:reduce){.launch-orbit{animation:none}.launch-art>img{transition:none}}
@media(max-width:760px){.launch-page{min-height:unset;padding:1rem}.launch-card{grid-template-columns:1fr;gap:1.5rem;padding:1.2rem;border-radius:1rem}.launch-art{width:min(68vw,20rem);max-height:24rem;aspect-ratio:1/.9}.launch-copy h1{font-size:2.35rem}.launch-footnote{margin-bottom:0}}
</style>
