import type { Router } from 'vue-router';
import { safeReturnPath } from './navigation';

const informationPaths = ['/support', '/privacy'];

export function informationReturnPath(value: unknown): string | undefined {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//') || /[\\\u0000-\u0020]/.test(value)) return;
  const pathname = value.split(/[?#]/)[0];
  if (['/signin', '/launch', '/access-revoked', '/account-deletion'].includes(pathname)) return value;
  if (safeReturnPath(value) === value) return value;
}

/** Keep the entry page across support/privacy links and reloads, without a back-button loop. */
export function installInformationNavigation(router: Router): void {
  let entry = informationReturnPath(router.options.history.state.informationReturn);
  router.afterEach((to, from, failure) => {
    if (failure || !informationPaths.includes(to.path)) return;
    const state = router.options.history.state;
    const source = informationPaths.includes(from.path)
      ? state.informationReturn ?? entry
      : from.matched.length ? from.fullPath : state.informationReturn ?? state.back;
    entry = informationReturnPath(source);
    router.options.history.replace(to.fullPath, { informationReturn: entry ?? null });
  });
}
