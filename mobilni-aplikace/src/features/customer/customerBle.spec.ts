import { afterEach, describe, expect, it, vi } from 'vitest';

const platform = vi.hoisted(() => ({ value: 'web' }));
vi.mock('@capacitor/core', () => ({
  Capacitor: {
    isNativePlatform: () => platform.value !== 'web',
    getPlatform: () => platform.value,
  },
  registerPlugin: () => ({}),
}));

import { customerBle } from './customerBle';

afterEach(() => { platform.value = 'web'; });

describe('customer BLE platform support', () => {
  it.each(['ios', 'android'])('is enabled on %s native apps', (value) => {
    platform.value = value;
    expect(customerBle.supported()).toBe(true);
  });

  it('stays disabled in browser environments', () => {
    platform.value = 'web';
    expect(customerBle.supported()).toBe(false);
  });
});
