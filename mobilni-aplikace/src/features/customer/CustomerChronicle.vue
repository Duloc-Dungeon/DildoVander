<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountDeletionRequested, offlineSession, signingOut, shareNativeChronicleImage } from '../identity/session';
import {
  ChronicleClient, chronicleCanRead, isChronicleAccountId, isChronicleEvent, type ChronicleAward, type ChronicleEvent,
  type ChronicleFilter, type ChronicleKind, type ChronicleTimelineItem,
} from './chronicle';

const kinds: { value: ChronicleKind | ''; label: string }[] = [
  { value: '', label: 'Všechny události' }, { value: 'yolo', label: 'YOLO příběhy' },
  { value: 'duel', label: 'Duely' }, { value: 'team', label: 'Týmové zápasy' },
  { value: 'boss', label: 'Souboje s bossem' }, { value: 'case', label: 'Případy' },
  { value: 'wand', label: 'Hůlky' }, { value: 'evening', label: 'Večery' },
  { value: 'conversation', label: 'Rozhovory' },
];

const route = useRoute();
const router = useRouter();
const client = new ChronicleClient();
const items = ref<ChronicleTimelineItem[]>([]);
const badges = ref<ChronicleAward[]>([]);
const participantDirectory = ref<Array<{ id: string; name: string }>>([]);
const participantScanLoading = ref(false);
const participantScanComplete = ref(false);
const participantScanError = ref(false);
const participantScanPages = ref(0);
const selected = ref<ChronicleTimelineItem | null>(null);
const detailHeading = ref<HTMLElement>();
const imageUrl = ref('');
const thumbnailUrls = ref<Record<string, string>>({});
const badgeImageUrls = ref<Record<string, string>>({});
const kind = ref('');
const participant = ref('');
const badge = ref('');
const cursor = ref<string | null>(null);
const loading = ref(false);
const loadingMore = ref(false);
const loaded = ref(false);
const failed = ref(false);
const moreFailed = ref(false);
const detailLoading = ref(false);
const revealing = ref(false);
const illustrationBusy = ref(false);
const sharing = ref(false);
const actionError = ref('');
const actionStatus = ref('');
const online = ref(typeof navigator !== 'undefined' && navigator.onLine);
const filtersOpen = ref(typeof window !== 'undefined' && window.matchMedia('(min-width: 769px)').matches);
let controller = new AbortController();
let requestVersion = 0;
let detailVersion = 0;
let imageController: AbortController | null = null;
let debounce: ReturnType<typeof setTimeout> | undefined;
let replacingRoute = false;
let badgeImageVersion = 0;
let badgeImageControllers: AbortController[] = [];
let participantScanController: AbortController | null = null;
let thumbnailVersion = 0;
const thumbnailControllers = new Map<string, AbortController>();
const thumbnailObservers = new WeakMap<HTMLElement, IntersectionObserver>();

function clearThumbnails(): void {
  thumbnailVersion++;
  thumbnailControllers.forEach(current => current.abort());
  thumbnailControllers.clear();
  Object.values(thumbnailUrls.value).forEach(URL.revokeObjectURL);
  thumbnailUrls.value = {};
}

async function loadThumbnail(id: string): Promise<void> {
  const item = items.value.find(value => value.id === id);
  if (!canRead() || !item || !isChronicleEvent(item) || item.illustrationState !== 'ready'
    || thumbnailUrls.value[id] || thumbnailControllers.has(id)) return;
  const current = new AbortController();
  thumbnailControllers.set(id, current);
  const version = thumbnailVersion;
  const requestedOwner = account.value?.id;
  try {
    const blob = await client.image(id, false, current.signal);
    if (current.signal.aborted || version !== thumbnailVersion || requestedOwner !== account.value?.id || !canRead()) return;
    thumbnailUrls.value = { ...thumbnailUrls.value, [id]: URL.createObjectURL(blob) };
  } catch { /* Keep the readable event card when its optional illustration is unavailable. */ }
  finally { if (thumbnailControllers.get(id) === current) thumbnailControllers.delete(id); }
}

function observeThumbnail(element: HTMLElement, value: { id: string; ready: boolean }): void {
  thumbnailObservers.get(element)?.disconnect();
  if (!value.ready) return;
  if (typeof IntersectionObserver === 'undefined') { void loadThumbnail(value.id); return; }
  const observer = new IntersectionObserver(entries => {
    if (entries.some(entry => entry.isIntersecting)) {
      observer.disconnect();
      void loadThumbnail(value.id);
    }
  }, { rootMargin: '120px' });
  thumbnailObservers.set(element, observer);
  observer.observe(element);
}

const vChronicleThumbnail = {
  mounted(element: HTMLElement, binding: { value: { id: string; ready: boolean } }) { observeThumbnail(element, binding.value); },
  updated(element: HTMLElement, binding: { value: { id: string; ready: boolean } }) { observeThumbnail(element, binding.value); },
  beforeUnmount(element: HTMLElement) { thumbnailObservers.get(element)?.disconnect(); thumbnailObservers.delete(element); },
};

