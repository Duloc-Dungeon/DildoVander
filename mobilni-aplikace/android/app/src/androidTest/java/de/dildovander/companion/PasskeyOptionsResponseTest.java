package de.dildovander.companion;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.fail;

import java.io.IOException;
import org.junit.Test;

public final class PasskeyOptionsResponseTest {
    @Test
    public void optionsRejectionsPreserveBackendCodesAndStatusBeforeCredentialLaunch() throws Exception {
        for (String code : new String[] {
            "beta_enrollment_closed", "beta_enrollment_unavailable", "beta_enrollment_closed_or_changed",
            "invitation_expired_or_used", "reauthentication_required", "csrf_required"
        }) {
            assertRejected(403, "{\"code\":\"" + code + "\",\"message\":\"private response text\"}", code);
        }
        assertRejected(503, "{\"code\":\"beta_enrollment_unavailable\"}", "beta_enrollment_unavailable");
    }

    @Test
    public void invalidErrorBodiesKeepADefinitiveGenericRejection() throws Exception {
        for (String body : new String[] {
            null, "", "<html>private response text</html>", "[]", "{}", "{", "{\"code\":42}",
            "{\"code\":true}", "{\"code\":null}", "{\"code\":\"\"}",
            "{\"code\":\"private response text\"}", "{\"code\":\"beta_enrollment_closed\\n\"}",
            "{\"code\":\"Beta_enrollment_closed\"}", "{\"code\":\"" + repeated('a', 65) + "\"}",
            "{\"code\":\"beta_enrollment_closed\",\"detail\":\"" + repeated('x', PasskeyOptionsResponse.MAX_BYTES) + "\"}"
        }) {
            assertRejected(403, body, "passkey_rejected");
        }
        assertRejected(302, "{\"code\":\"beta_enrollment_closed\"}", "passkey_rejected");
    }

    @Test
    public void successfulOptionsStillUseTheRealJsonParserAndRejectInvalidResponses() throws Exception {
        assertEquals("test-challenge", PasskeyOptionsResponse.parse(200,
            "{\"challenge\":\"test-challenge\",\"rpId\":\"dildovander.de\"}").getString("challenge"));
        for (String body : new String[] { null, "", "[]", "{", repeated('x', PasskeyOptionsResponse.MAX_BYTES + 1) }) {
            try {
                PasskeyOptionsResponse.parse(200, body);
                fail("Invalid successful options reached credential launch");
            } catch (IOException failure) {
                assertFalse(failure instanceof PasskeyOptionsResponse.Rejected);
            }
        }
    }

    private static void assertRejected(int status, String body, String code) throws Exception {
        try {
            PasskeyOptionsResponse.parse(status, body);
            fail("Rejected options reached credential launch");
        } catch (PasskeyOptionsResponse.Rejected rejection) {
            assertEquals(status, rejection.status);
            assertEquals(code, rejection.code);
            assertEquals(PasskeyOptionsResponse.FAILURE_MESSAGE, rejection.getMessage());
        }
    }

    private static String repeated(char value, int count) {
        char[] characters = new char[count];
        java.util.Arrays.fill(characters, value);
        return new String(characters);
    }
}
