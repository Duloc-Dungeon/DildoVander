import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import LaunchManagementPanel from './LaunchManagementPanel.vue';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../identity/session', () => ({ accountRequest: api.request }));

const launch = () => ({
  revision: 7, betaEnabled: true, betaLaunchAt: null, betaUnlocked: false,
  productionEnabled: true, productionLaunchAt: null, productionUnlocked: false,
  timeZone: 'Europe/Prague', teaser: 'Ještě chvilku.', initialLoadingMessage: 'Hvězdy se rovnají.',
  personalizedLoadingEnabled: false, personalizedLoadingPrompt: 'Hravě a tajemně.',
});
const wrapper = () => mount(LaunchManagementPanel, { global: { stubs: { AwField: { template: '<div><label :for="id">{{ label }}</label><slot /></div>', props: ['id', 'label'] }, AwNotice: { template: '<div role="alert"><slot /></div>' } } } });

beforeEach(() => {
  api.request.mockReset();
  api.request.mockImplementation(async (path: string) => path.endsWith('/artwork') ? { jobs: [] } : path.endsWith('/audit') ? { entries: [] } : launch());
});

describe('Launch Management', () => {
  it('loads independent audience controls and saves Prague local times as UTC instants', async () => {
    const view = wrapper(); await flushPromises();
    expect(view.text()).toContain('Europe/Prague');
    await view.get('#beta-launch').setValue('2026-03-30T10:00');
    await view.get('#production-launch').setValue('2026-10-30T10:00');
    await view.get('button').trigger('click'); await flushPromises();
    const save = api.request.mock.calls.find(([path, method]) => path === '/app/v1/admin/launch' && method === 'PUT');
    expect(save?.[2]).toMatchObject({ expectedRevision: 7, betaLaunchAt: Date.UTC(2026, 2, 30, 8), productionLaunchAt: Date.UTC(2026, 9, 30, 9), timeZone: 'Europe/Prague' });
    view.unmount();
  });

  it('rejects a nonexistent spring daylight-saving time before sending a change', async () => {
    const view = wrapper(); await flushPromises();
    await view.get('#beta-launch').setValue('2026-03-29T02:30');
    await view.get('button').trigger('click'); await flushPromises();
    expect(view.get('[role="alert"]').text()).toContain('neexistuje');
    expect(api.request.mock.calls.some(([path, method]) => path === '/app/v1/admin/launch' && method === 'PUT')).toBe(false);
    view.unmount();
  });

  it('does not spend AI resources until an administrator requests a sample', async () => {
    const view = wrapper(); await flushPromises();
    expect(api.request.mock.calls.some(([path]) => path.includes('personalization-preview'))).toBe(false);
    view.unmount();
  });
});
