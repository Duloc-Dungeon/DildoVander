<script setup lang="ts">
import keepsakeArtwork from '../../assets/wns-v2/art/memory-orb.png';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountDeletionRequested, offlineSession, signingOut } from './session';
import {
  AccountCollectionClient,
  isLinkedCollectionItem,
  isOwnedCollectionItem,
  type AccountCollectionItem,
  type AccountCollectionPage,
  type AccountCollectionQuery,
  type CollectionAccessFilter,
  type CollectionReadinessState,
} from './accountCollection';

defineProps<{ locale?: 'cs' | 'en' }>();

const copy = computed(() => ({
  title: 'Tvoje kolekce', intro: 'Tady najdeš dostupné výsledky, jejich přístup i stav dokončení.',
  signIn: 'Přihlas se a zobraz výsledky uložené ke svému účtu.', offline: 'Tvoje výsledky nejsou offline dostupné.',
  unavailableAccount: 'Výsledky nejsou dostupné, dokud se mění přihlášení.',
  loading: 'Načítám tvoji kolekci…', refresh: 'Obnovit kolekci', searchLabel: 'Hledat v kolekci',
  accessLabel: 'Přístup', allAccess: 'Všechny výsledky', ownedAccess: 'Tvoje památky', linkedAccess: 'Propojené pouze ke čtení',
  submitSearch: 'Hledat', loadMore: 'Načíst další', loadingMore: 'Načítám další…', refreshing: 'Obnovuji…',
  empty: 'Zatím tu nemáš žádné dostupné výsledky.', filteredEmpty: 'Těmto filtrům žádné výsledky neodpovídají.',
  failure: 'Kolekci se nepodařilo načíst. Přístup se mohl změnit nebo je služba dočasně nedostupná. Zkus ji obnovit.',
  moreFailure: 'Další výsledky se nepodařilo načíst. Zkus to znovu.', noMore: 'Další výsledky nejsou k dispozici.',
  morePaused: 'Při prohlížení starších výsledků se automatické obnovování pozastaví. Obnovením kolekce ověříš aktuální stav.',
  limitReached: 'Zobrazuje se nejvýše 200 výsledků. Starší položky najdeš upřesněním hledání.',
  result: 'Otevřít výsledek', unnamed: 'Uložený výsledek', noTitle: 'Bez názvu příběhu', portrait: 'Portrét je k dispozici',
  owner: 'Tvoje památka · můžeš ji spravovat', linked: 'Propojeno s účtem · pouze ke čtení',
  unknownAccess: 'Podrobnosti přístupu nelze ověřit; odpojení není dostupné.',
  character: 'Postava', evening: 'Večer', linkedAt: 'Propojeno',
  unlink: 'Odpojit od účtu', unlinking: 'Odpojuji…',
  confirmUnlink: 'Odebrat toto propojení pouze ke čtení z účtu? Původní výsledek ani jeho uložené soubory se tím nesmažou.',
  ready: 'Připraveno', pending: 'Čeká', processing: 'Probíhá', partial: 'Částečně dostupné',
  failed: 'Nepodařilo se dokončit', unavailable: 'Nedostupné',
  reasons: {
    awaiting_result: 'Čeká se na výsledek.', awaiting_capacity: 'Čeká se na volnou kapacitu.',
    paused_preparing: 'Příprava je pozastavená.', reconciliation_required: 'Výsledek se ověřuje.',
    generation_failed: 'Generování selhalo. Dostupný obsah ověříš otevřením výsledku.', result_unavailable: 'Tento výsledek už není dostupný.',
  } as Record<string, string>,
  story: 'Příběh je k dispozici', artwork: 'Výsledek obrazu je k dispozici', completedParts: 'Dostupné části',
}));

const items = ref<AccountCollectionItem[]>([]);
const searchText = ref('');
const accessFilter = ref<CollectionAccessFilter>('all');
const appliedSearch = ref('');
const appliedAccess = ref<CollectionAccessFilter>('all');
const nextCursor = ref<string | null>(null);
const loading = ref(false);
const loadingMore = ref(false);
const unlinking = ref<string | null>(null);
const loaded = ref(false);
const error = ref(false);
const moreError = ref(false);
const online = ref(typeof navigator !== 'undefined' && navigator.onLine);
const expandedPages = ref(false);
const isEmpty = computed(() => !items.value.length && loaded.value && !loading.value && !error.value);
const filterIsActive = computed(() => Boolean(appliedSearch.value) || appliedAccess.value !== 'all');
const accountId = () => account.value?.id ?? null;
const canRead = () => Boolean(accountId() && online.value && !offlineSession.value && !signingOut.value && !accountDeletionRequested.value);
const client = new AccountCollectionClient(accountId, canRead);

