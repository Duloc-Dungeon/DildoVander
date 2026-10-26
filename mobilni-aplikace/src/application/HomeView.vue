<script setup lang="ts">
import tableScene from '../assets/brand/responsive-table/table-scene.webp';
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { account, accountDeletionRequested, accountRequest } from '../features/identity/session';
import { useRoute } from 'vue-router';
import { isCustomerAccount } from './access';
import AwNotice from '../components/AwNotice.vue';
import EveningSetupLinks from './EveningSetupLinks.vue';
import CustomerJourneyCard from '../features/customer/CustomerJourneyCard.vue';
import { SessionRecoveryController, type SessionRecoveryState } from '../features/customer/sessionRecovery';
import type { CustomerJourneyInput } from '../features/customer/customerJourney';
import { projectCustomerResult, type CustomerResultProjection } from '../features/customer/customerResult';
import { customerCopy } from './customerCopy';

const route = useRoute();
const isCustomer = computed(() => isCustomerAccount(account.value) && !accountDeletionRequested.value);
const greeting = computed(() => account.value
  ? customerCopy.text('home.greeting-account', { name: account.value.displayName })
  : customerCopy.text('home.greeting-public'));
interface CustomerProfileSnapshot { profile: { status: string } | null; sessionState: string | null; activeSession: boolean; latestResult: CustomerResultProjection | null }
interface CustomerSessionsResponse { items: unknown[] }
const terminalSessionStates = new Set(['completed', 'failed']);
const journeyProfile = ref<CustomerProfileSnapshot['profile']>(null);
const journeySessionState = ref<string | null>(null);
const journeyResult = ref<CustomerResultProjection | null>(null);
const journeyLoading = ref(false);
const journeyStale = ref(false);
const recoveryState = reactive<SessionRecoveryState>({
  refreshing: false, stale: false, retrying: false, retryDelayMs: null, error: undefined,
});
const journeyRecovery = new SessionRecoveryController<CustomerProfileSnapshot>({
  state: recoveryState,
  pollIntervalMs: 2500,
  retryBaseDelayMs: 1000,
  retryMaxDelayMs: 30_000,
  refresh: async signal => {
    const [character, sessions] = await Promise.all([
      accountRequest<{ profile: unknown }>('/app/v1/customer/character', 'GET', undefined, signal),
      accountRequest<CustomerSessionsResponse>('/app/v1/customer/sessions', 'GET', undefined, signal),
    ]);
    const profile = parseCustomerProfile(character.profile);
    const sessionState = parseSessionState(sessions);
    const activeSession = sessionState !== null && !terminalSessionStates.has(sessionState);
    // The account endpoint returns newest sessions first. Resolve its exact
    // completed session through the server binding; archive associations are not outcomes.
    const completed = !activeSession ? sessions.items.find(item =>
      !!item && typeof item === 'object' && (item as { state?: unknown }).state === 'completed') : undefined;
    const sessionId = (completed as { id?: unknown } | undefined)?.id;
    if (completed && (typeof sessionId !== 'string' || !sessionId)) throw new Error('Neplatná identita rozhovoru.');
    const latestResult = typeof sessionId === 'string'
      ? projectCustomerResult(await accountRequest<unknown>(`/app/v1/customer/sessions/${encodeURIComponent(sessionId)}/result`, 'GET', undefined, signal), sessionId)
      : null;
    return {
      profile,
      sessionState,
      activeSession,
      latestResult,
    };
  },
  hasActiveSession: snapshot => snapshot.activeSession || snapshot.latestResult?.shouldPoll === true,
  onValue: snapshot => {
    journeyProfile.value = snapshot.profile;
    journeySessionState.value = snapshot.sessionState;
    journeyResult.value = snapshot.latestResult;
    journeyLoading.value = false;
    journeyStale.value = false;
  },
});
const customerJourney = computed<CustomerJourneyInput>(() => ({
  account: isCustomer.value ? account.value : null,
  profile: journeyProfile.value,
  sessionState: journeySessionState.value,
  loading: journeyLoading.value || recoveryState.refreshing,
  error: Boolean(recoveryState.error) || journeyResult.value?.valid === false,
  stale: journeyStale.value || recoveryState.stale,
  latestAuthorizedResult: journeyResult.value?.locator ? { locator: journeyResult.value.locator } : null,
  resultPending: journeyResult.value?.valid === true && !journeyResult.value.route && journeyResult.value.shouldPoll,
}));
const pageMounted = ref(false);
watch(() => recoveryState.refreshing, refreshing => {
  if (!refreshing) journeyLoading.value = false;
});

