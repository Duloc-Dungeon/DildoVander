import { onScopeDispose, ref, watch } from 'vue';
import { account, accountTransport } from '../identity/session';
import { mediaEssence } from '../archive/client';
import type { Match } from './game';

/** Only the match-scoped, revision-bound route can supply the opponent's approved PNG. */
export function useOpponentWandArtwork() {
  const url = ref('');
  let key = '';
  let generation = 0;
  let controller: AbortController | undefined;
  let pending: Promise<void> | undefined;

  function clear() {
    generation++;
    controller?.abort(); controller = undefined; pending = undefined; key = '';
    if (url.value) URL.revokeObjectURL(url.value);
    url.value = '';
  }
  async function refresh(match: Match | undefined) {
    const accountId = account.value?.id;
    const path = match?.opponent.wandArtworkUrl;
    const matchId = match?.id;
    if (!match || !accountId || !matchId || match.mode === 'boss' || !/^[a-f0-9]{32}$/i.test(matchId)
      || typeof path !== 'string' || /\s/.test(path) || !new RegExp(`^/app/v1/game/matches/${matchId}/opponent/wand-artwork\\?revision=[a-f0-9]{64}$`, 'i').test(path)) {
      clear(); return;
    }
    const nextKey = `${accountId}/${matchId}/${match.opponent.id}/${path}`;
    if (key === nextKey && (url.value || pending)) return pending;
    clear(); key = nextKey;
    const token = generation;
    const request = new AbortController(); controller = request;
    const load = async () => {
      try {
        const response = await accountTransport(path, { method: 'GET', signal: request.signal, redirect: 'error', cache: 'no-store' });
        if (!response.ok) throw new Error('Opponent artwork unavailable');
        const image = await response.blob();
        if (mediaEssence(image.type) !== 'image/png') throw new Error('Opponent artwork is not PNG');
        if (token !== generation || request.signal.aborted || account.value?.id !== accountId) return;
        url.value = URL.createObjectURL(image);
      } catch {
        if (token === generation) clear();
      }
    };
    const promise = load(); pending = promise;
    try { await promise; }
    finally { if (pending === promise) pending = undefined; }
  }
  watch(() => account.value?.id, clear, { flush: 'sync' });
  onScopeDispose(clear);
  return { url, refresh, clear };
}
