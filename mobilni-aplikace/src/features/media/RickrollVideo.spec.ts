import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import RickrollVideo from './RickrollVideo.vue';

const source = '/app/v1/launch/rickroll.mp4';
beforeEach(() => {
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue();
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
  vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => {});
});
afterEach(() => { vi.restoreAllMocks(); });

it('preloads while waiting, then automatically starts unmuted at 80% on activation', async () => {
  const view = mount(RickrollVideo, { props: { src: source, active: false } });
  try {
    await flushPromises();
    expect(view.element.style.display).toBe('none');
    expect(view.get('video').attributes('preload')).toBe('auto');
    expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled();
    await view.setProps({ active: true });
    await flushPromises();
    const video = view.get('video').element;
    expect(video.volume).toBe(0.8);
    expect(video.muted).toBe(false);
    expect(video.hasAttribute('playsinline')).toBe(true);
    expect(HTMLMediaElement.prototype.play).toHaveBeenCalledOnce();
    await view.setProps({ active: false });
    await flushPromises();
    expect(HTMLMediaElement.prototype.pause).toHaveBeenCalled();
  } finally { view.unmount(); }
});

it('explains blocked autoplay without losing the exit', async () => {
  vi.mocked(HTMLMediaElement.prototype.play).mockRejectedValue(new DOMException('blocked', 'NotAllowedError'));
  const view = mount(RickrollVideo, { props: { src: source, active: true } });
  try {
    await flushPromises();
    expect(view.get('[role="status"]').text()).toContain('zablokoval');
    await view.get('button').trigger('click');
    expect(view.emitted('dismiss')).toHaveLength(1);
  } finally { view.unmount(); }
});

it('finishes cleanly and releases the media on leaving', async () => {
  const view = mount(RickrollVideo, { props: { src: source, active: true } });
  await flushPromises();
  const video = view.get('video').element;
  await view.get('video').trigger('ended');
  expect(view.emitted('dismiss')).toHaveLength(1);
  view.unmount();
  expect(video.hasAttribute('src')).toBe(false);
  expect(HTMLMediaElement.prototype.load).toHaveBeenCalled();
});
