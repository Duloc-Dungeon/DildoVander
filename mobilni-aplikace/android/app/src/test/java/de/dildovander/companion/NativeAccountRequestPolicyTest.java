package de.dildovander.companion;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertThrows;
import static org.junit.Assert.assertTrue;

import java.util.Collections;
import java.util.Arrays;
import java.util.HashMap;
import java.util.Map;

import okhttp3.Request;
import org.junit.Test;

public final class NativeAccountRequestPolicyTest {
    @Test public void pushRoutesAreExplicitAndMethodBound() {
        for (String method : new String[] {"GET", "PUT", "DELETE"}) {
            assertTrue(NativeAccountRequestPolicy.allows("/app/v1/push", method));
        }
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/push/test", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/push", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/push/test", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/push?account=other", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/push/other", "DELETE"));
    }

    @Test public void applicationVersionIsReadOnlyAndHasNoQuery() {
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/application-version", "GET"));
        for (String method : Arrays.asList("POST", "PUT", "DELETE")) {
            assertFalse(NativeAccountRequestPolicy.allows("/app/v1/application-version", method));
        }
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/application-version?platform=ios", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/application-version/extra", "GET"));
    }

    @Test public void adminCharacterRoutesAndTransportLimitsAreExact() {
        String id = "12345678123442348234123456789abc";
        String profile = "/app/v1/admin/characters/" + id;
        String upload = profile + "/portrait?expectedRevision=12";
        String read = profile + "/portrait?revision=12";
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/characters", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/characters?after=" + id, "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/characters?after=" + id, "PUT"));
        assertTrue(NativeAccountRequestPolicy.allows(profile, "GET"));
        assertTrue(NativeAccountRequestPolicy.allows(profile, "PUT"));
        assertTrue(NativeAccountRequestPolicy.allows(upload, "PUT"));
        assertTrue(NativeAccountRequestPolicy.allows(read, "GET"));
        assertFalse(NativeAccountRequestPolicy.allows(read, "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows(upload, "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/characters", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows(profile, "DELETE"));
        for (String invalid : Arrays.asList(
            profile + "/portrait?revision=0", profile + "/portrait?expectedRevision=0",
            profile + "/portrait?expectedRevision=01", profile + "/portrait?expectedRevision=12&extra=1",
            profile + "/portrait?expectedRevision=12&expectedRevision=13",
            profile + "/portrait?expectedRevision=%31", profile + "/portrait?expectedRevision=12 ",
            "/app/v1/admin/characters?after=ABCDEF0123456789ABCDEF0123456789",
            "/app/v1/admin/characters?after=not-a-cursor",
            "/app/v1/admin/characters?after=" + id + "&extra=1",
            "/app/v1/admin/characters?after=" + id + "&after=" + id,
            "/app/v1/admin/characters?",
            "/app/v1/admin/characters/not-a-profile", "/app/v1/admin/characters/" + id + "/portrait/../other")) {
            assertFalse(NativeAccountRequestPolicy.allows(invalid, "GET"));
            assertFalse(NativeAccountRequestPolicy.allows(invalid, "PUT"));
        }
        assertEquals(256 * 1024, NativeAccountRequestPolicy.requestBodyLimit(profile, "PUT", "application/json"));
        assertEquals(8 * 1024 * 1024, NativeAccountRequestPolicy.requestBodyLimit(upload, "PUT", "image/webp"));
        assertEquals(256 * 1024, NativeAccountRequestPolicy.requestBodyLimit(upload, "PUT", "image/svg+xml"));
        assertEquals(8 * 1024 * 1024, NativeAccountRequestPolicy.responseBodyLimit(read, "GET"));
        assertEquals(256 * 1024, NativeAccountRequestPolicy.responseBodyLimit(upload, "PUT"));
        assertEquals(256 * 1024, NativeAccountRequestPolicy.responseBodyLimit(profile, "GET"));
        assertEquals(256 * 1024, NativeAccountRequestPolicy.responseBodyLimit("/app/v1/admin/characters?after=" + id, "GET"));
    }

    @Test public void sealedCaseRoutesAreMethodAndScopeBound() {
        String root = "/app/v1/sealed-cases";
        String id = root + "/sc_" + String.join("", Collections.nCopies(32, "a"));
        assertTrue(NativeAccountRequestPolicy.allows(root, "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(root + "/current", "GET"));
        for (String read : Arrays.asList("play", "context", "moderator", "portraits/player_1")) {
            assertTrue(NativeAccountRequestPolicy.allows(id + "/" + read, "GET"));
            assertFalse(NativeAccountRequestPolicy.allows(id + "/" + read, "POST"));
        }
        for (String write : Arrays.asList("lobby", "start", "actions", "pause", "resume", "statements", "ballots", "oversight", "disconnect", "scenario-consent", "ai-players")) {
            assertTrue(NativeAccountRequestPolicy.allows(id + "/" + write, "POST"));
            assertFalse(NativeAccountRequestPolicy.allows(id + "/" + write, "GET"));
        }
        for (String invalid : Arrays.asList(id + "/world", id + "/play?viewer=other", id + "/portraits/../other", root + "/sc_bad/play", id + "/play/", id + "/%70lay")) {
            assertFalse(NativeAccountRequestPolicy.allows(invalid, "GET"));
        }
    }

    @Test public void workshopArtworkReadsAreBoundToExactIdentifiersAndMethod() {
        String operation = "a".repeat(32);
        String asset = "123e4567-e89b-42d3-a456-426614174000";
        String path = "/app/v1/customer/wand/workshop/sessions/" + operation + "/artwork/assets/" + asset;
        assertTrue(NativeAccountRequestPolicy.allows(path, "GET"));
        assertTrue(NativeAccountRequestPolicy.allows(path.replace(asset, asset.toUpperCase(java.util.Locale.ROOT)), "GET"));
        assertEquals(32 * 1024 * 1024, NativeAccountRequestPolicy.responseBodyLimit(path, "GET"));
        for (String method : Arrays.asList("POST", "PUT", "DELETE", "HEAD")) {
            assertFalse(NativeAccountRequestPolicy.allows(path, method));
        }
        for (String invalid : Arrays.asList(path + "?x=1", path + "?", path + "/extra", path + "/", path + "\n",
                path.replace(operation, "a".repeat(31)), path.replace(operation, "a".repeat(33)),
                path.replace(asset, "bad"), path.replace(asset, asset.replace("-42d3-", "-02d3-")),
                path.replace(asset, asset.replace("-a456-", "-0456-")), path.replace("/artwork/assets/", "/assets/"))) {
            assertFalse(NativeAccountRequestPolicy.allows(invalid, "GET"));
        }
    }

    @Test public void consentAndWandWorkshopRoutesAreMethodBound() {
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/account/ai-consent", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/account/ai-consent", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/customer/wand", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/customer/wand/sessions", "POST"));
        String path = "/app/v1/customer/wand/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/ticket";
        assertTrue(NativeAccountRequestPolicy.allows(path, "POST"));
        assertFalse(NativeAccountRequestPolicy.allows(path, "GET"));
        assertFalse(NativeAccountRequestPolicy.allows(path + "?extra=1", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows(path + "/extra", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows(path.replace("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bad"), "POST"));
    }

    @Test public void mobileTrunkWorkshopRoutesAndAudioLimitsAreExplicit() {
        String root = "/app/v1/customer/wand/workshop";
        String session = root + "/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        assertTrue(NativeAccountRequestPolicy.allows(root, "GET"));
        assertTrue(NativeAccountRequestPolicy.allows(root + "?upgrade=1", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows(root + "?upgrade=0", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows(root + "/sessions", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(root + "/sessions/current", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows(root + "/sessions/current?upgrade=1", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows(session, "GET"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/answers", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/skip", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/audio", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/transcribe", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/artwork/retry", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/messages", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/finish", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/ticket", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/artwork/repair", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/messages/live-call_7", "PUT"));
        assertTrue(NativeAccountRequestPolicy.allows(session + "/answers/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows(session + "/answers", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows(session + "/skip", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows(session + "/audio/extra", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows(session + "/messages/live-call_7", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows(session + "/messages/has.dot", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows(session + "/messages/" + "a".repeat(161), "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows(session + "/messages/live-call_7?x=1", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows(session + "/artwork/repair", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows(session + "/answers/invalid", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows(session + "/transcribe?retry=1", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows(root + "?upgrade=1&upgrade=0", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows(root + "?upgrade=true", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows(root + "/sessions/current?extra=1", "GET"));

        assertEquals(8 * 1024 * 1024, NativeAccountRequestPolicy.requestBodyLimit(session + "/transcribe", "POST", "audio/webm; codecs=opus"));
        assertEquals(8 * 1024 * 1024, NativeAccountRequestPolicy.requestBodyLimit(session + "/transcribe", "POST", "audio/mp4"));
        assertEquals(16 * 1024, NativeAccountRequestPolicy.requestBodyLimit(session + "/transcribe", "POST", "application/json"));
        assertEquals(16 * 1024, NativeAccountRequestPolicy.requestBodyLimit(session + "/audio", "POST", "application/json"));
        assertEquals(8 * 1024 * 1024, NativeAccountRequestPolicy.responseBodyLimit(session + "/audio", "POST"));
        assertEquals(32 * 1024 * 1024, NativeAccountRequestPolicy.responseBodyLimit(session + "/transcribe", "POST"));
    }

    @Test public void transcriptionQuestionHeadersArePathAndValueBound() {
        String session = "/app/v1/customer/wand/workshop/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        String path = session + "/transcribe";
        Map<String, String> headers = new HashMap<>();
        headers.put("X-Wand-Question-Id", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        headers.put("X-Wand-Question-Revision", "3");
        assertTrue(NativeAccountRequestPolicy.apply(new Request.Builder(), "https://dildovander.de", null,
            "csrf", "POST", false, headers, path) != null);
        assertThrows(IllegalArgumentException.class, () -> NativeAccountRequestPolicy.apply(new Request.Builder(),
            "https://dildovander.de", null, "csrf", "POST", false, Collections.emptyMap(), path));
        assertThrows(IllegalArgumentException.class, () -> NativeAccountRequestPolicy.apply(new Request.Builder(),
            "https://dildovander.de", null, "csrf", "POST", false,
            Collections.singletonMap("X-Wand-Question-Id", "b".repeat(32)), path));

        Map<String, String> invalidId = new HashMap<>(headers);
        invalidId.put("X-Wand-Question-Id", "B".repeat(32));
        assertThrows(IllegalArgumentException.class, () -> NativeAccountRequestPolicy.apply(new Request.Builder(),
            "https://dildovander.de", null, "csrf", "POST", false, invalidId, path));
        Map<String, String> invalidRevision = new HashMap<>(headers);
        invalidRevision.put("X-Wand-Question-Revision", "0");
        assertThrows(IllegalArgumentException.class, () -> NativeAccountRequestPolicy.apply(new Request.Builder(),
            "https://dildovander.de", null, "csrf", "POST", false, invalidRevision, path));
        assertThrows(IllegalArgumentException.class, () -> NativeAccountRequestPolicy.apply(new Request.Builder(),
            "https://dildovander.de", null, "csrf", "POST", false, headers, session + "/answers"));
    }
    @Test public void mobileGameRoutesAreExplicitAndMethodBound() {
        String match = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/game", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/game/matches", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/game/matches/" + match, "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/game/matches/" + match + "/choose", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/game/matches/" + match + "/choose", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/game/matches/" + match + "/choose?extra=1", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/game/matches/" + match + "/admin", "POST"));
    }
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
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/waiting-room", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/waiting-room?after=abcdef0123456789abcdef0123456789", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/waiting-room?after=bad", "GET"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/waiting-room?after=abcdef0123456789abcdef0123456789&extra=1", "GET"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/waiting-room/abcdef0123456789abcdef0123456789/group", "PUT"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/0123456789abcdef0123456789abcdef/countdown", "PUT"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/0123456789abcdef0123456789abcdef/countdown/confirm", "POST"));
        assertTrue(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/0123456789abcdef0123456789abcdef/start", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/waiting-room", "POST"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/waiting-room/not-a-user/group", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/waiting-room/abcdef0123456789abcdef0123456789/group?other=1", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/0123456789abcdef0123456789abcdef/countdown/confirm", "PUT"));
        assertFalse(NativeAccountRequestPolicy.allows("/app/v1/admin/launch/groups/../countdown", "PUT"));
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
