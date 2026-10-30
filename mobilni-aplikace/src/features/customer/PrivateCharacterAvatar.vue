<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { account, accountRequest } from '../identity/session';
import { customerCopy } from '../../application/customerCopy';
import { launchCopy } from '../launch/launchCopy';
import { usePrivateCharacterPortrait } from './privateCharacterPortrait';
import curtain from '../../assets/brand/invitation-curtain-small.webp';
import AwButton from '../../components/AwButton.vue';
import PortraitMirror from '../../components/scene/PortraitMirror.vue';
import type { PrelaunchWelcome } from '../launch/generatedPrelaunchCharacter';
import { characterIntroductionText, characterWelcomeText } from '../launch/characterWelcomeText';
import { useVisitHumor } from '../humor/visitHumor';

const props = defineProps<{ profileId: string; revision: number; name: string; textOnly?: boolean }>();
const { copy: humor, roll: humorRoll } = useVisitHumor();
interface PortraitState {
  character: { profileId: string; revision: number } | null;
  artwork: { status: string; loadingMessage?: string | null; finalMessage?: string | null; welcome?: PrelaunchWelcome | null } | null;
}
const state = ref<PortraitState | null>(null);
const stateKey = ref('');
const readFailed = ref(false);
const reading = ref(false);
const retrying = ref(false);
let epoch = 0;
let request: AbortController | undefined;
let timer: ReturnType<typeof setTimeout> | undefined;
const identity = () => account.value ? `${account.value.id}:${props.profileId}:${props.revision}` : '';
const portrait = usePrivateCharacterPortrait(() => !props.textOnly && stateKey.value === identity() && state.value?.artwork?.status === 'ready' && account.value
  && state.value.character?.profileId === props.profileId && state.value.character.revision === props.revision
  ? { accountId: account.value.id, profileId: props.profileId, revision: props.revision } : null);
const english = computed(() => customerCopy.language.value === 'en');
const welcome = computed(() => stateKey.value === identity() ? state.value?.artwork?.welcome : null);
const introduction = computed(() => {
  if (stateKey.value !== identity() || !state.value?.artwork) return '';
  return characterIntroductionText(state.value.artwork, state.value.artwork.status === 'ready', humorRoll.value) || humor.value.portrait;
});
const failed = computed(() => portrait.failed.value || state.value?.artwork?.status === 'failed');
const pageHidden = ref(document.hidden);
// The private portrait loader publishes a URL only after decoding the image.
const portraitReady = computed(() => !!portrait.url.value);

function cancel(): void {
  epoch++;
  request?.abort();
  if (timer) clearTimeout(timer);
  timer = undefined;
}
async function refresh(): Promise<void> {
  cancel();
  const expected = identity();
  if (!expected || document.hidden) return;
  const captured = epoch;
  const controller = request = new AbortController();
  const current = () => captured === epoch && identity() === expected && !controller.signal.aborted;
  reading.value = true;
  try {
    const value = await accountRequest<PortraitState>('/app/v1/prelaunch/character', 'GET', undefined, controller.signal);
    if (!current()) return;
    state.value = value.character?.profileId === props.profileId && value.character.revision === props.revision ? value : null;
    stateKey.value = expected;
    readFailed.value = false;
    if (['queued', 'pending', 'running'].includes(state.value?.artwork?.status ?? '')) {
      timer = setTimeout(() => { void refresh(); }, 5000);
    }
  } catch { if (current()) readFailed.value = true; }
  finally { if (current()) reading.value = false; }
}
async function retry(): Promise<void> {
  if (retrying.value) return;
  if (readFailed.value) { await refresh(); return; }
  if (portrait.failed.value && state.value?.artwork?.status === 'ready') { await portrait.retry(); return; }
  const expected = identity();
  const captured = epoch;
  const controller = request = new AbortController();
  retrying.value = true;
  try {
    await accountRequest('/app/v1/prelaunch/character/retry', 'POST', {
      expectedProfileId: props.profileId, expectedRevision: props.revision,
    }, controller.signal);
    if (captured !== epoch || identity() !== expected || controller.signal.aborted) return;
    await refresh();
  } catch {
    if (captured === epoch && identity() === expected && !controller.signal.aborted) readFailed.value = true;
  } finally { if (identity() === expected) retrying.value = false; }
}
function visibility(): void {
  pageHidden.value = document.hidden;
  if (document.hidden) cancel();
  else void refresh();
}
watch(identity, () => { cancel(); stateKey.value = ''; state.value = null; readFailed.value = false; retrying.value = false; void refresh(); }, { immediate: true, flush: 'sync' });
onMounted(() => {
  document.addEventListener('visibilitychange', visibility);
  window.addEventListener('online', refresh);
});
onBeforeUnmount(() => {
  cancel();
  document.removeEventListener('visibilitychange', visibility);
  window.removeEventListener('online', refresh);
});
</script>

