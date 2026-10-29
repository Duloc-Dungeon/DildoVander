import { ref } from 'vue';

export const pushSettingsRevision = ref(0);
const dismissalRevision = ref(0);
const fallbackDismissals = new Set<string>();
const dismissalKey = (accountId: string) => `wns:push-prompt:${accountId}`;

export function isPushPromptDismissed(accountId: string): boolean {
  // The revision keeps other mounted offers in sync with a dismissal.
  void dismissalRevision.value;
  if (fallbackDismissals.has(accountId)) return true;
  try { return sessionStorage.getItem(dismissalKey(accountId)) === 'later'; }
  catch { return false; }
}

export function dismissPushPrompt(accountId: string): void {
  try { sessionStorage.setItem(dismissalKey(accountId), 'later'); }
  catch { fallbackDismissals.add(accountId); }
  dismissalRevision.value++;
}
