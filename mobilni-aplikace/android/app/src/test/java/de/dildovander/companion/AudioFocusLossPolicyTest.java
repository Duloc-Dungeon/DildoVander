package de.dildovander.companion;

import org.junit.Test;

import static org.junit.Assert.*;

public final class AudioFocusLossPolicyTest {
    @Test public void stopsForPermanentAndTransientLossButNotGainOrDuck() {
        assertTrue(AudioFocusLossPolicy.shouldStop(-1)); // AUDIOFOCUS_LOSS
        assertTrue(AudioFocusLossPolicy.shouldStop(-2)); // AUDIOFOCUS_LOSS_TRANSIENT
        assertTrue(AudioFocusLossPolicy.shouldStop(-3)); // AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK
        assertFalse(AudioFocusLossPolicy.shouldStop(1)); // AUDIOFOCUS_GAIN
        assertFalse(AudioFocusLossPolicy.shouldStop(2)); // AUDIOFOCUS_GAIN_TRANSIENT
    }
}
