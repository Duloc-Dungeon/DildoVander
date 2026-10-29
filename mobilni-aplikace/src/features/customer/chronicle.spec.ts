import { describe, expect, it } from 'vitest';
import { isChronicleEvent, parseChronicleItem } from './chronicle';

const eventId = '15ca3a2a-8612-4d66-8fc4-8e65a64c9712';

describe('chronicle projection', () => {
  it('keeps locked placeholders free of any result, story, people or badge data', () => {
    const item = parseChronicleItem({
      id: eventId, sourceKind: 'yolo', sourceId: '7d2a779a73e94ab19f2225acdd108eaa',
      occurredAt: 1793300000, illustrationState: 'ready', revealed: false, revision: 1,
      title: 'SECRET: the opponent lost', summary: 'hidden story', result: 'loss', winnerId: 'c1',
      participants: [{ displayName: 'hidden player' }], awards: [{ title: 'hidden badge' }],
    });
    expect(item).toEqual({ id: eventId, sourceKind: 'yolo', sourceId: '7d2a779a73e94ab19f2225acdd108eaa',
      occurredAt: 1793300000, illustrationState: 'ready', revealed: false });
    expect(item && isChronicleEvent(item)).toBe(false);
  });

  it('accepts a complete event with account-scoped decisions and exact award provenance', () => {
    const item = parseChronicleItem({
      id: eventId, sourceKind: 'yolo', sourceId: '7d2a779a73e94ab19f2225acdd108eaa',
      groupId: '779bc47013c24400b28b8a4507d56367', occurredAt: 1793300000,
      title: 'Konvice, která odmítla doručení', summary: 'Berta doručila konvici.',
      participants: [{ accountId: '399b5bbdd3f647c5b3d7a8167ce8b922', displayName: 'Berta', role: 'challenger', characterName: 'Berta Popelavá', wandName: 'Jasan', visualReferenceVersion: 'portrait-v1' }],
      result: 'winner', winnerId: '399b5bbdd3f647c5b3d7a8167ce8b922', story: ['Plán', 'Komplikace', 'Rozsudek'],
      decisions: [{ accountId: '399b5bbdd3f647c5b3d7a8167ce8b922', choice: 'Ochránit konvici', consequence: 'Plán pokračoval.' }],
      awards: [{ id: 'expert-victim', title: 'Oběť odborníka', scope: 'group', evidence: 'První YOLO zápas.', eventId, acquiredAt: 1793300000 }],
      illustrationState: 'not_requested', revealed: true, shareText: 'Obřadně doručeno.', revision: 3,
      decisiveChoice: 'Ochrana konvice',
    });
    expect(item && isChronicleEvent(item)).toBe(true);
    if (!item || !isChronicleEvent(item)) throw new Error('Expected revealed event');
    expect(item.awards[0].eventId).toBe(item.id);
    expect(item.decisions[0].consequence).toBe('Plán pokračoval.');
  });

  it('rejects mismatched award origins and malformed identifiers', () => {
    const base = {
      id: eventId, sourceKind: 'duel', sourceId: '12345678123442348234123456789abc',
      groupId: '779bc47013c24400b28b8a4507d56367', occurredAt: 1793300000,
      title: 'Duel', summary: 'Shrnutí.', participants: [], story: [], decisions: [],
      awards: [{ id: 'award', title: 'Odznak', scope: 'account', evidence: 'Důkaz', eventId: '25ca3a2a-8612-4d66-8fc4-8e65a64c9712', acquiredAt: 1793300000 }],
      illustrationState: 'failed', revealed: true, revision: 1,
    };
    expect(parseChronicleItem(base)).toBeNull();
    expect(parseChronicleItem({ ...base, sourceKind: 'unknown' })).toBeNull();
  });

  it('accepts bounded opaque conversation sources, boss actors and decorated group names', () => {
    const item = parseChronicleItem({
      id: eventId, sourceKind: 'conversation', sourceId: 'c'.repeat(64), groupId: 'account:399b5bbdd3f647c5b3d7a8167ce8b922',
      groupName: 'Osobní sbírka', occurredAt: 1793300000, title: 'Vzpomínka z dílny', summary: 'Jedna zvolená replika.',
      participants: [{ accountId: 'baron:779bc47013c24400b28b8a4507d56367', displayName: 'Baron', role: 'opponent', visualReferenceVersion: 'boss-v1' }],
      result: 'loss', winnerId: 'baron:779bc47013c24400b28b8a4507d56367', story: ['Soukromý rozhovor.'], decisions: [],
      awards: [], illustrationState: 'ready', revealed: true, revision: 1, shareAllowed: false,
    });
    expect(item && isChronicleEvent(item)).toBe(true);
    if (!item || !isChronicleEvent(item)) throw new Error('Expected opaque source to remain readable.');
    expect(item.sourceId).toBe('c'.repeat(64));
    expect(item.groupName).toBe('Osobní sbírka');
  });
});
