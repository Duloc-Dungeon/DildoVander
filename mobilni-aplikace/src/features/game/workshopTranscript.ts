export interface WorkshopTranscriptSnapshot {
  role: 'user' | 'assistant';
  sequence: number;
  text: string;
  final: boolean;
}

export interface WorkshopTranscriptState {
  latestSequence: number;
  user: WorkshopTranscriptSnapshot | null;
  assistant: WorkshopTranscriptSnapshot | null;
}

export const emptyWorkshopTranscript = (): WorkshopTranscriptState => ({ latestSequence: 0, user: null, assistant: null });

/** Interim recognition is a revision of a full utterance, never another delta. */
export function updateWorkshopTranscript(state: WorkshopTranscriptState, snapshot: WorkshopTranscriptSnapshot): WorkshopTranscriptState {
  if (snapshot.sequence < state.latestSequence) return state;
  const previous = state[snapshot.role];
  const other = state[snapshot.role === 'user' ? 'assistant' : 'user'];
  if (other?.sequence === snapshot.sequence || previous?.sequence === snapshot.sequence && previous.final) return state;
  return { ...state, latestSequence: snapshot.sequence, [snapshot.role]: snapshot };
}
