import { Capacitor } from '@capacitor/core';
import { accountRequest } from '../features/identity/session';
import { createApplicationUpdates } from './applicationUpdates';

let storage: Storage | undefined;
try { storage = window.localStorage; } catch { /* Private webviews can deny storage. */ }
const platform = Capacitor.isNativePlatform() ? Capacitor.getPlatform() : 'web';
export const applicationUpdates = createApplicationUpdates({
  platform: platform === 'ios' || platform === 'android' ? platform : 'web',
  getInfo: async () => (await import('@capacitor/app')).App.getInfo(),
  loadPolicy: signal => accountRequest('/app/v1/application-version', 'GET', undefined, signal),
  storage,
});