watch(
  [() => account.value?.id, () => isCustomerAccount(account.value), accountDeletionRequested],
  () => {
    journeyRecovery.invalidate();
    journeyProfile.value = null;
    journeySessionState.value = null;
    journeyResult.value = null;
    journeyLoading.value = isCustomer.value;
    journeyStale.value = isCustomer.value;
    if (isCustomer.value && pageMounted.value) refreshCustomerJourney();
  },
  { flush: 'sync', immediate: true },
);

function refreshCustomerJourney(force = false): void {
  if (!isCustomer.value) {
    journeyRecovery.setActive(false);
    journeyLoading.value = false;
    return;
  }
  if (!pageMounted.value || document.visibilityState === 'hidden' || navigator.onLine === false) {
    journeyRecovery.setActive(false);
    journeyLoading.value = false;
    journeyStale.value = true;
    return;
  }
  if (force) journeyRecovery.setActive(false);
  journeyLoading.value = true;
  void journeyRecovery.setActive(true);
}

function onVisibilityChange(): void {
  refreshCustomerJourney(document.visibilityState !== 'hidden');
}

function onOffline(): void {
  if (!isCustomer.value) return;
  journeyRecovery.setActive(false);
  journeyLoading.value = false;
  journeyStale.value = true;
}

function onOnline(): void {
  refreshCustomerJourney(true);
}

function parseCustomerProfile(value: unknown): CustomerProfileSnapshot['profile'] {
  if (value === null) return null;
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Neplatný profil zákazníka.');
  const status = (value as { status?: unknown }).status;
  if (typeof status !== 'string' || !status) throw new Error('Neplatný stav profilu zákazníka.');
  return { status };
}

function parseSessionState(value: CustomerSessionsResponse): string | null {
  if (!value || !Array.isArray(value.items)) throw new Error('Neplatný seznam rozhovorů.');
  const states = value.items.map(item => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new Error('Neplatný stav rozhovoru.');
    const state = (item as { state?: unknown }).state;
    if (typeof state !== 'string' || !state) throw new Error('Neplatný stav rozhovoru.');
    return state;
  });
  return states.find(state => !terminalSessionStates.has(state)) ?? states[0] ?? null;
}

onMounted(() => {
  pageMounted.value = true;
  document.addEventListener('visibilitychange', onVisibilityChange);
  window.addEventListener('online', onOnline);
  window.addEventListener('offline', onOffline);
  refreshCustomerJourney();
});
onBeforeUnmount(() => {
  pageMounted.value = false;
  document.removeEventListener('visibilitychange', onVisibilityChange);
  window.removeEventListener('online', onOnline);
  window.removeEventListener('offline', onOffline);
  journeyRecovery.dispose();
});
</script>

