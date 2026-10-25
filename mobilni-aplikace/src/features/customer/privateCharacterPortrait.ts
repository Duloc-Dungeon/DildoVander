import { onScopeDispose, ref, watch } from 'vue';
import { accountTransport } from '../identity/session';

export interface PrivatePortraitIdentity {
  accountId: string;
  profileId: string;
  revision: number;
}

function identityKey(value: PrivatePortraitIdentity | null): string {
  return value && value.accountId && value.profileId && Number.isSafeInteger(value.revision) && value.revision > 0
    ? JSON.stringify([value.accountId, value.profileId, value.revision]) : '';
}

async function decodePortrait(url: string): Promise<void> {
  const image = new Image();
  image.src = url;
  await image.decode();
}

/** Private portraits never share the public archive transport or its cache. */
export function usePrivateCharacterPortrait(
  identity: () => PrivatePortraitIdentity | null,
  transport: typeof accountTransport = accountTransport,
  decode: (url: string) => Promise<void> = decodePortrait,
) {
  const url = ref('');
  const pending = ref(false);
  const failed = ref(false);
  let epoch = 0;
  let controller: AbortController | undefined;

  function clear(): void {
    epoch++;
    controller?.abort();
    controller = undefined;
    if (url.value) URL.revokeObjectURL(url.value);
    url.value = '';
    pending.value = false;
    failed.value = false;
  }

  async function load(): Promise<void> {
    clear();
    const target = identity();
    const expectedKey = identityKey(target);
    if (!target || !expectedKey) return;
    const captured = epoch;
    const request = controller = new AbortController();
    const current = () => captured === epoch && !request.signal.aborted && identityKey(identity()) === expectedKey;
    pending.value = true;
    let candidate = '';
    try {
      const path = `/app/v1/prelaunch/character/portrait?profileId=${encodeURIComponent(target.profileId)}&revision=${target.revision}`;
      const response = await transport(path, {
        headers: new Headers({ Accept: 'image/png, image/jpeg, image/webp' }), signal: request.signal, redirect: 'error',
      });
      if (!current()) return;
      if (!response.ok) throw new Error('portrait_unavailable');
      const image = await response.blob();
      if (!current()) return;
      if (!['image/png', 'image/jpeg', 'image/webp'].includes(image.type) || image.size === 0 || image.size > 8 * 1024 * 1024) {
        throw new Error('portrait_invalid');
      }
      candidate = URL.createObjectURL(image);
      await decode(candidate);
      if (!current()) return;
      url.value = candidate;
      candidate = '';
    } catch {
      if (current()) failed.value = true;
    } finally {
      if (candidate) URL.revokeObjectURL(candidate);
      if (current()) pending.value = false;
    }
  }

  function fail(): void { clear(); failed.value = true; }
  watch(() => identityKey(identity()), () => { void load(); }, { immediate: true, flush: 'sync' });
  onScopeDispose(clear);
  return { url, pending, failed, retry: load, clear, fail };
}
