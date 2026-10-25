import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { effectScope, ref } from 'vue';
import { flushPromises } from '@vue/test-utils';
import { usePrivateCharacterPortrait, type PrivatePortraitIdentity } from './privateCharacterPortrait';

vi.mock('../identity/session', () => ({ accountTransport: vi.fn() }));
const scopes: ReturnType<typeof effectScope>[] = [];
const image = () => new Response(new Blob(['portrait'], { type: 'image/webp' }));
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(complete => { resolve = complete; });
  return { promise, resolve };
}
function fixture(transport: (path: string, init?: RequestInit) => Promise<Response>, decode: (url: string) => Promise<void> = vi.fn(async () => {})) {
  const identity = ref<PrivatePortraitIdentity | null>({ accountId: 'owner', profileId: 'profile-a', revision: 1 });
  const scope = effectScope();
  scopes.push(scope);
  const portrait = scope.run(() => usePrivateCharacterPortrait(() => identity.value, transport, decode))!;
  return { identity, portrait, scope };
}
beforeEach(() => {
  let next = 0;
  vi.spyOn(URL, 'createObjectURL').mockImplementation(() => `blob:portrait-${++next}`);
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
});
afterEach(() => { scopes.splice(0).forEach(scope => scope.stop()); vi.restoreAllMocks(); });

it('binds bytes to profile identity even when a replacement reuses revision one', async () => {
  const old = deferred<Response>();
  const transport = vi.fn().mockReturnValueOnce(old.promise).mockResolvedValue(image());
  const { identity, portrait } = fixture(transport);
  identity.value = { accountId: 'owner', profileId: 'profile-b', revision: 1 };
  await flushPromises();
  expect(portrait.url.value).toBe('blob:portrait-1');
  old.resolve(image());
  await flushPromises();
  expect(portrait.url.value).toBe('blob:portrait-1');
  expect(URL.createObjectURL).toHaveBeenCalledTimes(1);
  expect(transport.mock.calls.map(([path]) => path)).toEqual([
    '/app/v1/prelaunch/character/portrait?profileId=profile-a&revision=1',
    '/app/v1/prelaunch/character/portrait?profileId=profile-b&revision=1',
  ]);
});

it('discards a late decode after profile or account changes and revokes both blobs', async () => {
  const oldDecode = deferred<void>();
  const decode = vi.fn().mockReturnValueOnce(oldDecode.promise).mockResolvedValue(undefined);
  const { identity, portrait, scope } = fixture(vi.fn().mockImplementation(async () => image()), decode);
  await flushPromises();
  identity.value = { accountId: 'another-owner', profileId: 'profile-a', revision: 1 };
  await flushPromises();
  expect(portrait.url.value).toBe('blob:portrait-2');
  oldDecode.resolve();
  await flushPromises();
  expect(portrait.url.value).toBe('blob:portrait-2');
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:portrait-1');
  scope.stop();
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:portrait-2');
});

it('clears a deleted profile immediately and never republishes its in-flight image', async () => {
  const response = deferred<Response>();
  const transport = vi.fn().mockReturnValue(response.promise);
  const { identity, portrait } = fixture(transport);
  identity.value = null;
  expect((transport.mock.calls[0][1] as RequestInit).signal?.aborted).toBe(true);
  response.resolve(image());
  await flushPromises();
  expect(portrait.url.value).toBe('');
  expect(URL.createObjectURL).not.toHaveBeenCalled();
});

it('rejects unsafe content and retries image delivery with GET only, without generating again', async () => {
  const transport = vi.fn().mockResolvedValueOnce(new Response('<html>error</html>', { headers: { 'Content-Type': 'text/html' } })).mockResolvedValueOnce(image());
  const { portrait } = fixture(transport);
  await flushPromises();
  expect(portrait.failed.value).toBe(true);
  expect(portrait.url.value).toBe('');
  await portrait.retry();
  expect(portrait.failed.value).toBe(false);
  expect(portrait.url.value).toBe('blob:portrait-1');
  expect(transport.mock.calls.every(([path, options]) => path.includes('/portrait?') && !options.method)).toBe(true);
});