let contextEpoch = 0;
let requestGeneration = 0;
let automaticPolls = 0;
let pollTimer: ReturnType<typeof setTimeout> | undefined;
let active = false;
let activeAccountId: string | null = null;
let disposed = false;
const seenCursors = new Set<string>();

const dateFormatter = computed(() => new Intl.DateTimeFormat('cs', { dateStyle: 'medium' }));
const dateTimeFormatter = computed(() => new Intl.DateTimeFormat('cs', { dateStyle: 'medium', timeStyle: 'short' }));
function formatUnix(value: number | null, includeTime = false): string {
  if (value === null) return '';
  const date = new Date(value * 1000);
  if (!Number.isFinite(date.getTime())) return '';
  return (includeTime ? dateTimeFormatter.value : dateFormatter.value).format(date);
}
function isoUnix(value: number): string | undefined {
  const date = new Date(value * 1000);
  return Number.isFinite(date.getTime()) ? date.toISOString() : undefined;
}
function statusLabel(state: CollectionReadinessState): string {
  return copy.value[state];
}
function resultLocation(item: AccountCollectionItem) {
  return { path: '/keepsake/', query: { account: '1', k: item.locator } };
}
function currentQuery(cursor: string | null = null): AccountCollectionQuery {
  return { search: appliedSearch.value, access: appliedAccess.value, cursor };
}
function clearPoll(): void {
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = undefined;
}
function clearVisibleRows(): void {
  items.value = [];
  nextCursor.value = null;
  loaded.value = false;
  expandedPages.value = false;
  seenCursors.clear();
}
function stopAndClear(): void {
  contextEpoch++;
  requestGeneration++;
  clearPoll();
  client.invalidate();
  clearVisibleRows();
  searchText.value = '';
  appliedSearch.value = '';
  accessFilter.value = 'all';
  appliedAccess.value = 'all';
  loading.value = false;
  loadingMore.value = false;
  unlinking.value = null;
  error.value = false;
  moreError.value = false;
  automaticPolls = 0;
}
function hasPollableItem(): boolean {
  return items.value.some(item => {
    if (!item.readiness.shouldPoll) return false;
    return item.readiness.state === 'pending' || item.readiness.state === 'processing'
      || (item.readiness.state === 'partial' && item.readiness.reason !== 'generation_failed'
        && item.readiness.reason !== 'result_unavailable');
  });
}
function schedulePoll(): void {
  clearPoll();
  if (!active || disposed || !canRead() || !online.value || document.hidden || expandedPages.value
    || loadingMore.value || unlinking.value || automaticPolls >= 12 || !hasPollableItem()) return;
  pollTimer = setTimeout(() => { void poll(); }, 5000);
}
function mergePage(rows: AccountCollectionItem[]): AccountCollectionItem[] {
  const merged = new Map(items.value.map(item => [item.locator, item]));
  for (const item of rows) {
    if (!merged.has(item.locator)) merged.set(item.locator, item);
  }
  return [...merged.values()].slice(0, 200);
}
function updateItems(page: AccountCollectionPage, append: boolean): void {
  items.value = append ? mergePage(page.items) : page.items.slice(0, 200);
  nextCursor.value = page.items.length >= 200 ? null : page.nextCursor;
  loaded.value = true;
}

async function loadFirstPage(): Promise<void> {
  if (!active || disposed || !canRead() || document.hidden) return;
  clearPoll();
  automaticPolls = 0;
  const generation = ++requestGeneration;
  client.invalidate();
  clearVisibleRows();
  error.value = false;
  moreError.value = false;
  loading.value = true;
  try {
    const page = await client.readPage(currentQuery());
    if (generation !== requestGeneration || !canRead() || document.hidden) return;
    updateItems(page, false);
  } catch {
    if (generation === requestGeneration && canRead()) {
      error.value = true;
      loaded.value = true;
    }
  } finally {
    if (generation === requestGeneration) {
      loading.value = false;
      schedulePoll();
    }
  }
}