function clearBadgeImages(): void {
  badgeImageVersion++;
  badgeImageControllers.forEach(current => current.abort());
  badgeImageControllers = [];
  Object.values(badgeImageUrls.value).forEach(URL.revokeObjectURL);
  badgeImageUrls.value = {};
}

const canRead = () => chronicleCanRead() && online.value;
const selectedEvent = computed(() => selected.value && isChronicleEvent(selected.value) ? selected.value : null);
const yoloContext = computed(() => selectedEvent.value?.sourceKind === 'yolo'
  ? selectedEvent.value.story.slice(0, Math.max(0, selectedEvent.value.story.length - 3)) : []);
const yoloActs = computed(() => selectedEvent.value?.sourceKind === 'yolo'
  ? selectedEvent.value.story.slice(-3) : []);
const locked = computed(() => selected.value !== null && !selectedEvent.value);
const people = computed(() => {
  const seen = new Map<string, string>();
  for (const person of participantDirectory.value) seen.set(person.id, person.name);
  for (const item of items.value) {
    if (isChronicleEvent(item)) for (const person of item.participants) {
      if (isChronicleAccountId(person.accountId)) seen.set(person.accountId, person.displayName);
    }
  }
  return [...seen].map(([id, name]) => ({ id, name })).sort((a, b) => a.name.localeCompare(b.name, 'cs'));
});
const hasMore = computed(() => cursor.value !== null);

function clearImage(): void {
  if (imageUrl.value) URL.revokeObjectURL(imageUrl.value);
  imageUrl.value = '';
  imageController?.abort();
  imageController = null;
}

function reset(): void {
  requestVersion++;
  controller.abort();
  controller = new AbortController();
  items.value = [];
  badges.value = [];
  clearBadgeImages();
  clearThumbnails();
  participantScanController?.abort();
  participantScanController = null;
  participantDirectory.value = [];
  participantScanLoading.value = false;
  participantScanComplete.value = false;
  participantScanError.value = false;
  participantScanPages.value = 0;
  cursor.value = null;
  loaded.value = false;
  failed.value = false;
  moreFailed.value = false;
  loading.value = false;
  loadingMore.value = false;
}

async function scanAllParticipants(): Promise<void> {
  if (!canRead() || participantScanLoading.value || participantScanComplete.value) return;
  participantScanController?.abort();
  const current = new AbortController();
  participantScanController = current;
  const requestedOwner = account.value?.id;
  const seenPeople = new Map(people.value.map(person => [person.id, person.name]));
  const seenCursors = new Set<string>();
  let before: string | undefined;
  participantScanLoading.value = true;
  participantScanError.value = false;
  participantScanPages.value = 0;
  try {
    do {
      const page = await client.list(before ? { before } : {}, current.signal);
      if (current.signal.aborted || requestedOwner !== account.value?.id || !canRead()) return;
      participantScanPages.value++;
      for (const item of page.items) if (isChronicleEvent(item)) {
        for (const person of item.participants) {
          if (isChronicleAccountId(person.accountId)) seenPeople.set(person.accountId, person.displayName);
        }
      }
      participantDirectory.value = [...seenPeople].map(([id, name]) => ({ id, name }))
        .sort((a, b) => a.name.localeCompare(b.name, 'cs'));
      if (!page.nextCursor) { participantScanComplete.value = true; break; }
      if (seenCursors.has(page.nextCursor)) throw new Error('Chronicle pagination did not advance.');
      seenCursors.add(page.nextCursor);
      before = page.nextCursor;
    } while (!current.signal.aborted);
  } catch {
    if (!current.signal.aborted && requestedOwner === account.value?.id && canRead()) participantScanError.value = true;
  } finally {
    if (participantScanController === current) { participantScanController = null; participantScanLoading.value = false; }
  }
}

function currentFilter(before?: string): ChronicleFilter {
  return {
    ...(kind.value ? { kind: kind.value } : {}),
    ...(participant.value ? { participant: participant.value } : {}),
    ...(badge.value ? { badge: badge.value } : {}),
    ...(before ? { before } : {}),
  };
}

async function refresh(): Promise<void> {
  if (!canRead()) { reset(); return; }
  const version = ++requestVersion;
  controller.abort();
  controller = new AbortController();
  const signal = controller.signal;
  const requestedOwner = account.value?.id;
  items.value = [];
  clearThumbnails();
  cursor.value = null;
  loaded.value = false;
  failed.value = false;
  moreFailed.value = false;
  loading.value = true;
  try {
    const page = await client.list(currentFilter(), signal);
    if (version !== requestVersion || requestedOwner !== account.value?.id || !canRead()) return;
    items.value = page.items;
    cursor.value = page.nextCursor;
    badges.value = page.badges;
    loadBadgeImages(page.badges);
    loaded.value = true;
  } catch {
    if (version === requestVersion && requestedOwner === account.value?.id && canRead()) failed.value = true;
  } finally {
    if (version === requestVersion) loading.value = false;
  }
}

