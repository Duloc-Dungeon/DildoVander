import { beforeEach, expect, it, vi } from 'vitest';
import { readRickrollVideo } from './rickrollMedia';

const api = vi.hoisted(() => ({ request: vi.fn(), native: vi.fn() }));
vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: api.native } }));
vi.mock('../identity/session', () => ({ accountRequest: api.request, accountOrigin: { value: 'https://dildovander.de' } }));
beforeEach(() => { api.native.mockReturnValue(false); api.request.mockReset(); });

it('uses same-origin streaming on web and the authenticated backend origin on native', async () => {
  api.request.mockResolvedValue({ available: true, url: '/app/v1/launch/rickroll.mp4' });
  expect(await readRickrollVideo()).toBe('/app/v1/launch/rickroll.mp4');
  api.native.mockReturnValue(true);
  expect(await readRickrollVideo()).toBe('https://dildovander.de/app/v1/launch/rickroll.mp4');
});

it.each([{ available: false, url: null }, { available: true, url: '/other.mp4' }, { available: true, url: 'https://other.invalid/video.mp4' }])('does not enable missing or unexpected media: %j', async response => {
  api.request.mockResolvedValue(response);
  expect(await readRickrollVideo()).toBeUndefined();
});
