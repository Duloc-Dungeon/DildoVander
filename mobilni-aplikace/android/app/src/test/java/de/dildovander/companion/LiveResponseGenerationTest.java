package de.dildovander.companion;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class LiveResponseGenerationTest {
    @Test public void dropsRetiredAudioAndProposalEventsAndRejectsFutureEvents() {
        LiveResponseGeneration state = new LiveResponseGeneration();
        assertEquals(LiveResponseGeneration.Decision.ACCEPT, state.accepts(0));
        assertTrue(state.advance(1));
        assertEquals(LiveResponseGeneration.Decision.DROP, state.accepts(0));
        assertEquals(LiveResponseGeneration.Decision.ACCEPT, state.accepts(1));
        assertEquals(LiveResponseGeneration.Decision.INVALID, state.accepts(2));
    }

    @Test public void cancellationCannotMoveGenerationBackwards() {
        LiveResponseGeneration state = new LiveResponseGeneration();
        assertTrue(state.advance(1));
        assertFalse(state.advance(1));
        assertFalse(state.advance(0));
        assertEquals(1, state.current());
    }

    @Test public void missingGenerationIsOnlyAcceptedBeforeFirstCancellation() {
        LiveResponseGeneration state = new LiveResponseGeneration();
        assertEquals(LiveResponseGeneration.Decision.ACCEPT, state.accepts(-1));
        assertTrue(state.advance(1));
        assertEquals(LiveResponseGeneration.Decision.DROP, state.accepts(-1));
    }
}