async function loadMore(): Promise<void> {
  if (!canRead() || !cursor.value || loadingMore.value) return;
  const version = requestVersion;
  const requestedOwner = account.value?.id;
  loadingMore.value = true;
  moreFailed.value = false;
  try {
    const page = await client.list(currentFilter(cursor.value), controller.signal);
    if (version !== requestVersion || requestedOwner !== account.value?.id || !canRead()) return;
    const ids = new Set(items.value.map(item => item.id));
    items.value.push(...page.items.filter(item => !ids.has(item.id)));
    cursor.value = page.nextCursor;
    badges.value = page.badges;
    loadBadgeImages(page.badges);
  } catch {
    if (version === requestVersion && requestedOwner === account.value?.id && canRead()) moreFailed.value = true;
  } finally {
    if (version === requestVersion) loadingMore.value = false;
  }
}

function loadBadgeImages(values: ChronicleAward[]): void {
  clearBadgeImages();
  if (!canRead()) return;
  const version = badgeImageVersion;
  const owner = account.value?.id;
  const requested = values.slice(0, 24);
  badgeImageControllers = requested.map(() => new AbortController());
  requested.forEach((award, index) => {
    const request = badgeImageControllers[index];
    void client.badgeImage(award.eventId, award.id, request.signal).then(blob => {
      if (request.signal.aborted || version !== badgeImageVersion || owner !== account.value?.id || !canRead()) return;
      badgeImageUrls.value = { ...badgeImageUrls.value, [award.id]: URL.createObjectURL(blob) };
    }).catch(() => { /* Evidence and date remain available if an emblem is missing. */ });
  });
}

function scheduleRefresh(): void {
  clearTimeout(debounce);
  debounce = setTimeout(() => { void refresh(); }, 250);
}

function setEventQuery(id?: string): void {
  if ((typeof route.query.event === 'string' ? route.query.event : '') === (id ?? '')) return;
  const query = { ...route.query };
  if (id) query.event = id;
  else delete query.event;
  if (!id) { delete query.sourceKind; delete query.sourceId; }
  replacingRoute = true;
  void router.replace({ path: route.path, query, hash: route.hash }).finally(() => { replacingRoute = false; });
}

async function openEvent(id: string): Promise<void> {
  if (!canRead()) return;
  setEventQuery(id);
  const version = ++detailVersion;
  const requestedOwner = account.value?.id;
  actionError.value = '';
  actionStatus.value = '';
  clearImage();
  selected.value = null;
  detailLoading.value = true;
  try {
    const item = await client.detail(id, controller.signal);
    if (version !== detailVersion || requestedOwner !== account.value?.id || !canRead()) return;
    selected.value = item;
    detailLoading.value = false;
    await focusDetailHeading();
    if (isChronicleEvent(item)) void loadImage(item);
  } catch {
    if (version === detailVersion && requestedOwner === account.value?.id && canRead()) actionError.value = 'Tuto událost se nepodařilo otevřít. Zkus kroniku obnovit.';
  } finally {
    if (version === detailVersion) detailLoading.value = false;
  }
}

async function openSource(sourceKind: string, sourceId: string): Promise<void> {
  if (!canRead()) return;
  const version = ++detailVersion;
  const requestedOwner = account.value?.id;
  setEventQuery(undefined);
  actionError.value = '';
  actionStatus.value = '';
  clearImage();
  selected.value = null;
  detailLoading.value = true;
  try {
    const item = await client.source(sourceKind, sourceId, controller.signal);
    if (version !== detailVersion || requestedOwner !== account.value?.id || !canRead()) return;
    selected.value = item;
    detailLoading.value = false;
    await focusDetailHeading();
    setEventQuery(item.id);
    if (isChronicleEvent(item)) void loadImage(item);
  } catch {
    if (version === detailVersion && requestedOwner === account.value?.id && canRead()) actionError.value = 'Vzpomínku se nepodařilo načíst. Až ji server uloží, najdeš ji v kronice.';
  } finally {
    if (version === detailVersion) detailLoading.value = false;
  }
}

async function focusDetailHeading(): Promise<void> {
  await nextTick();
  const heading = detailHeading.value;
  if (!heading) return;
  heading.focus({ preventScroll: true });
  if (typeof heading.scrollIntoView === 'function') {
    heading.scrollIntoView({ behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'start' });
  }
}

async function reveal(item: ChronicleTimelineItem): Promise<void> {
  if (item.revealed || revealing.value || !canRead()) return;
  const version = detailVersion;
  const requestedOwner = account.value?.id;
  revealing.value = true;
  actionError.value = '';
  try {
    const event = await client.reveal(item.id, controller.signal);
    if (version !== detailVersion || requestedOwner !== account.value?.id || !canRead()) return;
    selected.value = event;
    await focusDetailHeading();
    const index = items.value.findIndex(candidate => candidate.id === event.id);
    if (index >= 0) items.value[index] = event;
    badges.value = [...new Map([...badges.value, ...event.awards].map(award => [award.id, award])).values()];
    void loadImage(event);
  } catch {
    if (version === detailVersion && requestedOwner === account.value?.id && canRead()) actionError.value = 'Výsledek se nepodařilo ověřit. Zkus událost otevřít znovu.';
  } finally {
    if (version === detailVersion) revealing.value = false;
  }
}

