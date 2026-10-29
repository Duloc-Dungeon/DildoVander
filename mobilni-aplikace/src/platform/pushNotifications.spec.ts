import { beforeEach, describe, expect, it, vi } from 'vitest';
const native = vi.hoisted(() => ({ platform: 'web', register: vi.fn(), addListener: vi.fn(), unregister: vi.fn(), availability: vi.fn(), checkPermissions: vi.fn() }));
vi.mock('@capacitor/core', () => ({ Capacitor: { getPlatform: () => native.platform, isPluginAvailable: () => true }, registerPlugin: () => ({ pushAvailability: native.availability }) }));
vi.mock('@capacitor/push-notifications', () => ({ PushNotifications: native }));
import { decodeApplicationKey, listenForNativePush, nativePushConfigured, readPushPermission, registerPush, safePushPath, unregisterLocalPush } from './pushNotifications';

describe('notification permission state', () => {
  it.each(['default', 'granted', 'denied'])('distinguishes web %s without asking for permission', async permission => {
    native.platform = 'web';
    vi.stubGlobal('Notification', { permission });
    expect(await readPushPermission()).toBe(permission === 'default' ? 'prompt' : permission);
    vi.unstubAllGlobals();
  });
  it.each(['prompt', 'prompt-with-rationale', 'granted', 'denied'])('reads native %s without registering', async receive => {
    native.platform = 'android'; native.checkPermissions.mockResolvedValue({ receive });
    expect(await readPushPermission()).toBe(receive === 'prompt-with-rationale' ? 'prompt' : receive);
    native.platform = 'web';
  });
});

describe('push destinations', () => {
  it('admits only known internal destinations', () => {
    expect(safePushPath('/play')).toBe('/play');
    expect(safePushPath('/play/match/abc-123')).toBe('/play/match/abc-123');
    for (const path of ['https://evil.example', '//evil.example', '/play?token=secret', '/play/../settings', '/play/match/a%2fb', null]) {
      expect(safePushPath(path)).toBeNull();
    }
  });
});

describe('web push registration', () => {
  const key = Uint8Array.from({ length: 65 }, (_, index) => index === 0 ? 4 : index);
  const encoded = btoa(String.fromCharCode(...key)).replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_');
  const settings = { web: true, android: false, ios: false, publicKey: encoded, enabled: false };
  beforeEach(() => { native.platform = 'web'; vi.restoreAllMocks(); });

  it('validates the VAPID public key', () => {
    expect(decodeApplicationKey(encoded)).toEqual(key);
    expect(() => decodeApplicationKey('bad')).toThrow();
  });

  it('reuses an existing subscription without prompting or creating another one', async () => {
    const existing = { options: { applicationServerKey: key.buffer }, toJSON: () => ({ endpoint: 'https://push.example/id', keys: { p256dh: 'key', auth: 'auth' } }) };
    const subscribe = vi.fn();
    vi.stubGlobal('navigator', { serviceWorker: { register: vi.fn().mockResolvedValue({ active: {}, pushManager: { getSubscription: vi.fn().mockResolvedValue(existing), subscribe } }) } });
    expect(await registerPush(settings, () => {})).toEqual({ platform: 'web', endpoint: 'https://push.example/id', p256dh: 'key', auth: 'auth' });
    expect(subscribe).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });

  it('never registers a device after the account context is invalidated', async () => {
    expect(await registerPush(settings, () => { throw new Error('changed account'); }).catch(error => error.message)).toBe('changed account');
  });

  it('does not call Firebase registration in Android builds without client configuration', async () => {
    native.platform = 'android';
    native.availability.mockResolvedValue({ available: false });
    expect(await nativePushConfigured()).toBe(false);
    await expect(registerPush(settings, () => {})).rejects.toThrow('nejsou v tomto sestavení připravena');
    expect(native.register).not.toHaveBeenCalled();
    await unregisterLocalPush();
    expect(native.unregister).not.toHaveBeenCalled();
    native.platform = 'web';
  });

  it('registers a native token, handles registration failure and restricts tap destinations', async () => {
    native.platform = 'ios';
    const callbacks: Record<string, (value: any) => void> = {};
    native.addListener.mockImplementation(async (name, callback) => {
      callbacks[name] = callback;
      return { remove: vi.fn() };
    });
    const tokenChanged = vi.fn();
    const navigate = vi.fn();
    await listenForNativePush(tokenChanged, navigate);
    native.register.mockImplementation(async () => { callbacks.registration!({ value: 'apns-token' }); });
    expect(await registerPush(settings, () => {})).toEqual({ platform: 'ios', token: 'apns-token' });
    expect(tokenChanged).toHaveBeenCalledWith('apns-token');
    callbacks.pushNotificationActionPerformed!({ notification: { data: { url: '/play' } } });
    callbacks.pushNotificationActionPerformed!({ notification: { data: { url: 'https://evil.example' } } });
    expect(navigate).toHaveBeenCalledExactlyOnceWith('/play');
    native.register.mockImplementation(async () => { callbacks.registrationError!({ error: 'rejected' }); });
    await expect(registerPush(settings, () => {})).rejects.toThrow('Zařízení se nepodařilo');
    native.platform = 'web';
  });
});
