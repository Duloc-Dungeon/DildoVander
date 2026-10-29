import type { Router, RouteLocationNormalized } from 'vue-router';
import { account, authenticationGenerationSnapshot, offlineSession, offlineSessionExpiresAt, refreshIdentity } from '../features/identity/session';
import { accessDenial, canEnterStaffArea, requiresCustomerMembership } from './access';
import { safeReturnPath } from './navigation';
import { launchAllowed, refreshCustomerMembership, refreshLaunchAccess } from './launchAccess';
import { notify } from './notifications';

export function accessRedirect(route: Pick<RouteLocationNormalized, 'meta' | 'fullPath'>) {
  const denied = accessDenial(route.meta, account.value, offlineSession.value);
  if (denied === 'signin') return { path: '/signin', query: { return: safeReturnPath(route.fullPath) }, replace: true };
  if (denied === 'staff') return { path: '/me/character', replace: true };
  if (denied) return { path: '/', query: { access: denied }, replace: true };
  return null;
}

/** Reconcile an already open screen only after the server confirms the evening is closed. */
export function waitingRoomRedirect(route: Pick<RouteLocationNormalized, 'meta' | 'path'>) {
  if (launchAllowed.value !== false || !account.value || !route.meta.requiresAccount
    || canEnterStaffArea(account.value) || account.value.roles.includes('Tester')
    || ['/launch', '/me/account', '/access-revoked', '/privacy', '/support', '/account-deletion'].includes(route.path)) return null;
  return { path: '/launch', replace: true };
}

export function installAccessGuard(router: Router): void {
  router.beforeEach(async (to, from) => {
    // Vue matches case and trailing-slash variants. Resolve the named record so
    // every screen and guard sees its canonical path, preserving dynamic IDs.
    if (to.name) {
      const canonical = router.resolve({ name: to.name, params: to.params, query: to.query, hash: to.hash });
      if (canonical.path !== to.path) return { path: canonical.path, query: to.query, hash: to.hash, replace: true };
    }
    const publicPath = to.name === 'notFound' || ['/signin', '/privacy', '/support', '/account-deletion'].includes(to.path);
    if (publicPath) return;
    if (to.meta.offlineLocal && offlineSession.value && offlineSessionExpiresAt.value > Date.now()
      && account.value?.roles.some(role => ['Owner', 'Administrator', 'Operator', 'Tester'].includes(role))) {
      return accessRedirect(to) ?? undefined;
    }
    // A route transition checks the server session, not a remembered UI role.
    try { await refreshIdentity(); } catch {
      if (account.value) {
        notify('Připojení k účtu se nepodařilo ověřit. Zkontrolujte připojení a zkuste to znovu.', 'danger');
        if (from.matched.length > 0) return false;
      }
      return { path: '/signin', query: { return: safeReturnPath(to.fullPath) }, replace: true };
    }
    const generation = authenticationGenerationSnapshot();
    const identityKey = () => JSON.stringify([account.value?.id, [...(account.value?.roles ?? [])].sort()]);
    const identity = identityKey();
    const reconcileIdentity = () => {
      if (generation === authenticationGenerationSnapshot() && identity === identityKey()) return null;
      return accessRedirect(to) ?? { path: to.path, query: to.query, hash: to.hash, replace: true };
    };
    if (!account.value) {
      if (to.meta.requiresAccount) return { path: '/signin', query: { return: safeReturnPath(to.fullPath) }, replace: true };
      const allowed = await refreshLaunchAccess();
      const changed = reconcileIdentity();
      if (changed) return changed;
      if (allowed) return undefined;
      return { path: '/signin', query: { return: safeReturnPath(to.fullPath) }, replace: true };
    }
    const denied = accessRedirect(to);
    if (denied) return denied;
    if (requiresCustomerMembership(account.value) && !['/access-revoked', '/privacy', '/support', '/account-deletion', '/me/account'].includes(to.path)) {
      const membership = await refreshCustomerMembership();
      const changed = reconcileIdentity();
      if (changed) return changed;
      if (membership === 'revoked') return { path: '/access-revoked', replace: true };
      if (membership !== 'active' && to.path !== '/launch') return { path: '/launch', replace: true };
    }
    if (['/access-revoked', '/me/account'].includes(to.path)) return undefined;
    if (to.path !== '/launch' && account.value && !account.value.roles.includes('Owner')) {
      const allowed = await refreshLaunchAccess();
      const changed = reconcileIdentity();
      if (changed) return changed;
      if (!allowed) return { path: '/launch', replace: true };
    }
    return undefined;
  });
}