async function loadImage(event: ChronicleEvent): Promise<void> {
  clearImage();
  if (event.illustrationState !== 'ready' || !canRead()) return;
  const requestedOwner = account.value?.id;
  const current = new AbortController();
  imageController = current;
  try {
    const blob = await client.image(event.id, false, current.signal);
    if (current.signal.aborted || requestedOwner !== account.value?.id || !canRead()
      || selectedEvent.value?.id !== event.id) return;
    imageUrl.value = URL.createObjectURL(blob);
  } catch {
    // The saved text remains readable when the optional illustration is unavailable.
  }
}

async function generateIllustration(event: ChronicleEvent): Promise<void> {
  if (illustrationBusy.value || ['deferred', 'queued', 'running', 'generating'].includes(event.illustrationState)) return;
  illustrationBusy.value = true;
  actionError.value = '';
  try {
    await client.requestIllustration(event.id, controller.signal);
    actionStatus.value = 'Příprava ilustrace pokračuje na pozadí.';
    await refresh();
    const updated = items.value.find(item => item.id === event.id);
    if (updated && isChronicleEvent(updated)) selected.value = updated;
  } catch {
    actionError.value = 'Ilustraci se nepodařilo zařadit. Zkus to znovu později.';
  } finally {
    illustrationBusy.value = false;
  }
}

function pngFileName(event: ChronicleEvent, mediaType: string): string {
  const extension = mediaType === 'image/jpeg' ? 'jpg' : mediaType === 'image/webp' ? 'webp' : 'png';
  return `vzpominka-${event.id}.${extension}`;
}

async function share(event: ChronicleEvent): Promise<void> {
  if (!event.shareAllowed || sharing.value) return;
  const requestedOwner = account.value?.id;
  sharing.value = true;
  actionError.value = '';
  actionStatus.value = '';
  try {
    const blob = await client.image(event.id, true, controller.signal);
    if (requestedOwner !== account.value?.id || !canRead()) return;
    const file = new File([blob], pngFileName(event, blob.type), { type: blob.type });
    if (typeof navigator.share === 'function' && (!navigator.canShare || navigator.canShare({ files: [file] }))) {
      await navigator.share({ files: [file], title: event.title, text: event.shareText ?? event.summary });
      actionStatus.value = 'Obrázek je připravený ke sdílení.';
    } else if (CapacitorNative.value) {
      const result = await shareNativeChronicleImage(blob, file.name, { check: () => {
        if (requestedOwner !== account.value?.id || !canRead()) throw new Error('Přihlášení se mezitím změnilo.');
      } });
      actionStatus.value = result.completed ? 'Systémové sdílení je dokončené.' : 'Sdílení bylo zrušeno.';
    } else {
      throw new Error('Systémové sdílení obrázků není v tomto prohlížeči dostupné.');
    }
  } catch (error) {
    if ((error as Error).name !== 'AbortError') actionError.value = 'Obrázek se nepodařilo sdílet. Zkus to znovu.';
  } finally {
    sharing.value = false;
  }
}

import { Capacitor } from '@capacitor/core';
const CapacitorNative = computed(() => Capacitor.isNativePlatform());

function formatDate(seconds: number): string {
  return new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'long' }).format(new Date(seconds * 1000));
}

function eventLabel(sourceKind: string): string {
  return ({ yolo: 'YOLO příběh', duel: 'Duel', team: 'Týmový zápas', boss: 'Souboj s bossem',
    case: 'Případ', wand: 'Hůlka', evening: 'Večer', conversation: 'Rozhovor' } as Record<string, string>)[sourceKind] ?? 'Událost';
}

function resultLabel(event: ChronicleEvent): string {
  if (event.result === 'fiasco') return 'Společné fiasko';
  if (event.result === 'rescue' && event.sourceKind === 'boss') return 'Ollivander je volný';
  if (event.result === 'draw') return 'Remíza';
  if (event.sourceKind === 'team' && (event.result === 'a' || event.result === 'b')) {
    const winningRole = event.result === 'a' ? 'team-a' : 'team-b';
    const winners = event.participants.filter(person => person.role === winningRole).map(person => person.displayName);
    return winners.length ? `Vítězný tým · ${winners.join(' + ')}` : 'Vítězný tým';
  }

  const ownerId = account.value?.id;
  const winner = event.winnerId ? event.participants.find(person => person.accountId === event.winnerId) : undefined;
  let ownerWon: boolean | undefined;
  if (event.result === 'win') ownerWon = true;
  else if (event.result === 'loss') ownerWon = false;
  else if (winner && ownerId) ownerWon = winner.accountId === ownerId;
  else if (event.result === 'challenger' || event.result === 'opponent') {
    const winningRole = event.result;
    const winningParticipant = event.participants.find(person => person.role === winningRole);
    if (winningParticipant && ownerId) ownerWon = winningParticipant.accountId === ownerId;
  }

  if (ownerWon === true) return winner?.displayName ? `Vítězství · ${winner.displayName}` : 'Vítězství';
  if (ownerWon === false) return `Prohra · vítězí ${winner?.displayName ?? 'soupeř'}`;
  if (event.sourceKind === 'case' && event.result) return event.result;
  return event.result ? 'Výsledek je uložený' : '';
}

