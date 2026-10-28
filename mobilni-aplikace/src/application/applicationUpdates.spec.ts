import { afterEach, describe, expect, it, vi } from 'vitest';
import { compareApplicationBuilds, createApplicationUpdates, parseApplicationBuild, parseVersionPolicy } from './applicationUpdates';

const policy = { minimumBuild: '100', latestBuild: '110', updateUrl: 'https://example.test/ios/' };
function fixture(build = '99', cached?: unknown) {
  const storage = {
    getItem: vi.fn(() => cached === undefined ? null : JSON.stringify(cached)),
    setItem: vi.fn(),
  };
  const getInfo = vi.fn(async () => ({ version: '1.0', build }));
  const loadPolicy = vi.fn(async (_signal: AbortSignal): Promise<unknown> => ({ ios: policy }));
  const updates = createApplicationUpdates({ platform: 'ios', getInfo, loadPolicy, storage });
  return { updates, getInfo, loadPolicy, storage };
}
afterEach(() => vi.useRealTimers());

describe('application version enforcement', () => {
  it('keeps the initial gate closed after native info resolves while the policy is pending', async () => {
    const { updates, loadPolicy } = fixture();
    let respond!: (value: unknown) => void;
    loadPolicy.mockImplementation(() => new Promise(resolve => { respond = resolve; }));
    const checking = updates.check();
    await vi.waitFor(() => expect(loadPolicy).toHaveBeenCalledOnce());
    expect(updates.installed.value?.build).toBe('99');
    expect(updates.blocked.value).toBe(true);
    respond({ ios: policy });
    await checking;
    expect(updates.required.value).toBe(true);
    expect(updates.blocked.value).toBe(true);
  });
  it.each([
    ['10.9.1', '10.10.1', -1], ['10.10.2', '10.10.1', 1], ['10.10', '10.10.0', 0], ['1000', '999.99.99', 1],
  ])('compares dotted iOS builds %s and %s numerically', (left, right, expected) => {
    expect(compareApplicationBuilds(parseApplicationBuild(left)!, parseApplicationBuild(right)!)).toBe(expected);
  });
  it('enforces the actual dotted iOS release build format', async () => {
    const { updates, loadPolicy } = fixture('10.9.1');
    loadPolicy.mockResolvedValue({ ios: { ...policy, minimumBuild: '10.10.1', latestBuild: '10.10.2' } });
    await updates.check();
    expect(updates.required.value).toBe(true);
  });
  it('holds the initial route until the installed build has been checked', async () => {
    const { updates } = fixture();
    expect(updates.blocked.value).toBe(true);
    await updates.check();
    expect(updates.required.value).toBe(true);
    expect(updates.blocked.value).toBe(true);
    expect(updates.checking.value).toBe(false);
  });
  it.each(['100', '109', '110', '1000'])('compares build %s numerically and includes the minimum', async build => {
    const { updates } = fixture(build);
    await updates.check();
    expect(updates.blocked.value).toBe(false);
    expect(updates.available.value).toBe(Number(build) < 110);
  });
  it('selects the native platform independently', async () => {
    const updates = createApplicationUpdates({ platform: 'android',
      getInfo: async () => ({ version: '0.1.0', build: '1000001' }),
      loadPolicy: async () => ({ ios: policy, android: { ...policy, minimumBuild: '1000002', latestBuild: '1000002' } }),
    });
    await updates.check();
    expect(updates.required.value).toBe(true);
    expect(updates.policy.value?.minimumBuild).toBe('1000002');
  });
  it('keeps mandatory updates across restarts and failed or malformed checks', async () => {
    const { updates, loadPolicy } = fixture('99', policy);
    expect(updates.blocked.value).toBe(true);
    loadPolicy.mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce({ ios: { ...policy, updateUrl: 'javascript:alert(1)' } });
    await updates.check();
    expect(updates.required.value).toBe(true);
    await updates.check();
    expect(updates.policy.value).toEqual(policy);
    expect(updates.blocked.value).toBe(true);
    expect(updates.error.value).not.toBe('');
  });
  it('does not strand a known supported installation on a first offline check', async () => {
    const { updates, loadPolicy } = fixture('100');
    loadPolicy.mockRejectedValue(new Error('offline'));
    await updates.check();
    expect(updates.blocked.value).toBe(false);
    expect(updates.policy.value).toBe(null);
  });
  it('allows a successful authoritative rollback to revoke the minimum', async () => {
    const { updates, loadPolicy, storage } = fixture('99', policy);
    loadPolicy.mockResolvedValue({ ios: { minimumBuild: '0', latestBuild: '0', updateUrl: '' } });
    await updates.check();
    expect(updates.blocked.value).toBe(false);
    expect(storage.setItem).toHaveBeenCalledWith('wns.application-version.ios.v1', JSON.stringify(updates.policy.value));
  });
  it.each(['', 'abc', '1.2.3.4', '-1', '0', '2147483648', '1\n'])('does not bypass enforcement with unknown build %s', async build => {
    const { updates } = fixture(build);
    await updates.check();
    expect(updates.blocked.value).toBe(true);
    expect(updates.error.value).not.toBe('');
  });
  it('bounds a hung check, coalesces observers and permits retry', async () => {
    vi.useFakeTimers();
    const { updates, getInfo } = fixture();
    getInfo.mockImplementationOnce(() => new Promise(() => {}));
    const first = updates.check();
    const second = updates.check();
    await vi.advanceTimersByTimeAsync(8000);
    await Promise.all([first, second]);
    expect(getInfo).toHaveBeenCalledTimes(1);
    expect(updates.checking.value).toBe(false);
    expect(updates.blocked.value).toBe(true);
    await updates.check();
    expect(updates.required.value).toBe(true);
    expect(updates.error.value).toBe('');
  });
  it('retains the live block when storage is unavailable', async () => {
    const { updates, storage } = fixture();
    storage.setItem.mockImplementation(() => { throw new Error('storage denied'); });
    await updates.check();
    expect(updates.required.value).toBe(true);
    expect(updates.error.value).toBe('');
  });
  it('leaves the web build outside native version comparison', async () => {
    const { getInfo, loadPolicy } = fixture();
    const updates = createApplicationUpdates({ platform: 'web', getInfo, loadPolicy });
    await updates.check();
    expect(updates.blocked.value).toBe(false);
    expect(getInfo).not.toHaveBeenCalled();
    expect(loadPolicy).not.toHaveBeenCalled();
  });
});

describe('untrusted policy validation', () => {
  it.each([null, {}, { ...policy, minimumBuild: -1 }, { ...policy, minimumBuild: '120' },
    { ...policy, latestBuild: 1.5 }, { ...policy, latestBuild: '2147483648' },
    { ...policy, updateUrl: 'http://example.test/install' }, { ...policy, updateUrl: '//example.test/' },
    { ...policy, updateUrl: 'https://user:secret@example.test/' }, { ...policy, updateUrl: '' }])('rejects invalid policies', value => {
    expect(parseVersionPolicy(value)).toBe(null);
  });
});
