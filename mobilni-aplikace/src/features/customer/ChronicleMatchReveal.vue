<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import { account, accountDeletionRequested, offlineSession, signingOut } from '../identity/session';
import { chronicleCanRead, ChronicleClient, isChronicleEvent, type ChronicleEvent } from './chronicle';

const props = defineProps<{ matchId: string; mode: string; status: string; verdictVisible: boolean }>();
const client = new ChronicleClient();
const event = ref<ChronicleEvent | null>(null);
const loading = ref(false);
const error = ref(false);
let generation = 0;
let controller = new AbortController();
const online = ref(typeof navigator === 'undefined' || navigator.onLine);
const eligible = computed(() => props.status === 'complete' && (props.mode !== 'yolo' || props.verdictVisible));
const canRead = () => online.value && chronicleCanRead();

function reset(): void {
  generation++;
  controller.abort();
  controller = new AbortController();
  event.value = null;
  loading.value = false;
  error.value = false;
}

function connectivityChanged(): void { online.value = navigator.onLine; }

function statusOf(error: unknown): number | undefined {
  return error && typeof error === 'object' && 'status' in error && typeof error.status === 'number'
    ? error.status : undefined;
}

function waitForRetry(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const finish = () => {
      window.clearTimeout(timer);
      signal.removeEventListener('abort', abort);
      resolve();
    };
    const abort = () => {
      window.clearTimeout(timer);
      signal.removeEventListener('abort', abort);
      reject(new DOMException('Chronicle source lookup was cancelled.', 'AbortError'));
    };
    const timer = window.setTimeout(finish, milliseconds);
    signal.addEventListener('abort', abort, { once: true });
    if (signal.aborted) abort();
  });
}

async function sourceWithDeliveryRetry(sourceKind: string, sourceId: string, signal: AbortSignal) {
  const delays = [500, 1000, 2000, 2500];
  for (let attempt = 0; ; attempt++) {
    try {
      return await client.source(sourceKind, sourceId, signal);
    } catch (error) {
      if (statusOf(error) !== 404 || attempt >= delays.length) throw error;
      await waitForRetry(delays[attempt], signal);
    }
  }
}

async function load(): Promise<void> {
  if (!eligible.value || !canRead()) return;
  const current = ++generation;
  const owner = account.value?.id;
  controller.abort();
  const requestController = new AbortController();
  controller = requestController;
  loading.value = true;
  error.value = false;
  event.value = null;
  try {
    const sourceKind = props.mode === 'yolo' ? 'yolo' : props.mode === 'team' ? 'team' : props.mode === 'boss' ? 'boss' : 'duel';
    const source = await sourceWithDeliveryRetry(sourceKind, props.matchId, requestController.signal);
    if (current !== generation || owner !== account.value?.id || !eligible.value || !canRead()) return;
    const revealed = isChronicleEvent(source) ? source : await client.reveal(source.id, requestController.signal);
    if (current !== generation || owner !== account.value?.id || !eligible.value || !canRead()) return;
    event.value = revealed;
  } catch {
    if (current === generation && owner === account.value?.id && !requestController.signal.aborted && eligible.value && canRead()) error.value = true;
  } finally {
    if (current === generation) loading.value = false;
  }
}

watch([() => props.matchId, () => props.mode, () => props.status, () => props.verdictVisible,
  () => account.value?.id, offlineSession, signingOut, accountDeletionRequested, online], () => {
  reset();
  if (eligible.value && canRead()) void load();
}, { immediate: true, flush: 'sync' });

window.addEventListener('online', connectivityChanged);
window.addEventListener('offline', connectivityChanged);
onBeforeUnmount(() => {
  window.removeEventListener('online', connectivityChanged);
  window.removeEventListener('offline', connectivityChanged);
  reset();
});
</script>

<template>
  <section v-if="eligible && chronicleCanRead()" class="match-keepsake" aria-live="polite">
    <p v-if="loading" role="status">Výsledek je potvrzený. Vzpomínka se ukládá do kroniky…</p>
    <div v-else-if="event">
      <div><p class="match-keepsake__eyebrow">Nová vzpomínka v kronice</p><h2>{{ event.title }}</h2>
        <p v-if="event.awards.length">Při zápase získáváš: {{ event.awards.map(award => award.title).join(' · ') }}</p>
        <p v-else>Tenhle zápas má své místo v kronice.</p></div>
      <RouterLink class="match-keepsake__link" :to="{ path: '/me/keepsakes', query: { event: event.id } }">Otevřít vzpomínku →</RouterLink>
    </div>
    <div v-else-if="error"><p>Výsledek zůstává uložený. Vzpomínku můžeš znovu otevřít v kronice.</p><AwButton variant="secondary" @click="load">Zkusit znovu</AwButton></div>
  </section>
</template>

<style scoped>
.match-keepsake { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 1rem; border: 1px solid var(--wns-line, #343939); border-radius: 1rem; padding: 1rem 1.25rem; background: var(--wns-surface, #191e1c); }
.match-keepsake h2, .match-keepsake p { margin: 0; }
.match-keepsake h2 { font-size: 1.15rem; }
.match-keepsake > div { display: grid; gap: .45rem; }
.match-keepsake__eyebrow { color: var(--wns-mint, #b8f3d4); font-size: .78rem; font-weight: 750; }
.match-keepsake__link { color: var(--wns-mint, #b8f3d4); font-weight: 700; }
.match-keepsake :focus-visible { outline: 2px solid var(--wns-mint, #b8f3d4); outline-offset: 4px; }
</style>
