package de.dildovander.companion;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class NativeGuestHeaderPolicyTest {
    @Test public void allowsTraceContextAndOnlyScopedDuelBearerGrant() {
        assertTrue(NativeGuestHeaderPolicy.allows("/guest/v1/archive/items", "GET", "traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-00"));
        assertTrue(NativeGuestHeaderPolicy.allows("/guest/v1/duels/11111111-1111-1111-1111-111111111111/choices", "POST", "authorization", "Bearer AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        assertFalse(NativeGuestHeaderPolicy.allows("/guest/v1/archive/items", "GET", "authorization", "Bearer AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        assertFalse(NativeGuestHeaderPolicy.allows("/guest/v1/duels/11111111-1111-1111-1111-111111111111/choices", "GET", "authorization", "Bearer AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }
}