<template>
  <figure v-if="state?.character || readFailed" class="private-character-avatar" :class="{ 'private-character-avatar--text': textOnly }">
    <figcaption>
      <h2>{{ textOnly ? name : english ? 'Your portrait' : 'Tvůj portrét' }}</h2>
      <div v-if="introduction" class="private-character-avatar__welcome">
        <p class="private-character-avatar__joke">{{ introduction }}</p>
        <p v-if="characterWelcomeText(welcome?.identity)">{{ characterWelcomeText(welcome?.identity) }}</p>
        <p v-if="characterWelcomeText(welcome?.teaser)" class="private-character-avatar__privacy">{{ characterWelcomeText(welcome?.teaser) }}</p>
      </div>
      <template v-if="!textOnly">
      <p v-if="readFailed" role="status">Stav portrétu se nepodařilo načíst. Zkus ho obnovit.</p>
      <p v-else-if="failed" role="status">Portrét se nepodařilo připravit. Postava zůstává uložená. Zkus to znovu.</p>
      <p v-else-if="!state?.artwork" role="status">Postava je uložená. Ještě potřebuje svůj portrét.</p>
      <p v-else-if="!portrait.url.value" role="status">{{ customerCopy.text('customer.avatar-loading-night', { name }) }}</p>
      <p class="private-character-avatar__privacy">{{ english ? 'This portrait belongs to this character. It is not published automatically.' : 'Tahle podoba patří této postavě. Portrét se nikam nezveřejňuje automaticky.' }}</p>
      <AwButton v-if="readFailed || failed || !state?.artwork" :busy="reading || retrying || portrait.pending.value" :disabled="reading || retrying || portrait.pending.value || account?.openAiConsent !== true" @click="retry">{{ !state?.artwork && !readFailed ? 'Připravit portrét' : 'Zkusit znovu' }}</AwButton>
      </template>
    </figcaption>
    <PortraitMirror v-if="!textOnly" class="private-character-avatar__mirror" :open="portraitReady" :animate-reveal="false" :replayable="portraitReady" :active="!pageHidden">
      <img v-if="portrait.url.value" :src="portrait.url.value" :alt="`${launchCopy.portraitAlt}: ${name}`" width="480" height="640" @error="portrait.fail()" />
      <img v-else-if="state?.artwork" :src="curtain" alt="" width="480" height="640" />
    </PortraitMirror>
  </figure>
</template>

<style scoped>
.private-character-avatar { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--wns-space-32); margin: var(--wns-space-12) 0 var(--wns-space-24); align-items: center; }
.private-character-avatar__mirror { width: min(100% - 16px, 300px); margin-inline: auto; }
.private-character-avatar__mirror > img { display: block; width: 100%; height: 100%; aspect-ratio: 4 / 5; object-fit: cover; object-position: 50% 30%; }
.private-character-avatar figcaption { min-width: 0; overflow-wrap: anywhere; line-height: var(--wns-leading-body); }
.private-character-avatar figcaption h2 { margin: 0 0 var(--wns-space-20); color: var(--wns-muted); font-size: 1rem; font-weight: 650; }
.private-character-avatar figcaption p { margin: 0 0 var(--wns-space-16); max-width: 40ch; }
.private-character-avatar__welcome .private-character-avatar__joke { max-width: 28ch; margin-bottom: var(--wns-space-24); font-size: clamp(1.4rem, 4vw, 2.1rem); font-weight: 650; line-height: 1.4; letter-spacing: -.02em; text-wrap: pretty; }
.private-character-avatar__privacy { color: var(--wns-muted); font-size: .875rem; }
@media (min-width: 760px) {
  .private-character-avatar:not(.private-character-avatar--text) { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: clamp(32px, 5vw, 64px); }
  .private-character-avatar__mirror { width: calc(100% - 16px); max-width: 380px; }
}
</style>
