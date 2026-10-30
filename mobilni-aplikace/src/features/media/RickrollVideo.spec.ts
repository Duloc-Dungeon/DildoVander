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

it('explains blocked autoplay and offers a play button while keeping the countdown locked', async () => {
  vi.mocked(HTMLMediaElement.prototype.play).mockRejectedValue(new DOMException('blocked', 'NotAllowedError'));
  const view = mount(RickrollVideo, { props: { src: source, active: true } });
  try {
    await flushPromises();
    expect(view.get('[role="status"]').text()).toContain('zablokoval');
    await view.get('button').trigger('click');
    expect(view.emitted('dismiss')).toBeUndefined();
  } finally { view.unmount(); }
});

it('finishes cleanly and releases the media on leaving', async () => {
  const view = mount(RickrollVideo, { props: { src: source, active: true } });
  await flushPromises();
  const video = view.get('video').element;
  for (let second = 1; second <= 10; second++) {
    video.currentTime = second;
    await view.get('video').trigger('timeupdate');
  }
  await view.get('video').trigger('ended');
  expect(view.emitted('dismiss')).toHaveLength(1);
  view.unmount();
  expect(video.hasAttribute('src')).toBe(false);
  expect(HTMLMediaElement.prototype.load).toHaveBeenCalled();
});

it('shows ten seconds, rejects seeking and unlocks only after continuous playback', async () => {
  const view = mount(RickrollVideo, { props: { src: source, active: true } });
  try {
    await flushPromises();
    expect(view.get('[role="timer"]').text()).toBe('Přeskočit za 10 s');
    expect(view.get('button').attributes('disabled')).toBeDefined();
    const media = view.get('video');
    media.element.currentTime = 100;
    await media.trigger('timeupdate');
    expect(view.get('[role="timer"]').text()).toContain('10 s');
    await media.trigger('ended');
    expect(view.emitted('dismiss')).toBeUndefined();
    for (let second = 1; second <= 9; second++) {
      media.element.currentTime = 100 + second;
      await media.trigger('timeupdate');
    }
    expect(view.get('[role="timer"]').text()).toContain('1 s');
    media.element.currentTime = 110;
    await media.trigger('timeupdate');
    expect(view.find('[role="timer"]').exists()).toBe(false);
    expect(view.get('button').attributes('disabled')).toBeUndefined();
    await view.get('button').trigger('click');
    expect(view.emitted('dismiss')).toHaveLength(1);
  } finally { view.unmount(); }
});

it('allows leaving when media cannot load', async () => {
  const view = mount(RickrollVideo, { props: { src: source, active: true } });
  try {
    await flushPromises();
    await view.get('video').trigger('error');
    await view.get('button').trigger('click');
    expect(view.emitted('dismiss')).toHaveLength(1);
  } finally { view.unmount(); }
});