async function poll(): Promise<void> {
  if (!active || disposed || !canRead() || document.hidden || expandedPages.value
    || loading.value || loadingMore.value || unlinking.value || automaticPolls >= 12) return;
  clearPoll();
  const generation = requestGeneration;
  automaticPolls++;
  loading.value = true;
  try {
    const page = await client.readPage(currentQuery());
    if (generation !== requestGeneration || !canRead() || document.hidden) return;
    updateItems(page, false);
    error.value = false;
  } catch {
    if (generation === requestGeneration && canRead()) error.value = true;
  } finally {
    if (generation === requestGeneration) {
      loading.value = false;
      schedulePoll();
    }
  }
}

async function loadMore(): Promise<void> {
  const cursor = nextCursor.value;
  if (!cursor || items.value.length >= 200 || loading.value || loadingMore.value || unlinking.value
    || !active || !canRead() || document.hidden) return;
  if (seenCursors.has(cursor)) {
    moreError.value = true;
    nextCursor.value = null;
    return;
  }
  seenCursors.add(cursor);
  clearPoll();
  const generation = requestGeneration;
  loadingMore.value = true;
  moreError.value = false;
  try {
    const page = await client.readPage(currentQuery(cursor));
    if (generation !== requestGeneration || !canRead() || document.hidden) return;
    if (page.nextCursor && seenCursors.has(page.nextCursor)) {
      updateItems({ ...page, nextCursor: null }, true);
      moreError.value = true;
    } else {
      updateItems(page, true);
    }
    expandedPages.value = true;
  } catch {
    if (generation === requestGeneration && canRead()) {
      seenCursors.delete(cursor);
      moreError.value = true;
    }
  } finally {
    if (generation === requestGeneration) {
      loadingMore.value = false;
      schedulePoll();
    }
  }
}

function submitSearch(): void {
  if (!canRead()) return;
  appliedSearch.value = searchText.value.trim().slice(0, 80);
  appliedAccess.value = accessFilter.value;
  void loadFirstPage();
}

async function unlink(item: AccountCollectionItem): Promise<void> {
  const live = items.value.find(candidate => candidate.locator === item.locator);
  if (!live || !isLinkedCollectionItem(live) || !canRead() || document.hidden || unlinking.value) return;
  const generation = contextEpoch;
  const account = accountId();
  if (!window.confirm(copy.value.confirmUnlink)) return;
  const confirmedItem = items.value.find(candidate => candidate.locator === item.locator);
  if (generation !== contextEpoch || accountId() !== account || !canRead() || document.hidden
    || !confirmedItem || !isLinkedCollectionItem(confirmedItem)) return;
  unlinking.value = item.locator;
  clearPoll();
  try {
    await client.unlink(item.locator);
    if (generation !== contextEpoch || accountId() !== account || !canRead()) return;
    await loadFirstPage();
  } catch {
    if (generation === contextEpoch && accountId() === account && canRead()) error.value = true;
  } finally {
    if (generation === contextEpoch) {
      unlinking.value = null;
      schedulePoll();
    }
  }
}

function reconcileContext(): void {
  if (!canRead()) {
    if (active || items.value.length || loading.value || loadingMore.value || unlinking.value) {
      active = false;
      activeAccountId = null;
      stopAndClear();
    }
    return;
  }
  const currentAccountId = accountId();
  if (active && activeAccountId !== currentAccountId) {
    active = false;
    activeAccountId = null;
    stopAndClear();
  }
  if (!active && currentAccountId) {
    active = true;
    activeAccountId = currentAccountId;
    void loadFirstPage();
  }
}

function onConnectivityChange(): void {
  online.value = navigator.onLine;
}
function onVisibilityChange(): void {
  clearPoll();
  if (document.hidden) {
    if (loading.value || loadingMore.value) {
      requestGeneration++;
      client.invalidate();
      loading.value = false;
      loadingMore.value = false;
    }
    return;
  }
  if (active && canRead()) {
    automaticPolls = 0;
    void loadFirstPage();
  }
}

watch([() => account.value?.id, offlineSession, signingOut, accountDeletionRequested, online], reconcileContext, { flush: 'sync' });
onMounted(() => {
  online.value = navigator.onLine;
  document.addEventListener('visibilitychange', onVisibilityChange);
  window.addEventListener('online', onConnectivityChange);
  window.addEventListener('offline', onConnectivityChange);
  reconcileContext();
});
onBeforeUnmount(() => {
  disposed = true;
  active = false;
  stopAndClear();
  document.removeEventListener('visibilitychange', onVisibilityChange);
  window.removeEventListener('online', onConnectivityChange);
  window.removeEventListener('offline', onConnectivityChange);
});
</script>

