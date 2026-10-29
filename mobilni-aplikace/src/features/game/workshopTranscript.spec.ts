import { describe, expect, it } from 'vitest';
import { emptyWorkshopTranscript, updateWorkshopTranscript } from './workshopTranscript';

describe('streamed workshop captions', () => {
  it('replaces revised recognition instead of duplicating words and retains both speakers', () => {
    let state = emptyWorkshopTranscript();
    state = updateWorkshopTranscript(state, { sequence: 1, role: 'user', text: 'Chci chranit', final: false });
    state = updateWorkshopTranscript(state, { sequence: 1, role: 'user', text: 'Chci chránit most', final: false });
    state = updateWorkshopTranscript(state, { sequence: 1, role: 'user', text: 'Chci chránit most.', final: true });
    state = updateWorkshopTranscript(state, { sequence: 2, role: 'assistant', text: 'Takže', final: false });
    state = updateWorkshopTranscript(state, { sequence: 2, role: 'assistant', text: 'Takže most.', final: true });
    expect(state.user?.text).toBe('Chci chránit most.');
    expect(state.assistant?.text).toBe('Takže most.');
  });

  it('rejects stale assistant updates after the guest starts speaking again', () => {
    let state = updateWorkshopTranscript(emptyWorkshopTranscript(), { sequence: 1, role: 'assistant', text: 'První nápad', final: false });
    state = updateWorkshopTranscript(state, { sequence: 2, role: 'user', text: 'Počkej', final: false });
    expect(updateWorkshopTranscript(state, { sequence: 1, role: 'assistant', text: 'Starý nápad navíc', final: true })).toBe(state);
  });

  it('never reopens a final turn or assigns one turn to two speakers', () => {
    const state = updateWorkshopTranscript(emptyWorkshopTranscript(), { sequence: 1, role: 'user', text: 'Hotová odpověď.', final: true });
    expect(updateWorkshopTranscript(state, { sequence: 1, role: 'user', text: 'Hotová', final: false })).toBe(state);
    expect(updateWorkshopTranscript(state, { sequence: 1, role: 'assistant', text: 'Záměna role', final: false })).toBe(state);
  });
});
