package de.dildovander.companion;

import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.HashSet;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

import okhttp3.Request;

final class NativeAccountRequestPolicy {
    private static final Set<String> HEADERS = new HashSet<>(Arrays.asList(
        "accept", "content-type", "x-dv-csrf", "x-dv-confirm", "x-dv-device-id", "idempotency-key",
        "session-id", "boot-id", "media-epoch", "input-epoch", "input-sequence", "x-dv10-consent", "traceparent",
        "x-wand-question-id", "x-wand-question-revision"));

    private NativeAccountRequestPolicy() { }

    static boolean allows(String path, String method) {
        if (path == null || method == null || path.length() > 20000 || !path.startsWith("/") || path.startsWith("//") ||
            path.contains("\\") || path.contains("#") || path.contains("\r") || path.contains("\n") ||
            path.contains("//") || !("GET".equals(method) || "POST".equals(method) || "PUT".equals(method) || "DELETE".equals(method))) return false;
        try {
            URI uri = new URI(path);
            if (uri.isAbsolute() || uri.getRawAuthority() != null || uri.getRawPath() == null || !uri.getRawPath().equals(uri.getPath()) || uri.getRawFragment() != null) return false;
            String route = uri.getRawPath();
            if (route.startsWith("/app/v1/")) return allowsAppPath(route, method, uri.getRawQuery());
            if ("/v1/admin/show/chronicle".equals(route)) return "GET".equals(method) && validQuery(uri.getRawQuery(), "deviceId", "eveningId");
            if ("/v1/admin/logs".equals(route)) return "GET".equals(method) && validDeviceQuery(uri.getRawQuery());
            if ("/v1/evenings".equals(route)) return uri.getRawQuery() == null && ("GET".equals(method) || "POST".equals(method));
            if ("/v1/twin/instances".equals(route) || route.startsWith("/v1/twin/instances/")) return allowsTwinPath(route, method);
            if (route.startsWith("/v1/show/devices/")) return allowsShowPath(route, method, uri.getRawQuery());
            return false;
        } catch (Exception exception) { return false; }
    }

    private static boolean allowsTwinPath(String path, String method) {
        String[] parts = path.split("/");
        if (parts.length == 4) return "GET".equals(method);
        if (parts.length == 5 && validSegment(parts[4], 96)) return "GET".equals(method);
        if (parts.length == 6 && validSegment(parts[4], 96)) {
            return ("recording".equals(parts[5]) && "GET".equals(method)) ||
                ("commands".equals(parts[5]) && "POST".equals(method)) ||
                ("settings".equals(parts[5]) && ("GET".equals(method) || "PUT".equals(method)));
        }
        return parts.length == 7 && validSegment(parts[4], 96) && "media".equals(parts[5]) &&
            (("GET".equals(method) && Arrays.asList("state", "output").contains(parts[6])) ||
             ("POST".equals(method) && Arrays.asList("start", "cancel", "input", "photo", "played").contains(parts[6])));
    }

    private static boolean allowsShowPath(String path, String method, String query) {
        String[] parts = path.split("/");
        if (parts.length < 5 || !validSegment(parts[4], 128)) return false;
        String[] suffix = Arrays.copyOfRange(parts, 5, parts.length);
        if ("GET".equals(method)) {
            if (suffix.length == 0) return query == null;
            if (suffix.length == 1 && Arrays.asList("identities", "prophecies", "duels").contains(suffix[0])) return validQuery(query, "eveningId");
            if (suffix.length == 2 && Arrays.asList("duels", "ambient-previews").contains(suffix[0])) return query == null && isUuid(suffix[1]);
            return false;
        }
        if (!"POST".equals(method) || query != null) return false;
        if (suffix.length == 1) return Arrays.asList("commands", "end-evening", "offline-outcomes", "duels", "ambient-previews").contains(suffix[0]);
        if (suffix.length == 3 && "prophecies".equals(suffix[0]) && "reveal".equals(suffix[2])) return validSegment(suffix[1], 128);
        return (suffix.length == 3 && "duels".equals(suffix[0]) && isUuid(suffix[1]) && Arrays.asList("start", "cancel", "grants").contains(suffix[2])) ||
            (suffix.length == 4 && "duels".equals(suffix[0]) && isUuid(suffix[1]) && "grants".equals(suffix[2]) && "revoke".equals(suffix[3]));
    }

    private static boolean validSegment(String value, int limit) {
        return value != null && !value.isEmpty() && value.length() <= limit && value.matches("^[A-Za-z0-9._-]+$");
    }

