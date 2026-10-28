import { Capacitor, registerPlugin, type PluginListenerHandle } from '@capacitor/core';
import { PushNotifications } from '@capacitor/push-notifications';

export type PushPlatform = 'web' | 'android' | 'ios';
export interface PushSettings {
  web: boolean;
  android: boolean;
  ios: boolean;
  publicKey: string | null;
  enabled: boolean;
}
export interface PushRegistration {
  platform: PushPlatform;
  token?: string;
  endpoint?: string;
  p256dh?: string;
  auth?: string;
}

export function pushPlatform(): PushPlatform {
  const platform = Capacitor.getPlatform();
  return platform === 'android' || platform === 'ios' ? platform : 'web';
}

export function supportsPush(): boolean {
  return pushPlatform() !== 'web'
    ? Capacitor.isPluginAvailable('PushNotifications')
    : window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
}

export async function nativePushConfigured(): Promise<boolean> {
  if (pushPlatform() !== 'android') return true;
  try {
    const identity = registerPlugin<{ pushAvailability(): Promise<{ available: boolean }> }>('KufrIdentity');
    return (await identity.pushAvailability()).available === true;
  } catch { return false; }
}

export function safePushPath(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  return ['/play', '/launch', '/me', '/me/account'].includes(value)
    || /^\/play\/match\/[A-Za-z0-9-]{1,80}$/.test(value) ? value : null;
}

export function decodeApplicationKey(value: string): Uint8Array<ArrayBuffer> {
  const decoded = atob(value.replace(/-/g, '+').replace(/_/g, '/'));
  const bytes = Uint8Array.from(decoded, character => character.charCodeAt(0));
  if (bytes.length !== 65 || bytes[0] !== 4) throw new Error('Neplatné nastavení oznámení.');
  return bytes;
}

let worker: Promise<ServiceWorkerRegistration> | undefined;
export function pushWorker(): Promise<ServiceWorkerRegistration> {
  worker ??= navigator.serviceWorker.register('/push-sw.js', { scope: '/' }).then(async registration => {
    if (registration.active) return registration;
    // This worker deliberately does not control pages or cache private content.
    const installing = registration.installing ?? registration.waiting;
    if (!installing) throw new Error('Oznámení se nepodařilo připravit.');
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => { installing.removeEventListener('statechange', changed); reject(new Error('Oznámení se nepodařilo připravit.')); }, 15000);
      function changed() {
        if (installing!.state === 'activated' || installing!.state === 'redundant') {
          clearTimeout(timer);
          installing!.removeEventListener('statechange', changed);
          if (installing!.state === 'activated') resolve();
          else reject(new Error('Oznámení se nepodařilo připravit.'));
        }
      }
      installing.addEventListener('statechange', changed);
      changed();
    });
    return registration;
  }).catch(error => { worker = undefined; throw error; });
  return worker;
}

let listeners: Promise<void> | undefined;
let onToken: (token: string) => void = () => {};
let onOpen: (path: string) => void = () => {};
let pendingToken: { resolve: (token: string) => void; reject: (error: Error) => void } | undefined;

export async function listenForNativePush(token: (value: string) => void, open: (path: string) => void): Promise<void> {
  onToken = token;
  onOpen = open;
  if (pushPlatform() === 'web' || !supportsPush()) return Promise.resolve();
  if (!await nativePushConfigured()) throw new Error('Oznámení nejsou v tomto sestavení připravena.');
  listeners ??= (async () => {
    const added: PluginListenerHandle[] = [];
    try {
      added.push(await PushNotifications.addListener('registration', registration => {
        pendingToken?.resolve(registration.value);
        onToken(registration.value);
      }));
      added.push(await PushNotifications.addListener('registrationError', () => {
        pendingToken?.reject(new Error('Zařízení se nepodařilo přihlásit k oznámením.'));
      }));
      added.push(await PushNotifications.addListener('pushNotificationActionPerformed', action => {
        const path = safePushPath(action.notification.data?.url);
        if (path) onOpen(path);
      }));
    } catch (error) {
      await Promise.all(added.map(listener => listener.remove()));
      listeners = undefined;
      throw error;
    }
  })();
  return listeners;
}

async function nativeToken(): Promise<string> {
  if (!listeners) throw new Error('Oznámení ještě nejsou připravena.');
  await listeners;
  if (pendingToken) throw new Error('Přihlášení k oznámením už probíhá.');
  return new Promise<string>((resolve, reject) => {
    let settled = false;
    const timer = setTimeout(() => finish(new Error('Přihlášení k oznámením trvalo příliš dlouho.')), 20000);
    function finish(error?: Error, token?: string) {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      pendingToken = undefined;
      if (error) reject(error);
      else resolve(token!);
    }
    pendingToken = { resolve: token => finish(undefined, token), reject: error => finish(error) };
    void PushNotifications.register().catch(() => finish(new Error('Zařízení se nepodařilo přihlásit k oznámením.')));
  });
}

/** Called only from an explicit user action; permission prompts must keep that gesture. */
export async function requestPushPermission(): Promise<boolean> {
  if (pushPlatform() === 'web') return await Notification.requestPermission() === 'granted';
  return (await PushNotifications.requestPermissions()).receive === 'granted';
}

export async function registerPush(settings: PushSettings, check: () => void): Promise<PushRegistration> {
  const platform = pushPlatform();
  check();
  if (platform !== 'web') {
    if (!await nativePushConfigured()) throw new Error('Oznámení nejsou v tomto sestavení připravena.');
    check();
    const token = await nativeToken();
    check();
    return { platform, token };
  }
  if (!settings.publicKey) throw new Error('Oznámení nejsou na serveru připravena.');
  const registration = await pushWorker();
  check();
  const existing = await registration.pushManager.getSubscription();
  check();
  const key = decodeApplicationKey(settings.publicKey);
  if (existing?.options.applicationServerKey
    && !equalBytes(new Uint8Array(existing.options.applicationServerKey), key)) {
    await existing.unsubscribe();
    check();
  }
  const subscription = existing && equalBytes(new Uint8Array(existing.options.applicationServerKey ?? []), key)
    ? existing : await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
  check();
  const json = subscription.toJSON();
  if (!json.endpoint || !json.keys?.p256dh || !json.keys.auth) throw new Error('Prohlížeč vrátil neplatné přihlášení k oznámením.');
  return { platform, endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth };
}

function equalBytes(left: Uint8Array, right: Uint8Array): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index]);
}

export async function hasPushPermission(): Promise<boolean> {
  return pushPlatform() === 'web' ? Notification.permission === 'granted'
    : (await PushNotifications.checkPermissions()).receive === 'granted';
}

export async function unregisterLocalPush(): Promise<void> {
  if (pushPlatform() !== 'web') {
    if (!await nativePushConfigured()) return;
    await PushNotifications.unregister();
    return;
  }
  const registration = await navigator.serviceWorker.getRegistration('/push-sw.js');
  const subscription = await registration?.pushManager.getSubscription();
  await subscription?.unsubscribe();
}
