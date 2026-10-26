package de.dildovander.companion;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class ProfileGenerationTest {
    @Test public void forgetOrReplacementInvalidatesDelayedProfileResponses() {
        ProfileGeneration generation = new ProfileGeneration();
        long oldRequest = generation.begin();
        generation.invalidate();
        assertFalse(generation.isCurrent(oldRequest));
        long replacement = generation.begin();
        assertTrue(generation.isCurrent(replacement));
        assertFalse(generation.isCurrent(oldRequest));
    }
}
