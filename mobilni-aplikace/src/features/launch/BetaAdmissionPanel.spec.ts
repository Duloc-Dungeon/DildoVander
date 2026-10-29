import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import BetaAdmissionPanel from './BetaAdmissionPanel.vue';

const api = vi.hoisted(() => ({ request: vi.fn(), reauthenticate: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'owner', displayName: 'Owner', roles: ['Owner'] }), accountRequest: api.request, authenticatePasskey: api.reauthenticate };
});

const groupId = '0123456789abcdef0123456789abcdef';
const secondGroupId = '123456789abcdef0123456789abcdef0';
const userId = 'abcdef0123456789abcdef0123456789';
const member: { userId: string; displayName: string; joinedAt: number; groupId: string | null; groupName: string | null; state: 'waiting' | 'revoked'; accessRevision: number; fixedUniverse: string | null } = { userId, displayName: 'Ava Longname', joinedAt: Date.UTC(2026, 9, 25) / 1000, groupId, groupName: 'Interní beta', state: 'waiting', accessRevision: 4, fixedUniverse: 'Astronomie' };
const group = (id: string, name: string, extras: Record<string, unknown> = {}) => ({ id, name, launchAt: null, revision: 2, openedAt: null, ...extras });
let effectiveMember = member;
let effectiveGroups = [group(groupId, 'Interní beta'), group(secondGroupId, 'Sobota')];

beforeEach(() => {
  effectiveMember = member;
  effectiveGroups = [group(groupId, 'Interní beta'), group(secondGroupId, 'Sobota')];
  api.request.mockReset().mockImplementation(async (path: string, method?: string) => {
    if (path === '/app/v1/admin/launch/beta-admission') return { revision: 3, groupId, groupName: 'Interní beta', recruitmentOpen: true, fixedUniverse: null };
    if (path === '/app/v1/admin/launch/groups') return { groups: effectiveGroups };
    if (path === '/app/v1/admin/launch/waiting-room') return { members: [effectiveMember] };
    if (path.endsWith('/chronicle-close') && method === 'POST') {
      const id = path.split('/')[6];
      const closedAt = 1790000000;
      effectiveGroups = effectiveGroups.map(item => item.id === id ? { ...item, chronicleClosedAt: closedAt, revision: item.revision + 1 } : item);
      return { closedAt };
    }
    return {};
  });
});

