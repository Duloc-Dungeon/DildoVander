import { createRouter, createWebHistory, type RouteLocationNormalizedLoaded } from 'vue-router';
import { watch } from 'vue';
import { installAccessGuard } from './accessGuard';
import { installInformationNavigation } from './informationNavigation';
import { customerCopy, type CustomerCopyKey } from './customerCopy';
import { accountToolRoutes } from './accountToolRoutes';

export const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    { path: '/', name: 'home', redirect: to => ({ path: '/signin', query: to.query, hash: to.hash === '#director' ? '' : to.hash }) },
    { path: '/demo', redirect: '/' },
    { path: '/support', name: 'support', component: () => import('../features/information/SupportView.vue'), meta: { title: 'Podpora' } },
    { path: '/privacy', name: 'privacy', component: () => import('../features/information/PrivacyView.vue'), meta: { title: 'Ochrana soukromí' } },
    { path: '/launch', name: 'launch', component: () => import('../features/launch/LaunchView.vue'), meta: { title: 'Brzy', private: true, requiresAccount: true } },
    { path: '/account-deletion', name: 'account-deletion', component: () => import('../features/identity/AccountDeletionStatusView.vue'), meta: { title: 'Stav smazání účtu', private: true } },
    { path: '/access-revoked', name: 'access-revoked', component: () => import('../features/identity/RevokedAccessView.vue'), meta: { title: 'Přístup odebrán', private: true, requiresAccount: true } },
    { path: '/me', redirect: '/me/character' },
    { path: '/play/pod-peceti', name: 'sealed-case-lobby', component: () => import('../features/sealed-case/SealedCaseView.vue'), meta: { title: 'Pod pečetí', private: true, requiresAccount: true } },
    { path: '/play/pod-peceti/:id', name: 'sealed-case', component: () => import('../features/sealed-case/SealedCaseView.vue'), meta: { title: 'Pod pečetí', private: true, requiresAccount: true } },
    { path: '/play/wand', name: 'wand-workshop', component: () => import('../features/game/WandWorkshopView.vue'), meta: { title: 'Kufrova dílna', private: true, requiresAccount: true } },
    { path: '/play/teams', name: 'game-teams', component: () => import('../features/game/TeamView.vue'), meta: { title: 'Spojenci', private: true, requiresAccount: true } },
    { path: '/play/team/:id', name: 'game-team', component: () => import('../features/game/TeamView.vue'), meta: { title: 'Týmový souboj', private: true, requiresAccount: true } },
    { path: '/play/chronicle', name: 'game-chronicle-redirect', redirect: '/me/keepsakes' },
    ...['/play', '/play/challenge', '/play/practice', '/play/chronicle', '/play/match/:id', '/play/boss', '/play/boss/:id', '/play/keeper', '/play/ranking', '/play/messages'].map((path, index) => ({ path, name: `game-${index}`, component: () => import('../features/game/PlayView.vue'), meta: { title: 'Hra', private: true, requiresAccount: true } })),
    { path: '/me/character', name: 'customer-character', component: () => import('../features/customer/CustomerView.vue'), meta: { title: 'Moje postava', private: true, requiresAccount: true } },
    { path: '/me/conversation', name: 'customer-conversation', redirect: '/play/wand' },
    { path: '/me/keepsakes', name: 'customer-keepsakes', component: () => import('../features/customer/CustomerView.vue'), meta: { title: 'Moje památky', private: true, requiresAccount: true } },
    { path: '/me/account', name: 'customer-account', component: () => import('../features/customer/CustomerView.vue'), meta: { title: 'Můj účet', private: true, requiresAccount: true } },
    ...accountToolRoutes,
    ...['/tonight', '/suitcase/:pathMatch(.*)*', '/connections', '/app/:pathMatch(.*)*', '/companion/:pathMatch(.*)*'].map(path => ({ path, redirect: '/play' })),
    ...['/workshop/:pathMatch(.*)*', '/simulator/:pathMatch(.*)*'].map(path => ({ path, redirect: '/play/wand' })),
    ...['/archive', '/archive/chronicle', '/keepsake/chronicle.html', '/keepsake/evening/:pathMatch(.*)*'].map(path => ({ path, redirect: '/me/keepsakes' })),
    { path: '/archive/duel', name: 'duel', component: () => import('../features/archive/DuelView.vue'), meta: { title: 'Souboj', private: true } },
    { path: '/keepsake/duel.html', redirect: to => ({ path: '/archive/duel', query: to.query }) },
    { path: '/keepsake/:pathMatch(.*)*', name: 'keepsake', component: () => import('../features/archive/KeepsakeView.vue'), meta: { title: 'Moje památka', private: true } },
    { path: '/signin', name: 'signin', component: () => import('../features/identity/SignInView.vue'), meta: { title: 'Přihlášení', private: true } },
    { path: '/settings', name: 'settings', component: () => import('../features/identity/SettingsView.vue'), meta: { title: 'Nastavení', private: true, requiresAccount: true, lab: true } },
    { path: '/settings/admin', name: 'admin', component: () => import('../features/identity/AdminView.vue'), meta: { title: 'Správa', private: true, requiresAccount: true, administrator: true, staff: true } },
    { path: '/settings/admin/launch', name: 'launch-admin', component: () => import('../features/launch/LaunchManagementView.vue'), meta: { title: 'Večer a pozvánky', private: true, requiresAccount: true, administrator: true, staff: true } },
    { path: '/settings/admin/characters', name: 'character-admin', component: () => import('../features/customer/CharacterAdminView.vue'), meta: { title: 'Postavy a portréty', private: true, requiresAccount: true, administrator: true, staff: true } },
    { path: '/:pathMatch(.*)*', name: 'notFound', component: () => import('./NotFoundView.vue'), meta: { title: 'Stránka nenalezena' } },
  ],
  scrollBehavior(to, from, saved) {
    if (saved) return saved;
    if ((to.path === '/settings/admin' && to.hash === '#invitations')
      || (to.path === '/settings/admin/launch' && to.hash === '#waiting-room')) return { el: to.hash, top: 112 };
    return to.path === from.path ? false : { top: 0 };
  },
});

const localizedRouteTitles: Partial<Record<string, CustomerCopyKey>> = {
  home: 'home.title',
  support: 'support.title',
  privacy: 'privacy.title',
  'account-deletion': 'deletion.title',
  'customer-character': 'customer.character-pane',
  'customer-conversation': 'customer.conversation-pane',
  'customer-keepsakes': 'customer.keepsakes-pane',
  'customer-account': 'customer.account-pane',
  keepsake: 'route.title-keepsake',
  signin: 'signin.title',
  notFound: 'route.title-not-found',
};

function updateDocumentTitle(to: Pick<RouteLocationNormalizedLoaded, 'name' | 'meta'> = router.currentRoute.value): void {
  const key = to.name ? localizedRouteTitles[String(to.name)] : undefined;
  const title = key ? customerCopy.text(key) : to.meta.title ?? 'Wand Night Stand';
  document.title = `${title} · Wand Night Stand`;
}

router.afterEach(to => {
  updateDocumentTitle(to);
  let robots = document.querySelector<HTMLMetaElement>('meta[name="robots"]');
  if (to.meta.private) {
    if (!robots) { robots = document.createElement('meta'); robots.name = 'robots'; document.head.append(robots); }
    robots.content = 'noindex, nofollow, noarchive';
  } else { robots?.remove(); }
});

watch(customerCopy.language, () => updateDocumentTitle(), { flush: 'sync' });

installInformationNavigation(router);
installAccessGuard(router);