    private static boolean isUuid(String value) {
        return value != null && value.matches("(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
    }

    private static boolean isArchiveUuid(String value) {
        return value != null && value.matches("(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");
    }

    private static boolean validQuery(String raw, String expected) {
        if (raw == null) return false;
        String[] pairs = raw.split("&", -1);
        return pairs.length == 1 && pairs[0].startsWith(expected + "=") && pairs[0].length() > expected.length() + 1 && pairs[0].length() <= 256;
    }

    private static boolean validDeviceQuery(String raw) {
        if (raw == null) return true;
        Set<String> allowed = new HashSet<>(Arrays.asList("after", "limit", "level", "traceId", "operationId"));
        Set<String> seen = new HashSet<>();
        for (String pair : raw.split("&", -1)) {
            String[] fields = pair.split("=", -1);
            if (fields.length != 2 || !allowed.contains(fields[0]) || !seen.add(fields[0]) || fields[1].length() > 128) return false;
            if ("after".equals(fields[0]) || "limit".equals(fields[0])) {
                if (!fields[1].matches("^[0-9]{1,19}$")) return false;
            } else if ("level".equals(fields[0]) && !Arrays.asList("trace", "debug", "information", "warning", "error", "critical", "info").contains(fields[1])) return false;
        }
        return true;
    }

    private static boolean allowsSealedCasePath(String path, String method) {
        if ("/app/v1/sealed-cases".equals(path)) return "POST".equals(method);
        if ("/app/v1/sealed-cases/current".equals(path)) return "GET".equals(method);
        if (path.matches("^/app/v1/sealed-cases/sc_[a-f0-9]{32}/portraits/[A-Za-z0-9_-]{1,128}$")) return "GET".equals(method);
        if (!path.matches("^/app/v1/sealed-cases/sc_[a-f0-9]{32}(/[a-z-]+)?$")) return false;
        String[] parts = path.split("/");
        if (parts.length == 5) return "GET".equals(method);
        return "GET".equals(method) ? Arrays.asList("play", "context", "moderator").contains(parts[5])
            : "POST".equals(method) && Arrays.asList("lobby", "start", "actions", "pause", "resume", "statements", "ballots", "oversight", "disconnect", "scenario-consent", "ai-players").contains(parts[5]);
    }

    static int requestBodyLimit(String path, String method, String contentType) {
        if (adminCharacterProfilePath(path) && "PUT".equals(method)) return 256 * 1024;
        if (adminCharacterPortraitPath(path, method, true)) {
            return Arrays.asList("image/jpeg", "image/png", "image/webp").contains(mediaType(contentType))
                ? 8 * 1024 * 1024 : 256 * 1024;
        }
        if (path.matches("^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}/transcribe$")
            && "POST".equals(method) && Arrays.asList("audio/webm", "audio/mp4", "audio/ogg", "audio/wav").contains(mediaType(contentType))) {
            return 8 * 1024 * 1024;
        }
        if (path.matches("^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}/audio$") && "POST".equals(method)) {
            return 16 * 1024;
        }
        if (path.startsWith("/app/v1/customer/wand/workshop/")) return 16 * 1024;
        return 32 * 1024 * 1024;
    }

    static int responseBodyLimit(String path, String method) {
        if (("GET".equals(method) && (adminCharacterListPath(path) || adminCharacterProfilePath(path))) ||
            ("PUT".equals(method) && (adminCharacterProfilePath(path) || adminCharacterPortraitPath(path, method, true)))) {
            return 256 * 1024;
        }
        if (adminCharacterPortraitPath(path, method, false)) return 8 * 1024 * 1024;
        if (path.matches("^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}/audio$") && "POST".equals(method)) {
            return 8 * 1024 * 1024;
        }
        return 32 * 1024 * 1024;
    }

    private static String mediaType(String contentType) {
        return contentType == null ? "" : contentType.split(";", 2)[0].trim().toLowerCase(Locale.ROOT);
    }

    private static boolean allowsAppPath(String path, String method, String query) {
        if (allowsWandWorkshopPath(path, method, query)) return true;
        if (query == null && allowsSealedCasePath(path, method)) return true;
        if (query == null && "/app/v1/game".equals(path)) return "GET".equals(method);
        if (query == null && "/app/v1/game/matches".equals(path)) return "POST".equals(method);
        if (query == null && "/app/v1/game/progress".equals(path)) return "GET".equals(method);
        if (query == null && "/app/v1/game/progress/ack".equals(path)) return "POST".equals(method);
        if (query == null && path.matches("^/app/v1/admin/game/groups/[0-9a-f]{32}/boss$")) return "GET".equals(method) || "POST".equals(method);
        if (query == null && "/app/v1/game/teams".equals(path)) return "GET".equals(method) || "POST".equals(method);
        if (query == null && path.matches("^/app/v1/game/teams/[0-9a-f]{32}$")) return "GET".equals(method);
        if (query == null && path.matches("^/app/v1/game/teams/[0-9a-f]{32}/(accept|decline|cancel|choose|transfer)$")) return "POST".equals(method);
        if (query == null && "/app/v1/game/boss".equals(path)) return "GET".equals(method);
        if (query == null && "/app/v1/game/boss/attempts".equals(path)) return "POST".equals(method);
        if (query == null && path.matches("^/app/v1/game/boss/attempts/[0-9a-f]{32}$")) return "GET".equals(method);
        if (query == null && path.matches("^/app/v1/game/boss/attempts/[0-9a-f]{32}/(choose|cancel)$")) return "POST".equals(method);
        if (query == null && path.matches("^/app/v1/game/matches/[0-9a-f]{32}$")) return "GET".equals(method);
        if (query == null && path.matches("^/app/v1/game/matches/[0-9a-f]{32}/(accept|decline|cancel|choose|loadout|task|retry)$")) return "POST".equals(method);
        if (query != null && !query.isEmpty()) {
            if ("/app/v1/admin/characters".equals(path) && "GET".equals(method) && query.matches("^after=[0-9a-f]{32}$")) return true;
            if (path.matches("^/app/v1/admin/characters/[0-9a-fA-F-]{32,36}/portrait$") &&
                ("GET".equals(method) || "PUT".equals(method))) return validAdminCharacterPortraitQuery(query, "GET".equals(method));
            if ("/app/v1/admin/launch/waiting-room".equals(path) && "GET".equals(method)) return query.matches("^after=[0-9a-f]{32}$");
            if ("/app/v1/admin/operations/logs".equals(path) && "GET".equals(method)) return validOptionalQuery(query, "after", "level");
            if ("/app/v1/customer/completion-updates".equals(path) && "GET".equals(method)) return validOptionalQuery(query, "cursor", "limit");
            if ("/app/v1/archive/collection".equals(path) && "GET".equals(method)) return validCollectionQuery(query);
            if ("/app/v1/prelaunch/character/portrait".equals(path) && "GET".equals(method)) return validPortraitQuery(query);
            if ("/app/v1/customer/character".equals(path) && "DELETE".equals(method)) return validCharacterDeleteQuery(query);
            return false;
        }
        if ("/app/v1/launch".equals(path)) return "GET".equals(method);
        if ("/app/v1/application-version".equals(path)) return "GET".equals(method);
        if ("/app/v1/enrollment/default".equals(path)) return "GET".equals(method);
        if ("/app/v1/enrollment/default/admit".equals(path)) return "POST".equals(method);
        if ("/app/v1/prelaunch/character".equals(path)) return "GET".equals(method) || "PUT".equals(method);
        if ("/app/v1/prelaunch/character/retry".equals(path)) return "POST".equals(method);
        if ("/app/v1/admin/launch".equals(path)) return "GET".equals(method) || "PUT".equals(method);
        if ("/app/v1/admin/launch/beta-admission".equals(path)) return "GET".equals(method) || "PUT".equals(method);
        if ("/app/v1/admin/launch/beta-admission/group".equals(path)) return "POST".equals(method);
        if ("/app/v1/admin/launch/groups".equals(path)) return "GET".equals(method) || "POST".equals(method);
        if ("/app/v1/admin/launch/waiting-room".equals(path)) return "GET".equals(method);
        if (path.matches("^/app/v1/admin/launch/waiting-room/[0-9a-f]{32}/group$")) return "PUT".equals(method);
        if (path.matches("^/app/v1/admin/launch/groups/[0-9a-f]{32}/countdown$")) return "PUT".equals(method);
        if (path.matches("^/app/v1/admin/launch/groups/[0-9a-f]{32}/countdown/confirm$")) return "POST".equals(method);
        if (path.matches("^/app/v1/admin/launch/groups/[0-9a-f]{32}/start$")) return "POST".equals(method);
        if (path.matches("^/app/v1/admin/launch/groups/[0-9a-f]{32}$")) return "PUT".equals(method);
        if (path.matches("^/app/v1/admin/launch/groups/[0-9a-f]{32}/members$")) return "GET".equals(method);
        if (path.matches("^/app/v1/admin/launch/groups/[0-9a-f]{32}/members/[0-9a-f]{32}$")) return "PUT".equals(method);
        if ("/app/v1/admin/launch/audit".equals(path) || "/app/v1/admin/launch/artwork".equals(path)) return "GET".equals(method);
        if ("/app/v1/admin/launch/personalization-preview".equals(path)) return "POST".equals(method);
        if ("/app/v1/admin/launch/artwork".equals(path)) return "GET".equals(method);
        if (path.matches("^/app/v1/admin/launch/artwork/[A-Za-z0-9-]{32,36}/retry$")) return "POST".equals(method);
        if ("/app/v1/session".equals(path)) return "GET".equals(method) || "DELETE".equals(method);
        if ("/app/v1/account".equals(path)) return "GET".equals(method) || "DELETE".equals(method);
        if ("/app/v1/account/deletion".equals(path)) return "GET".equals(method);
        if ("/app/v1/account/ai-consent".equals(path)) return "POST".equals(method);
        if ("/app/v1/customer/wand".equals(path)) return "GET".equals(method);
        if ("/app/v1/customer/wand/sessions".equals(path)) return "POST".equals(method);
        if (path.matches("^/app/v1/customer/wand/sessions/[0-9a-f]{32}/ticket$")) return "POST".equals(method);
        if ("/app/v1/passkeys/request-options".equals(path) || "/app/v1/passkeys/sign-in".equals(path) ||
            "/app/v1/passkeys/reauthentication-options".equals(path) || "/app/v1/passkeys/reauthenticate".equals(path) ||
            "/app/v1/passkeys/options".equals(path) || "/app/v1/passkeys/complete".equals(path) ||
            "/app/v1/enrollment/options".equals(path) || "/app/v1/enrollment/complete".equals(path) ||
            "/app/v1/customer/enrollment/options".equals(path) || "/app/v1/customer/enrollment/complete".equals(path)) return "POST".equals(method);
        if ("/app/v1/customer/character".equals(path)) return Arrays.asList("GET", "PUT", "DELETE").contains(method);
        if ("/app/v1/customer/access".equals(path)) return "GET".equals(method);
        if ("/app/v1/customer/conversation-preferences".equals(path)) return "GET".equals(method) || "PUT".equals(method);
        if ("/app/v1/customer/character/correction".equals(path)) return "POST".equals(method);
        if ("/app/v1/customer/sessions".equals(path) || "/app/v1/customer/duels".equals(path) ||
            "/app/v1/customer/admin/sessions".equals(path) || "/app/v1/customer/admin/characters".equals(path)) return "GET".equals(method);
        if (query == null && "/app/v1/admin/characters".equals(path)) return "GET".equals(method);
        if (adminCharacterProfilePath(path)) return "GET".equals(method) || "PUT".equals(method);
        if ("/app/v1/customer/start-tickets".equals(path)) return "POST".equals(method);
        if ("/app/v1/customer/completion-updates".equals(path)) return "GET".equals(method);
        if ("/app/v1/archive/collection".equals(path)) return "GET".equals(method);
        if ("/app/v1/archive/associations".equals(path)) return "GET".equals(method) || "POST".equals(method);
        if ("/app/v1/customer/memory".equals(path)) return "GET".equals(method);
        if ("/app/v1/admin/operations/logs".equals(path) || "/app/v1/admin/operations/status".equals(path)) return "GET".equals(method);
        if ("/app/v1/admin/operations/modes".equals(path)) return "PUT".equals(method);
        if ("/app/v1/admin/model-releases".equals(path)) return "GET".equals(method);
        if ("/app/v1/admin/model-releases/active".equals(path)) return "POST".equals(method);
        if ("/app/v1/admin/users".equals(path)) return "GET".equals(method);
        if ("/app/v1/admin/invitations".equals(path) || "/app/v1/admin/grants".equals(path)) return "POST".equals(method);
        String[] parts = path.split("/");
        if (parts.length == 7 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "customer".equals(parts[3]) && "completion-updates".equals(parts[4]) && isUuid(parts[5]) && "read".equals(parts[6])) return "PUT".equals(method);
        if (parts.length == 7 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "tonight".equals(parts[3]) && "devices".equals(parts[4]) && "memory-status".equals(parts[6])) return validSegment(parts[5], 128) && "GET".equals(method);
        if (parts.length == 5 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "archive".equals(parts[3]) && "associations".equals(parts[4])) return "GET".equals(method) || "POST".equals(method);
        if (parts.length >= 5 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "archive".equals(parts[3]) && "owned".equals(parts[4])) return allowsArchivePath(parts, method);
        if (parts.length >= 5 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "archive".equals(parts[3]) && "associations".equals(parts[4])) return allowsArchiveAssociation(parts, method);
        if (path.startsWith("/app/v1/persona-lab/")) return allowsPersonaLab(path, method);
        if (path.startsWith("/app/v1/testing-sessions")) return allowsTestingSession(path, method);
        if (path.startsWith("/app/v1/customer/")) return allowsCustomerRouted(path, method);
        if (path.startsWith("/app/v1/archive/owned/") || path.startsWith("/app/v1/archive/associations/")) {
            String[] archive = path.split("/");
            return "owned".equals(archive[4]) ? allowsArchivePath(archive, method) : allowsArchiveAssociation(archive, method);
        }
        if (parts.length == 6 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "devices".equals(parts[3]) && "association".equals(parts[5])) {
            return parts[4].matches("^[A-Za-z0-9_-]{1,128}$") && ("POST".equals(method) || "DELETE".equals(method));
        }
        if (parts.length == 7 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "customer".equals(parts[3]) && "admin".equals(parts[4]) && "devices".equals(parts[5])) return parts[6].matches("^[A-Za-z0-9._-]{1,128}$") && ("GET".equals(method) || "PUT".equals(method));
        if (parts.length == 8 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "customer".equals(parts[3]) && "admin".equals(parts[4]) && "sessions".equals(parts[5])) return "POST".equals(method) && parts[6].matches("^[0-9a-fA-F-]{36}$") && "end".equals(parts[7]);
        if (parts.length == 6 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "workshop".equals(parts[3]) && validSegment(parts[4], 96) && "engines".equals(parts[5])) return "GET".equals(method);
        if (parts.length == 7 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "admin".equals(parts[3]) && "devices".equals(parts[4]) && validSegment(parts[5], 128) && "waiting".equals(parts[6])) return "GET".equals(method);
        if (parts.length == 8 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "admin".equals(parts[3]) && "devices".equals(parts[4]) && validSegment(parts[5], 128) && "waiting".equals(parts[6])) return "POST".equals(method) && Arrays.asList("open", "state", "skip").contains(parts[7]);
        if (parts.length == 6 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "admin".equals(parts[3]) && "users".equals(parts[4]) && validSegment(parts[5], 128)) return "PUT".equals(method);
        if (parts.length == 6 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "admin".equals(parts[3]) && "grants".equals(parts[4]) && validSegment(parts[5], 128)) return "DELETE".equals(method);
        if (parts.length == 5 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "passkeys".equals(parts[3]) && validSegment(parts[4], 2048)) return "PUT".equals(method) || "DELETE".equals(method);
        if (parts.length == 5 && "app".equals(parts[1]) && "v1".equals(parts[2]) && Arrays.asList("sessions", "installations").contains(parts[3]) && validSegment(parts[4], 128)) return "DELETE".equals(method) && "sessions".equals(parts[3]) || "PUT".equals(method) && "installations".equals(parts[3]);
        if (parts.length == 8 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "admin".equals(parts[3]) && "operations".equals(parts[4]) && "keepsakes".equals(parts[5]) && isUuid(parts[6])) return "POST".equals(method) && "revoke-shares".equals(parts[7]);
        if (parts.length == 10 && "app".equals(parts[1]) && "v1".equals(parts[2]) && "admin".equals(parts[3]) && "operations".equals(parts[4]) && "keepsakes".equals(parts[5]) && isUuid(parts[6])) return "POST".equals(method) && "jobs".equals(parts[7]) && isUuid(parts[8]) && "cancel".equals(parts[9]);
        return false;
    }

    private static boolean allowsWandWorkshopPath(String path, String method, String query) {
        if ("/app/v1/customer/wand/workshop".equals(path)) return "GET".equals(method) && validWorkshopUpgradeQuery(query);
        if ("/app/v1/customer/wand/workshop/sessions".equals(path)) return "POST".equals(method);
        if ("/app/v1/customer/wand/workshop/sessions/current".equals(path)) return "GET".equals(method) && validWorkshopUpgradeQuery(query);
        if (query != null) return false;
        String session = "^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}";
        if (path.matches(session)) return "GET".equals(method);
        if (path.matches(session + "/messages/[A-Za-z0-9_-]{1,160}")) return "PUT".equals(method);
        if (path.matches(session + "/(messages|finish|ticket|artwork/repair)")) return "POST".equals(method);
        if (path.matches(session + "/(answers|skip|audio|transcribe|artwork/retry)")) return "POST".equals(method);
        return path.matches(session + "/answers/[0-9a-f]{32}") && "PUT".equals(method);
    }

    private static boolean validWorkshopUpgradeQuery(String query) {
        return query == null || "upgrade=0".equals(query) || "upgrade=1".equals(query);
    }

    private static boolean allowsArchivePath(String[] p, String method) {
        if (p.length == 7 && "deletions".equals(p[5]) && isArchiveUuid(p[6])) return "GET".equals(method);
        if (p.length < 6 || !"owned".equals(p[4]) || !isArchiveUuid(p[5])) return false;
        if (p.length == 6) return "GET".equals(method) || "DELETE".equals(method);
        if (p.length == 7 && "assets".equals(p[6])) return false;
        if (p.length == 8 && "assets".equals(p[6]) && isArchiveUuid(p[7])) return "GET".equals(method);
        if (p.length == 7) {
            if (Arrays.asList("recovery-links", "share-links").contains(p[6])) return "POST".equals(method) || "DELETE".equals(method);
            if ("portrait".equals(p[6])) return "DELETE".equals(method);
            return "POST".equals(method) && Arrays.asList("revoke-access", "portrait-consents", "portrait-uploads", "portraits", "exports", "generation", "wizard-identity", "account-associations").contains(p[6]);
        }
        return p.length == 9 && "POST".equals(method) && Arrays.asList("portraits", "generation").contains(p[6]) && isArchiveUuid(p[7]) && "cancel".equals(p[8]);
    }

    private static boolean allowsArchiveAssociation(String[] p, String method) {
        if (p.length == 5) return "GET".equals(method) || "POST".equals(method);
        if (p.length < 6 || !isArchiveUuid(p[5])) return false;
        if (p.length == 6) return "GET".equals(method) || "DELETE".equals(method);
        return p.length == 8 && "GET".equals(method) && "assets".equals(p[6]) && isArchiveUuid(p[7]);
    }

    private static boolean allowsTestingSession(String path, String method) {
        String[] all = path.split("/");
        if (all.length < 4 || !"testing-sessions".equals(all[3])) return false;
        String[] r = Arrays.copyOfRange(all, 4, all.length);
        if (r.length == 0) return "GET".equals(method) || "POST".equals(method);
        if (r.length == 1 && "catalog".equals(r[0])) return "GET".equals(method);
        if ("characters".equals(r[0])) {
            if (r.length == 1) return "GET".equals(method) || "POST".equals(method);
            if (!isHex(r[1], 32)) return false;
            if (r.length == 2) return "GET".equals(method) || "PUT".equals(method) || "DELETE".equals(method);
            return r.length == 3 && "enrich".equals(r[2]) && "POST".equals(method);
        }
        if (!isHex(r[0], 32)) return false;
        if (r.length == 1) return "GET".equals(method) || "DELETE".equals(method);
        if (r.length == 2) {
            if ("mode".equals(r[1])) return "PUT".equals(method);
            if (Arrays.asList("duels", "diagnostics").contains(r[1]) && "GET".equals(method)) return true;
            return "POST".equals(method) && Arrays.asList("character", "select", "generate", "retry-generation", "fault", "cancel", "end", "duels", "portrait").contains(r[1]);
        }
        if (r.length == 3 && "duels".equals(r[1]) && isUuid(r[2])) return "GET".equals(method);
        if (r.length == 4 && "duels".equals(r[1]) && isUuid(r[2])) return "POST".equals(method) && Arrays.asList("start", "move", "narrate", "advance").contains(r[3]);
        if (r.length == 4 && "keepsakes".equals(r[1]) && isUuid(r[2]) && "photo".equals(r[3])) return "POST".equals(method);
        return r.length == 5 && "keepsakes".equals(r[1]) && isUuid(r[2]) && "assets".equals(r[3]) && isUuid(r[4]) && "GET".equals(method);
    }

    private static boolean allowsPersonaLab(String path, String method) {
        String[] all = path.split("/");
        if (all.length < 4 || !"persona-lab".equals(all[3])) return false;
        String[] r = Arrays.copyOfRange(all, 4, all.length);
        if (r.length == 1) {
            if ("catalog".equals(r[0]) || "analytics".equals(r[0]) || "diagnostics".equals(r[0])) return "GET".equals(method);
            if ("sessions".equals(r[0]) || "suites".equals(r[0])) return "GET".equals(method) || "POST".equals(method);
            return ("revisions".equals(r[0]) || "rollback".equals(r[0])) && "POST".equals(method);
        }
        if ("suites".equals(r[0])) return r.length == 2 && isHex(r[1], 32) && "GET".equals(method);
        if ("revisions".equals(r[0])) return r.length == 3 && "publish".equals(r[2]) && (isHex(r[1], 32) || r[1].matches("^baseline-[0-9a-f]{16}$")) && "POST".equals(method);
        if (!"sessions".equals(r[0]) || !isHex(r[1], 32)) return false;
        if (r.length == 2) return "GET".equals(method) || "DELETE".equals(method);
        if (r.length != 3) return false;
        if ("context".equals(r[2])) return "GET".equals(method);
        if (Arrays.asList("turns", "end", "socket-ticket", "hardware-ticket").contains(r[2])) return "POST".equals(method);
        if ("evaluation".equals(r[2])) return "PUT".equals(method);
        return "image".equals(r[2]) && ("POST".equals(method) || "DELETE".equals(method));
    }

    private static boolean allowsCustomerRouted(String path, String method) {
        String[] p = path.split("/");
        if (p.length == 6 && "waiting".equals(p[4]) && validSegment(p[5], 128)) return "GET".equals(method);
        if (p.length == 7 && "waiting".equals(p[4]) && validSegment(p[5], 128)) return "POST".equals(method) && Arrays.asList("join", "leave", "claim").contains(p[6]);
        if (p.length == 7 && "admin".equals(p[4]) && "devices".equals(p[5]) && validSegment(p[6], 128)) return "GET".equals(method) || "PUT".equals(method);
        if (p.length == 8 && "admin".equals(p[4]) && "devices".equals(p[5]) && validSegment(p[6], 128) && "waiting".equals(p[7])) return "GET".equals(method);
        if (p.length == 9 && "admin".equals(p[4]) && "devices".equals(p[5]) && validSegment(p[6], 128) && "waiting".equals(p[7])) return "POST".equals(method) && Arrays.asList("open", "state", "skip").contains(p[8]);
        if ("memory".equals(p[4])) return (p.length == 5 && "GET".equals(method)) || (p.length == 6 && isUuid(p[5]) && "PUT".equals(method)) || (p.length == 7 && isUuid(p[5]) && "forget".equals(p[6]) && "POST".equals(method));
        if (p.length == 6 && "data".equals(p[4]) && "exports".equals(p[5])) return "POST".equals(method);
        if (p.length == 7 && "data".equals(p[4]) && "exports".equals(p[5]) && isUuid(p[6])) return "GET".equals(method) || "DELETE".equals(method);
        if (p.length == 6 && "completion-updates".equals(p[4])) return "GET".equals(method);
        if (p.length == 7 && "completion-updates".equals(p[4]) && isUuid(p[5]) && "read".equals(p[6])) return "PUT".equals(method);
        if (p.length == 6 && "sessions".equals(p[4]) && isUuid(p[5])) return "GET".equals(method) || "DELETE".equals(method);
        if (p.length == 7 && "sessions".equals(p[4]) && isUuid(p[5])) return ("GET".equals(method) && "result".equals(p[6])) || ("POST".equals(method) && "end".equals(p[6]));
        if (p.length == 6 && "duels".equals(p[4]) && isUuid(p[5])) return "GET".equals(method);
        if (p.length == 7 && "keepsakes".equals(p[4]) && isUuid(p[5]) && "wand-publication".equals(p[6])) return "POST".equals(method);
        if (p.length == 8 && "keepsakes".equals(p[4]) && isUuid(p[5]) && "wand-artwork".equals(p[6]) && "retry".equals(p[7])) return "POST".equals(method);
        if (p.length == 8 && "admin".equals(p[4]) && "sessions".equals(p[5]) && isUuid(p[6]) && "end".equals(p[7])) return "POST".equals(method);
        if (p.length == 8 && "data".equals(p[4]) && "exports".equals(p[5]) && isUuid(p[6]) && "download".equals(p[7])) return "GET".equals(method);
        return false;
    }

    private static boolean isHex(String value, int length) { return value != null && value.length() == length && value.matches("(?i)^[0-9a-f]+$"); }

    private static boolean validOptionalQuery(String raw, String... allowed) {
        String[] pairs = raw.split("&", -1);
        Set<String> names = new HashSet<>();
        for (String pair : pairs) {
            String[] fields = pair.split("=", -1);
            if (fields.length != 2 || !Arrays.asList(allowed).contains(fields[0]) || fields[1].length() > 2048 || !names.add(fields[0])) return false;
            if ("limit".equals(fields[0])) {
                try { int value = Integer.parseInt(fields[1]); if (value < 1 || value > 50 || !Integer.toString(value).equals(fields[1])) return false; }
                catch (NumberFormatException exception) { return false; }
            } else if ("after".equals(fields[0])) {
                if (!fields[1].matches("^[0-9]{1,19}$")) return false;
            } else if ("level".equals(fields[0])) {
                if (!Arrays.asList("trace", "debug", "information", "warning", "error", "critical", "info").contains(fields[1])) return false;
            } else if ("cursor".equals(fields[0])) {
                if (!fields[1].matches("^[A-Za-z0-9_-]{1,2048}$")) return false;
            } else if ("access".equals(fields[0])) {
                if (!Arrays.asList("all", "owned", "linked").contains(fields[1])) return false;
            } else if ("q".equals(fields[0])) {
                if (!fields[1].matches("^(?:[A-Za-z0-9._~-]|%[0-9A-F]{2}|\\+)*$") || fields[1].length() > 240) return false;
            }
        }
        return true;
    }

    private static boolean validCollectionQuery(String raw) {
        if (raw.length() > 20000) return false;
        String[] pairs = raw.split("&", -1);
        if (pairs.length > 4) return false;
        Set<String> names = new HashSet<>();
        for (String pair : pairs) {
            String[] fields = pair.split("=", -1);
            if (fields.length != 2 || !Arrays.asList("limit", "cursor", "q", "access").contains(fields[0]) || !names.add(fields[0])) return false;
            String value = canonicalFormValue(fields[1]);
            if (value == null) return false;
            if ("limit".equals(fields[0])) {
                try { int number = Integer.parseInt(value); if (number < 1 || number > 50 || !Integer.toString(number).equals(value)) return false; }
                catch (NumberFormatException exception) { return false; }
            } else if ("cursor".equals(fields[0]) && (value.isEmpty() || value.length() > 2048)) return false;
            else if ("access".equals(fields[0]) && !Arrays.asList("all", "owned", "linked").contains(value)) return false;
            else if ("q".equals(fields[0]) && (value.length() > 80 || !value.equals(trimWhitespace(value)))) return false;
        }
        return true;
    }

    private static String canonicalFormValue(String encoded) {
        try {
            String decoded = java.net.URLDecoder.decode(encoded, StandardCharsets.UTF_8.name());
            StringBuilder canonical = new StringBuilder();
            for (byte item : decoded.getBytes(StandardCharsets.UTF_8)) {
                int value = item & 0xff;
                if ((value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9') || value == '*' || value == '-' || value == '.' || value == '_') {
                    canonical.append((char) value);
                } else if (value == ' ') canonical.append('+');
                else canonical.append('%').append("0123456789ABCDEF".charAt(value >>> 4)).append("0123456789ABCDEF".charAt(value & 15));
            }
            return encoded.equals(canonical.toString()) ? decoded : null;
        } catch (IllegalArgumentException | java.io.UnsupportedEncodingException exception) { return null; }
    }

    private static String trimWhitespace(String value) {
        int start = 0, end = value.length();
        while (start < end) {
            int codePoint = value.codePointAt(start);
            if (!isWhitespace(codePoint)) break;
            start += Character.charCount(codePoint);
        }
        while (start < end) {
            int codePoint = value.codePointBefore(end);
            if (!isWhitespace(codePoint)) break;
            end -= Character.charCount(codePoint);
        }
        return value.substring(start, end);
    }

    private static boolean isWhitespace(int codePoint) {
        return Character.isWhitespace(codePoint) || Character.isSpaceChar(codePoint) || codePoint == 0x0085 || codePoint == 0xFEFF;
    }

    private static boolean validPortraitQuery(String raw) {
        String[] pairs = raw.split("&", -1);
        if (pairs.length != 2) return false;
        String profile = null, revision = null;
        for (String pair : pairs) {
            String[] item = pair.split("=", -1);
            if (item.length != 2) return false;
            if ("profileId".equals(item[0])) profile = item[1];
            else if ("revision".equals(item[0])) revision = item[1];
            else return false;
        }
        return validId(profile) && revision != null && revision.matches("^[1-9][0-9]{0,18}$");
    }

    private static boolean adminCharacterProfilePath(String path) {
        return path != null && path.matches("^/app/v1/admin/characters/[0-9a-fA-F]{32}$|^/app/v1/admin/characters/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
    }

    private static boolean adminCharacterListPath(String path) {
        return "/app/v1/admin/characters".equals(path) ||
            (path != null && path.matches("^/app/v1/admin/characters\\?after=[0-9a-f]{32}$"));
    }

    private static boolean adminCharacterPortraitPath(String path, String method, boolean upload) {
        if (!"GET".equals(method) && !"PUT".equals(method)) return false;
        String expectedMethod = upload ? "PUT" : "GET";
        if (!expectedMethod.equals(method) || path == null) return false;
        String marker = "/portrait?";
        int index = path.indexOf(marker);
        return index > 0 && adminCharacterProfilePath(path.substring(0, index)) &&
            validAdminCharacterPortraitQuery(path.substring(index + marker.length()), !upload);
    }

    private static boolean validAdminCharacterPortraitQuery(String raw, boolean read) {
        if (raw == null) return false;
        String key = read ? "revision" : "expectedRevision";
        return raw.matches("^" + key + "=[1-9][0-9]{0,18}$");
    }

    private static boolean validCharacterDeleteQuery(String raw) {
        String[] pairs = raw.split("&", -1);
        if (pairs.length != 2) return false;
        String profile = null, revision = null;
        for (String pair : pairs) {
            String[] item = pair.split("=", -1);
            if (item.length != 2) return false;
            if ("expectedProfileId".equals(item[0])) profile = item[1];
            else if ("expectedRevision".equals(item[0])) revision = item[1];
            else return false;
        }
        return validId(profile) && revision != null && revision.matches("^[1-9][0-9]{0,18}$");
    }

    private static boolean validId(String value) {
        return isUuid(value) || (value != null && value.length() == 32 && value.matches("(?i)^[0-9a-f]+$"));
    }

    static Request.Builder apply(Request.Builder builder, String origin, String cookie, String csrf,
                                 String method, boolean confirmed, Map<String, String> headers, String path) {
        if (builder == null || !validOrigin(origin) || !allows(path, method)) throw new IllegalArgumentException("invalid_account_request");
        builder.header("Origin", origin);
        if (cookie != null && !cookie.isEmpty()) builder.header("Cookie", cookie);
        if (!"GET".equals(method)) {
            if (csrf == null || csrf.isEmpty() || csrf.length() > 256) throw new IllegalArgumentException("csrf_required");
            builder.header("X-DV-CSRF", csrf);
        }
        if (confirmed) builder.header("X-DV-Confirm", "yes");
        validateWandTranscriptionHeaders(path, method, headers);
        if (headers != null) {
            for (Map.Entry<String, String> entry : headers.entrySet()) {
                String name = entry.getKey().toLowerCase(Locale.ROOT);
                String value = entry.getValue();
                if (!HEADERS.contains(name) || value == null || value.length() > 1024 || value.contains("\r") || value.contains("\n")) {
                    throw new IllegalArgumentException("invalid_account_header");
                }
                if ("traceparent".equals(name) && !value.matches("(?i)^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$")) {
                    throw new IllegalArgumentException("invalid_account_header");
                }
                if ("x-dv10-consent".equals(name) && (!"POST".equals(method) || !path.matches("^/app/v1/archive/owned/[0-9a-fA-F-]{36}/portrait-uploads$") || !value.matches("^[1-9][0-9]{0,9}$"))) {
                    throw new IllegalArgumentException("invalid_account_header");
                }
                if (name.startsWith("x-wand-question-") && !isWandTranscriptionPath(path, method)) {
                    throw new IllegalArgumentException("invalid_account_header");
                }
                if (!"x-dv-csrf".equals(name)) builder.header(entry.getKey(), value);
            }
        }
        return builder;
    }

    private static void validateWandTranscriptionHeaders(String path, String method, Map<String, String> headers) {
        int idCount = 0, revisionCount = 0;
        String id = null, revision = null;
        if (headers != null) {
            for (Map.Entry<String, String> entry : headers.entrySet()) {
                String name = entry.getKey().toLowerCase(Locale.ROOT);
                if ("x-wand-question-id".equals(name)) { idCount++; id = entry.getValue(); }
                if ("x-wand-question-revision".equals(name)) { revisionCount++; revision = entry.getValue(); }
            }
        }
        if (!isWandTranscriptionPath(path, method)) {
            if (idCount > 0 || revisionCount > 0) throw new IllegalArgumentException("invalid_account_header");
            return;
        }
        if (idCount != 1 || revisionCount != 1 || id == null || !id.matches("^[0-9a-f]{32}$")
            || revision == null || !revision.matches("^[1-9][0-9]{0,18}$")) {
            throw new IllegalArgumentException("invalid_account_header");
        }
    }

    private static boolean isWandTranscriptionPath(String path, String method) {
        return "POST".equals(method)
            && path.matches("^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}/transcribe$");
    }

    private static boolean validOrigin(String origin) {
        try {
            URI uri = new URI(origin);
            return "https".equals(uri.getScheme()) && uri.getHost() != null && uri.getRawUserInfo() == null &&
                (uri.getRawPath() == null || uri.getRawPath().isEmpty()) && uri.getRawQuery() == null && uri.getRawFragment() == null;
        } catch (Exception exception) { return false; }
    }

    private static boolean validQuery(String raw, String first, String second) {
        if (raw == null) return false;
        String[] pairs = raw.split("&", -1);
        if (pairs.length != 2) return false;
        Set<String> names = new HashSet<>();
        for (String pair : pairs) {
            String[] fields = pair.split("=", -1);
            if (fields.length != 2 || fields[1].isEmpty() || !names.add(fields[0])) return false;
        }
        return names.contains(first) && names.contains(second);
    }
}
