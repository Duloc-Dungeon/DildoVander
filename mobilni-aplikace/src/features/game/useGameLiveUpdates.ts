import { onBeforeUnmount, onMounted, watch } from 'vue';
import { account } from '../identity/session';
import { connectGameLive } from './gameLive';

/** Coalesce notifications without losing one arriving during a read or command. */
export function useGameLiveUpdates(refresh: () => Promise<void>, blocked: () => boolean) {
  let stop: (() => void) | undefined;
  let pending = false;
  let draining = false;
  let mounted = false;

  async function drain() {
    if (!mounted || !pending || draining || blocked() || document.hidden || !account.value) return;
    pending = false;
    draining = true;
    try { await refresh(); }
    finally {
      draining = false;
      // A burst requires at most one further snapshot, read after the command.
      if (pending) void drain();
    }
  }
  function changed() { pending = true; void drain(); }
  function visible() { if (!document.hidden && pending) void drain(); }
  function reconnect() {
    stop?.(); stop = undefined; pending = false;
    if (mounted && account.value) stop = connectGameLive(changed);
  }
  watch(blocked, () => { void drain(); });
  watch(() => account.value?.id, reconnect, { flush: 'sync' });
  onMounted(() => {
    mounted = true;
    reconnect();
    document.addEventListener('visibilitychange', visible);
  });
  onBeforeUnmount(() => {
    mounted = false; pending = false;
    stop?.();
    document.removeEventListener('visibilitychange', visible);
  });
}
