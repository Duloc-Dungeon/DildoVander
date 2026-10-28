import { mount, flushPromises } from '@vue/test-utils';
import { beforeEach, expect, it, vi } from 'vitest';
import InvitationGroupFields from './InvitationGroupFields.vue';
import { defineComponent, ref } from 'vue';
const api = vi.hoisted(() => ({ request: vi.fn(), reauthenticate: vi.fn() }));
vi.mock('./session', async () => { const {ref} = await import('vue'); return {account:ref({id:'owner'}),accountRequest:api.request,authenticatePasskey:api.reauthenticate}; });
beforeEach(() => { api.request.mockReset(); api.request.mockImplementation(async (_path,method) => method ? {group:{id:'group-b',name:'Sobota',launchAt:null,revision:1}} : {groups:[{id:'group-a',name:'Pátek',launchAt:Date.UTC(2026,10,6,19),revision:3}]}); });
it('creates a group schedule separately from global settings and selects it for subsequent invitation links', async () => {
  const view = mount(InvitationGroupFields,{props:{modelValue:''}});
  try {
    await flushPromises(); await view.get('#invitation-group-name').setValue(' Sobota ');
    await view.get('button').trigger('click'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/launch/groups','POST',{name:'Sobota',launchAt:null,adultContentEnabled:true,flirtEnabled:true});
    expect(view.emitted('update:modelValue')?.at(-1)).toEqual(['group-b']);
    expect(api.request.mock.calls.some(([path])=>path==='/app/v1/admin/launch')).toBe(false);
  } finally { view.unmount(); }
});
it('updates only the selected group with its optimistic revision', async () => {
  const view=mount(InvitationGroupFields,{props:{modelValue:'group-a'}});
  try {
    await flushPromises(); await view.get('#invitation-group').setValue('group-a');
    await view.get('#invitation-group-opening').setValue('2026-11-07T19:00');
    await view.findAll('button').at(-1)!.trigger('click'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/launch/groups/group-a','PUT',{expectedRevision:3,name:'Pátek',launchAt:new Date('2026-11-07T19:00').getTime(),adultContentEnabled:true,flirtEnabled:true});
    expect(api.request.mock.calls.some(([path])=>path==='/app/v1/admin/launch')).toBe(false);
  } finally { view.unmount(); }
});
it('keeps the save confirmation visible when the parent updates its selected group', async () => {
  const view = mount(defineComponent({
    components: { InvitationGroupFields },
    setup() { return { groupId: ref('') }; },
    template: '<InvitationGroupFields v-model="groupId" />',
  }));
  try {
    await flushPromises();
    await view.get('#invitation-group-name').setValue('Sobota');
    await view.findAll('button').find(button => button.text() === 'Vytvořit skupinu')!.trigger('click');
    await flushPromises();
    expect(view.text()).toContain('Skupina je uložená.');
    expect((view.get('#invitation-group').element as HTMLSelectElement).value).toBe('group-b');
  } finally { view.unmount(); }
});
it('loads saved content settings and restores both defaults for a new group', async () => {
  api.request.mockReset().mockImplementation(async (_path, method) => method
    ? { group: { id: 'group-a', name: 'Pátek', launchAt: null, revision: 4, adultContentEnabled: false, flirtEnabled: false } }
    : { groups: [{ id: 'group-a', name: 'Pátek', launchAt: null, revision: 3, adultContentEnabled: false, flirtEnabled: true }] });
  const view = mount(InvitationGroupFields, { props: { modelValue: 'group-a' } });
  try {
    await flushPromises();
    const settings = view.findAll('.group-game-settings input');
    expect(settings[0].element).toHaveProperty('checked', false);
    expect(settings[1].element).toHaveProperty('checked', true);
    await settings[1].setValue(false);
    await view.findAll('button').find(button => button.text() === 'Uložit skupinu')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/launch/groups/group-a', 'PUT', {
      expectedRevision: 3, name: 'Pátek', launchAt: null, adultContentEnabled: false, flirtEnabled: false,
    });
    expect(settings[0].element).toHaveProperty('checked', false);
    expect(settings[1].element).toHaveProperty('checked', false);
    await view.findAll('button').find(button => button.text() === 'Nová skupina')!.trigger('click');
    expect(settings[0].element).toHaveProperty('checked', true);
    expect(settings[1].element).toHaveProperty('checked', true);
  } finally { view.unmount(); }
});
it('preserves the exact confirmed deadline when renaming without changing its displayed minute', async () => {
  const exactLaunchAt = Date.UTC(2026, 10, 6, 19, 0, 37, 842);
  api.request.mockReset().mockImplementation(async (_path, method) => method
    ? { group: { id: 'group-a', name: 'Pátek nově', launchAt: exactLaunchAt, revision: 4 } }
    : { groups: [{ id: 'group-a', name: 'Pátek', launchAt: exactLaunchAt, revision: 3 }] });
  const view = mount(InvitationGroupFields, { props: { modelValue: 'group-a' } });
  try {
    await flushPromises();
    await view.get('#invitation-group').setValue('group-a');
    const displayedMinute = (view.get('#invitation-group-opening').element as HTMLInputElement).value;
    expect(displayedMinute).toMatch(/T\d{2}:\d{2}$/);
    await view.get('#invitation-group-name').setValue('Pátek nově');
    await view.findAll('button').at(-1)!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/launch/groups/group-a', 'PUT', {
      expectedRevision: 3, name: 'Pátek nově', launchAt: exactLaunchAt, adultContentEnabled: true, flirtEnabled: true,
    });
  } finally { view.unmount(); }
});

it('lets an owner reauthenticate and explicitly retry group creation without losing the draft', async () => {
  api.reauthenticate.mockReset().mockResolvedValue(undefined);
  const defaultRequest = api.request.getMockImplementation()!;
  let expired = true;
  api.request.mockImplementation(async (path, method, ...args) => {
    if (method && expired) throw Object.assign(new Error('Nejprve ověřte svou totožnost.'), { code: 'recent_passkey_required' });
    return defaultRequest(path, method, ...args);
  });
  const view = mount(InvitationGroupFields, { props: { modelValue: '' } });
  try {
    await flushPromises();
    await view.get('#invitation-group-name').setValue('Sobota');
    await view.findAll('button').find(button => button.text() === 'Vytvořit skupinu')!.trigger('click');
    await flushPromises();
    expect(view.text()).toContain('Nejprve ověřte svou totožnost.');
    await view.findAll('button').find(button => button.text() === 'Ověřit totožnost přístupovým klíčem')!.trigger('click');
    await flushPromises();
    expect(api.reauthenticate).toHaveBeenCalledWith('reauth');
    expect(api.request.mock.calls.filter(([, method]) => method === 'POST')).toHaveLength(1);
    expect((view.get('#invitation-group-name').element as HTMLInputElement).value).toBe('Sobota');
    expired = false;
    await view.findAll('button').find(button => button.text() === 'Vytvořit skupinu')!.trigger('click');
    await flushPromises();
    expect(view.emitted('update:modelValue')?.at(-1)).toEqual(['group-b']);
  } finally { view.unmount(); }
});
