<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { account, accountRequest } from '../identity/session';
import { customerCopy } from '../../application/customerCopy';
import { launchCopy } from '../launch/launchCopy';
import { usePrivateCharacterPortrait } from './privateCharacterPortrait';
import curtain from '../../assets/brand/invitation-curtain-small.webp';
import AwButton from '../../components/AwButton.vue';

const props = defineProps<{ profileId: string; revision: number; name: string }>();
interface PortraitState {
  character: { profileId: string; revision: number } | null;
  artwork: { status: string; loadingMessage?: string | null; finalMessage?: string | null } | null;
}
const state = ref<PortraitState | null>(null);
const stateKey = ref('');
const readFailed = ref(false);
const reading = ref(false);
let epoch = 0;
let request: AbortController | undefined;
let timer: ReturnType<typeof setTimeout> | undefined;
const identity = () => account.value ? `${account.value.id}:${props.profileId}:${props.revision}` : '';
const portrait = usePrivateCharacterPortrait(() => stateKey.value === identity() && state.value?.artwork?.status === 'ready' && account.value
  && state.value.character?.profileId === props.profileId && state.value.character.revision === props.revision
  ? { accountId: account.value.id, profileId: props.profileId, revision: props.revision } : null);
const english = computed(() => customerCopy.language.value === 'en');

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
  if (portrait.failed.value) await portrait.retry();
  else await refresh();
}
function visibility(): void {
  if (document.hidden) cancel();
  else void refresh();
}
watch(identity, () => { cancel(); stateKey.value = ''; state.value = null; readFailed.value = false; void refresh(); }, { immediate: true, flush: 'sync' });
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
  <figure v-if="state?.artwork || readFailed" class="private-character-avatar">
    <img v-if="portrait.url.value" :src="portrait.url.value" :alt="`${launchCopy.portraitAlt}: ${name}`" width="480" height="640" @error="portrait.fail()" />
    <img v-else-if="state?.artwork" :src="curtain" alt="" width="480" height="640" />
    <figcaption>
      <h2>{{ english ? 'Your portrait' : 'Tvůj portrét' }}</h2>
      <p v-if="portrait.url.value">{{ customerCopy.text('customer.avatar-night', { name }) }}</p>
      <p v-else-if="readFailed || portrait.failed.value || state?.artwork?.status === 'failed'" role="status">{{ launchCopy.failed }}</p>
      <p v-else role="status">{{ customerCopy.text('customer.avatar-loading-night', { name }) }}</p>
      <p class="private-character-avatar__privacy">{{ english ? 'This portrait belongs to this character. It is not published automatically.' : 'Tahle podoba patří této postavě. Portrét se nikam nezveřejňuje automaticky.' }}</p>
      <AwButton v-if="readFailed || portrait.failed.value || state?.artwork?.status === 'failed'" :busy="reading || portrait.pending.value" @click="retry">{{ english ? 'Refresh portrait' : 'Obnovit portrét' }}</AwButton>
    </figcaption>
  </figure>
</template>

<style scoped>
.private-character-avatar { display: grid; grid-template-columns: minmax(7rem, .55fr) minmax(0, 1fr); gap: clamp(1.25rem, 3vw, 2.5rem); margin: 0 0 1rem; align-items: center; }
.private-character-avatar > img { display: block; width: 100%; height: auto; max-height: 25rem; aspect-ratio: 3/4; object-fit: cover; object-position: 50% 30%; border-radius: 12px; }
.private-character-avatar figcaption { min-width: 0; overflow-wrap: anywhere; line-height: 1.75; }
.private-character-avatar__privacy { color: var(--aw-muted); font-size: .8rem; }
@media (max-width: 480px) { .private-character-avatar { grid-template-columns: 1fr; } .private-character-avatar > img { max-width: 18rem; } }
</style>
