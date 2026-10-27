// This gate only consumes already-authorized projections, never WORLD_TRUTH.
export interface PresentationEvent {
  id: string;
  scopeId: string;
  sequence: number;
  type: string;
  audience: 'self' | 'public' | 'participants';
  projectionRevision: number;
  occurredAt: number;
  replay: boolean;
}

export function createPresentationGate() {
  let enabled = false;
  let visible = true;
  let scope = '';
  let sequence = -1;
  let revision = -1;
  const seen = new Set<string>();
  return {
    setEnabled(value: boolean) { enabled = value; },
    setVisible(value: boolean) { visible = value; },
    setScope(value: string) {
      if (value === scope) return;
      scope = value; sequence = -1; revision = -1; seen.clear();
    },
    prime(value: number, projectionRevision: number) {
      sequence = Math.max(sequence, value); revision = Math.max(revision, projectionRevision);
    },
    accept(event: PresentationEvent, now: number): boolean {
      if (!scope || event.scopeId !== scope || !event.id || seen.has(event.id)) return false;
      seen.add(event.id);
      if (seen.size > 256) seen.delete(seen.values().next().value!);
      const ordered = event.sequence > sequence && event.projectionRevision >= revision;
      if (!ordered) return false;
      sequence = Math.max(sequence, event.sequence);
      revision = Math.max(revision, event.projectionRevision);
      return enabled && visible && ordered && !event.replay && event.audience !== 'self'
        && Number.isFinite(event.occurredAt) && now >= event.occurredAt && now - event.occurredAt <= 800;
    },
  };
}
