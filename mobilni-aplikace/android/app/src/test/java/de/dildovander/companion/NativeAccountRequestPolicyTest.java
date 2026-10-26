package de.dildovander.companion;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertThrows;
import static org.junit.Assert.assertTrue;

import java.util.Collections;
import java.util.HashMap;
import java.util.Map;

import okhttp3.Request;
import org.junit.Test;

public final class NativeAccountRequestPolicyTest {
    @Test public void acceptsAccountRoutesAndRejectsExternalOrArbitraryAdminRoutes() {
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/account", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/v1/admin/show/chronicle?deviceId=device-1&eveningId=evening-1", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("https://attacker.invalid/app/v1/account", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/v1/admin/modes", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/modes", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/unlisted", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/passkeys/request-options", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/launch", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/enrollment/default", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/enrollment/default", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/enrollment/default/admit", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/enrollment/default/admit", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/customer/access", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/customer/access", "PUT"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/archive/owned/11111111-1111-4111-a111-111111111111", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/customer/completion-updates?limit=20", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/testing-sessions/0123456789abcdef0123456789abcdef/character", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows("/v1/show/devices/device-1/arbitrary-admin-route", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows("/v1/show/devices/device-1/commands", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows("/v1/admin/logs?limit=200&level=info&after=10", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/v1/admin/logs?unexpected=true", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/%2e%2e/admin", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/account#fragment", "GET"));
    }

    @Test public void launchGroupsHaveNarrowMethodsAndNoQueriesOrTraversal() {
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/0123456789abcdef0123456789abcdef", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/0123456789abcdef0123456789abcdef", "DELETE"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups", "DELETE"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups?other=1", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/../audit", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/not-a-group", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups//0123456789abcdef0123456789abcdef", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/0123456789abcdef0123456789abcdef/", "PUT"));
    }

    @Test public void betaAdmissionAndMemberRoutesHaveNarrowMethods() {
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/beta-admission", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/beta-admission", "PUT"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/beta-admission/group", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/0123456789abcdef0123456789abcdef/members", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/0123456789abcdef0123456789abcdef/members/abcdef0123456789abcdef0123456789", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/not-a-group/members", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/0123456789abcdef0123456789abcdef/members/../users", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/beta-admission", "DELETE"));
    }

    @Test public void requestConstructionAddsOriginCookieCsrfAndExplicitConfirmation() {
        Map<String, String> headers = new HashMap<>();
        headers.put("Accept", "application/json");
        Request request = NativeAccountRequestPolicy.apply(new Request.Builder().url("https://dildovander.de/app/v1/account"),
            "https://dildovander.de", "__Host-dv-account=session", "csrf-value", "DELETE", true,
            headers, "/app/v1/account").delete().build();
        assertEquals("https://dildovander.de", request.header("Origin"));
        assertEquals("__Host-dv-account=session", request.header("Cookie"));
        assertEquals("csrf-value", request.header("X-DV-CSRF"));
        assertEquals("yes", request.header("X-DV-Confirm"));
        assertEquals("application/json", request.header("Accept"));
    }

    @Test public void callerCannotOverrideCsrfOrInjectAuthorization() {
        Map<String, String> headers = new HashMap<>();
        headers.put("Authorization", "Bearer untrusted");
        assertThrows(IllegalArgumentException.class, () -> NativeAccountRequestPolicy.apply(new Request.Builder().url("https://dildovander.de/app/v1/account"),
            "https://dildovander.de", null, "csrf-value", "POST", false, headers, "/app/v1/account"));
        assertThrows(IllegalArgumentException.class, () -> NativeAccountRequestPolicy.apply(new Request.Builder().url("https://dildovander.de/app/v1/account"),
            "https://dildovander.de", null, "", "POST", false, Collections.emptyMap(), "/app/v1/account"));
    }

    @Test public void collectionQueryUsesCanonicalFormEncodingAndSwiftUtf16Limit() throws Exception {
        String eightyCzechChars = "č".repeat(80);
        String encoded = java.net.URLEncoder.encode(eightyCzechChars, "UTF-8");
        assertEquals(480, encoded.length());
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/archive/collection?q=" + encoded, "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/archive/collection?q=" + java.net.URLEncoder.encode("č".repeat(81), "UTF-8"), "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/archive/collection?q=+term", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/archive/collection?q=%c4%8D", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/archive/collection?q=hello+world", "GET"));
    }
}
