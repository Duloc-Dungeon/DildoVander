import { describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { Duel } from '../archive/showContracts';
import type { TestingSessionsClient } from './client';
import type { TestCharacter, TestSession } from './contracts';
import TestingDuels from './TestingDuels.vue';

describe('test duel participant labels', () => {
  it('distinguishes same-name participants and follows label changes without changing narrative names', async () => {
    const sessions: TestSession[] = ['left', 'right'].map(id => ({
      id, characterId: id, ownerId: 'tester', virtualDeviceId: id, labSessionId: id,
      engine: 'gpt-live-1', inputMode: 'text', status: 'open', createdAt: '', endedAt: null, revision: 1,
    }));
    const characters: TestCharacter[] = ['Petr', 'Jana'].map((label, index) => ({
      id: sessions[index]!.id, label,
      profile: { profileId: sessions[index]!.id, revision: 1, status: 'ready', userInput: { name: 'Mozkomor', world: 'Harry Potter' } },
    }));
    const participant = (identityId: string) => ({ identityId, displayName: 'Mozkomor', wandId: 'wand', strength: 1, cunning: 1, defense: 1 });
    const duel: Duel = {
      id: 'duel', revision: 1, state: 'Running', mode: 'interactive', round: 1, deadline: null,
      left: participant('left'), right: participant('right'), leftChoiceLocked: false, rightChoiceLocked: false,
      requiresParticipantConsent: true, leftJoined: true, rightJoined: true, rounds: [], result: null,
      narrationMode: 'web_only', narrationStatus: 'text_only_duel', commentary: null,
    };
    const client = { duels: vi.fn().mockResolvedValue([duel]), cooldownRemainingMs: () => 0 } as unknown as TestingSessionsClient;
    const wrapper = mount(TestingDuels, { props: { sessionId: 'left', sessions, characters, client, enabled: true } });
    try {
      await flushPromises();
      expect(wrapper.findAll('h3').map(heading => heading.text())).toEqual(['Mozkomor · Petr', 'Mozkomor · Jana']);
      await wrapper.setProps({ characters: [{ ...characters[0]!, label: null }, characters[1]!] });
      expect(wrapper.findAll('h3').map(heading => heading.text())).toEqual(['Mozkomor', 'Mozkomor · Jana']);
      expect(duel.left.displayName).toBe('Mozkomor');
    } finally { wrapper.unmount(); }
  });
});
