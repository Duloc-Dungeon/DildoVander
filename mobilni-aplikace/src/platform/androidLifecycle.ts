import { Capacitor } from '@capacitor/core';
import type { Router } from 'vue-router';
import { androidAppLink, captureAndroidAppLink } from './androidAppLinks';
import { nativeDebugBridge } from '../features/debug/NativeDebugBridge';

// Keep web and iOS navigation unchanged; Android's hardware Back uses Vue history.
export async function installAndroidLifecycle(router: Router): Promise<void> {
  if (Capacitor.getPlatform() !== 'android') return;
  const { App } = await import('@capacitor/app');
  let opening = false;
  const open = async (url: string) => {
    const target = androidAppLink(url);
    if (!target || opening) return;
    opening = true;
    try {
      // Leave the previous microphone/session before consuming another identity.
      await nativeDebugBridge<{ stop(): Promise<void> }>().stop();
      const destination = captureAndroidAppLink(url);
      if (destination) await router.replace(destination);
    } finally {
      opening = false;
    }
  };
  await App.addListener('appUrlOpen', ({ url }) => { void open(url).catch(() => {}); });
  const launch = await App.getLaunchUrl();
  if (launch) void open(launch.url).catch(() => {});
  await App.addListener('backButton', async () => {
    const focused = document.activeElement;
    if (focused instanceof HTMLElement && focused.matches('input, textarea, [contenteditable="true"]')) {
      focused.blur();
      return;
    }
    if (window.history.state?.back) router.back();
    else await App.minimizeApp();
  });
  await App.addListener('appStateChange', ({ isActive }) => {
    if (isActive) window.dispatchEvent(new Event('online'));
  });
}
