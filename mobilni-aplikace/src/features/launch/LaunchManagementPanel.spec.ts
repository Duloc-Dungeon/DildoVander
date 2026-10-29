import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import LaunchManagementPanel from './LaunchManagementPanel.vue';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../identity/session', () => ({ accountRequest: api.request }));
const config = { revision: 7, timeZone: 'Europe/Prague', personalizedLoadingEnabled: true, personalizedLoadingPrompt: 'Pokyn pro postavu.' };
const wrapper = () => mount(LaunchManagementPanel, { global: { stubs: { AdmissionManagementPanel: true } } });

beforeEach(() => {
  api.request.mockReset().mockImplementation(async (path: string) => path.endsWith('/artwork') ? { jobs: [] } : path.endsWith('/audit') ? { entries: [] } : { ...config });
});

describe('personalization administration', () => {
  it('saves live personalization without obsolete launch plans or manual guest copy', async () => {
    const view = wrapper();
    await flushPromises();
    expect(view.find('#production-launch').exists()).toBe(false);
    expect(view.find('#beta-launch').exists()).toBe(false);
    expect(view.find('#launch-teaser').exists()).toBe(false);
    expect(view.find('#launch-loading').exists()).toBe(false);
    expect(view.text()).not.toContain('Náhled čekárny');
    await view.get('#launch-prompt').setValue('Nový pokyn.');
    await view.findAll('button').find(button => button.text() === 'Uložit osobní věty')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/launch', 'PUT', {
      expectedRevision: 7, timeZone: 'Europe/Prague', personalizedLoadingEnabled: true, personalizedLoadingPrompt: 'Nový pokyn.',
    });
    expect(view.text()).toContain('Nastavení osobních vět je uložené.');
    view.unmount();
  });

  it('keeps the draft visible after a failed save and requests no automatic AI sample', async () => {
    const view = wrapper();
    await flushPromises();
    api.request.mockRejectedValue(new Error('revision_conflict'));
    await view.get('#launch-prompt').setValue('Rozepsaný pokyn.');
    await view.findAll('button').find(button => button.text() === 'Uložit osobní věty')!.trigger('click');
    await flushPromises();
    expect(view.text()).toContain('revision_conflict');
    expect(view.get<HTMLTextAreaElement>('#launch-prompt').element.value).toBe('Rozepsaný pokyn.');
    expect(view.get<HTMLDetailsElement>('details').element.open).toBe(true);
    expect(api.request.mock.calls.some(([path]) => path.includes('personalization-preview'))).toBe(false);
    view.unmount();
  });

  it('keeps personalization available when auxiliary lists fail', async () => {
    api.request.mockImplementation(async (path: string) => {
      if (path !== '/app/v1/admin/launch') throw new Error('unavailable');
      return { ...config };
    });
    const view = wrapper();
    await flushPromises();
    expect(view.find('#launch-prompt').exists()).toBe(true);
    expect(view.text()).toContain('Přehled portrétů nebo historie změn není dostupný');
    view.unmount();
  });
});