function originHref(event: ChronicleEvent): string | null {
  if (['yolo', 'duel'].includes(event.sourceKind) && /^[a-f0-9]{32}$/i.test(event.sourceId)) return `/play/match/${event.sourceId}`;
  if (event.sourceKind === 'team' && /^[a-f0-9]{32}$/i.test(event.sourceId)) return `/play/team/${event.sourceId}`;
  if (event.sourceKind === 'boss') return /^[a-f0-9]{32}$/i.test(event.sourceId) ? `/play/boss/${event.sourceId}` : '/play/boss';
  if (event.sourceKind === 'conversation') return '/play/messages';
  if (event.sourceKind === 'wand') return '/play/wand';
  return null;
}

function applyRouteEvent(): void {
  if (replacingRoute) return;
  const id = typeof route.query.event === 'string' ? route.query.event : '';
  const sourceKind = typeof route.query.sourceKind === 'string' ? route.query.sourceKind : '';
  const sourceId = typeof route.query.sourceId === 'string' ? route.query.sourceId : '';
  if (!id && sourceKind && sourceId) { void openSource(sourceKind, sourceId); return; }
  if (id && id !== selected.value?.id) void openEvent(id);
  else if (!id && selected.value) { detailVersion++; selected.value = null; clearImage(); }
}

function connectivityChanged(): void {
  online.value = navigator.onLine;
  if (!online.value) { reset(); selected.value = null; clearImage(); }
  else void refresh();
}
function viewportChanged(): void {
  filtersOpen.value = window.matchMedia('(min-width: 769px)').matches;
}

function filtersToggled(event: Event): void {
  filtersOpen.value = (event.currentTarget as HTMLDetailsElement).open;
}

watch([kind, participant, badge], scheduleRefresh, { flush: 'sync' });
watch([() => account.value?.id, offlineSession, signingOut, accountDeletionRequested], () => {
  detailVersion++;
  selected.value = null;
  clearImage();
  reset();
  if (canRead()) { void refresh(); applyRouteEvent(); }
}, { flush: 'sync' });
watch(() => [route.query.event, route.query.sourceKind, route.query.sourceId], applyRouteEvent, { flush: 'post' });
onMounted(() => {
  window.addEventListener('online', connectivityChanged);
  window.addEventListener('offline', connectivityChanged);
  window.addEventListener('resize', viewportChanged, { passive: true });
  if (canRead()) void refresh();
  applyRouteEvent();
});
onBeforeUnmount(() => {
  clearTimeout(debounce);
  reset();
  detailVersion++;
  clearImage();
  window.removeEventListener('online', connectivityChanged);
  window.removeEventListener('offline', connectivityChanged);
  window.removeEventListener('resize', viewportChanged);
});
</script>

