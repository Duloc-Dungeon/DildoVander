import { Capacitor } from '@capacitor/core';
import { accountOrigin, accountRequest } from '../identity/session';

const videoPath = '/app/v1/launch/rickroll.mp4';

export async function readRickrollVideo(signal?: AbortSignal): Promise<string | undefined> {
  try {
    const value = await accountRequest<{ available: boolean; url: string | null }>('/app/v1/launch/rickroll', 'GET', undefined, signal);
    if (signal?.aborted || !value.available || value.url !== videoPath) return;
    if (!Capacitor.isNativePlatform()) return videoPath;
    const origin = new URL(accountOrigin.value);
    if (origin.protocol !== 'https:' || origin.username || origin.password) return;
    return new URL(videoPath, origin).href;
  } catch { /* Missing media never blocks normal entry or gameplay. */ }
}