describe('owner waiting room and shared opening schedule', () => {
  it('requests fresh opening state when a loaded countdown expires', async () => {
    vi.useFakeTimers();
    effectiveGroups = [group(groupId, 'Interní beta', { launchAt: Date.now() + 1_000 })];
    const view = mount(BetaAdmissionPanel);
    try {
      await flushPromises();
      expect(view.text()).toContain('Společný odpočet');
      await vi.advanceTimersByTimeAsync(1_000);
      expect(view.text()).toContain('Společný termín uplynul. Obnovte stav otevření.');
      expect(view.text()).not.toContain('Čekárna zůstává zavřená');
    } finally { view.unmount(); vi.useRealTimers(); }
  });

  it('marks previous readiness unverified after a failed refresh instead of presenting stale counts', async () => {
    const view = mount(BetaAdmissionPanel);
    try {
      await flushPromises();
      expect(view.get('.evening-readiness').text()).toContain('Interní beta');
      const original = api.request.getMockImplementation()!;
      api.request.mockImplementation(async (path: string, ...args: unknown[]) => {
        if (path === '/app/v1/admin/launch/waiting-room') throw new Error('Network unavailable');
        return original(path, ...args);
      });
      await view.findAll('button').find(button => button.text() === 'Obnovit čekárnu')!.trigger('click');
      await flushPromises();
      expect(view.get('.evening-readiness').findAll('dd').map(item => item.text())).toEqual(Array(4).fill('Stav není ověřený'));
      expect(view.get('#management-group').attributes('disabled')).toBeDefined();
      expect(view.text()).toContain('Obnovit stav');
    } finally { view.unmount(); }
  });

  it('shows verified group readiness and keeps secondary lists collapsed', async () => {
    const view = mount(BetaAdmissionPanel);
    try {
      await flushPromises();
      const readiness = view.get('.evening-readiness');
      expect(readiness.attributes('aria-label')).toBe('Připravenost večera');
      expect(readiness.text()).toContain('Interní beta');
      expect(readiness.text()).toContain('Čekající hosté');
      expect(readiness.text()).toContain('1');
      expect(readiness.text()).toContain('Přijímání hostů');
      expect(readiness.text()).toContain('Otevřené');
      expect(readiness.text()).toContain('Společný termín otevření zatím není nastavený');
      expect(view.find('#waiting-room').exists()).toBe(true);
      expect(view.find('#countdown-minutes').exists()).toBe(false);
      expect(view.text()).toContain('včetně beta');
      await view.get('.waiting-room__production-schedule button').trigger('click');
      expect(view.emitted('edit-production-schedule')).toHaveLength(1);
      expect(view.find<HTMLDetailsElement>('details[aria-labelledby="waiting-room-title"]').element.open).toBe(false);
      expect(view.find<HTMLDetailsElement>('details[aria-labelledby="beta-admission-settings-title"]').element.open).toBe(false);
      expect(view.get('#beta-admission-group').element.closest('details')?.getAttribute('aria-labelledby')).toBe('beta-admission-settings-title');
    } finally { view.unmount(); }
  });


  it('offers the same shared-term editor for any selected group and exposes no independent timer controls', async () => {
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    await view.get('#management-group').setValue(secondGroupId);
    expect(view.get('.waiting-room__production-schedule').text()).toContain('včetně beta');
    await view.find('.waiting-room__production-schedule button').trigger('click');
    expect(view.emitted('edit-production-schedule')).toHaveLength(1);
    expect(view.find('#countdown-minutes').exists()).toBe(false);
    expect(view.text()).not.toContain('Potvrdit a spustit odpočet');
    expect(view.text()).not.toContain('Uložit návrh odpočtu');
    expect(view.findAll('button').some(button => button.text() === 'Spustit noc')).toBe(false);
    expect(api.request.mock.calls.some(([path]) => String(path).includes('/countdown'))).toBe(false);
    view.unmount();
  });
  it('loads every roster page before enabling group changes', async () => {
    const defaultRequest = api.request.getMockImplementation()!;
    api.request.mockImplementation(async (path: string, ...args: unknown[]) => {
      if (path === '/app/v1/admin/launch/waiting-room') {
        return { members: [member], nextCursor: member.userId };
      }
      if (path === `/app/v1/admin/launch/waiting-room?after=${encodeURIComponent(member.userId)}`) {
        return { members: [{ ...member, userId: 'last-member', displayName: 'Last waiting guest' }], nextCursor: null };
      }
      return defaultRequest(path, ...args);
    });
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    expect(view.text()).toContain('Ava Longname');
    expect(view.text()).toContain('Last waiting guest');
    expect(view.findAll('button').find(button => button.text() === 'Obnovit čekárnu')?.attributes('disabled')).toBeUndefined();
    view.unmount();
  });

  it('blocks changes when a later roster page fails', async () => {
    const defaultRequest = api.request.getMockImplementation()!;
    api.request.mockImplementation(async (path: string, ...args: unknown[]) => {
      if (path === '/app/v1/admin/launch/waiting-room') return { members: [member], nextCursor: member.userId };
      if (path.includes('/waiting-room?after=')) throw new Error('Network unavailable');
      return defaultRequest(path, ...args);
    });
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    expect(view.text()).toContain('Obnovte stav před další změnou.');
    expect(view.text()).not.toContain('Ava Longname');
    view.unmount();
  });

  it('shows the all-group roster, unassigned/revoked status and existing identity details', async () => {
    effectiveMember = { ...member, groupId: null, groupName: null, state: 'revoked', accessRevision: 0 };
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    expect(view.text()).toContain('Ava Longname');
    expect(view.text()).toContain('Bez skupiny · Přístup odebrán');
    expect(view.text()).toContain('Astronomie');
    expect(view.text()).toContain('Nejdříve přiřaďte skupinu, potom samostatně obnovte přístup.');
    expect(view.get('select[aria-label="Skupina pro Ava Longname"]').element).toBeTruthy();
    view.unmount();
  });

  it('moves a waiting member with source-group and membership revision guards', async () => {
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    await view.get('select[aria-label="Skupina pro Ava Longname"]').setValue(secondGroupId);
    await view.findAll('button').find(button => button.text() === 'Přesunout')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith(
      `/app/v1/admin/launch/waiting-room/${userId}/group`, 'PUT',
      { expectedMembershipRevision: 4, expectedSourceGroupId: groupId, targetGroupId: secondGroupId },
      undefined,
      `Přesunout Ava Longname do skupiny Sobota?\nÚčet a jeho data zůstanou zachované.`,
    );
    view.unmount();
  });

  it('assigns a legacy unassigned guest with the null-source revision guard', async () => {
    effectiveMember = { ...member, groupId: null, groupName: null, accessRevision: 0 };
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    expect(view.findAll('button').some(button => button.text() === 'Odebrat přístup')).toBe(false);
    await view.get('select[aria-label="Skupina pro Ava Longname"]').setValue(secondGroupId);
    await view.findAll('button').find(button => button.text() === 'Přesunout')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith(
      `/app/v1/admin/launch/waiting-room/${userId}/group`, 'PUT',
      { expectedMembershipRevision: 0, expectedSourceGroupId: null, targetGroupId: secondGroupId },
      undefined,
      `Přesunout Ava Longname do skupiny Sobota?\nÚčet a jeho data zůstanou zachované.`,
    );
    view.unmount();
  });

  it('warns before assigning a waiting guest into an already open group', async () => {
    effectiveGroups[1] = group(secondGroupId, 'Sobota', { openedAt: Date.now() - 1000 });
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    await view.get('select[aria-label="Skupina pro Ava Longname"]').setValue(secondGroupId);
    await view.findAll('button').find(button => button.text() === 'Přesunout')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith(
      `/app/v1/admin/launch/waiting-room/${userId}/group`, 'PUT',
      { expectedMembershipRevision: 4, expectedSourceGroupId: groupId, targetGroupId: secondGroupId },
      undefined,
      `Přesunout Ava Longname do skupiny Sobota?\nTato skupina už je otevřená; účastník může vstoupit okamžitě.\nÚčet a jeho data zůstanou zachované.`,
    );
    view.unmount();
  });

  it('keeps the recruitment default independent from group management selection', async () => {
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    await view.get('#management-group').setValue(secondGroupId);
    await view.get('#beta-admission-group').setValue(secondGroupId);
    await view.findAll('button').find(button => button.text() === 'Nastavit jako výchozí')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/launch/beta-admission/group', 'POST', { expectedRevision: 3, groupId: secondGroupId });
    expect((view.get('#management-group').element as HTMLSelectElement).value).toBe(secondGroupId);
    view.unmount();
  });


  it('offers the same shared-term editor for any selected group and exposes no independent timer controls', async () => {
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    await view.get('#management-group').setValue(secondGroupId);
    expect(view.get('.waiting-room__production-schedule').text()).toContain('včetně beta');
    await view.find('.waiting-room__production-schedule button').trigger('click');
    expect(view.emitted('edit-production-schedule')).toHaveLength(1);
    expect(view.find('#countdown-minutes').exists()).toBe(false);
    expect(view.text()).not.toContain('Potvrdit a spustit odpočet');
    expect(view.text()).not.toContain('Uložit návrh odpočtu');
    expect(view.findAll('button').some(button => button.text() === 'Spustit noc')).toBe(false);
    expect(api.request.mock.calls.some(([path]) => String(path).includes('/countdown'))).toBe(false);
    view.unmount();
  });
  it('saves an explicit evening finale from revealed memories and then hides the action', async () => {
    effectiveGroups = [group(groupId, 'Interní beta', { openedAt: Date.now() - 1000 })];
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    expect(view.text()).toContain('Uloží se závěrečná kronika');
    const close = view.findAll('button').find(button => button.text() === 'Uložit závěr večera');
    expect(close).toBeDefined();
    await close!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith(`/app/v1/admin/launch/groups/${groupId}/chronicle-close`, 'POST', { expectedRevision: 2 }, undefined,
      'Uložit závěr večera pro skupinu Interní beta? Kronika použije jen už odhalené vzpomínky.');
    expect(view.text()).toContain('Závěr večera pro skupinu Interní beta byl uložen.');
    expect(view.findAll('button').some(button => button.text() === 'Uložit závěr večera')).toBe(false);
    view.unmount();
  });

  it('reconciles an ambiguous assignment before enabling another mutation', async () => {
    const original = api.request.getMockImplementation()!;
    api.request.mockImplementation(async (path: string, ...args: unknown[]) => {
      if (path === `/app/v1/admin/launch/waiting-room/${userId}/group`) throw new Error('response lost');
      return original(path, ...args);
    });
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    await view.get('select[aria-label="Skupina pro Ava Longname"]').setValue(secondGroupId);
    await view.findAll('button').find(button => button.text() === 'Přesunout')!.trigger('click');
    await flushPromises();
    expect(view.text()).toContain('Aktuální stav byl znovu načten');
    expect(view.findAll('button').some(button => button.text() === 'Přesunout' && !button.attributes('disabled'))).toBe(true);
    view.unmount();
  });
});