<template>
  <section class="chronicle" aria-labelledby="chronicle-heading">
    <header class="chronicle__header">
      <div><p class="chronicle__eyebrow">Tvoje příběhy, skutečné události</p><h2 id="chronicle-heading">Ilustrovaná kronika</h2></div>
      <AwButton variant="secondary" :busy="loading" :disabled="!canRead()" @click="refresh">Obnovit kroniku</AwButton>
    </header>
    <AwNotice v-if="!account" tone="info" :live="false">Přihlas se a otevři své uložené události.</AwNotice>
    <AwNotice v-else-if="offlineSession || signingOut || accountDeletionRequested" tone="warning" :live="false">Kronika není dostupná, dokud se mění přihlášení.</AwNotice>
    <AwNotice v-else-if="!online" tone="warning" :live="false">Kroniku zobrazíš po připojení k internetu.</AwNotice>
    <AwNotice v-else-if="failed" tone="warning" :live="true">Kroniku se nepodařilo načíst. Zkus ji obnovit.</AwNotice>

    <div v-else class="chronicle__layout">
      <details class="chronicle__filter-disclosure" :open="filtersOpen" @toggle="filtersToggled"><summary>Filtry kroniky</summary><form class="chronicle__filters" aria-label="Filtrovat kroniku" @submit.prevent="refresh">
        <label>Událost<select v-model="kind"><option v-for="option in kinds" :key="option.value" :value="option.value">{{ option.label }}</option></select></label>
        <label>Postava<select v-model="participant"><option value="">Všichni účastníci</option><option v-for="person in people" :key="person.id" :value="person.id">{{ person.name }}</option></select></label>
        <label>Odznak<select v-model="badge"><option value="">Všechny odznaky</option><option v-for="earned in badges" :key="earned.id" :value="earned.id">{{ earned.title }}</option></select></label>
        <AwButton type="submit" variant="secondary" :busy="loading">Použít filtry</AwButton>
        <div class="chronicle__participant-scan"><AwButton type="button" variant="secondary" :busy="participantScanLoading" :disabled="!canRead() || participantScanComplete" @click="scanAllParticipants">{{ participantScanLoading ? `Hledám účastníky · ${participantScanPages} str.` : participantScanComplete ? 'Účastníci z celé kroniky načtení' : 'Najít účastníky ve starších událostech' }}</AwButton><span v-if="participantScanError" role="status">Starší účastníky se nepodařilo načíst. Zkus to znovu.</span></div>
      </form></details>

      <div class="chronicle__columns">
        <section class="chronicle__timeline" aria-label="Události v kronice" aria-live="polite">
          <p v-if="loading && !loaded">Načítám uložené události…</p>
          <p v-else-if="!items.length && loaded">Zatím tu nemáš žádnou dostupnou událost. Kronikář si datum prvního zápisu odmítá domyslet.</p>
          <ol v-else>
            <li v-for="item in items" :key="item.id" :class="{ 'chronicle__entry--locked': !item.revealed }">
              <button class="chronicle__entry" type="button" v-chronicle-thumbnail="{ id: item.id, ready: isChronicleEvent(item) && item.illustrationState === 'ready' }" @click="openEvent(item.id)">
                <img v-if="isChronicleEvent(item) && thumbnailUrls[item.id]" class="chronicle__entry-art" :src="thumbnailUrls[item.id]" alt="" width="88" height="66" />
                <span v-else class="chronicle__entry-mark" aria-hidden="true">{{ item.revealed ? '✦' : '•' }}</span>
                <span class="chronicle__entry-copy">
                  <small>{{ formatDate(item.occurredAt) }} · {{ item.revealed ? eventLabel(item.sourceKind) : 'Zapečetěná událost' }}<template v-if="isChronicleEvent(item) && item.groupName"> · {{ item.groupName }}</template></small>
                  <strong>{{ item.revealed ? item.title : 'Vzpomínka čeká na otevření' }}</strong>
                  <span v-if="item.revealed && isChronicleEvent(item)">{{ item.participants.map(person => person.displayName).join(' × ') }}</span>
                  <span v-else>Otevři ji až ve chvíli, kdy chceš znát výsledek.</span>
                </span>
                <span aria-hidden="true">→</span>
              </button>
            </li>
          </ol>
          <AwNotice v-if="moreFailed" tone="warning" :live="true">Další události se nepodařilo načíst. Zkus to znovu.</AwNotice>
          <AwButton v-if="hasMore" variant="secondary" :busy="loadingMore" @click="loadMore">Načíst starší události</AwButton>
        </section>

        <section class="chronicle__detail" aria-label="Detail události" aria-live="polite">
          <p v-if="detailLoading">Otevírám vzpomínku…</p>
          <AwNotice v-else-if="actionError" tone="warning" :live="true">{{ actionError }}</AwNotice>
          <p v-else-if="!selected">Vyber událost z kroniky. Její výsledek zůstane skrytý, dokud ji neotevřeš.</p>
          <template v-else-if="locked">
            <p class="chronicle__eyebrow">Zapečetěná vzpomínka</p>
            <h3 ref="detailHeading" tabindex="-1">Otevřít událost?</h3>
            <p>Výsledek a příběh se zobrazí až po tvém rozhodnutí.</p>
            <AwButton variant="primary" :busy="revealing" @click="reveal(selected!)">Odhalit vzpomínku</AwButton>
          </template>
          <template v-else-if="selectedEvent">
            <p class="chronicle__eyebrow">{{ formatDate(selectedEvent.occurredAt) }} · {{ eventLabel(selectedEvent.sourceKind) }}<template v-if="selectedEvent.groupName"> · {{ selectedEvent.groupName }}</template></p>
            <h3 ref="detailHeading" tabindex="-1">{{ selectedEvent.title }}</h3>
            <p>{{ selectedEvent.summary }}</p>
            <img v-if="imageUrl" class="chronicle__art" :src="imageUrl" alt="Ilustrace této události" />
            <div v-else-if="['deferred', 'queued', 'running', 'generating'].includes(selectedEvent.illustrationState)" class="chronicle__art-placeholder" role="status">Ilustrace se připravuje. Uložený příběh už můžeš číst.</div>
            <div v-else-if="['none', 'not_requested'].includes(selectedEvent.illustrationState)" class="chronicle__art-placeholder">Příběh je uložený. Jeho ilustraci můžeš výslovně vyžádat níže.</div>
            <div v-else-if="selectedEvent.illustrationState === 'awaiting_reconciliation'" class="chronicle__art-placeholder" role="status">Výsledek přípravy ilustrace se ověřuje. Uložený příběh zůstává dostupný.</div>
            <div v-else-if="selectedEvent.illustrationState === 'failed'" class="chronicle__art-placeholder">Ilustraci se nepodařilo připravit. Událost zůstala uložená.</div>
            <p v-if="selectedEvent.result" class="chronicle__result"><strong>Výsledek:</strong> {{ resultLabel(selectedEvent) }}</p>
            <section v-if="selectedEvent.sourceKind === 'yolo' && selectedEvent.story.length >= 3" class="chronicle__yolo-context" aria-label="Zadání a situace">
              <p v-for="(context, index) in yoloContext" :key="`context-${index}`">{{ context }}</p>
            </section>
            <ol v-if="selectedEvent.sourceKind === 'yolo' && selectedEvent.story.length >= 3" class="chronicle__story chronicle__story--yolo">
              <li v-for="(beat, index) in yoloActs" :key="`act-${index}`"><strong>{{ ['Plán', 'Komplikace', 'Poslední špatný nápad'][index] }}</strong><span>{{ beat }}</span></li>
            </ol>
            <ol v-else-if="selectedEvent.story.length" class="chronicle__story"><li v-for="(beat, index) in selectedEvent.story" :key="index"><span>{{ beat }}</span></li></ol>
            <p v-if="selectedEvent.decisiveChoice"><strong>Rozhodující volba:</strong> {{ selectedEvent.decisiveChoice }}</p>
            <section v-if="selectedEvent.participants.length" class="chronicle__cast" aria-label="Účastníci">
              <h4>Účastníci</h4>
              <ul><li v-for="person in selectedEvent.participants" :key="person.accountId"><strong>{{ person.displayName }}</strong><span>{{ person.characterName && person.characterName !== person.displayName ? person.characterName : person.role }}<template v-if="person.wandName"> · {{ person.wandName }}</template></span></li></ul>
            </section>
            <section v-if="selectedEvent.decisions.length" class="chronicle__decisions" aria-label="Rozhodnutí a následky">
              <h4>Rozhodnutí a následky</h4>
              <ul><li v-for="(decision, index) in selectedEvent.decisions" :key="`${decision.accountId}-${index}`"><strong>{{ selectedEvent.participants.find(person => person.accountId === decision.accountId)?.displayName || 'Účastník' }}: {{ decision.choice }}</strong><span>{{ decision.consequence }}</span><small v-if="decision.evidence">{{ decision.evidence }}</small></li></ul>
            </section>
            <section v-if="selectedEvent.awards.length" class="chronicle__awards" aria-label="Odznaky z této události">
              <h4>Odznaky získané při události</h4>
              <ul><li v-for="award in selectedEvent.awards" :key="award.id"><strong>{{ award.title }}</strong><span>{{ award.evidence }}</span></li></ul>
            </section>
            <nav class="chronicle__actions" aria-label="Akce k události">
              <RouterLink v-if="originHref(selectedEvent)" :to="originHref(selectedEvent)!">{{ selectedEvent.sourceKind === 'boss' ? 'Původní dobrodružství' : selectedEvent.sourceKind === 'team' ? 'Původní týmový zápas' : 'Původní zápas' }}</RouterLink>
              <AwButton v-if="['none', 'not_requested', 'failed', 'awaiting_reconciliation'].includes(selectedEvent.illustrationState)" variant="secondary" :busy="illustrationBusy" @click="generateIllustration(selectedEvent)">Zkusit ilustraci znovu</AwButton>
              <AwButton v-if="selectedEvent.shareAllowed" variant="secondary" :busy="sharing" :disabled="selectedEvent.illustrationState !== 'ready'" @click="share(selectedEvent)">Sdílet obrázek</AwButton>
            </nav>
            <p v-if="actionStatus" role="status">{{ actionStatus }}</p>
          </template>
        </section>
      </div>

      <section v-if="badges.length" class="chronicle__badge-shelf" aria-label="Odznaky">
        <h3>Tvoje odznaky</h3>
        <ul><li v-for="earned in badges" :key="earned.id"><button type="button" @click="badge = earned.id; openEvent(earned.eventId)"><img v-if="badgeImageUrls[earned.id]" :src="badgeImageUrls[earned.id]" alt="" width="72" height="82" /><span v-else class="chronicle__badge-fallback" aria-hidden="true">✦</span><span><strong>{{ earned.title }}</strong><span>{{ earned.evidence }}</span><small>Získáno {{ formatDate(earned.acquiredAt) }} · otevřít událost</small></span></button></li></ul>
      </section>
    </div>
  </section>