<template>
  <section class="account-collection aw-card" aria-labelledby="account-collection-title">
    <header class="collection-header">
      <div>
        <h2 id="account-collection-title">{{ copy.title }}</h2>
        <p>{{ copy.intro }}</p>
      </div>
      <AwButton :busy="loading" :disabled="!canRead() || loadingMore || Boolean(unlinking)" @click="loadFirstPage">{{ loading ? copy.refreshing : copy.refresh }}</AwButton>
    </header>

    <AwNotice v-if="!account" tone="info" :live="false">{{ copy.signIn }}</AwNotice>
    <AwNotice v-else-if="offlineSession || signingOut || accountDeletionRequested" tone="warning" :live="false">{{ copy.unavailableAccount }}</AwNotice>
    <AwNotice v-else-if="!online" tone="warning" :live="false">{{ copy.offline }}</AwNotice>
    <template v-else>
      <form v-if="!isEmpty || filterIsActive || error" class="collection-search" @submit.prevent="submitSearch">
        <label class="collection-field">
          <span>{{ copy.searchLabel }}</span>
          <input v-model="searchText" type="search" maxlength="80" autocomplete="off" />
        </label>
        <label class="collection-field">
          <span id="account-collection-access-label">{{ copy.accessLabel }}</span>
          <select v-model="accessFilter" aria-labelledby="account-collection-access-label">
            <option value="all">{{ copy.allAccess }}</option>
            <option value="owned">{{ copy.ownedAccess }}</option>
            <option value="linked">{{ copy.linkedAccess }}</option>
          </select>
        </label>
        <AwButton type="submit" :disabled="loading || loadingMore || Boolean(unlinking)">{{ copy.submitSearch }}</AwButton>
      </form>

      <AwNotice v-if="error" tone="warning" :live="true">{{ copy.failure }}</AwNotice>
      <p v-if="!error && loading && !items.length" class="collection-status" role="status">{{ copy.loading }}</p>
      <div v-else-if="!error && isEmpty" class="collection-empty">
        <img v-if="!filterIsActive" :src="keepsakeArtwork" alt="" width="960" height="640" loading="lazy" />
        <p>{{ filterIsActive ? copy.filteredEmpty : copy.empty }}</p>
        <nav v-if="!filterIsActive" class="collection-empty__actions" aria-label="Další kroky">
          <RouterLink to="/play">Zkusit hru</RouterLink>
          <RouterLink to="/play/ranking">Otevřít žebříček</RouterLink>
        </nav>
      </div>
      <ol v-else-if="!error && items.length" class="collection-list">
        <li v-for="item in items" :key="item.locator" class="collection-card">
          <div class="collection-card__heading">
            <div>
              <h3>{{ item.officialName || copy.unnamed }}</h3>
              <p v-if="item.storyTitle" class="collection-card__story">{{ item.storyTitle }}</p>
            </div>
            <span class="collection-status-pill" :data-state="item.readiness.state">{{ statusLabel(item.readiness.state) }}</span>
          </div>
          <p v-if="item.readiness.reason" class="collection-card__reason">{{ copy.reasons[item.readiness.reason] }}</p>
          <ul v-if="item.readiness.hasStory || item.readiness.hasArtwork" class="collection-parts" :aria-label="copy.completedParts">
            <li v-if="item.readiness.hasStory">{{ copy.story }}</li>
            <li v-if="item.readiness.hasArtwork">{{ copy.artwork }}</li>
          </ul>
          <p v-if="item.hasPortrait" class="collection-private">{{ copy.portrait }}</p>
          <p v-if="isOwnedCollectionItem(item) && item.characterName" class="collection-metadata">{{ copy.character }}: {{ item.characterName }}</p>
          <p v-if="isOwnedCollectionItem(item) && item.eveningAt !== null" class="collection-metadata">{{ copy.evening }}: {{ formatUnix(item.eveningAt, true) }}</p>
          <div class="collection-card__footer">
            <span v-if="isOwnedCollectionItem(item)" class="collection-access">{{ copy.owner }}</span>
            <span v-else-if="isLinkedCollectionItem(item)" class="collection-access">{{ copy.linked }}</span>
            <span v-else class="collection-access">{{ copy.unknownAccess }}</span>
            <time v-if="isoUnix(item.linkedAt)" :datetime="isoUnix(item.linkedAt)">{{ copy.linkedAt }}: {{ formatUnix(item.linkedAt) }}</time>
          </div>
          <div class="collection-card__actions">
            <RouterLink :to="resultLocation(item)">{{ copy.result }}</RouterLink>
            <AwButton v-if="isLinkedCollectionItem(item)" variant="danger" :busy="unlinking === item.locator" :disabled="Boolean(unlinking) || loading || loadingMore" @click="unlink(item)">
              {{ unlinking === item.locator ? copy.unlinking : copy.unlink }}
            </AwButton>
          </div>
        </li>
      </ol>
      <AwNotice v-if="!error && moreError" tone="warning" :live="true">{{ copy.moreFailure }}</AwNotice>
      <AwButton v-if="!error && nextCursor && items.length < 200" :busy="loadingMore" :disabled="loading || Boolean(unlinking)" @click="loadMore">{{ loadingMore ? copy.loadingMore : copy.loadMore }}</AwButton>
      <p v-else-if="!error && items.length >= 200" class="collection-end">{{ copy.limitReached }}</p>
      <p v-else-if="!error && loaded && items.length" class="collection-end">{{ copy.noMore }}</p>
      <p v-if="!error && expandedPages" class="collection-poll-paused">{{ copy.morePaused }}</p>
    </template>
  </section>
