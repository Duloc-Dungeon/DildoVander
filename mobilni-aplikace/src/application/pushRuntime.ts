import { watch } from 'vue';
import type { Router } from 'vue-router';
import { account, accountDeletionRequested, accountRequest, initialized } from '../features/identity/session';
import { hasPushPermission, listenForNativePush, pushPlatform, registerPush, supportsPush, type PushSettings } from '../platform/pushNotifications';
import { pausePushReconnection, pushReconnectionCheck, resumePushReconnection, serializePushMutation } from '../platform/pushCoordinator';

export function startPushRuntime(router: Router): void {
  if (pushPlatform() === 'web' || !supportsPush()) return;
  let generation = 0;
  let identity = account.value?.id;
  let pendingPath: string | undefined;
  const listenerReady = listenForNativePush(token => {
    const expected = generation;
    const checkReconnection = pushReconnectionCheck();
    const accountId = account.value?.id;
    if (!accountId || accountDeletionRequested.value) return;
    void (async () => {
      const settings = await accountRequest<PushSettings>('/app/v1/push');
      if (settings.enabled && expected === generation && accountId === account.value?.id && !accountDeletionRequested.value) {
        await serializePushMutation(() => {
          checkReconnection();
          if (expected !== generation || accountId !== account.value?.id || accountDeletionRequested.value) throw new Error('Účet se změnil.');
          return accountRequest('/app/v1/push', 'PUT', { platform: pushPlatform(), token });
        });
      }
    })().catch(() => {});
  }, path => {
    // Cold-start taps can arrive while identity and the initial route are loading.
    pendingPath = path;
    if (initialized.value) openPending();
  });
  function openPending() {
    const path = pendingPath;
    pendingPath = undefined;
    if (path) void router.isReady().then(() => router.push(path)).catch(() => {});
  }
  watch([() => account.value?.id, initialized, accountDeletionRequested], async () => {
    const expected = ++generation;
    if (identity !== account.value?.id) {
      identity = account.value?.id;
      resumePushReconnection();
    }
    if (accountDeletionRequested.value) pausePushReconnection();
    const checkReconnection = pushReconnectionCheck();
    if (initialized.value && pendingPath) openPending();
    const accountId = account.value?.id;
    if (!initialized.value || !accountId || accountDeletionRequested.value) return;
    const check = () => {
      checkReconnection();
      if (expected !== generation || accountId !== account.value?.id || accountDeletionRequested.value) throw new Error('Účet se změnil.');
    };
    try {
      await listenerReady;
      check();
      const settings = await accountRequest<PushSettings>('/app/v1/push');
      check();
      if (!settings.enabled || !settings[pushPlatform()] || !await hasPushPermission()) return;
      check();
      const registration = await registerPush(settings, check);
      check();
      await serializePushMutation(() => { check(); return accountRequest('/app/v1/push', 'PUT', registration); });
    } catch {
      // Reconnecting push must never block account startup. Settings offers retry.
    }
  }, { immediate: true });
  void listenerReady.catch(() => {});
}
