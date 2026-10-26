import { computed, ref } from 'vue';
import { Capacitor } from '@capacitor/core';
import { App } from '@capacitor/app';

const prefix = 'wand.invitation-presentation.v1.';
export const invitationIntroDuration = 8_000;
export const presentationForeground = ref(typeof document !== 'undefined' && !document.hidden);
const elapsed = ref(0);
let activeKey = '';
let clock: VisiblePresentationClock;
let ticker: ReturnType<typeof setInterval> | undefined;
let nativeActive = true;
let installed = false;

export class VisiblePresentationClock {
  elapsed: number;
  private anchor: number | null = null;
  constructor(elapsed = 0) { this.elapsed = Math.max(0, Math.min(invitationIntroDuration, elapsed)); }
  update(now: number, visible: boolean): number {
    if (this.anchor !== null) this.elapsed = Math.min(invitationIntroDuration, this.elapsed + Math.max(0, now - this.anchor));
    this.anchor = visible && this.elapsed < invitationIntroDuration ? now : null;
    return this.elapsed;
  }
}

function storageKey(accountId: string, creationId: string): string {
  return prefix + JSON.stringify([accountId, creationId]);
}
function persist(): void {
  if (!activeKey) return;
  try { localStorage.setItem(activeKey, String(elapsed.value)); } catch { /* The in-memory clock still pauses correctly. */ }
}
function checkpoint(): void {
  if (!clock) return;
  elapsed.value = clock.update(performance.now(), !!activeKey && presentationForeground.value);
  persist();
}
function visibility(): void {
  // Account for the interval up to this event before pausing it.
  checkpoint();
  presentationForeground.value = !document.hidden && nativeActive;
  checkpoint();
}
function install(): void {
  if (installed || typeof window === 'undefined') return;
  installed = true;
  document.addEventListener('visibilitychange', visibility);
  window.addEventListener('pagehide', () => { checkpoint(); presentationForeground.value = false; checkpoint(); });
  window.addEventListener('pageshow', visibility);
  if (Capacitor.isNativePlatform()) {
    void App.addListener('appStateChange', ({ isActive }) => { nativeActive = isActive; visibility(); });
    void App.getState().then(({ isActive }) => { nativeActive = isActive; visibility(); }).catch(() => {});
  }
}

/** Called only at the first actual consented PUT, never during the passkey ceremony. */
export function beginInvitationPresentation(accountId: string, creationId: string): void {
  selectInvitationPresentation(accountId, creationId);
}
export function selectInvitationPresentation(accountId?: string, creationId?: string): void {
  install();
  const nextKey = accountId && creationId ? storageKey(accountId, creationId) : '';
  if (activeKey === nextKey) return;
  checkpoint();
  activeKey = nextKey;
  if (ticker) clearInterval(ticker);
  ticker = undefined;
  let saved = 0;
  try { saved = Number(localStorage.getItem(activeKey)); } catch { /* Start in memory. */ }
  elapsed.value = Number.isFinite(saved) ? Math.max(0, Math.min(invitationIntroDuration, saved)) : 0;
  clock = new VisiblePresentationClock(elapsed.value);
  checkpoint();
  if (activeKey) ticker = setInterval(checkpoint, 100);
}
export function clearInvitationPresentation(accountId: string, creationId: string): void {
  const key = storageKey(accountId, creationId);
  if (key === activeKey) selectInvitationPresentation();
  try { localStorage.removeItem(key); } catch { /* Best effort in restricted storage. */ }
}
export function finishInvitationPresentation(): void {
  if (!activeKey) return;
  elapsed.value = invitationIntroDuration;
  clock = new VisiblePresentationClock(invitationIntroDuration);
  persist();
}
export const invitationVisibleMs = computed(() => elapsed.value);
