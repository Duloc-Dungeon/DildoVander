import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import LaunchManagementPanel from './LaunchManagementPanel.vue';

const api = vi.hoisted(() => ({ request: vi.fn(), reauthenticate: vi.fn() }));
vi.mock('../identity/session', () => ({ account: null, accountRequest: api.request, authenticatePasskey: api.reauthenticate }));

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

describe('Příprava večera', () => {
  it.each([false, true])('saves from messages and exposes its own failure without opening unrelated settings (failure=%s)', async failure => {
    api.request.mockImplementation(async (path: string, method?: string, body?: Record<string, unknown>) => {
      if (path === '/app/v1/admin/launch' && method === 'PUT') {
        if (failure) throw new Error('Uložení se nepodařilo.');
        return { ...launch(), revision: 8, teaser: body?.teaser };
      }
      return path.endsWith('/artwork') ? { jobs: [] } : path.endsWith('/audit') ? { entries: [] } : launch();
    });
    const view = wrapper();
    try {
      await flushPromises();
      await view.get('#launch-teaser').setValue('Dveře se brzy otevřou.');
      await view.findAll('button').find(button => button.text() === 'Uložit plán i zprávy')!.trigger('click');
      await flushPromises();
      expect(api.request).toHaveBeenCalledWith('/app/v1/admin/launch', 'PUT', expect.objectContaining({ expectedRevision: 7, teaser: 'Dveře se brzy otevřou.' }));
      expect(api.request.mock.calls.filter(([, method]) => method === 'PUT')).toHaveLength(1);
      expect(view.findAll<HTMLDetailsElement>('details.launch-admin__disclosure')[0].element.open).toBe(false);
      if (failure) {
        expect(view.findAll<HTMLDetailsElement>('details.launch-admin__disclosure')[1].element.open).toBe(true);
        expect((view.get('#launch-teaser').element as HTMLTextAreaElement).value).toBe('Dveře se brzy otevřou.');
      } else {
        expect(view.text()).toContain('Plán a krátké sdělení jsou uložené.');
      }
    } finally { view.unmount(); }
  });

  it('keeps advanced settings collapsed and preserves an unsaved message draft', async () => {
    const view = wrapper();
    try {
      await flushPromises();
      const disclosures = view.findAll<HTMLDetailsElement>('details.launch-admin__disclosure');
      expect(disclosures).toHaveLength(5);
      expect(disclosures.every(item => !item.element.open)).toBe(true);

      const messageDisclosure = disclosures.find(item => item.text().includes('Zprávy a osobní věta'))!;
      await messageDisclosure.get('summary').trigger('click');
      const teaser = view.get('#launch-teaser');
      await teaser.setValue('Zpráva zůstává rozepsaná.');
      await messageDisclosure.get('summary').trigger('click');
      await messageDisclosure.get('summary').trigger('click');
      expect((view.get('#launch-teaser').element as HTMLTextAreaElement).value).toBe('Zpráva zůstává rozepsaná.');
      expect(api.request.mock.calls.some(([path]) => path === '/app/v1/admin/launch/personalization-preview')).toBe(false);
    } finally { view.unmount(); }
  });

  it('keeps launch controls available when optional portrait or audit data is unavailable', async () => {
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/artwork') || path.endsWith('/audit')) throw new Error('Service unavailable');
      return launch();
    });
    const view = wrapper();
    try {
      await flushPromises();
      expect(view.find('#beta-launch').exists()).toBe(true);
      expect(view.text()).toContain('Nastavení přístupu můžete dál upravovat.');
      await view.get('.launch-admin__schedule button').trigger('click');
      await flushPromises();
      expect(api.request.mock.calls.some(([path, method]) => path === '/app/v1/admin/launch' && method === 'PUT')).toBe(true);
    } finally { view.unmount(); }
  });

  it('offers owner reauthentication after a rejected save without automatically replaying the change', async () => {
    api.reauthenticate.mockReset().mockResolvedValue(undefined);
    const defaultRequest = api.request.getMockImplementation()!;
    api.request.mockImplementation(async (path, method, ...args) => {
      if (method === 'PUT') throw Object.assign(new Error('Nejprve ověřte svou totožnost.'), { code: 'recent_passkey_required' });
      return defaultRequest(path, method, ...args);
    });
    const view = wrapper();
    try {
      await flushPromises();
      await view.get('#launch-teaser').setValue('Připraveno na večer.');
      await view.get('.launch-admin__schedule button').trigger('click');
      await flushPromises();
      expect(view.find<HTMLDetailsElement>('details.launch-admin__disclosure').element.open).toBe(true);
      await view.findAll('button').find(button => button.text() === 'Ověřit totožnost přístupovým klíčem')!.trigger('click');
      await flushPromises();
      expect(api.reauthenticate).toHaveBeenCalledWith('reauth');
      expect((view.get('#launch-teaser').element as HTMLTextAreaElement).value).toBe('Připraveno na večer.');
      expect(api.request.mock.calls.filter(([, method]) => method === 'PUT')).toHaveLength(1);
    } finally { view.unmount(); }
  });

  it('loads independent audience controls and saves Prague local times as UTC instants', async () => {
    const view = wrapper(); await flushPromises();
    expect(view.text()).toContain('Europe/Prague');
    await view.get('#beta-launch').setValue('2026-03-30T10:00');
    await view.get('#production-launch').setValue('2026-10-30T10:00');
    await view.get('.launch-admin__schedule button').trigger('click'); await flushPromises();
    const save = api.request.mock.calls.find(([path, method]) => path === '/app/v1/admin/launch' && method === 'PUT');
    expect(save?.[2]).toMatchObject({ expectedRevision: 7, betaLaunchAt: Date.UTC(2026, 2, 30, 8), productionLaunchAt: Date.UTC(2026, 9, 30, 9), timeZone: 'Europe/Prague' });
    view.unmount();
  });

  it('rejects a nonexistent spring daylight-saving time before sending a change', async () => {
    const view = wrapper(); await flushPromises();
    await view.get('#beta-launch').setValue('2026-03-29T02:30');
    await view.get('.launch-admin__schedule button').trigger('click'); await flushPromises();
    expect(view.findAll('[role="alert"]').some(alert => alert.text().includes('neexistuje'))).toBe(true);
    expect(api.request.mock.calls.some(([path, method]) => path === '/app/v1/admin/launch' && method === 'PUT')).toBe(false);
    view.unmount();
  });

  it('does not spend AI resources until an administrator requests a sample', async () => {
    const view = wrapper(); await flushPromises();
    expect(api.request.mock.calls.some(([path]) => path.includes('personalization-preview'))).toBe(false);
    view.unmount();
  });
});
