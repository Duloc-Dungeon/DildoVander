import { mount, flushPromises } from '@vue/test-utils';
import { beforeEach, expect, it, vi } from 'vitest';
import InvitationGroupFields from './InvitationGroupFields.vue';
const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('./session', async () => { const {ref} = await import('vue'); return {account:ref({id:'owner'}),accountRequest:api.request}; });
beforeEach(() => { api.request.mockReset(); api.request.mockImplementation(async (_path,method) => method ? {group:{id:'group-b',name:'Sobota',launchAt:null,revision:1}} : {groups:[{id:'group-a',name:'Pátek',launchAt:Date.UTC(2026,10,6,19),revision:3}]}); });
it('creates a group schedule separately from global settings and selects it for subsequent invitation links', async () => {
  const view = mount(InvitationGroupFields,{props:{modelValue:''}});
  try {
    await flushPromises(); await view.get('#invitation-group-name').setValue(' Sobota ');
    await view.get('button').trigger('click'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/launch/groups','POST',{name:'Sobota',launchAt:null});
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
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/launch/groups/group-a','PUT',{expectedRevision:3,name:'Pátek',launchAt:new Date('2026-11-07T19:00').getTime()});
    expect(api.request.mock.calls.some(([path])=>path==='/app/v1/admin/launch')).toBe(false);
  } finally { view.unmount(); }
});
