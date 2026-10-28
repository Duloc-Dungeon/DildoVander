import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createApplicationUpdates } from './applicationUpdates';
import ApplicationUpdateGate from './ApplicationUpdateGate.vue';

const native = vi.hoisted(() => ({ active: (_event: { isActive: boolean }) => {}, remove: vi.fn() }));
vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => true } }));
vi.mock('@capacitor/app', () => ({ App: { addListener: vi.fn(async (_name, callback) => {
  native.active = callback;
  return { remove: native.remove };
}) } }));
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); vi.clearAllMocks(); });

describe('application update gate', () => {
  it('checks on reconnect, visibility and every five minutes while visible, then removes observers', async () => {
    vi.useFakeTimers();
    const loadPolicy = vi.fn(async () => ({ ios: { minimumBuild: '0', latestBuild: '0', updateUrl: '' } }));
    const updates = createApplicationUpdates({ platform: 'ios', getInfo: async () => ({ version: '1.0', build: '20' }), loadPolicy });
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    const wrapper = mount(ApplicationUpdateGate, { props: { updates } });
    await vi.advanceTimersByTimeAsync(0);
    expect(loadPolicy).toHaveBeenCalledTimes(1);
    hidden.mockReturnValue(true);
    await vi.advanceTimersByTimeAsync(5 * 60 * 1000);
    window.dispatchEvent(new Event('online'));
    await vi.advanceTimersByTimeAsync(0);
    expect(loadPolicy).toHaveBeenCalledTimes(1);
    hidden.mockReturnValue(false);
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(0);
    expect(loadPolicy).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(5 * 60 * 1000);
    expect(loadPolicy).toHaveBeenCalledTimes(3);
    window.dispatchEvent(new Event('online'));
    await vi.advanceTimersByTimeAsync(0);
    expect(loadPolicy).toHaveBeenCalledTimes(4);
    wrapper.unmount();
    await vi.advanceTimersByTimeAsync(5 * 60 * 1000);
    window.dispatchEvent(new Event('online'));
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(0);
    expect(loadPolicy).toHaveBeenCalledTimes(4);
  });
  it('keeps route content unmounted through a required update, retry and offline resume', async () => {
    const loadPolicy = vi.fn(async () => ({ ios: { minimumBuild: '20', latestBuild: '20', updateUrl: 'https://example.test/ios/' } }));
    const updates = createApplicationUpdates({ platform: 'ios', getInfo: async () => ({ version: '1.0', build: '19' }), loadPolicy });
    const wrapper = mount(ApplicationUpdateGate, { props: { updates }, slots: { default: '<div data-route>Private route</div>' }, attachTo: document.body });
    expect(wrapper.find('[data-route]').exists()).toBe(false);
    await flushPromises();
    expect(wrapper.find('h1').text()).toBe('Je čas na aktualizaci');
    expect(wrapper.find('a').attributes('href')).toBe('https://example.test/ios/');
    expect(wrapper.find('[data-route]').exists()).toBe(false);
    loadPolicy.mockRejectedValueOnce(new Error('offline'));
    await wrapper.get('button').trigger('click');
    await flushPromises();
    expect(wrapper.find('[role="alert"]').exists()).toBe(true);
    expect(wrapper.find('[data-route]').exists()).toBe(false);
    native.active({ isActive: true });
    await flushPromises();
    expect(loadPolicy).toHaveBeenCalledTimes(3);
    wrapper.unmount();
    expect(native.remove).toHaveBeenCalledTimes(1);
  });
  it('shows an optional update without preventing supported routes', async () => {
    const updates = createApplicationUpdates({ platform: 'ios', getInfo: async () => ({ version: '1.0', build: '20' }),
      loadPolicy: async () => ({ ios: { minimumBuild: '20', latestBuild: '21', updateUrl: 'https://example.test/ios/' } }),
    });
    const wrapper = mount(ApplicationUpdateGate, { props: { updates }, slots: { default: '<div data-route>Supported route</div>' } });
    await flushPromises();
    expect(wrapper.find('[data-route]').exists()).toBe(true);
    expect(wrapper.find('aside').text()).toContain('Je dostupná nová verze');
    wrapper.unmount();
  });
});
