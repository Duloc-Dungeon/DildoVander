<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountDeletionRequested, accountRequest, offlineSession, signingOut } from '../identity/session';

type SealedCaseTeam = 'Conspirator' | 'Guardian';
interface SealedCaseReward {
  caseId: string;
  releasedAt: string;
  team: SealedCaseTeam;
  confirmedPlans: number;
  successfulPlans: number;
}

defineProps<{ locale?: 'cs' | 'en' }>();
const copy = computed(() => ({
  title: 'Památky z Pod pečetí', intro: 'Záznam tvé účasti v uzavřených případech aktuální skupiny.',
  caseTitle: 'Případ posledního zvonu', keeper: 'Strážce', conspirator: 'Spiklenec',
  confirmed: 'Potvrzené hlavní plány', successful: 'Provedené zásahy', released: 'Uzavřeno',
  refresh: 'Obnovit záznamy případů', loading: 'Načítám tvoje stopy v případech…',
  empty: 'Zatím tu nejsou žádné uzavřené případy uložené k tomuto účtu.',
  signedOut: 'Přihlas se a zobraz památky uložené ke svému účtu.', offline: 'Tvoje památky nejsou dostupné offline.',
  unavailable: 'Památky nejsou dostupné, dokud se mění přihlášení.',
  failure: 'Tvoje stopy v případech jsou dočasně nedostupné. Zkus je obnovit.',
}));

const items = ref<SealedCaseReward[]>([]);
const busy = ref(false);
const error = ref(false);
const online = ref(typeof navigator !== 'undefined' && navigator.onLine);
const dateFormatter = computed(() => new Intl.DateTimeFormat('cs', { dateStyle: 'medium' }));
const canRead = () => Boolean(account.value && online.value && !offlineSession.value
  && !signingOut.value && !accountDeletionRequested.value);
let generation = 0;
let controller = new AbortController();

function exactKeys(value: Record<string, unknown>, expected: readonly string[]): boolean {
  return Object.keys(value).length === expected.length && expected.every(key => Object.prototype.hasOwnProperty.call(value, key));
}

function parsePage(value: unknown): SealedCaseReward[] | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const page = value as Record<string, unknown>;
  if (!exactKeys(page, ['schemaVersion', 'items']) || page.schemaVersion !== 1
    || !Array.isArray(page.items) || page.items.length > 100) return null;

  const seen = new Set<string>();
  const parsed: SealedCaseReward[] = [];
  for (const raw of page.items) {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
    const row = raw as Record<string, unknown>;
    if (!exactKeys(row, ['caseId', 'releasedAt', 'team', 'confirmedPlans', 'successfulPlans'])
      || typeof row.caseId !== 'string' || !/^sc_[0-9a-f]{32}$/.test(row.caseId) || seen.has(row.caseId)
      || typeof row.releasedAt !== 'string' || row.releasedAt.length > 64 || !Number.isFinite(Date.parse(row.releasedAt))
      || (row.team !== 'Conspirator' && row.team !== 'Guardian')
      || !Number.isSafeInteger(row.confirmedPlans) || (row.confirmedPlans as number) < 0 || (row.confirmedPlans as number) > 100
      || !Number.isSafeInteger(row.successfulPlans) || (row.successfulPlans as number) < 0
      || (row.successfulPlans as number) > (row.confirmedPlans as number)) return null;
    seen.add(row.caseId);
    parsed.push(row as unknown as SealedCaseReward);
  }
  return parsed.sort((left, right) => Date.parse(right.releasedAt) - Date.parse(left.releasedAt));
}

function reset(): void {
  generation++;
  controller.abort();
  controller = new AbortController();
  items.value = [];
  busy.value = false;
  error.value = false;
}

function releasedDate(value: string): string {
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? dateFormatter.value.format(date) : '';
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
    const value = await accountRequest<unknown>('/app/v1/sealed-cases/rewards', 'GET', undefined, requestController.signal);
    if (requestGeneration !== generation || owner !== account.value?.id || !canRead()) return;
    const page = parsePage(value);
    if (!page) throw new Error('Invalid sealed case reward response.');
    items.value = page;
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
  <section class="case-rewards aw-card" aria-labelledby="case-rewards-title">
    <header class="case-rewards__header">
      <div>
        <h2 id="case-rewards-title">{{ copy.title }}</h2>
        <p>{{ copy.intro }}</p>
      </div>
      <AwButton :busy="busy" :disabled="!canRead()" @click="refresh">{{ copy.refresh }}</AwButton>
    </header>
    <AwNotice v-if="!account" tone="info" :live="false">{{ copy.signedOut }}</AwNotice>
    <AwNotice v-else-if="offlineSession || signingOut || accountDeletionRequested" tone="warning" :live="false">{{ copy.unavailable }}</AwNotice>
    <AwNotice v-else-if="!online" tone="warning" :live="false">{{ copy.offline }}</AwNotice>
    <AwNotice v-else-if="error" tone="warning" :live="true">{{ copy.failure }}</AwNotice>
    <p v-else-if="busy && !items.length" class="case-rewards__status" role="status">{{ copy.loading }}</p>
    <p v-else-if="!items.length" class="case-rewards__empty">{{ copy.empty }}</p>
    <ul v-else class="case-rewards__list">
      <li v-for="item in items" :key="item.caseId" class="case-rewards__item">
        <div class="case-rewards__heading">
          <div><h3>{{ copy.caseTitle }}</h3><p>{{ releasedDate(item.releasedAt) }}</p></div>
          <span class="case-rewards__badge">{{ item.team === 'Conspirator' ? copy.conspirator : copy.keeper }}</span>
        </div>
        <p class="case-rewards__counts">{{ copy.confirmed }}: {{ item.confirmedPlans }} · {{ copy.successful }}: {{ item.successfulPlans }}</p>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.case-rewards { display: grid; gap: 1rem; min-width: 0; }
.case-rewards__header, .case-rewards__heading { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: .65rem 1rem; }
.case-rewards__header h2, .case-rewards__heading h3 { margin-bottom: .35rem; }
.case-rewards__header p, .case-rewards__heading p, .case-rewards__empty, .case-rewards__counts { margin: 0; color: var(--aw-muted); }
.case-rewards__list { display: grid; gap: .75rem; margin: 0; padding: 0; list-style: none; }
.case-rewards__item { display: grid; gap: .75rem; min-width: 0; padding: 1rem; border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); }
.case-rewards__badge { display: inline-flex; align-items: center; min-height: 2rem; padding: .35rem .8rem; border: 1px solid var(--aw-accent); border-radius: 999px; color: var(--aw-ivory); background: var(--aw-accent-soft); font-weight: 650; }
</style>
