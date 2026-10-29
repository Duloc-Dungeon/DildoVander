<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountDeletionRequested, accountRequest, offlineSession, signingOut } from '../identity/session';

defineProps<{ locale?: 'cs' | 'en' }>();
const copy = computed(() => ({
  title: 'Tvé tituly', intro: 'Ocenění za hru ve skupině propojené s tvým účtem.',
  refresh: 'Obnovit tituly', loading: 'Načítám tvoje tituly…', empty: 'Zatím sis žádný titul neodnesl.',
  signedOut: 'Přihlas se a zobraz tituly uložené ke svému účtu.', offline: 'Tvoje tituly nejsou dostupné offline.',
  unavailable: 'Tituly nejsou dostupné, dokud se mění přihlášení.',
  failure: 'Tvoje tituly jsou dočasně nedostupné. Zkus je obnovit.',
}));

const titles = ref<string[]>([]);
const busy = ref(false);
const error = ref(false);
const online = ref(typeof navigator !== 'undefined' && navigator.onLine);
const canRead = () => Boolean(account.value && online.value && !offlineSession.value
  && !signingOut.value && !accountDeletionRequested.value);
let generation = 0;
let controller = new AbortController();

function reset(): void {
  generation++;
  controller.abort();
  controller = new AbortController();
  titles.value = [];
  busy.value = false;
  error.value = false;
}

function parseTitles(value: unknown): string[] | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const rawTitles = (value as Record<string, unknown>).titles;
  if (!Array.isArray(rawTitles) || rawTitles.length > 100
    || rawTitles.some(title => typeof title !== 'string' || title.trim().length === 0
      || title.trim().length > 120 || /[\u0000-\u001f\u007f]/.test(title))) return null;
  return [...new Set(rawTitles.map(title => (title as string).trim()))];
}

async function refresh(): Promise<void> {
  if (!canRead()) return;
  const owner = account.value?.id;
  const requestGeneration = ++generation;
  controller.abort();
  controller = new AbortController();
  const requestController = controller;
  busy.value = true;
  error.value = false;
  try {
    const value = await accountRequest<unknown>('/app/v1/game/progress', 'GET', undefined, requestController.signal);
    if (requestGeneration !== generation || owner !== account.value?.id || !canRead()) return;
    const parsed = parseTitles(value);
    if (!parsed) throw new Error('Invalid game title response.');
    titles.value = parsed;
  } catch {
    if (requestGeneration === generation && owner === account.value?.id && canRead()) error.value = true;
  } finally {
    if (requestGeneration === generation) busy.value = false;
  }
}

function contextChanged(): void {
  reset();
  if (canRead()) void refresh();
}
function onConnectivityChange(): void {
  online.value = navigator.onLine;
  contextChanged();
}

watch([() => account.value?.id, offlineSession, signingOut, accountDeletionRequested, online], contextChanged, { flush: 'sync' });
onMounted(() => {
  online.value = navigator.onLine;
  window.addEventListener('online', onConnectivityChange);
  window.addEventListener('offline', onConnectivityChange);
  contextChanged();
});
onBeforeUnmount(() => {
  reset();
  window.removeEventListener('online', onConnectivityChange);
  window.removeEventListener('offline', onConnectivityChange);
});
</script>

<template>
  <section class="game-titles aw-card" aria-labelledby="game-titles-title">
    <header class="game-titles__header">
      <div>
        <h2 id="game-titles-title">{{ copy.title }}</h2>
        <p>{{ copy.intro }}</p>
      </div>
      <AwButton :busy="busy" :disabled="!canRead()" @click="refresh">{{ copy.refresh }}</AwButton>
    </header>
    <AwNotice v-if="!account" tone="info" :live="false">{{ copy.signedOut }}</AwNotice>
    <AwNotice v-else-if="offlineSession || signingOut || accountDeletionRequested" tone="warning" :live="false">{{ copy.unavailable }}</AwNotice>
    <AwNotice v-else-if="!online" tone="warning" :live="false">{{ copy.offline }}</AwNotice>
    <AwNotice v-else-if="error" tone="warning" :live="true">{{ copy.failure }}</AwNotice>
    <p v-else-if="busy && !titles.length" class="game-titles__status" role="status">{{ copy.loading }}</p>
    <p v-else-if="!titles.length" class="game-titles__empty">{{ copy.empty }}</p>
    <ul v-else class="game-titles__list" :aria-label="copy.title">
      <li v-for="title in titles" :key="title" class="game-titles__badge">{{ title }}</li>
    </ul>
  </section>
</template>

<style scoped>
.game-titles { display: grid; gap: 1rem; min-width: 0; }
.game-titles__header { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: .65rem 1rem; }
.game-titles__header h2 { margin-bottom: .35rem; }
.game-titles__header p, .game-titles__empty { margin: 0; color: var(--aw-muted); }
.game-titles__list { display: flex; flex-wrap: wrap; gap: .55rem; margin: 0; padding: 0; list-style: none; }
.game-titles__badge { display: inline-flex; align-items: center; min-height: 2rem; padding: .35rem .8rem; border: 1px solid var(--aw-accent); border-radius: 999px; color: var(--aw-ivory); background: var(--aw-accent-soft); font-weight: 650; }
</style>