</template>

<style scoped>
.chronicle { display: grid; gap: var(--wns-space-24, 1.5rem); min-width: 0; }
.chronicle__header { display: flex; align-items: end; justify-content: space-between; gap: 1rem; }
.chronicle__filter-disclosure > summary { display: none; }
.chronicle h2, .chronicle h3, .chronicle h4, .chronicle p { margin-top: 0; }
.chronicle h2 { margin-bottom: 0; font: 750 1.45rem/1.2 var(--wns-font-family, sans-serif); }
.chronicle__eyebrow { margin-bottom: .5rem; color: var(--wns-mint, #b8f3d4); font-size: .78rem; font-weight: 750; letter-spacing: .08em; text-transform: uppercase; }
.chronicle__filters { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)) auto; align-items: end; gap: .75rem; padding: 1rem; border: 1px solid var(--wns-line, #343939); border-radius: 1rem; background: var(--wns-surface, #191e1c); }
.chronicle__participant-scan { grid-column: 1 / -1; display: flex; flex-wrap: wrap; align-items: center; gap: .6rem; }
.chronicle__filters label { display: grid; gap: .4rem; font-size: .85rem; font-weight: 700; }
.chronicle select { min-height: 44px; width: 100%; border: 1px solid var(--wns-line, #343939); border-radius: .55rem; padding: .5rem .7rem; color: var(--wns-text, #f5f5f0); background: var(--wns-background, #101412); font: inherit; }
.chronicle__columns { display: grid; grid-template-columns: minmax(0, .9fr) minmax(0, 1.1fr); gap: clamp(1rem, 3vw, 2rem); align-items: start; }
.chronicle__timeline, .chronicle__detail, .chronicle__badge-shelf { min-width: 0; border: 1px solid var(--wns-line, #343939); border-radius: 1rem; padding: clamp(1rem, 3vw, 1.5rem); background: var(--wns-surface, #191e1c); }
.chronicle__timeline > ol, .chronicle__badge-shelf ul { list-style: none; margin: 0; padding: 0; }
.chronicle__timeline > ol { display: grid; }
.chronicle__timeline > ol > li + li { border-top: 1px solid var(--wns-line, #343939); }
.chronicle__entry { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; align-items: center; gap: .75rem; width: 100%; min-height: 76px; padding: .75rem .2rem; border: 0; color: inherit; background: transparent; text-align: left; cursor: pointer; }
.chronicle__entry:hover strong { color: var(--wns-mint, #b8f3d4); }
.chronicle__entry-mark { display: grid; place-items: center; width: 2rem; height: 2rem; border: 1px solid var(--wns-line); border-radius: 50%; color: var(--wns-mint, #b8f3d4); }
.chronicle__entry-art { display: block; width: 5.5rem; height: 4.125rem; object-fit: cover; border: 1px solid var(--wns-line); border-radius: .4rem; background: var(--wns-bg, #101311); }
.chronicle__entry-copy, .chronicle__entry-copy span { min-width: 0; overflow-wrap: anywhere; }
.chronicle__entry-copy { display: grid; gap: .25rem; }
.chronicle__entry-copy small, .chronicle__entry-copy > span { color: var(--wns-muted, #adb5b0); font-size: .82rem; }
.chronicle__entry-copy strong { font-size: 1rem; }
.chronicle__entry--locked .chronicle__entry-mark { color: var(--wns-muted, #adb5b0); }
.chronicle__detail { position: sticky; top: 1rem; display: grid; gap: .75rem; overflow-wrap: anywhere; }
.chronicle__detail h3 { margin-bottom: .15rem; font-size: clamp(1.25rem, 2.3vw, 1.8rem); }
.chronicle__art { display: block; width: 100%; max-height: 34rem; aspect-ratio: 4 / 3; object-fit: contain; border: 1px solid var(--wns-line, #343939); border-radius: .8rem; background: #0d100e; }
.chronicle__art-placeholder { display: grid; place-items: center; min-height: 10rem; border: 1px dashed var(--wns-line, #59645d); border-radius: .8rem; padding: 1.25rem; color: var(--wns-muted, #adb5b0); text-align: center; }
.chronicle__result { padding: .8rem 1rem; border-inline-start: 3px solid var(--wns-mint, #b8f3d4); background: color-mix(in srgb, var(--wns-mint, #b8f3d4) 8%, transparent); }
.chronicle__story, .chronicle__decisions ul, .chronicle__cast ul, .chronicle__awards ul { display: grid; gap: .65rem; margin: 0; padding-inline-start: 1.3rem; }
.chronicle__story li { padding-inline-start: .3rem; }
.chronicle__cast li, .chronicle__decisions li, .chronicle__awards li { display: grid; gap: .2rem; }
.chronicle__cast li span, .chronicle__decisions li span, .chronicle__decisions li small, .chronicle__awards li span { color: var(--wns-muted, #adb5b0); overflow-wrap: anywhere; }
.chronicle__detail h4 { margin-bottom: .5rem; }
.chronicle__actions { display: flex; flex-wrap: wrap; align-items: center; gap: .65rem; padding-top: .5rem; }
.chronicle__actions a { color: var(--wns-mint, #b8f3d4); }
.chronicle__badge-shelf > ul { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 15rem), 1fr)); gap: .7rem; }
.chronicle__badge-shelf li button { display: grid; gap: .3rem; width: 100%; min-height: 92px; padding: .85rem; border: 1px solid var(--wns-line, #343939); border-radius: .7rem; color: inherit; background: transparent; text-align: left; cursor: pointer; }
.chronicle__badge-shelf li button { grid-template-columns: 4.5rem minmax(0, 1fr); align-items: center; }
.chronicle__badge-shelf li button > span:last-child { display: grid; gap: .3rem; }
.chronicle__badge-shelf img { width: 4.5rem; height: 5.1rem; object-fit: contain; }
.chronicle__badge-fallback { display: grid; place-items: center; width: 4.5rem; height: 5.1rem; border: 1px solid var(--wns-line); border-radius: 999px; color: var(--wns-mint); font-size: 2rem; }
.chronicle__badge-shelf li button:hover strong { color: var(--wns-mint, #b8f3d4); }
.chronicle__badge-shelf li span, .chronicle__badge-shelf li small { color: var(--wns-muted, #adb5b0); }
.chronicle :focus-visible { outline: 2px solid var(--wns-mint, #b8f3d4); outline-offset: 3px; }
@media (max-width: 48rem) {
  .chronicle__header { align-items: start; flex-direction: column; }
  .chronicle__filter-disclosure > summary { display: flex; min-height: 44px; align-items: center; color: var(--wns-mint); font-weight: 700; cursor: pointer; }
  .chronicle__filters { grid-template-columns: minmax(0, 1fr); }
  .chronicle__participant-scan { grid-column: auto; }
  .chronicle__columns { grid-template-columns: minmax(0, 1fr); }
  .chronicle__detail { position: static; }
}
</style>
