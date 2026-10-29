import type { Router, RouteLocationNormalized } from 'vue-router';
import { account, accountRequest, offlineSession, offlineSessionExpiresAt, refreshIdentity } from '../features/identity/session';
import { accessDenial, canEnterStaffArea, isCustomerAccount } from './access';
import { safeReturnPath } from './navigation';
import { launchAllowed, refreshCustomerMembership } from './launchAccess';
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
  router.beforeEach(async to => {
    const publicPath = ['/signin', '/privacy', '/support', '/account-deletion'].includes(to.path)
      || import.meta.env.DEV && to.path === '/local-try-hub';
    if (publicPath) return;
    if (to.meta.offlineLocal && offlineSession.value && offlineSessionExpiresAt.value > Date.now()
      && account.value?.roles.some(role => ['Owner', 'Administrator', 'Operator', 'Tester'].includes(role))) {
      return accessRedirect(to) ?? undefined;
    }
    // A route transition checks the server session, not a remembered UI role.
    try { await refreshIdentity(); } catch {
      if (account.value) {
        notify('Připojení k účtu se nepodařilo ověřit. Zkontrolujte připojení a zkuste to znovu.', 'danger');
        return false;
      }
      return { path: '/signin', query: { return: safeReturnPath(to.fullPath) }, replace: true };
    }
    if (!account.value) {
      if (to.meta.requiresAccount) return { path: '/signin', query: { return: safeReturnPath(to.fullPath) }, replace: true };
      try {
        const launch = await accountRequest<{ allowed: boolean }>('/app/v1/launch');
        launchAllowed.value = launch.allowed === true;
        if (launch.allowed) return undefined;
      } catch { launchAllowed.value = null; }
      return { path: '/signin', query: { return: safeReturnPath(to.fullPath) }, replace: true };
    }
    const denied = accessRedirect(to);
    if (denied) return denied;
    if (isCustomerAccount(account.value) && !account.value.roles.includes('Owner') && !['/access-revoked', '/privacy', '/support', '/account-deletion', '/me/account'].includes(to.path)) {
      const membership = await refreshCustomerMembership();
      if (membership === 'revoked') return { path: '/access-revoked', replace: true };
      if (membership !== 'active' && to.path !== '/launch') return { path: '/launch', replace: true };
    }
    if (['/access-revoked', '/me/account'].includes(to.path)) return undefined;
    if (to.path !== '/launch' && account.value && !account.value.roles.includes('Owner')) {
      try {
        const launch = await accountRequest<{ allowed: boolean }>('/app/v1/launch');
        launchAllowed.value = launch.allowed === true;
        if (!launch.allowed) return { path: '/launch', replace: true };
      } catch {
        launchAllowed.value = null;
        return { path: '/launch', replace: true };
      }
    }
    return undefined;
  });
}
