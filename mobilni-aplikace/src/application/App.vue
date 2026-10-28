<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { Capacitor } from '@capacitor/core';
import { androidNavigationKey } from '../platform/androidAppLinks';
import { useRoute, useRouter } from 'vue-router';
import AwButton from '../components/AwButton.vue';
import AwNotice from '../components/AwNotice.vue';
import { notifications, dismissNotice, notify } from './notifications';
import { account, accountDeletionRequested, offlineSession, refreshIdentity } from '../features/identity/session';
import AccountAiConsent from '../features/identity/AccountAiConsent.vue';
import { deviceSession } from '../features/suitcase/deviceSession';
import { nativeDebug } from '../features/suitcase/nativeDebug';
import { directorStop, clearDirectorStop } from '../features/director/directorStop';
import { servoBench } from '../features/suitcase/servoBench';
import { accessDenial, canEnterStaffArea, isCustomerAccount } from './access';
import { accessRedirect } from './accessGuard';
import ShellPreferences from './ShellPreferences.vue';
import WnsNavigation from './WnsNavigation.vue';
import { wnsSound } from './wnsSound';
import { customerCopy } from './customerCopy';
import { customerMembershipState, launchAllowed, refreshCustomerMembership, refreshLaunchAccess } from './launchAccess';

const route = useRoute();
const router = useRouter();
const gameExperience = computed(() => route.path === '/play' || route.path.startsWith('/play/'));
const launchExperience = computed(() => route.path === '/launch' || route.path === '/signin'
  || customerMembershipState.value === 'revoked'
  || (customerAccount.value && customerMembershipState.value !== 'active')
  || (launchAllowed.value !== true && (['/privacy', '/support', '/account-deletion'].includes(route.path)
    || !canEnterStaffArea(account.value) && !account.value?.roles.includes('Tester'))));
