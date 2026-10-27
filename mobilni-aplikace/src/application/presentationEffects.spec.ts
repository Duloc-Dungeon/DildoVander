import { describe, expect, it } from 'vitest';
import { createPresentationGate } from './presentationEffects';

const event = { id: 'round-1', scopeId: 'match-1', sequence: 1, type: 'round-reveal', audience: 'participants' as const, projectionRevision: 2, occurredAt: 1000, replay: false };

describe('authorized presentation effects', () => {
  it('is silent until explicitly enabled and never plays private-sensitive events', () => {
    const gate = createPresentationGate();
    gate.setScope('match-1');
    expect(gate.accept(event, 1100)).toBe(false);
    gate.setEnabled(true);
    expect(gate.accept({ ...event, id: 'private-1', audience: 'self' }, 1100)).toBe(false);
    expect(gate.accept({ ...event, id: 'public-1', sequence: 2 }, 1100)).toBe(true);
  });
  it('drops replay, stale, future, wrong-scope and duplicate events', () => {
    const gate = createPresentationGate(); gate.setScope('match-1'); gate.setEnabled(true);
    expect(gate.accept({ ...event, id: 'replayed', sequence: 0, replay: true }, 1100)).toBe(false);
    expect(gate.accept({ ...event, id: 'stale', occurredAt: 0 }, 1100)).toBe(false);
    expect(gate.accept({ ...event, id: 'future', occurredAt: 2000 }, 1100)).toBe(false);
    expect(gate.accept({ ...event, scopeId: 'another' }, 1100)).toBe(false);
    expect(gate.accept({ ...event, sequence: 2 }, 1100)).toBe(true);
    expect(gate.accept(event, 1200)).toBe(false);
  });
  it('does not replay dropped background events after foreground return', () => {
    const gate = createPresentationGate(); gate.setScope('match-1'); gate.setEnabled(true);
    gate.setVisible(false);
    expect(gate.accept(event, 1100)).toBe(false);
    gate.setVisible(true);
    expect(gate.accept(event, 1200)).toBe(false);
  });
  it('does not replay initial snapshots or out-of-order revisions after reconnect', () => {
    const gate = createPresentationGate(); gate.setEnabled(true); gate.setScope('match-1');
    gate.prime(5, 9);
    expect(gate.accept({ ...event, sequence: 5, projectionRevision: 9 }, 1100)).toBe(false);
    expect(gate.accept({ ...event, id: 'older', sequence: 6, projectionRevision: 8 }, 1100)).toBe(false);
    expect(gate.accept({ ...event, id: 'next', sequence: 6, projectionRevision: 10 }, 1100)).toBe(true);
  });
});
