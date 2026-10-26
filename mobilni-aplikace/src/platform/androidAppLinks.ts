// Keep externally supplied URLs outside router history until the existing
// pre-module claim capture has removed their one-use fragments.
import { reactive } from 'vue';

const navigationRevisions = reactive(new Map<string, number>());
export function androidNavigationKey(path: string): string {
  const revision = navigationRevisions.get(path);
  return revision ? `${path}:${revision}` : 'default';
}

export function androidAppLink(url: string): string | null {
  let parsed: URL;
  try { parsed = new URL(url); } catch { return null; }
  if (parsed.origin !== 'https://dildovander.de' || parsed.username || parsed.password) return null;
  if (!['/signin', '/signin/', '/archive/duel', '/keepsake'].includes(parsed.pathname)
      && !parsed.pathname.startsWith('/keepsake/')) return null;
  return parsed.pathname + parsed.search + parsed.hash;
}

export function captureAndroidAppLink(raw: string): string | null {
  const target = androidAppLink(raw);
  if (!target || target.length > 8192) return null;
  const parsed = new URL(raw);
  if (['/signin', '/signin/'].includes(parsed.pathname)) {
    delete window.__dildovanderEnrollment;
    const enrollment = parsed.hash.match(/^#(invite|recovery)=([A-Za-z0-9_-]{32,256})$/);
    if (enrollment) window.__dildovanderEnrollment = { kind: enrollment[1] as 'invite' | 'recovery', token: enrollment[2]! };
  } else if (['/archive/duel', '/keepsake/duel.html'].includes(parsed.pathname)) {
    delete window.__dildovanderDuelGrant;
    const grant = parsed.hash.match(/^#grant=([A-Za-z0-9_-]{43})&side=(left|right)$/);
    const reversed = parsed.hash.match(/^#side=(left|right)&grant=([A-Za-z0-9_-]{43})$/);
    if (grant) window.__dildovanderDuelGrant = { token: grant[1]!, side: grant[2] as 'left' | 'right' };
    else if (reversed) window.__dildovanderDuelGrant = { token: reversed[2]!, side: reversed[1] as 'left' | 'right' };
  } else {
    delete window.__dildovanderGuestClaim;
    if (parsed.hash) window.__dildovanderGuestClaim = parsed.hash.slice(1);
  }
  const aliases: Record<string, string> = {
    '/keepsake/duel.html': '/archive/duel', '/keepsake/chronicle.html': '/archive/chronicle',
    '/keepsake/evening/': '/archive', '/keepsake/evening': '/archive', '/keepsake/evening/index.html': '/archive',
  };
  const destination = (aliases[parsed.pathname] ?? parsed.pathname) + parsed.search;
  // Updating only the destination key cannot remount the current, different
  // route and let it steal the new one-use claim before navigation commits.
  navigationRevisions.set(destination, (navigationRevisions.get(destination) ?? 0) + 1);
  return destination;
}