const browserMode = !Capacitor.isNativePlatform();
const main = ref<HTMLElement>();
const navigationElement = ref<HTMLElement>();
const navigationHeight = ref('calc(5rem + var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px)))');
let navigationObserver: ResizeObserver | undefined;
const viewError = ref('');
const customerAccount = computed(() => isCustomerAccount(account.value));
const showCustomerNavigation = computed(() => customerAccount.value && !launchExperience.value
  && !/^\/play\/(?:match|team)\//.test(route.path) && !route.path.startsWith('/play/boss/') && route.path !== '/play/practice');
const navigation = computed(() => canEnterStaffArea(account.value) || account.value?.roles.includes('Tester') ? [
  { to: '/', label: 'Domů', symbol: '⌂' },
  { to: '/play', label: 'Hra', symbol: '◇' },
  ...(account.value?.roles.some(role => ['Owner', 'Administrator'].includes(role)) ? [{ to: '/settings/admin/launch', label: 'Večer', symbol: '✦' }] : []),
  { to: '/settings', label: customerCopy.text('shell.nav-account'), symbol: '⚙' },
] : [
  { to: '/', label: customerCopy.text('shell.nav-home'), symbol: '⌂' },
  { to: '/demo', label: 'Ukázka', symbol: '◇' },
  { to: '/archive', label: customerCopy.text('shell.nav-archive'), symbol: '▤' },
  { to: '/signin', label: customerCopy.text('shell.nav-account'), symbol: '⚙' },
]);
const identityKey = computed(() => account.value ? `${account.value.id}:${account.value.roles.join(',')}` : 'public');
const activeNavigationPath = computed(() => navigation.value
  .filter(item => item.to === '/' ? route.path === '/' : route.path === item.to || route.path.startsWith(`${item.to}/`))
  .sort((left, right) => right.to.length - left.to.length)[0]?.to);
function renderedViewKey(renderedRoute: { name?: string | symbol | null; fullPath: string; meta: { requiresAccount?: boolean } }): string {
  const identity = renderedRoute.meta.requiresAccount ? identityKey.value : 'public';
  return `${String(renderedRoute.name)}:${identity}:${androidNavigationKey(renderedRoute.fullPath)}`;
}
function revealActiveNavigation(): void {
  const navigation = navigationElement.value;
  const selected = navigation?.querySelector<HTMLElement>('a.selected');
  if (!navigation || !selected || !window.matchMedia('(max-width: 720px)').matches) return;
  const linkBounds = selected.getBoundingClientRect();
  const navigationBounds = navigation.getBoundingClientRect();
  navigation.scrollLeft += linkBounds.left - navigationBounds.left - (navigation.clientWidth - linkBounds.width) / 2;
}
watch(() => [route.path, identityKey.value], async () => {
  await nextTick();
  revealActiveNavigation();
});
const routeAccessible = computed(() => accessDenial(route.meta, account.value, offlineSession.value) === null);
const connected = computed(() => Boolean(deviceSession.snapshot.value && deviceSession.snapshot.value.phase !== 'disconnected'));
const deviceCanStop = computed(() => connected.value && deviceSession.snapshot.value?.phase !== 'stopped');
const phoneActive = computed(() => nativeDebug.status.value.running || nativeDebug.status.value.session || nativeDebug.pending.value);
const directorCanStop = computed(() => directorStop.value?.canStop() ?? false);
const stopBusy = computed(() => (deviceCanStop.value || phoneActive.value || directorCanStop.value)
  && (!deviceCanStop.value || deviceSession.stopPending.value)
  && (!phoneActive.value || nativeDebug.cancellationPending.value)
  && (!directorCanStop.value || directorStop.value?.busy())
  && !servoBench.connected.value);
async function stop(): Promise<void> {
  // Start independent cancellation paths together; a slow USB acknowledgement
  // cannot delay the already-permitted phone or suitcase Stop.
  const requests: Promise<unknown>[] = [Promise.resolve().then(() => deviceSession.stop())];
  if (servoBench.connected.value) requests.push(Promise.resolve().then(() => servoBench.stop()));
  if (phoneActive.value) requests.push(nativeDebug.cancel());
  if (directorCanStop.value && directorStop.value) requests.push(directorStop.value.stop());
  const results = await Promise.allSettled(requests);
  if (results.some(result => result.status === 'rejected')) notify('Stop se nepodařilo potvrdit. Zkontrolujte bezpečný stav.', 'danger');
}
watch(() => account.value ? `${account.value.id}:${account.value.roles.join(',')}` : '', (next, previous) => {
  if (next !== previous) clearDirectorStop();
  if (previous && next !== previous) {
    nativeDebug.invalidatePrivateState();
    void Promise.resolve(deviceSession.clearUserGrants()).catch(() => notify('Oprávnění této instalace se nepodařilo vyčistit. Odpojte ji před změnou uživatele.', 'danger'));
  }
  if (previous && !next && accountDeletionRequested.value) {
    void router.replace('/account-deletion');
    return;
  }
  const redirect = accessRedirect(route);
  if (redirect) void router.replace(redirect);
}, { flush: 'sync' });
watch(offlineSession, () => {
  if (!account.value && accountDeletionRequested.value) { return; }
  const redirect = accessRedirect(route);
  if (redirect) void router.replace(redirect);
}, { flush: 'sync' });
watch(customerMembershipState, state => {
  if (state !== 'revoked' || !customerAccount.value) return;
  nativeDebug.invalidatePrivateState();
  void nativeDebug.cancel();
  void Promise.resolve(deviceSession.clearUserGrants()).catch(() => {});
  if (!['/access-revoked', '/privacy', '/support', '/account-deletion', '/me/account'].includes(route.path)) {
    void router.replace('/access-revoked');
  }
}, { flush: 'sync' });
watch(() => route.path, () => {
  viewError.value = '';
  // Announce route changes while leaving same-route form/telemetry focus intact.
  requestAnimationFrame(() => main.value?.focus({ preventScroll: true }));
});
const removeRouterError = router.onError(() => { viewError.value = customerCopy.text('shell.page-error'); });
function refreshSession(): void {
  wnsSound.visible(!document.hidden);
  if (!document.hidden) {
    void refreshIdentity().then(() => {
      if (document.hidden) return;
      // Account changes reset cached access state synchronously. Fetch policies
      // after identity reconciliation so own-account routes can render the shell.
      void refreshLaunchAccess();
      void refreshCustomerMembership();
    }).catch(() => { /* Keep established identity and policies until verification succeeds. */ });
  }
}
onMounted(() => {
  refreshSession();
  if (typeof ResizeObserver !== 'undefined' && navigationElement.value) {
    navigationObserver = new ResizeObserver(() => {
      navigationHeight.value = `${navigationElement.value?.getBoundingClientRect().height ?? 0}px`;
      revealActiveNavigation();
    });
    navigationObserver.observe(navigationElement.value);
  }
  document.addEventListener('visibilitychange', refreshSession);
  void document.fonts?.ready.then(revealActiveNavigation);
  window.addEventListener('online', refreshSession);
});
onBeforeUnmount(() => {
  wnsSound.visible(false);
  removeRouterError();
  navigationObserver?.disconnect();
  document.removeEventListener('visibilitychange', refreshSession);
  window.removeEventListener('online', refreshSession);
});
</script>

<template>
  <a class="skip-link" href="#main-content">{{ customerCopy.text('shell.skip') }}</a>
  <AwButton v-if="(launchExperience || gameExperience) && (deviceCanStop || phoneActive || directorCanStop || servoBench.connected.value)" class="launch-emergency-stop" variant="danger" :busy="stopBusy" @click="stop">Stop</AwButton>
  <div class="application-shell" :class="{ 'launch-mode': launchExperience, 'game-mode': gameExperience, 'customer-mode': customerAccount, 'has-customer-navigation': showCustomerNavigation }" :style="{ '--application-navigation-height': navigationHeight, '--application-navigation-count': navigation.length }">
    <aside v-if="!customerAccount && !launchExperience && !gameExperience" class="application-sidebar" :aria-label="customerCopy.text('shell.navigation')">
      <RouterLink class="application-brand" to="/" :aria-label="customerCopy.text('shell.brand-home')"><img class="brand-mark" src="../assets/brand/emblem-small.webp" alt="" width="44" height="44" /><span>Wand Night Stand<small>{{ customerCopy.text('shell.brand-tagline') }}</small></span></RouterLink>
      <nav ref="navigationElement" class="primary-navigation" :aria-label="customerCopy.text('shell.sections')">
        <RouterLink v-for="item in navigation" :key="item.to" :to="item.to" :class="{ selected: item.to === activeNavigationPath }" :aria-current="item.to === activeNavigationPath ? 'page' : undefined">
          <span aria-hidden="true">{{ item.symbol }}</span><span>{{ item.label }}</span>
        </RouterLink>
      </nav>
      <div class="sidebar-details"><span class="eyebrow">{{ customerAccount ? customerCopy.text('shell.my-space') : account ? 'Pro hostitele' : 'Wand Night Stand' }}</span><p>{{ customerCopy.text(customerAccount ? 'shell.customer-guidance' : 'shell.public-guidance') }}</p></div>
    </aside>
    <div class="application-body">
      <header v-if="!launchExperience && !gameExperience" class="application-header">
        <p class="connection-context">{{ customerAccount ? customerCopy.text('shell.customer-area') : account ? 'Hostitelský prostor' : customerCopy.text('shell.public') }}</p>
        <div class="header-actions">
          <ShellPreferences :account-id="account?.id" />
          <RouterLink v-if="!account" class="account-link" to="/signin">{{ customerCopy.text('shell.sign-in') }}</RouterLink><RouterLink v-else class="account-link" :to="customerAccount ? '/me/account' : '/settings'">{{ account.displayName }}</RouterLink>
          <AwButton v-if="!launchExperience && !gameExperience && (deviceCanStop || phoneActive || directorCanStop || servoBench.connected.value)" variant="danger" :busy="stopBusy" @click="stop">Stop</AwButton>
        </div>
      </header>
      <div v-if="!launchExperience" class="notification-stack" :aria-label="customerCopy.text('shell.notifications')"><AwNotice v-for="notice in notifications" :key="notice.id" :tone="notice.tone"><div class="notification-content">{{ notice.message }}<button :aria-label="customerCopy.text('shell.close-notice')" @click="dismissNotice(notice.id)">×</button></div></AwNotice></div>
      <main id="main-content" ref="main" class="application-main" tabindex="-1">
        <AwNotice v-if="offlineSession && !launchExperience" tone="warning">{{ customerCopy.text('shell.offline') }}</AwNotice>
        <AwNotice v-if="viewError" tone="danger"><p>{{ viewError }}</p><AwButton @click="router.go(0)">{{ customerCopy.text('shell.reload') }}</AwButton></AwNotice>
        <AccountAiConsent v-else-if="account?.openAiConsent === false && !['/privacy', '/support', '/account-deletion'].includes(route.path)" />
        <RouterView v-else-if="routeAccessible" v-slot="{ Component, route: renderedRoute }">
          <KeepAlive :key="identityKey"><component :is="Component" v-if="renderedRoute.meta.keepAlive" :key="renderedRoute.name" /></KeepAlive>
          <component :is="Component" v-if="!renderedRoute.meta.keepAlive" :key="renderedViewKey(renderedRoute)" />
        </RouterView>
      </main>
      <WnsNavigation v-if="showCustomerNavigation" @resize="navigationHeight = `${$event}px`" />
      <footer v-if="!launchExperience && !gameExperience" class="application-footer"><span>Wand Night Stand</span><RouterLink to="/support">{{ customerCopy.text('shell.support') }}</RouterLink><RouterLink to="/privacy">{{ customerCopy.text('shell.privacy') }}</RouterLink><a v-if="browserMode" class="application-install-link" href="/ios/">{{ customerCopy.text('shell.iphone') }}</a><span>{{ customerCopy.text('shell.verification') }}</span></footer>
    </div>
  </div>
</template>
