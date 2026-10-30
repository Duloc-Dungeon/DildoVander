export const rickrollWindowMs = 15 * 60 * 1000;

export function withinRickrollWindow(launchAt: number | null, serverNow: number): boolean {
  return launchAt !== null && Number.isFinite(launchAt) && Number.isFinite(serverNow)
    && serverNow >= launchAt && serverNow - launchAt < rickrollWindowMs;
}

export function rickrollSeen(accountId: string, launchAt: number): boolean {
  try { return sessionStorage.getItem(`wand.rickroll.v2.${accountId}.${launchAt}`) === 'seen'; }
  catch { return false; }
}

export function markRickrollSeen(accountId: string, launchAt: number): void {
  try { sessionStorage.setItem(`wand.rickroll.v2.${accountId}.${launchAt}`, 'seen'); }
  catch { /* The current component still remembers completion without storage. */ }
}
