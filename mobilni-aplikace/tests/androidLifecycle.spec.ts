import { beforeEach, describe, expect, it, vi } from 'vitest';
const runtime = vi.hoisted(() => ({
  platform: 'android', listeners: new Map<string, (event: Record<string, unknown>) => Promise<void> | void>(),
  stop: vi.fn().mockResolvedValue(undefined), minimize: vi.fn().mockResolvedValue(undefined),
  launch: undefined as { url: string } | undefined,
}));
vi.mock('@capacitor/core', () => ({ Capacitor: { getPlatform: () => runtime.platform } }));
vi.mock('@capacitor/app', () => ({ App: {
  addListener: vi.fn(async (name, handler) => { runtime.listeners.set(name, handler); }),
  getLaunchUrl: vi.fn(async () => runtime.launch), minimizeApp: runtime.minimize,
} }));
vi.mock('../src/features/debug/NativeDebugBridge', () => ({ nativeDebugBridge: () => ({ stop: runtime.stop }) }));
import { installAndroidLifecycle } from '../src/platform/androidLifecycle';

describe('Android native lifecycle contract', () => {
  beforeEach(() => {
    vi.clearAllMocks(); runtime.platform = 'android'; runtime.launch = undefined; runtime.listeners.clear();
    delete window.__dildovanderEnrollment;
  });
  it('stops native audio and handles cold/warm links through sanitized router navigation', async () => {
    const router = { replace: vi.fn().mockResolvedValue(undefined), back: vi.fn() };
    runtime.launch = { url: 'https://dildovander.de/signin#invite=' + 'a'.repeat(43) };
    await installAndroidLifecycle(router as never);
    await vi.waitFor(() => expect(router.replace).toHaveBeenCalledWith('/signin'));
    expect(runtime.stop).toHaveBeenCalledOnce();
    expect(window.__dildovanderEnrollment?.token).toBe('a'.repeat(43));
    runtime.listeners.get('appUrlOpen')!({ url: 'https://dildovander.de/signin#recovery=' + 'b'.repeat(43) });
    await vi.waitFor(() => expect(runtime.stop).toHaveBeenCalledTimes(2));
    await vi.waitFor(() => expect(window.__dildovanderEnrollment?.token).toBe('b'.repeat(43)));
    expect(router.replace.mock.calls.every(call => call[0] === '/signin')).toBe(true);
  });
  it('does not install Android navigation on web or accept an unrelated URL', async () => {
    runtime.platform = 'web';
    const router = { replace: vi.fn(), back: vi.fn() };
    await installAndroidLifecycle(router as never);
    expect(runtime.listeners.size).toBe(0);
    runtime.platform = 'android';
    await installAndroidLifecycle(router as never);
    runtime.listeners.get('appUrlOpen')!({ url: 'https://evil.test/signin#invite=secret' });
    expect(runtime.stop).not.toHaveBeenCalled();
    expect(router.replace).not.toHaveBeenCalled();
  });
});
