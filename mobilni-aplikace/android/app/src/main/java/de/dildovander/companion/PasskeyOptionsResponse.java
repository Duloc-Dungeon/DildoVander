package de.dildovander.companion;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import org.json.JSONException;
import org.json.JSONObject;

final class PasskeyOptionsResponse {
    static final int MAX_BYTES = 131072;
    static final String FAILURE_MESSAGE = "Passkey verification failed. Try again or use another sign-in method.";

    static final class Rejected extends IOException {
        final int status;
        final String code;

        Rejected(int status, String code) {
            super(FAILURE_MESSAGE);
            this.status = status;
            this.code = code;
        }
    }

    static JSONObject parse(int status, String body) throws IOException {
        boolean bounded = body != null && body.getBytes(StandardCharsets.UTF_8).length <= MAX_BYTES;
        if (status != 200) {
            String code = "passkey_rejected";
            if (status >= 400 && status <= 599 && bounded) {
                try {
                    Object value = new JSONObject(body).opt("code");
                    // Only forward a bounded machine code, never server messages or arbitrary values.
                    if (value instanceof String && ((String) value).matches("[a-z][a-z0-9_]{0,63}")) {
                        code = (String) value;
                    }
                } catch (JSONException ignored) { /* Keep the generic rejection for non-JSON responses. */ }
            }
            throw new Rejected(status, code);
        }
        if (!bounded) throw new IOException("Passkey options are too large or missing");
        try {
            return new JSONObject(body);
        } catch (JSONException exception) {
            throw new IOException("Passkey options are invalid", exception);
        }
    }

    private PasskeyOptionsResponse() { }
}
