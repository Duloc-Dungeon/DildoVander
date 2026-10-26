import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import BetaAdmissionPanel from './BetaAdmissionPanel.vue';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'owner', displayName: 'Owner', roles: ['Owner'] }), accountRequest: api.request };
});

const groupId = '0123456789abcdef0123456789abcdef';
const userId = 'abcdef0123456789abcdef0123456789';
const member = { userId, displayName: 'Ava Longname', joinedAt: Date.UTC(2026, 9, 25) / 1000, accessActive: true, accessRevision: 4 };
let effectiveMember = member;

beforeEach(() => {
  effectiveMember = member;
  api.request.mockReset().mockImplementation(async (path: string) => {
    if (path === '/app/v1/admin/launch/beta-admission') return { revision: 3, groupId, groupName: 'Interní beta', recruitmentOpen: true, fixedUniverse: null };
    if (path === '/app/v1/admin/launch/groups') return { groups: [{ id: groupId, name: 'Interní beta', launchAt: Date.UTC(2026, 10, 10), revision: 2 }] };
    if (path.endsWith('/members')) return { members: [effectiveMember] };
    return {};
  });
});

describe('beta admission management', () => {
  it('shows the group, launch date and participant access state on mobile cards', async () => {
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    expect(view.text()).toContain('Interní beta');
    expect(view.text()).toContain('Ava Longname');
    expect(view.text()).toContain('Aktivní');
    expect(view.text()).toContain('Účet abcdef01');
    expect(view.text()).toContain('25. 10. 2026');
    view.unmount();
  });

  it('requires the named confirmation and sends the member revision for revocation', async () => {
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    await view.findAll('button').find(button => button.text() === 'Odebrat přístup')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith(
      `/app/v1/admin/launch/groups/${groupId}/members/${userId}`,
      'PUT',
      { expectedRevision: 4, active: false },
      undefined,
      'Odebrat přístup účastníkovi Ava Longname?\nNebude moct pokračovat v zážitku. Účet ani jeho data se nesmažou.',
    );
    view.unmount();
  });

  it('saves recruitment as an independent setting with optimistic revision', async () => {
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    await view.get('input[type="checkbox"]').setValue(false);
    await view.findAll('button').find(button => button.text() === 'Uložit přístup a univerzum')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/launch/beta-admission', 'PUT', {
      expectedRevision: 3, groupId, recruitmentOpen: false, fixedUniverse: null,
    });
    view.unmount();
  });

  it('reconciles an ambiguous revocation before re-enabling mutations', async () => {
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    effectiveMember = { ...member, accessActive: false, accessRevision: 5 };
    api.request.mockRejectedValueOnce(new Error('response lost'));
    await view.findAll('button').find(button => button.text() === 'Odebrat přístup')!.trigger('click');
    await flushPromises();
    expect(view.text()).toContain('Přístup odebrán');
    expect(view.findAll('button').some(button => button.text() === 'Obnovit přístup' && !button.attributes('disabled'))).toBe(true);
    view.unmount();
  });
});