<template>
  <div class="home-layout" :class="{ 'home-layout--customer': isCustomer }">
    <EveningSetupLinks />
    <section class="home-introduction page-stack" :aria-label="customerCopy.text(isCustomer ? 'home.customer-context' : account ? 'home.host-context' : 'home.public-context')">
      <p class="eyebrow home-context">{{ customerCopy.text(isCustomer ? 'home.customer-context' : account ? 'home.host-context' : 'home.public-context') }}</p>
      <h1>{{ greeting }}</h1>
      <p class="home-lead">{{ customerCopy.text('home.lead') }}</p>
      <AwNotice v-if="['operator', 'administrator'].includes(String(route.query.access))" tone="warning">{{ customerCopy.text('home.access-role') }}</AwNotice>
      <AwNotice v-if="route.query.access === 'online'" tone="warning">{{ customerCopy.text('home.access-online') }}</AwNotice>
      <CustomerJourneyCard v-if="isCustomer" :journey="customerJourney" prefer-game />
      <nav v-if="isCustomer" class="home-destinations" :aria-label="customerCopy.text('home.tasks-aria')">
        <RouterLink class="home-destination" to="/me/character"><span class="home-destination-icon" aria-hidden="true">✦</span><span><strong>{{ customerCopy.text('shell.nav-character') }}</strong><small>{{ customerCopy.text('home.character-path') }}</small></span></RouterLink>
        <RouterLink class="home-destination" to="/play"><span class="home-destination-icon" aria-hidden="true">◇</span><span><strong>Hra</strong><small>{{ customerCopy.text('home.game-path') }}</small></span></RouterLink>
        <RouterLink class="home-destination" to="/me/keepsakes"><span class="home-destination-icon" aria-hidden="true">▤</span><span><strong>{{ customerCopy.text('shell.nav-keepsakes') }}</strong><small>{{ customerCopy.text('home.keepsakes-path') }}</small></span></RouterLink>
        <RouterLink class="home-destination" to="/me/account"><span class="home-destination-icon" aria-hidden="true">⚙</span><span><strong>{{ customerCopy.text('shell.nav-account') }}</strong><small>{{ customerCopy.text('home.account-path') }}</small></span></RouterLink>
      </nav>
      <nav v-else class="home-destinations" :aria-label="customerCopy.text('home.tasks-aria')">
        <RouterLink class="home-destination home-destination--primary" :to="account ? '/play' : '/demo'"><span class="home-destination-icon" aria-hidden="true">◇</span><span><strong>{{ account ? 'Hra' : customerCopy.text('home.demo-link') }}</strong><small>{{ customerCopy.text('home.game-path') }}</small></span></RouterLink>
        <RouterLink class="home-destination" to="/archive"><span class="home-destination-icon" aria-hidden="true">▤</span><span><strong>{{ customerCopy.text('shell.nav-archive') }}</strong><small>{{ customerCopy.text('home.keepsakes-path') }}</small></span></RouterLink>
        <RouterLink class="home-destination" :to="account ? '/settings' : '/signin'"><span class="home-destination-icon" aria-hidden="true">⚙</span><span><strong>{{ customerCopy.text('shell.nav-account') }}</strong><small>{{ customerCopy.text('home.account-path') }}</small></span></RouterLink>
      </nav>
    </section>
    <figure class="home-brand-art"><img :src="tableScene" alt="" width="853" height="1844" /></figure>
  </div>
</template>

<style scoped>
.home-context { margin: 0; }
.home-introduction { min-width: 0; }
.home-lead { max-width: 36ch; }
.home-destinations { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: .75rem; margin-top: 1rem; }
.home-destination { display: flex; align-items: center; gap: .8rem; min-width: 0; min-height: 4.5rem; padding: .85rem; border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-control); background: var(--aw-surface); color: var(--aw-ivory); text-decoration: none; }
.home-destination:hover { border-color: var(--aw-accent); }
.home-destination > span:last-child { display: grid; gap: .2rem; min-width: 0; }
.home-destination strong { font-weight: 600; }
.home-destination small { color: var(--aw-muted); line-height: 1.35; }
.home-destination-icon { display: grid; place-items: center; flex: 0 0 2.35rem; width: 2.35rem; height: 2.35rem; border-radius: 50%; color: var(--aw-accent); background: var(--aw-void); }
.home-destination--primary { border-color: var(--aw-accent); }
.home-brand-art { align-self: center; }
@media (max-width: 600px) {
  .home-destinations { grid-template-columns: minmax(0, 1fr); }
  .home-brand-art { order: -1; }
}
</style>