</template>

<style scoped>
.account-collection { display: grid; gap: 1rem; min-width: 0; }
.collection-header, .collection-card__heading, .collection-card__footer, .collection-card__actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: .75rem 1rem; }
.collection-header h2 { margin-bottom: .35rem; }
.collection-header p, .collection-card__story, .collection-card__reason, .collection-card__footer, .collection-end, .collection-poll-paused { margin: 0; color: var(--aw-muted); }
.collection-search { display: grid; grid-template-columns: minmax(12rem, 1fr) minmax(12rem, .8fr) auto; align-items: end; gap: .75rem; }
.collection-field { display: grid; gap: .4rem; min-width: 0; font-weight: 600; }
.collection-field input, .collection-field select { box-sizing: border-box; width: 100%; min-height: 44px; padding: .55rem .7rem; border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-control); background: var(--aw-surface); color: var(--aw-ivory); font: inherit; }
.collection-list { display: grid; gap: .8rem; margin: 0; padding: 0; list-style: none; }
.collection-card { display: grid; gap: .8rem; min-width: 0; padding: 1rem; border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); }
.collection-card h3 { margin: 0; overflow-wrap: anywhere; }
.collection-card__heading > div { display: grid; gap: .35rem; min-width: 0; }
.collection-status-pill { padding: .2rem .65rem; border-radius: 999px; background: var(--aw-accent-soft); color: var(--aw-ivory); }
.collection-status-pill[data-state="failed"], .collection-status-pill[data-state="unavailable"] { background: var(--aw-surface); color: var(--aw-danger); }
.collection-card__reason { color: var(--aw-muted); }
.collection-parts { display: flex; flex-wrap: wrap; gap: .5rem 1.25rem; margin: 0; padding-inline-start: 1.25rem; }
.collection-private { margin: 0; color: var(--aw-muted); }
.collection-card__footer { justify-content: flex-start; font-size: .95em; }
.collection-card__footer time { margin-inline-start: auto; }
.collection-card__actions { justify-content: flex-start; }
.collection-card__actions a { display: inline-flex; align-items: center; min-height: 44px; }
.collection-empty, .collection-status { margin: 0; }
.collection-empty { display: grid; gap: 1rem; max-width: 38rem; }
.collection-empty img { display: block; width: 100%; height: 16rem; object-fit: contain; padding: 12%; background: var(--wns-violet); border-radius: var(--wns-radius-hero); }
.collection-empty__actions { display: flex; flex-wrap: wrap; gap: .75rem 1.25rem; }
.collection-empty__actions a { display: inline-flex; align-items: center; min-height: 44px; }
.collection-field input:focus-visible, .collection-field select:focus-visible, .collection-card__actions a:focus-visible { outline: 3px solid var(--aw-accent); outline-offset: 3px; }
@media (max-width: 680px) {
  .collection-search { grid-template-columns: minmax(0, 1fr); }
  .collection-header { align-items: flex-start; }
  .collection-card__footer time { margin-inline-start: 0; }
}
</style>
