import { onScopeDispose, ref, watch } from 'vue';
import { account, accountRequest } from '../identity/session';
import { AccountArchiveClient } from '../archive/accountArchive';
import { projectMobileGeneratedWand } from '../archive/generatedWand';
import { projectWandArtwork } from '../archive/wandArtwork';

interface CharacterSnapshot {
  generatedWand?: unknown;
  artwork?: unknown;
}

/** Loads only the signed-in player's canonical, owner-checked wand illustration. */
export function useGameWandArtwork() {
  const url = ref('');
  const name = ref('');
  const synthetic = ref(false);
  const artworkClient = new AccountArchiveClient(() => account.value?.id ?? '');
  let scopeKey = '';
  let boundArtworkKey = '';
  let generation = 0;
  let request: AbortController | undefined;
  let pending: { key: string; promise: Promise<void> } | undefined;

  function releaseUrl() {
    if (url.value) URL.revokeObjectURL(url.value);
    url.value = '';
  }

  function clear() {
    generation++;
    request?.abort();
    request = undefined;
    pending = undefined;
    scopeKey = '';
    boundArtworkKey = '';
    artworkClient.invalidate();
    releaseUrl();
    name.value = '';
    synthetic.value = false;
  }

  async function load(accountId: string, wandId: string, token: number) {
    const controller = new AbortController();
    request = controller;
    try {
      const snapshot = await accountRequest<CharacterSnapshot>(
        '/app/v1/customer/character', 'GET', undefined, controller.signal,
      );
      if (token !== generation || controller.signal.aborted || account.value?.id !== accountId) return;

      const wand = projectMobileGeneratedWand(snapshot.generatedWand);
      if (!wand || wand.wandId !== wandId) {
        boundArtworkKey = '';
        releaseUrl();
        name.value = '';
        synthetic.value = false;
        return;
      }

      name.value = wand.name;
      const artwork = projectWandArtwork(snapshot.artwork);
      if (!artwork || !['ready', 'repairing', 'repair_failed'].includes(artwork.status) || !artwork.assetId) {
        boundArtworkKey = '';
        releaseUrl();
        synthetic.value = false;
        return;
      }

      const key = `${accountId}/${artwork.locator}/${artwork.assetId}/${artwork.version}`;
      synthetic.value = artwork.synthetic;
      if (key === boundArtworkKey && url.value) return;
      boundArtworkKey = key;
      releaseUrl();

      const blobPath = `/app/v1/archive/owned/${artwork.locator}/assets/${artwork.assetId}`;
      const image = await artworkClient.blob(blobPath, ['image/png']);
      if (token !== generation || controller.signal.aborted || account.value?.id !== accountId || key !== boundArtworkKey) return;
      url.value = URL.createObjectURL(image);
    } catch (error) {
      if (token === generation && !controller.signal.aborted && (error as Error).name !== 'AbortError') {
        boundArtworkKey = '';
        releaseUrl();
        synthetic.value = false;
      }
    } finally {
      if (request === controller) request = undefined;
    }
  }

  async function refresh(wandId: string | undefined) {
    const accountId = account.value?.id;
    if (!wandId || !accountId) {
      clear();
      return;
    }

    const nextScope = `${accountId}/${wandId}`;
    if (nextScope !== scopeKey) {
      clear();
      scopeKey = nextScope;
    }

    if (pending?.key === nextScope) return pending.promise;
    const token = ++generation;
    const promise = load(accountId, wandId, token);
    pending = { key: nextScope, promise };
    try {
      await promise;
    } finally {
      if (pending?.promise === promise) pending = undefined;
    }
  }

  watch(() => account.value?.id, clear, { flush: 'sync' });
  onScopeDispose(clear);

  return { url, name, synthetic, refresh, clear };
}
