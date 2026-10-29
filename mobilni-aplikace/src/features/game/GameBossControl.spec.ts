import { flushPromises, mount } from '@vue/test-utils';
import { expect, it, vi } from 'vitest';
import GameBossControl from './GameBossControl.vue';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../identity/session', () => ({ accountRequest: api.request, authenticatePasskey: vi.fn() }));

it('saves the optional boss surprise with its independent revision', async () => {
  let enabled = false;
  api.request.mockReset();
  api.request.mockImplementation(async (path: string, method: string, body: { enabled: boolean }) => {
    if (method === 'POST') enabled = body.enabled;
    return { profile: { revealed: true, rescued: false, referenceXp: 10, revision: 7, rickrollEnabled: enabled, rickrollRevision: 3 }, proposedReferenceXp: 10 };
  });
  const view = mount(GameBossControl, { props: { groupId: 'a'.repeat(32) } });
  try {
    await flushPromises();
    await view.get('input[type="checkbox"]').setValue(true);
    await flushPromises();
    expect(api.request).toHaveBeenCalledWith(`/app/v1/admin/game/groups/${'a'.repeat(32)}/boss/rickroll`, 'POST',
      expect.objectContaining({ enabled: true, expectedRevision: 3, operationId: expect.any(String) }));
    expect(view.get('input').element).toHaveProperty('checked', true);
  } finally { view.unmount(); }
});

it('restores server state and explains an unconfirmed toggle', async () => {
  api.request.mockReset();
  api.request.mockImplementation(async (_path: string, method: string) => {
    if (method === 'POST') throw new Error('conflict');
    return { profile: { revealed: true, rescued: false, referenceXp: 10, revision: 7, rickrollEnabled: false, rickrollRevision: 3 }, proposedReferenceXp: 10 };
  });
  const view = mount(GameBossControl, { props: { groupId: 'a'.repeat(32) } });
  try {
    await flushPromises();
    await view.get('input[type="checkbox"]').setValue(true);
    await flushPromises();
    expect(view.text()).toContain('Změna překvapení nebyla potvrzena');
    expect(view.get('input').element).toHaveProperty('checked', false);
  } finally { view.unmount(); }
});
