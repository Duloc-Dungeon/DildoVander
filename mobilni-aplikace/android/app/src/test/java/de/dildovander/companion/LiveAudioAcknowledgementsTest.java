package de.dildovander.companion;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class LiveAudioAcknowledgementsTest {
    @Test public void permitsDelayedAcknowledgementsForAnOlderInputInOrder() {
        LiveAudioAcknowledgements queue = new LiveAudioAcknowledgements();
        assertTrue(queue.enqueue("input-a", 0, 9600, 100));
        assertTrue(queue.enqueue("input-b", 0, 9600, 300));
        assertTrue(queue.acknowledge("input-a", 0));
        assertTrue(queue.acknowledge("input-b", 0));
    }

    @Test public void rejectsOutOfOrderAndTimesOutUnacknowledgedAudio() {
        LiveAudioAcknowledgements queue = new LiveAudioAcknowledgements();
        assertTrue(queue.enqueue("input-a", 1, 9600, 100));
        assertFalse(queue.acknowledge("input-a", 2));
        assertFalse(queue.timedOut(10_099));
        assertTrue(queue.timedOut(10_100));
    }
}
