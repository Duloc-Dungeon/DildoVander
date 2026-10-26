package de.dildovander.companion;

final class NativeGuestHeaderPolicy {
    private NativeGuestHeaderPolicy() { }

    static boolean allows(String path, String method, String name, String value) {
        if (name == null || value == null || value.length() > 2048 || value.contains("\r") || value.contains("\n")) return false;
        String header = name.toLowerCase(java.util.Locale.ROOT);
        if ("traceparent".equals(header)) return value.matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-00$") &&
            !value.substring(3, 35).matches("0{32}") && !value.substring(36, 52).matches("0{16}");
        boolean publicDuel = path != null && path.startsWith("/guest/v1/duels/");
        if (publicDuel) {
            if ("accept".equals(header) || "content-type".equals(header)) return true;
            return "authorization".equals(header) && "POST".equals(method) && path.matches("^/guest/v1/duels/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/(?:choices|join)$") &&
                value.matches("^Bearer [A-Za-z0-9_-]{43}$");
        }
        return "accept".equals(header) || "content-type".equals(header) || "x-dv10-csrf".equals(header) ||
            "idempotency-key".equals(header) || "x-dv10-consent".equals(header);
    }
}
