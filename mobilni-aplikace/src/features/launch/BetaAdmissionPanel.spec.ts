import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import BetaAdmissionPanel from './BetaAdmissionPanel.vue';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'owner', displayName: 'Owner', roles: ['Owner'] }), accountRequest: api.request };
});

const groupId = '0123456789abcdef0123456789abcdef';
const secondGroupId = '123456789abcdef0123456789abcdef0';
const userId = 'abcdef0123456789abcdef0123456789';
const member: { userId: string; displayName: string; joinedAt: number; groupId: string | null; groupName: string | null; state: 'waiting' | 'revoked'; accessRevision: number; fixedUniverse: string | null } = { userId, displayName: 'Ava Longname', joinedAt: Date.UTC(2026, 9, 25) / 1000, groupId, groupName: 'Interní beta', state: 'waiting', accessRevision: 4, fixedUniverse: 'Astronomie' };
const group = (id: string, name: string, extras: Record<string, unknown> = {}) => ({ id, name, launchAt: null, revision: 2, countdownDurationSeconds: 600, forceStartAtEnd: false, countdownConfirmedAt: null, openedAt: null, ...extras });
let effectiveMember = member;
let effectiveGroups = [group(groupId, 'Interní beta'), group(secondGroupId, 'Sobota')];

beforeEach(() => {
  effectiveMember = member;
  effectiveGroups = [group(groupId, 'Interní beta'), group(secondGroupId, 'Sobota')];
  api.request.mockReset().mockImplementation(async (path: string, method?: string, body?: unknown) => {
    if (path === '/app/v1/admin/launch/beta-admission') return { revision: 3, groupId, groupName: 'Interní beta', recruitmentOpen: true, fixedUniverse: null };
    if (path === '/app/v1/admin/launch/groups') return { groups: effectiveGroups };
    if (path === '/app/v1/admin/launch/waiting-room') return { members: [effectiveMember] };
    if (path.endsWith('/countdown') && method === 'PUT') {
      const request = body as { durationSeconds: number; forceStartAtEnd: boolean };
      const id = path.split('/')[6];
      effectiveGroups = effectiveGroups.map(item => item.id === id ? { ...item, countdownDurationSeconds: request.durationSeconds, forceStartAtEnd: request.forceStartAtEnd, revision: item.revision + 1 } : item);
    }
    return {};
  });
});

describe('owner waiting room and group countdown', () => {
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

  it('saves a duration draft without starting the countdown and requires a separate confirmation', async () => {
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    await view.get('#countdown-minutes').setValue('25');
    await view.findAll('button').find(button => button.text() === 'Uložit návrh odpočtu')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith(`/app/v1/admin/launch/groups/${groupId}/countdown`, 'PUT', {
      expectedRevision: 2, durationSeconds: 1500, forceStartAtEnd: false,
    });
    expect(view.text()).toContain('Návrh je uložený');
    expect(view.text()).toContain('zobrazuje Brzy');
    expect(api.request).not.toHaveBeenCalledWith(`/app/v1/admin/launch/groups/${groupId}/countdown/confirm`, 'POST', expect.anything(), expect.anything(), expect.anything());
    await view.findAll('button').find(button => button.text() === 'Potvrdit a spustit odpočet')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith(`/app/v1/admin/launch/groups/${groupId}/countdown/confirm`, 'POST', { expectedRevision: 3 }, undefined, expect.stringContaining('na 25 minut'));
    view.unmount();
  });

  it('makes Force an after-expiry action and exposes manual start only after a non-force expiry', async () => {
    const view = mount(BetaAdmissionPanel);
    await flushPromises();
    await view.get('.waiting-room__force input').setValue(true);
    await view.findAll('button').find(button => button.text() === 'Uložit návrh odpočtu')!.trigger('click');
    await flushPromises();
    await view.findAll('button').find(button => button.text() === 'Potvrdit a spustit odpočet')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith(`/app/v1/admin/launch/groups/${groupId}/countdown/confirm`, 'POST', { expectedRevision: 3 }, undefined, expect.stringContaining('Po skončení se noc automaticky otevře.'));
    expect(view.findAll('button').some(button => button.text() === 'Spustit noc')).toBe(false);
    view.unmount();

    effectiveGroups = [group(groupId, 'Interní beta', { launchAt: Date.now() - 1000, countdownConfirmedAt: Date.now() - 601000 })];
    const expired = mount(BetaAdmissionPanel);
    await flushPromises();
    await expired.findAll('button').find(button => button.text() === 'Spustit noc')!.trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith(`/app/v1/admin/launch/groups/${groupId}/start`, 'POST', { expectedRevision: 2 }, undefined, `Otevřít noc pro skupinu Interní beta?`);
    expired.unmount();
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
