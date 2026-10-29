import Foundation

enum NativePasskeyOptionsResponse {
    static let maxBytes = 131072
    static let failureMessage = "Passkey verification failed. Try again or use another sign-in method."

    struct Rejected: LocalizedError {
        let status: Int
        let code: String
        var errorDescription: String? { NativePasskeyOptionsResponse.failureMessage }
    }

    enum InvalidResponse: LocalizedError {
        case invalid
        var errorDescription: String? { NativePasskeyOptionsResponse.failureMessage }
    }

    static func parse(status: Int, body: String) throws -> [String: Any] {
        let data = Data(body.utf8)
        let bounded = data.count <= maxBytes
        if status != 200 {
            var code = "passkey_rejected"
            if (400...599).contains(status), bounded,
               let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let candidate = object["code"] as? String {
                let bytes = Array(candidate.utf8)
                // Forward machine codes only; response text and credentials stay out of bridge errors.
                if (1...64).contains(bytes.count), let first = bytes.first, (97...122).contains(first),
                   bytes.allSatisfy({ (97...122).contains($0) || (48...57).contains($0) || $0 == 95 }) {
                    code = candidate
                }
            }
            throw Rejected(status: status, code: code)
        }
        guard bounded, let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw InvalidResponse.invalid
        }
        return object
    }
}

// Shared by the live native bridge and standalone macOS regression tests.
enum AccountTransportPolicy {
    private static let accountArchiveUUIDPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-8][0-9a-fA-F]{3}-[89aAbB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$"

    static func accountArchivePortraitUpload(path: String, method: String) -> Bool {
        guard path.hasPrefix("/"), method == "POST", !path.contains("?"), !path.contains("#"), !path.contains("%"),
              !path.contains("//"), !path.contains("\\"), !path.hasSuffix("/") else { return false }
        let parts = path.split(separator: "/").map(String.init)
        let routeParts = parts.map { $0.lowercased() }
        return parts.count == 6 && Array(routeParts.prefix(4)) == ["app", "v1", "archive", "owned"]
            && validArchiveUUID(parts[4]) && routeParts[5] == "portrait-uploads"
    }

    static func accountRequestBodyLimit(path: String, method: String, contentType: String?) -> Int {
        if adminCharacterProfilePath(path) && method == "PUT" { return 256 * 1024 }
        if adminCharacterPortrait(path: path, method: method, read: false) {
            let type = contentType?.split(separator: ";", maxSplits: 1).first?
                .trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            return ["image/jpeg", "image/png", "image/webp"].contains(type ?? "") ? 8 * 1024 * 1024 : 256 * 1024
        }
        if path == "/app/v1/customer/conversation-preferences" {
            if method == "GET" { return 0 }
            if method == "PUT" { return 2 * 1024 }
        }
        // These limits apply to accountTransport; the legacy JSON bridge keeps its own bound.
        if customerCompletionRead(path: path, method: method) { return 256 }
        if method == "POST", customerWaiting(path: path, method: method) { return 2 * 1024 }
        if path.range(of: "^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}/transcribe$", options: .regularExpression) != nil,
           method == "POST", let type = contentType?.split(separator: ";", maxSplits: 1).first?
            .trimmingCharacters(in: .whitespacesAndNewlines).lowercased(),
           ["audio/webm", "audio/mp4", "audio/ogg", "audio/wav"].contains(type) {
            return 8 * 1024 * 1024
        }
        if path.range(of: "^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}/audio$", options: .regularExpression) != nil,
           method == "POST" { return 16 * 1024 }
        if path.contains("/customer/wand/workshop/") { return 16 * 1024 }
        guard accountArchivePortraitUpload(path: path, method: method),
              let type = contentType?.split(separator: ";", maxSplits: 1).first?
                .trimmingCharacters(in: .whitespacesAndNewlines).lowercased(),
              ["image/jpeg", "image/png", "image/webp"].contains(type) else { return 256 * 1024 }
        return 8 * 1024 * 1024
    }

    static func accountRequestBodyAllowed(path: String, method: String, contentType: String?, size: Int) -> Bool {
        size >= 0 && size <= accountRequestBodyLimit(path: path, method: method, contentType: contentType)
    }

    static func accountResponseLimit(path: String, method: String) -> Int {
        if gameOpponentWandArtwork(path: path, method: method) { return 32 * 1024 * 1024 }
        if (method == "GET" && (adminCharacterList(path) || adminCharacterProfilePath(path))) ||
            (method == "PUT" && (adminCharacterProfilePath(path) || adminCharacterPortrait(path: path, method: method, read: false))) {
            return 256 * 1024
        }
        if adminCharacterPortrait(path: path, method: method, read: true) { return 8 * 1024 * 1024 }
        if path.range(of: "^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}/audio$", options: .regularExpression) != nil,
           method == "POST" { return 8 * 1024 * 1024 }
        if prelaunchPortrait(path: path, method: method) || sealedCasePortrait(path: path, method: method) { return 8 * 1024 * 1024 }
        if wandWorkshopArtwork(path: path, method: method) { return 32 * 1024 * 1024 }
        if method == "GET", path.contains("/keepsakes/"), path.contains("/assets/"),
           testingSessions(path: path, method: method) { return 32 * 1024 * 1024 }
        if method == "GET" && (path == "/app/v1/customer/memory" || accountDataExportDownload(path: path, method: method)) {
            return 32 * 1024 * 1024
        }
        guard path.hasPrefix("/"), method == "GET", !path.contains("?"), !path.contains("#"), !path.contains("%"),
              !path.contains("//"), !path.contains("\\"), !path.hasSuffix("/") else { return 1024 * 1024 }
        let parts = path.split(separator: "/").map(String.init)
        let routeParts = parts.map { $0.lowercased() }
        if parts.count == 7, Array(routeParts.prefix(4)) == ["app", "v1", "archive", "owned"],
           validArchiveUUID(parts[4]), routeParts[5] == "assets", validArchiveUUID(parts[6]) {
            return 32 * 1024 * 1024
        }
        if parts.count == 7, Array(routeParts.prefix(4)) == ["app", "v1", "archive", "associations"],
           validArchiveUUID(parts[4]), routeParts[5] == "assets", validArchiveUUID(parts[6]) {
            return 32 * 1024 * 1024
        }
        return 1024 * 1024
    }

    static func accountResponseAllowed(path: String, method: String, size: Int) -> Bool {
        size >= 0 && size <= accountResponseLimit(path: path, method: method)
    }

    static func accountResponseContentTypeAllowed(path: String, method: String, contentType: String?, status: Int = 200) -> Bool {
        if !(200..<300).contains(status) { return true }
        if gameOpponentWandArtwork(path: path, method: method) {
            return contentType?.split(separator: ";", maxSplits: 1).first?
                .trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "image/png"
        }
        if path.range(of: "^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}/audio$", options: .regularExpression) != nil,
           method == "POST" {
            guard let value = contentType?.split(separator: ";", maxSplits: 1).first?
                .trimmingCharacters(in: .whitespacesAndNewlines).lowercased() else { return false }
            return value == "audio/wav"
        }
        if adminCharacterPortrait(path: path, method: method, read: true) {
            guard let value = contentType?.split(separator: ";", maxSplits: 1).first?
                .trimmingCharacters(in: .whitespacesAndNewlines).lowercased() else { return false }
            return value == "image/webp"
        }
        guard prelaunchPortrait(path: path, method: method) || sealedCasePortrait(path: path, method: method) else { return true }
        guard let value = contentType?.split(separator: ";", maxSplits: 1).first?
            .trimmingCharacters(in: .whitespacesAndNewlines).lowercased() else { return false }
        return ["image/png", "image/jpeg", "image/webp"].contains(value)
    }

    static func accountHeaderAllowed(path: String, method: String, name: String, value: String) -> Bool {
        let normalizedName = name.lowercased()
        if normalizedName == "traceparent" { return validTraceparent(value) }
        if normalizedName == "x-wand-question-id" {
            return wandTranscription(path: path, method: method) && value.range(of: "^[0-9a-f]{32}$", options: .regularExpression) != nil
        }
        if normalizedName == "x-wand-question-revision" {
            return wandTranscription(path: path, method: method) && value.range(of: "^[1-9][0-9]{0,18}$", options: .regularExpression) != nil
        }
        guard normalizedName == "x-dv10-consent" else { return true }
        guard accountArchivePortraitUpload(path: path, method: method),
              value.utf8.count <= 10, value.allSatisfy({ $0.isASCII && $0.isNumber }),
              let version = Int32(value), version > 0 else { return false }
        return true
    }

    static func accountHeadersAllowed(path: String, method: String, headers: [String: Any]) -> Bool {
        let questionIds = headers.filter { $0.key.lowercased() == "x-wand-question-id" }
        let questionRevisions = headers.filter { $0.key.lowercased() == "x-wand-question-revision" }
        guard wandTranscription(path: path, method: method) else {
            return questionIds.isEmpty && questionRevisions.isEmpty
        }
        guard questionIds.count == 1, questionRevisions.count == 1,
              let id = questionIds.first?.value as? String,
              let revision = questionRevisions.first?.value as? String else { return false }
        return accountHeaderAllowed(path: path, method: method, name: "x-wand-question-id", value: id)
            && accountHeaderAllowed(path: path, method: method, name: "x-wand-question-revision", value: revision)
    }

    private static func wandTranscription(path: String, method: String) -> Bool {
        method == "POST"
            && path.range(of: "^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}/transcribe$", options: .regularExpression) != nil
    }

    private static func validArchiveUUID(_ value: String) -> Bool {
        value.count == 36 && value.range(of: accountArchiveUUIDPattern, options: .regularExpression) != nil
    }

    static func accountDataExportDownload(path: String, method: String) -> Bool {
        guard method == "GET", let parts = customerDataParts(path), parts.count == 7,
              Array(parts.prefix(5)) == ["app", "v1", "customer", "data", "exports"],
              parts[6] == "download" else { return false }
        return canonicalDataUUID(parts[5])
    }

    static func accountDataDownloadLengthAllowed(path: String, method: String, contentLength: String?, size: Int) -> Bool {
        guard accountDataExportDownload(path: path, method: method), let value = contentLength else { return true }
        guard !value.isEmpty, value.utf8.count <= 10, value.allSatisfy({ $0.isASCII && $0.isNumber }),
              let length = Int(value), length >= 0, length <= 32 * 1024 * 1024 else { return false }
        return length == size
    }

    private static func canonicalDataUUID(_ value: String) -> Bool {
        value.count == 36 && UUID(uuidString: value) != nil
    }

    private static func customerDataParts(_ path: String) -> [String]? {
        let parts = path.split(separator: "/").map(String.init)
        guard path == "/" + parts.joined(separator: "/") else { return nil }
        return parts
    }

    private static func customerCompletionPage(path: String, method: String) -> Bool {
        let root = "/app/v1/customer/completion-updates"
        guard method == "GET", path.count <= 4096 else { return false }
        if path == root { return true }
        guard path.hasPrefix(root + "?") else { return false }
        // The server issues an unpadded base64url cursor. Parse the raw query so
        // encoded names/values, duplicate keys and alternate numeric forms fail closed.
        let query = path.dropFirst(root.count + 1)
        let items = query.split(separator: "&", omittingEmptySubsequences: false)
        guard !query.isEmpty, items.count <= 2 else { return false }
        var seen = Set<String>()
        for item in items {
            let pair = item.split(separator: "=", omittingEmptySubsequences: false)
            guard pair.count == 2 else { return false }
            let key = String(pair[0]), value = String(pair[1])
            guard seen.insert(key).inserted else { return false }
            switch key {
            case "limit":
                guard let limit = Int(value), (1...50).contains(limit), String(limit) == value else { return false }
            case "cursor":
                guard !value.isEmpty, value.count <= 2048,
                      value.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }) else { return false }
            default: return false
            }
        }
        return true
    }

    private static func customerCompletionRead(path: String, method: String) -> Bool {
        guard method == "PUT", let parts = customerDataParts(path), parts.count == 6,
              Array(parts.prefix(4)) == ["app", "v1", "customer", "completion-updates"],
              canonicalDataUUID(parts[4]), parts[5] == "read" else { return false }
        return true
    }

    private static func canonicalFormValue(_ raw: Substring) -> String? {
        let encoded = String(raw)
        guard let value = encoded.replacingOccurrences(of: "+", with: " ").removingPercentEncoding else { return nil }
        var canonical = ""
        for byte in value.utf8 {
            switch byte {
            case 65...90, 97...122, 48...57, 42, 45, 46, 95:
                canonical += String(UnicodeScalar(byte))
            case 32: canonical += "+"
            default: canonical += String(format: "%%%02X", byte)
            }
        }
        return canonical == encoded ? value : nil
    }

    private static func accountCollectionPage(path: String, method: String) -> Bool {
        let root = "/app/v1/archive/collection"
        // Bounds include worst-case percent encoding of the UTF-16-limited values.
        guard method == "GET", path.utf8.count <= 20000 else { return false }
        if path == root { return true }
        guard path.hasPrefix(root + "?") else { return false }
        let query = path.dropFirst(root.count + 1)
        let items = query.split(separator: "&", omittingEmptySubsequences: false)
        guard !query.isEmpty, items.count <= 4 else { return false }
        var seen = Set<String>()
        for item in items {
            let pair = item.split(separator: "=", omittingEmptySubsequences: false)
            guard pair.count == 2 else { return false }
            let key = String(pair[0])
            guard ["limit", "cursor", "q", "access"].contains(key), seen.insert(key).inserted,
                  let value = canonicalFormValue(pair[1]) else { return false }
            switch key {
            case "limit":
                guard let limit = Int(value), (1...50).contains(limit), String(limit) == value else { return false }
            case "cursor":
                // The server authenticates the opaque cursor and its account/filter binding.
                guard !value.isEmpty, value.utf16.count <= 2048 else { return false }
            case "q":
                guard value.utf16.count <= 80, value == value.trimmingCharacters(in: .whitespacesAndNewlines) else { return false }
            case "access":
                guard ["all", "owned", "linked"].contains(value) else { return false }
            default: return false
            }
        }
        return true
    }

    private static func customerWaitingDevice(_ value: String) -> Bool {
        !value.isEmpty && value.count <= 128 && value != "." && value != ".."
            && value.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") }
    }

    private static func customerWaiting(path: String, method: String) -> Bool {
        guard let parts = customerDataParts(path) else { return false }
        if parts.count >= 5, Array(parts.prefix(4)) == ["app", "v1", "customer", "waiting"],
           customerWaitingDevice(parts[4]) {
            return (parts.count == 5 && method == "GET")
                || (parts.count == 6 && method == "POST" && ["join", "leave", "claim"].contains(parts[5]))
        }
        guard parts.count >= 7, Array(parts.prefix(5)) == ["app", "v1", "customer", "admin", "devices"],
              customerWaitingDevice(parts[5]), parts[6] == "waiting" else { return false }
        return (parts.count == 7 && method == "GET")
            || (parts.count == 8 && method == "POST" && ["open", "state", "skip"].contains(parts[7]))
    }

    private static func customerData(path: String, method: String) -> Bool {
        if path == "/app/v1/customer/memory" { return method == "GET" }
        if path == "/app/v1/customer/data/exports" { return method == "POST" }
        guard let parts = customerDataParts(path) else { return false }
        if parts.count >= 5, Array(parts.prefix(4)) == ["app", "v1", "customer", "memory"],
           canonicalDataUUID(parts[4]) {
            return (parts.count == 5 && method == "PUT")
                || (parts.count == 6 && parts[5] == "forget" && method == "POST")
        }
        guard parts.count == 6, Array(parts.prefix(5)) == ["app", "v1", "customer", "data", "exports"],
              canonicalDataUUID(parts[5]) else { return accountDataExportDownload(path: path, method: method) }
        return ["GET", "DELETE"].contains(method)
    }

    static func accountDeletionAccepted(path: String, method: String, status: Int, body: Data) -> Bool {
        guard path == "/app/v1/account", method == "DELETE", status == 202,
              let object = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else { return false }
        return object["status"] as? String == "deletion_requested"
    }

    static func credentialCommit(path: String, method: String) -> Bool {
        method == "POST" && ["/app/v1/passkeys/sign-in", "/app/v1/passkeys/reauthenticate",
            "/app/v1/enrollment/complete", "/app/v1/customer/enrollment/complete", "/app/v1/passkeys/complete"].contains(path)
    }

    // Caller priority flags cannot bypass the account queue. Only cancellation
    // operations with their exact selected-device route can use frozen cookies.
    static func priorityCancellation(path: String, method: String, deviceId: String, body: Data) -> Bool {
        guard method == "POST", body.count <= 4096, show(path: path, method: method, query: []),
              let object = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else { return false }
        let parts = path.split(separator: "/").map(String.init)
        guard parts.count >= 5, parts[3] == deviceId else { return false }
        if parts.count == 5 && parts[4] == "commands" {
            return ["stop", "cancel-speech", "cancel-cue"].contains(object["kind"] as? String ?? "")
        }
        return parts.count == 7 && parts[4] == "duels" && parts[6] == "cancel"
    }

    static func publicOrigin(_ raw: String) -> URL? {
        guard raw.count <= 2048, raw.rangeOfCharacter(from: .whitespacesAndNewlines) == nil,
              !raw.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }),
              !raw.contains("%"), !raw.contains("$"), !raw.contains("\\"),
              let parts = URLComponents(string: raw), parts.scheme == "https", let host = parts.host, !host.isEmpty,
              parts.user == nil, parts.password == nil, parts.path.isEmpty, parts.query == nil, parts.fragment == nil,
              parts.port == nil || (1...65535).contains(parts.port!), let url = parts.url else { return nil }
        return url
    }

    // Mirrors ModelBundle safeAssetPath, including safe non-.json filenames.
    // The maintained loader verifies JSON content and hashes even when a safe
    // non-.json asset is served with a generic binary MIME by the static server.
    static func publicResourceLimit(path: String) -> Int? {
        if path == "/models/active.json" { return 128 * 1024 }
        if path == "/downloads/index.json" { return 256 * 1024 }
        let prefix = "/models/bundles/"
        guard path.hasPrefix(prefix) else { return nil }
        let parts = path.dropFirst(prefix.count).split(separator: "/", maxSplits: 1, omittingEmptySubsequences: false)
        guard parts.count == 2, parts[0].count == 64, parts[0].allSatisfy({ "0123456789abcdef".contains($0) }),
              !parts[1].isEmpty, parts[1].count <= 240,
              parts[1].allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_" || $0 == "-" || $0 == "." || $0 == "/") }),
              String(parts[1]).range(of: "^[A-Za-z0-9_-]+(?:[.][A-Za-z0-9_-]+)*(?:/[A-Za-z0-9_-]+(?:[.][A-Za-z0-9_-]+)*)*$", options: .regularExpression) != nil else { return nil }
        return parts[1] == "model-bundle.json" ? 128 * 1024 : 96 * 1024 * 1024
    }

    static func publicResourceMIME(_ contentType: String?, path: String) -> Bool {
        guard let limit = publicResourceLimit(path: path),
              let type = contentType?.split(separator: ";", maxSplits: 1).first?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() else { return false }
        if type == "application/json" || type == "application/problem+json" { return true }
        // Only the validated immutable bundle data role has this byte policy;
        // active references, manifests and the download index stay JSON-only.
        return type == "application/octet-stream" && path.hasPrefix("/models/bundles/") && limit == 96 * 1024 * 1024
    }

    static func authenticatedDownloadLimit(path: String) -> Int? {
        if let limit = publicResourceLimit(path: path) { return limit }
        guard path.hasPrefix("/downloads/releases/"), !path.contains("%"), !path.contains("\\"),
              !path.contains("?"), !path.contains("#"), !path.contains("//"), !path.hasSuffix("/") else { return nil }
        let parts = path.split(separator: "/").map(String.init)
        guard parts.count == 4, Array(parts.prefix(2)) == ["downloads", "releases"], parts[2].count == 64,
              parts[2].allSatisfy({ "0123456789abcdef".contains($0) }),
              parts[3].range(of: "^[A-Za-z0-9_-]+(?:[.][A-Za-z0-9_-]+)*[.](?:zip|pdf|step|stp|stl|3mf|dxf|txt)$", options: .regularExpression) != nil else { return nil }
        return 2_147_483_648
    }

    static func authenticatedDownloadMIME(_ contentType: String?, path: String) -> Bool {
        guard let allowedLimit = authenticatedDownloadLimit(path: path),
              let type = contentType?.split(separator: ";", maxSplits: 1).first?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() else { return false }
        if publicResourceLimit(path: path) != nil { return publicResourceMIME(type, path: path) }
        return ["application/octet-stream", "application/zip", "application/pdf", "text/plain", "model/step", "model/stl"].contains(type)
            && allowedLimit == 2_147_483_648
    }

    static func show(path: String, method: String, query: [URLQueryItem]) -> Bool {
        guard !path.contains("%"), !path.contains("\\"), !path.contains("#"), !path.contains("//"), !path.hasSuffix("/"),
              !path.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }) else { return false }
        let parts = path.split(separator: "/").map(String.init)
        guard parts.count >= 4, Array(parts.prefix(3)) == ["v1", "show", "devices"],
              !parts[3].isEmpty, parts[3].count <= 128,
              parts[3].allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_" || $0 == "-" || $0 == ".") }) else { return false }
        let suffix = Array(parts.dropFirst(4))
        if method == "GET" {
            if suffix.count == 1 && ["identities", "prophecies", "duels"].contains(suffix[0]) {
                guard query.count == 1, query[0].name == "eveningId", let evening = query[0].value else { return false }
                return !evening.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && evening.count <= 240
                    && !evening.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
            }
            return query.isEmpty && (suffix.isEmpty || (suffix.count == 2 && ["duels", "ambient-previews"].contains(suffix[0]) && UUID(uuidString: suffix[1]) != nil))
        }
        guard method == "POST", query.isEmpty else { return false }
        if suffix.count == 1 { return ["commands", "end-evening", "offline-outcomes", "duels", "ambient-previews"].contains(suffix[0]) }
        if suffix.count == 3, suffix[0] == "prophecies", suffix[2] == "reveal" {
            return !suffix[1].isEmpty && suffix[1].count <= 128 && suffix[1].allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
        }
        guard suffix.count >= 3, suffix[0] == "duels", UUID(uuidString: suffix[1]) != nil else { return false }
        return (suffix.count == 3 && ["start", "cancel", "grants"].contains(suffix[2]))
            || (suffix.count == 4 && suffix[2] == "grants" && suffix[3] == "revoke")
    }

    static func guestUsesCookies(path: String) -> Bool {
        let route = URLComponents(string: path)?.path ?? ""
        return route != "/guest/v1/evening" && !route.hasPrefix("/guest/v1/evenings/")
            && route != "/guest/v1/chronicle" && !route.hasPrefix("/guest/v1/duels/")
    }

    static func guestHeader(path: String, method: String, name: String, value: String) -> Bool {
        guard value.utf8.count <= 1024, !value.contains("\r"), !value.contains("\n") else { return false }
        let key = name.lowercased()
        if key == "traceparent" { return validTraceparent(value) }
        if !guestUsesCookies(path: path) {
            if ["accept", "content-type"].contains(key) { return true }
            return key == "authorization" && method == "POST" && (path.hasSuffix("/choices") || path.hasSuffix("/join"))
                && guest(path: path, method: method) && value.hasPrefix("Bearer ") && value.count == 50
                && value.dropFirst(7).allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
        }
        return ["accept", "content-type", "x-dv10-csrf", "idempotency-key", "x-dv10-consent"].contains(key)
    }

    static func validTraceparent(_ value: String) -> Bool {
        guard value.utf8.count == 55,
              value.range(of: "^00-[0-9a-f]{32}-[0-9a-f]{16}-00$", options: .regularExpression) != nil else { return false }
        let parts = value.split(separator: "-")
        return parts[1].contains { $0 != "0" } && parts[2].contains { $0 != "0" }
    }

    static func newTraceparent() -> String {
        let trace = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let parent = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased().suffix(16)
        return "00-\(trace)-\(parent)-00"
    }

    static func device(path: String, method: String, query: [URLQueryItem]) -> Bool {
        guard !path.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }),
              !path.contains("%"), !path.contains("\\"), path.hasPrefix("/v1/") else { return false }
        if show(path: path, method: method, query: query) { return true }
        if path == "/v1/admin/logs" {
            return method == "GET" && query.allSatisfy { ["after", "limit", "level", "traceId", "operationId"].contains($0.name) && ($0.value?.count ?? 0) <= 128 }
        }
        if path == "/v1/admin/memory/context" {
            guard method == "GET", query.count == 3, Set(query.map { $0.name }) == Set(["deviceId", "conversationId", "personalityId"]) else { return false }
            return query.allSatisfy { item in
                guard let value = item.value, !value.isEmpty else { return false }
                if item.name == "conversationId" { return UUID(uuidString: value) != nil }
                return value.count <= (item.name == "deviceId" ? 128 : 80)
                    && value.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") }
            }
        }
        guard query.isEmpty else { return false }
        if path == "/v1/admin/memory/guest" { return method == "PUT" }
        let memory = path.split(separator: "/").map(String.init)
        if memory.count == 6, Array(memory.prefix(4)) == ["v1", "admin", "memory", "scopes"], method == "DELETE" {
            if ["conversation", "evening"].contains(memory[4]) { return UUID(uuidString: memory[5]) != nil }
            return memory[4] == "guest" && !memory[5].isEmpty && memory[5].count <= 80
                && memory[5].allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") }
        }
        if path == "/v1/admin/modes" || path == "/v1/diagnostics" || path == "/v1/evening-memory" { return method == "GET" }
        if path == "/v1/evenings" { return ["GET", "POST"].contains(method) }
        if path == "/v1/conversations" { return method == "POST" }
        let parts = path.split(separator: "/").map(String.init)
        guard parts.count >= 3, UUID(uuidString: parts[2]) != nil else { return false }
        if parts[1] == "audio" { return parts.count == 3 && method == "GET" }
        if parts[1] == "operations" { return parts.count == 3 && ["GET", "DELETE"].contains(method) }
        if parts[1] == "evenings" { return parts.count == 4 && parts[3] == "active" && method == "PUT" }
        guard parts[1] == "conversations" else { return false }
        if parts.count == 3 { return method == "DELETE" }
        if parts.count == 4 {
            if parts[3] == "image" && method == "DELETE" { return true }
            if parts[3] == "costume-context" { return method == "PUT" }
            return method == "POST" && ["realtime-live", "gpt-live", "image"].contains(parts[3])
        }
        if parts.count == 6 && method == "GET" && ["realtime-live", "gpt-live"].contains(parts[3])
            && UUID(uuidString: parts[4]) != nil && parts[5] == "audio-stream" { return true }
        return parts.count == 6 && method == "POST" && ["realtime-live", "gpt-live"].contains(parts[3])
            && UUID(uuidString: parts[4]) != nil && ["audio", "played", "playback", "availability"].contains(parts[5])
    }
    static func guest(path: String, method: String) -> Bool {
        guard path.count <= 4096, ["GET", "POST", "DELETE"].contains(method), !path.split(separator: "?", maxSplits: 1)[0].contains("%"),
              !path.contains(".."), !path.contains("\\"), !path.contains("#"),
              let components = URLComponents(string: path), components.scheme == nil, components.host == nil else { return false }
        let route = components.path
        if route == "/guest/v1/chronicle" { return method == "GET" && components.query == nil }
        let duel = route.split(separator: "/").map(String.init)
        if duel.count >= 4, Array(duel.prefix(3)) == ["guest", "v1", "duels"] {
            return components.query == nil && UUID(uuidString: duel[3]) != nil
                && ((duel.count == 4 && method == "GET") || (duel.count == 5 && ["choices", "join"].contains(duel[4]) && method == "POST"))
        }
        if method == "POST", ["/guest/v1/bootstrap", "/guest/v1/claim", "/guest/v1/recover", "/guest/v1/share-claim"].contains(route) { return components.query == nil }
        if route == "/guest/v1/evening" {
            return method == "GET" && components.queryItems?.contains(where: { !["cursor", "limit"].contains($0.name) || ($0.value?.count ?? 0) > 256 }) != true
        }
        if route.hasPrefix("/guest/v1/evenings/") {
            let pieces = route.split(separator: "/").map(String.init)
            return method == "GET" && components.query == nil && pieces.count == 10
                && pieces[2] == "evenings" && pieces[4] == "keepsakes" && pieces[6] == "editions" && pieces[8] == "assets"
                && [3, 5, 7, 9].allSatisfy { UUID(uuidString: pieces[$0]) != nil }
        }
        let allowedQuery = Set(["editionId", "view"])
        if components.queryItems?.contains(where: { !allowedQuery.contains($0.name) || ($0.value?.count ?? 0) > 128 }) == true { return false }
        let segments = route.split(separator: "/").map(String.init)
        guard segments.count >= 4, segments[0] == "guest", segments[1] == "v1",
              UUID(uuidString: segments[3]) != nil else { return false }
        if segments[2] == "deletions" { return method == "GET" && segments.count == 4 }
        guard segments[2] == "keepsakes" else { return false }
        if method == "GET" { return segments.count == 4 || (segments.count == 5 && segments[4] == "history") || (segments.count == 6 && segments[4] == "assets" && UUID(uuidString: segments[5]) != nil) }
        if segments.count == 4 { return method == "DELETE" }
        if segments.count == 5 {
            if ["recovery-links", "share-links"].contains(segments[4]) { return method == "POST" || method == "DELETE" }
            if segments[4] == "portrait" { return method == "DELETE" }
            return method == "POST" && ["revoke-access", "portrait-consents", "portrait-uploads", "portraits", "exports", "account-associations", "wizard-identity"].contains(segments[4])
        }
        return method == "POST" && segments.count == 7 && ["portraits", "generation"].contains(segments[4])
            && UUID(uuidString: segments[5]) != nil && segments[6] == "cancel"
    }

    static func testingSessions(path: String, method: String) -> Bool {
        let parts = path.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard parts.count >= 4, Array(parts.prefix(4)) == ["", "app", "v1", "testing-sessions"] else { return false }
        let route = Array(parts.dropFirst(4))
        func id(_ value: String) -> Bool { value.count == 32 && value.allSatisfy { "0123456789abcdef".contains($0) } }
        if route.isEmpty { return ["GET", "POST"].contains(method) }
        if route == ["catalog"] { return method == "GET" }
        if route[0] == "characters" {
            if route.count == 1 { return ["GET", "POST"].contains(method) }
            guard id(route[1]) else { return false }
            if route.count == 2 { return ["PUT", "DELETE"].contains(method) }
            return route.count == 3 && route[2] == "enrich" && method == "POST"
        }
        guard id(route[0]) else { return false }
        if route.count == 1 { return ["GET", "DELETE"].contains(method) }
        if route.count == 2 {
            if route[1] == "mode" { return method == "PUT" }
            if ["duels", "diagnostics"].contains(route[1]) && method == "GET" { return true }
            return ["character", "select", "generate", "retry-generation", "fault", "cancel", "end", "duels", "portrait"].contains(route[1]) && method == "POST"
        }
        if route.count == 3, route[1] == "duels", UUID(uuidString: route[2]) != nil { return method == "GET" }
        if route.count == 4, route[1] == "duels", UUID(uuidString: route[2]) != nil {
            return ["start", "move", "narrate", "advance"].contains(route[3]) && method == "POST"
        }
        if route.count == 4, route[1] == "keepsakes", UUID(uuidString: route[2]) != nil,
           route[3] == "photo" { return method == "POST" }
        return route.count == 5 && route[1] == "keepsakes" && UUID(uuidString: route[2]) != nil
            && route[3] == "assets" && UUID(uuidString: route[4]) != nil && method == "GET"
    }

    static func personaLab(path: String, method: String) -> Bool {
        let parts = path.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard parts.count >= 5, Array(parts.prefix(4)) == ["", "app", "v1", "persona-lab"] else { return false }
        let route = Array(parts.dropFirst(4))
        func hexID(_ value: String, length: Int = 32) -> Bool {
            value.count == length && value.allSatisfy { "0123456789abcdef".contains($0) }
        }
        if route.count == 1 {
            let exact: [String: Set<String>] = ["catalog": ["GET"], "sessions": ["GET", "POST"],
                "analytics": ["GET"], "suites": ["GET", "POST"], "diagnostics": ["GET"],
                "revisions": ["POST"], "rollback": ["POST"]]
            return exact[route[0]]?.contains(method) == true
        }
        if route[0] == "suites" { return route.count == 2 && hexID(route[1]) && method == "GET" }
        if route[0] == "revisions" {
            let id = route[1]
            let valid = hexID(id) || id.hasPrefix("baseline-") && hexID(String(id.dropFirst(9)), length: 16)
            return route.count == 3 && valid && route[2] == "publish" && method == "POST"
        }
        guard route[0] == "sessions", hexID(route[1]) else { return false }
        if route.count == 2 { return ["GET", "DELETE"].contains(method) }
        guard route.count == 3 else { return false }
        switch route[2] {
        case "context": return method == "GET"
        case "turns", "end", "socket-ticket", "hardware-ticket": return method == "POST"
        case "evaluation": return method == "PUT"
        case "image": return ["POST", "DELETE"].contains(method)
        default: return false
        }
    }

    static func customer(path: String, method: String) -> Bool {
        if customerCompletionPage(path: path, method: method)
            || customerCompletionRead(path: path, method: method)
            || customerWaiting(path: path, method: method) { return true }
        if customerData(path: path, method: method) { return true }
        let exact: [String: Set<String>] = [
            "/app/v1/customer/enrollment/options": ["POST"],
            "/app/v1/customer/enrollment/complete": ["POST"],
            "/app/v1/customer/access": ["GET"],
            "/app/v1/customer/groups": ["GET"],
            "/app/v1/customer/groups/active": ["PUT"],
            "/app/v1/customer/character": ["GET", "PUT", "DELETE"],
            "/app/v1/customer/conversation-preferences": ["GET", "PUT"],
            "/app/v1/customer/character/correction": ["POST"],
            "/app/v1/customer/sessions": ["GET"],
            "/app/v1/customer/duels": ["GET"],
            "/app/v1/customer/start-tickets": ["POST"],
            "/app/v1/customer/admin/sessions": ["GET"],
            "/app/v1/customer/admin/characters": ["GET"]]
        if exact[path]?.contains(method) == true { return true }
        let parts = path.split(separator: "/").map(String.init)
        if parts.count == 7, Array(parts.prefix(4)) == ["app", "v1", "customer", "keepsakes"], parts[5] == "wand-artwork", parts[6] == "retry" {
            return method == "POST" && UUID(uuidString: parts[4]) != nil && parts[4].count == 36 && path == "/app/v1/customer/keepsakes/\(parts[4])/wand-artwork/retry"
        }
        if parts.count == 6, Array(parts.prefix(4)) == ["app", "v1", "customer", "keepsakes"], parts[5] == "wand-publication" {
            return method == "POST" && UUID(uuidString: parts[4]) != nil && parts[4].count == 36 && path == "/app/v1/customer/keepsakes/\(parts[4])/wand-publication"
        }
        if parts.count == 5, Array(parts.prefix(4)) == ["app", "v1", "customer", "duels"] {
            return method == "GET" && UUID(uuidString: parts[4]) != nil && parts[4].count == 36 && path == "/app/v1/customer/duels/\(parts[4])"
        }
        if parts.count >= 5, Array(parts.prefix(4)) == ["app", "v1", "customer", "sessions"],
           UUID(uuidString: parts[4]) != nil {
            if parts.count == 6, parts[5] == "result" {
                return method == "GET" && parts[4].count == 36
                    && path == "/app/v1/customer/sessions/\(parts[4])/result"
            }
            return (parts.count == 5 && method == "GET") ||
                (parts.count == 6 && parts[5] == "end" && method == "POST")
        }
        if parts.count == 7, Array(parts.prefix(5)) == ["app", "v1", "customer", "admin", "sessions"],
           UUID(uuidString: parts[5]) != nil, parts[6] == "end" { return method == "POST" }
        return parts.count == 6 && path == "/" + parts.joined(separator: "/")
            && Array(parts.prefix(5)) == ["app", "v1", "customer", "admin", "devices"] &&
            !parts[5].isEmpty && parts[5].count <= 128 &&
            parts[5].allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") } &&
            ["GET", "PUT"].contains(method)
    }

    private static func gameOpponentWandArtwork(path: String, method: String) -> Bool {
        method == "GET" && (
            path.range(of: "^/app/v1/game/matches/[0-9a-f]{32}/opponent/wand-artwork\\?revision=[0-9a-fA-F]{64}\\z", options: .regularExpression) != nil
                || path.range(of: "^/app/v1/game/teams/[0-9a-f]{32}/opponents/[0-9a-f]{32}/wand-artwork\\?revision=[0-9a-fA-F]{64}\\z", options: .regularExpression) != nil
        )
    }

    static func gameLiveRead(path: String, method: String) -> Bool {
        method == "GET" && path.range(of: "^/app/v1/game/live\\?id=[A-Za-z0-9_-]{1,128}(&_=[0-9]{1,20})?\\z", options: .regularExpression) != nil
    }

    static func account(path: String, method: String) -> Bool {
        if path == "/app/v1/game/live/negotiate?negotiateVersion=1" { return method == "POST" }
        if ["GET", "POST", "DELETE"].contains(method) {
            let timestamp = method == "GET" ? "(&_=[0-9]{1,20})?" : ""
            if path.range(of: "^/app/v1/game/live\\?id=[A-Za-z0-9_-]{1,128}" + timestamp + "\\z", options: .regularExpression) != nil { return true }
        }
        if gameOpponentWandArtwork(path: path, method: method) { return true }
        if wandWorkshop(path: path, method: method) { return true }
        if path == "/app/v1/customer/wand" { return method == "GET" }
        if path == "/app/v1/customer/wand/sessions" { return method == "POST" }
        if path.range(of: "^/app/v1/customer/wand/sessions/[0-9a-f]{32}/ticket\\z", options: .regularExpression) != nil { return method == "POST" }
        if path == "/app/v1/game" { return method == "GET" }
        if path == "/app/v1/game/matches" { return method == "POST" }
        if path == "/app/v1/game/progress" { return method == "GET" }
        if path == "/app/v1/game/progress/ack" { return method == "POST" }
        if path.range(of: "^/app/v1/admin/game/groups/[0-9a-f]{32}/boss\\z", options: .regularExpression) != nil { return method == "GET" || method == "POST" }
        if path == "/app/v1/game/teams" { return method == "GET" || method == "POST" }
        if path.range(of: "^/app/v1/game/teams/[0-9a-f]{32}\\z", options: .regularExpression) != nil { return method == "GET" }
        if path.range(of: "^/app/v1/game/teams/[0-9a-f]{32}/(accept|decline|cancel|choose|transfer)\\z", options: .regularExpression) != nil { return method == "POST" }
        if path == "/app/v1/game/boss" { return method == "GET" }
        if path == "/app/v1/game/boss/attempts" { return method == "POST" }
        if path.range(of: "^/app/v1/game/boss/attempts/[0-9a-f]{32}\\z", options: .regularExpression) != nil { return method == "GET" }
        if path.range(of: "^/app/v1/game/boss/attempts/[0-9a-f]{32}/(choose|cancel)\\z", options: .regularExpression) != nil { return method == "POST" }
        if path.range(of: "^/app/v1/game/boss/attempts/[0-9a-f]{32}/(narration|audio)\\z", options: .regularExpression) != nil { return method == "POST" }
        if path.range(of: "^/app/v1/game/matches/[0-9a-f]{32}\\z", options: .regularExpression) != nil { return method == "GET" }
        if path.range(of: "^/app/v1/game/matches/[0-9a-f]{32}/(accept|decline|cancel|choose|loadout|task|retry)\\z", options: .regularExpression) != nil { return method == "POST" }
        if customerCompletionPage(path: path, method: method) || accountCollectionPage(path: path, method: method) { return true }
        if let parts = URLComponents(string: path), parts.scheme == nil, parts.host == nil, parts.fragment == nil,
           !parts.percentEncodedPath.contains("%"), !path.contains("\\"), path.count <= 4096 {
            if adminCharacters(path: path, method: method) { return true }
            if prelaunchAccount(path: parts.path, method: method, query: parts.queryItems ?? []) { return true }
            if show(path: parts.path, method: method, query: parts.queryItems ?? []) { return true }
            if method == "DELETE", parts.path == "/app/v1/customer/character", let query = parts.queryItems,
               query.count == 2, Set(query.map { $0.name }) == Set(["expectedRevision", "expectedProfileId"]),
               let revision = query.first(where: { $0.name == "expectedRevision" })?.value,
               let parsedRevision = Int64(revision), parsedRevision > 0,
               let profile = query.first(where: { $0.name == "expectedProfileId" })?.value,
               (UUID(uuidString: profile) != nil || (profile.count == 32 && profile.allSatisfy { "0123456789abcdef".contains($0) })) { return true }
            if method == "GET", parts.path == "/v1/admin/show/chronicle", let query = parts.queryItems,
               query.count == 2, Set(query.map { $0.name }) == Set(["deviceId", "eveningId"]),
               let device = query.first(where: { $0.name == "deviceId" })?.value, !device.isEmpty, device.count <= 128,
               device.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_" || $0 == "-" || $0 == ".") }),
               let evening = query.first(where: { $0.name == "eveningId" })?.value,
               !evening.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, evening.count <= 240,
               !evening.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) { return true }
        }
        if method == "GET", let query = URLComponents(string: path), query.scheme == nil, query.host == nil,
           query.path == "/app/v1/admin/operations/logs", path.count <= 1024 {
            return query.queryItems?.contains(where: { !["after", "level"].contains($0.name) || ($0.value?.count ?? 0) > 32 }) != true
        }
        guard !path.contains("%"), !path.contains("?"), !path.contains("#"), !path.contains("\\"),
              !path.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }) else { return false }
        if accountArchive(path: path, method: method) { return true }
        if path.hasPrefix("/app/v1/tonight/") {
            let parts = path.split(separator: "/").map(String.init)
            guard parts.count == 6, Array(parts.prefix(4)) == ["app", "v1", "tonight", "devices"],
                  parts[5] == "memory-status" else { return false }
            let deviceId = parts[4]
            return method == "GET" && path == "/app/v1/tonight/devices/\(deviceId)/memory-status"
                && !deviceId.isEmpty && deviceId.count <= 128
                && deviceId.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") }
        }
        if path.hasPrefix("/app/v1/customer/") { return customer(path: path, method: method) }
        if path.hasPrefix("/app/v1/testing-sessions") { return testingSessions(path: path, method: method) }
        if path.hasPrefix("/app/v1/persona-lab") { return personaLab(path: path, method: method) }
        if path == "/v1/evenings" { return ["GET", "POST"].contains(method) }
        let evening = path.split(separator: "/").map(String.init)
        if evening.count == 4, evening[0] == "v1", evening[1] == "evenings", UUID(uuidString: evening[2]) != nil, evening[3] == "active" { return method == "PUT" }
        let exact: [String: Set<String>] = [
            "/app/v1/push": ["GET", "PUT", "DELETE"], "/app/v1/push/test": ["POST"],
            "/app/v1/session": ["GET", "DELETE"], "/app/v1/account": ["GET", "DELETE"],
            "/app/v1/account/deletion": ["GET"],
            "/app/v1/account/ai-consent": ["POST"],
            "/app/v1/passkeys/request-options": ["POST"], "/app/v1/passkeys/sign-in": ["POST"],
            "/app/v1/passkeys/reauthentication-options": ["POST"], "/app/v1/passkeys/reauthenticate": ["POST"],
            "/app/v1/enrollment/options": ["POST"], "/app/v1/enrollment/complete": ["POST"],
            "/app/v1/passkeys/options": ["POST"], "/app/v1/passkeys/complete": ["POST"],
            "/app/v1/enrollment/default": ["GET"],
            "/app/v1/enrollment/default/admit": ["POST"],
            "/app/v1/admin/operations/status": ["GET"], "/app/v1/admin/operations/modes": ["PUT"],
            "/app/v1/admin/model-releases": ["GET"], "/app/v1/admin/model-releases/active": ["POST"],
            "/app/v1/admin/users": ["GET"], "/app/v1/admin/invitations": ["POST"], "/app/v1/admin/grants": ["POST"]]
        if exact[path]?.contains(method) == true { return true }
        let operations = path.split(separator: "/").map(String.init)
        if method == "POST", Array(operations.prefix(5)) == ["app", "v1", "admin", "operations", "keepsakes"],
           operations.count >= 7, UUID(uuidString: operations[5]) != nil {
            return (operations.count == 7 && operations[6] == "revoke-shares")
                || (operations.count == 9 && operations[6] == "jobs" && UUID(uuidString: operations[7]) != nil && operations[8] == "cancel")
        }
        let archive = path.split(separator: "/").map(String.init)
        if archive.count == 5, Array(archive.prefix(3)) == ["app", "v1", "workshop"], archive[4] == "engines" {
            return method == "GET" && !archive[3].isEmpty && archive[3].count <= 96
                && archive[3].allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") }
        }
        if Array(archive.prefix(4)) == ["app", "v1", "archive", "associations"] {
            if archive.count == 4 { return ["GET", "POST"].contains(method) }
            guard archive.count >= 5, UUID(uuidString: archive[4]) != nil else { return false }
            if archive.count == 5 { return ["GET", "DELETE"].contains(method) }
            return archive.count == 7 && method == "GET" && archive[5] == "assets" && UUID(uuidString: archive[6]) != nil
        }
        let device = path.split(separator: "/").map(String.init)
        if device.count == 5, Array(device.prefix(3)) == ["app", "v1", "devices"], device[4] == "association" {
            return ["POST", "DELETE"].contains(method) && device[3].count <= 128
                && device[3].allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
        }
        let twin = path.split(separator: "/").map(String.init)
        if twin.count >= 3, Array(twin.prefix(3)) == ["v1", "twin", "instances"] {
            if twin.count == 3 { return method == "GET" }
            guard !twin[3].isEmpty, twin[3].count <= 96, twin[3].allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") }) else { return false }
            if twin.count == 4 { return method == "GET" }
            if twin.count == 5 {
                return (twin[4] == "recording" && method == "GET") || (twin[4] == "commands" && method == "POST")
                    || (twin[4] == "settings" && ["GET", "PUT"].contains(method))
            }
            return twin.count == 6 && twin[4] == "media"
                && ((method == "GET" && ["state", "output"].contains(twin[5]))
                    || (method == "POST" && ["start", "cancel", "input", "photo", "played"].contains(twin[5])))
        }
        if path.hasPrefix("/app/v1/passkeys/"), ["DELETE", "PUT"].contains(method) {
            let id = String(path.dropFirst("/app/v1/passkeys/".count))
            return !id.isEmpty && id.count <= 2048 && id.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
        }
        let prefixes: [(String, String)] = [("/app/v1/sessions/", "DELETE"), ("/app/v1/installations/", "PUT"),
            ("/app/v1/admin/users/", "PUT"), ("/app/v1/admin/grants/", "DELETE")]
        return prefixes.contains { prefix, expected in
            guard method == expected, path.hasPrefix(prefix) else { return false }
            let id = String(path.dropFirst(prefix.count))
            return !id.isEmpty && id.count <= 128 && id.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
        }
    }

    private static func prelaunchPortrait(path: String, method: String) -> Bool {
        guard method == "GET", let components = URLComponents(string: path), components.scheme == nil,
              components.host == nil, components.fragment == nil,
              components.path == "/app/v1/prelaunch/character/portrait",
              !components.percentEncodedPath.contains("%"), components.percentEncodedQuery?.contains("%") != true,
              let query = components.queryItems, query.count == 2,
              Set(query.map { $0.name }) == Set(["profileId", "revision"]),
              let profileId = query.first(where: { $0.name == "profileId" })?.value,
              validLaunchProfileId(profileId),
              let value = query.first(where: { $0.name == "revision" })?.value,
              value.utf8.count <= 19, value.allSatisfy({ $0.isASCII && $0.isNumber }),
              let revision = Int64(value), revision > 0 else { return false }
        return true
    }

    private static func adminCharacterProfilePath(_ path: String) -> Bool {
        path.range(of: "^/app/v1/admin/characters/(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$", options: .regularExpression) != nil
    }

    private static func adminCharacterPortrait(path: String, method: String, read: Bool) -> Bool {
        let expectedMethod = read ? "GET" : "PUT"
        guard method == expectedMethod else { return false }
        let queryName = read ? "revision" : "expectedRevision"
        return path.range(of: "^/app/v1/admin/characters/(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/portrait\\?\(queryName)=[1-9][0-9]{0,18}$", options: .regularExpression) != nil
    }

    private static func adminCharacters(path: String, method: String) -> Bool {
        guard !path.contains(where: { $0.isWhitespace }) else { return false }
        if adminCharacterList(path) { return method == "GET" }
        if adminCharacterProfilePath(path) { return method == "GET" || method == "PUT" }
        return adminCharacterPortrait(path: path, method: method, read: true) ||
            adminCharacterPortrait(path: path, method: method, read: false)
    }

    private static func adminCharacterList(_ path: String) -> Bool {
        !path.contains(where: { $0.isWhitespace }) && (path == "/app/v1/admin/characters" ||
            path.range(of: "^/app/v1/admin/characters\\?after=[0-9a-f]{32}$", options: .regularExpression) != nil
        )
    }

    private static func sealedCasePortrait(path: String, method: String) -> Bool {
        method == "GET" && path.range(of: "^/app/v1/sealed-cases/sc_[a-f0-9]{32}/portraits/[A-Za-z0-9_-]{1,128}$", options: .regularExpression) != nil
    }

    private static func sealedCase(path: String, method: String) -> Bool {
        if path == "/app/v1/sealed-cases/scenarios" { return method == "GET" }
        if path == "/app/v1/sealed-cases" { return method == "POST" }
        if path == "/app/v1/sealed-cases/current" { return method == "GET" }
        if sealedCasePortrait(path: path, method: method) { return true }
        guard path.range(of: "^/app/v1/sealed-cases/sc_[a-f0-9]{32}(/[a-z-]+)?$", options: .regularExpression) != nil else { return false }
        let parts = path.split(separator: "/").map(String.init)
        if parts.count == 4 { return method == "GET" }
        return method == "GET" ? ["play", "context", "moderator"].contains(parts[4])
            : method == "POST" && ["lobby", "start", "actions", "pause", "resume", "statements", "ballots", "oversight", "disconnect", "scenario-consent", "ai-players"].contains(parts[4])
    }

    private static func prelaunchAccount(path: String, method: String, query: [URLQueryItem]) -> Bool {
        if query.isEmpty && sealedCase(path: path, method: method) { return true }
        if path == "/app/v1/launch" { return method == "GET" && query.isEmpty }
        if path == "/app/v1/application-version" { return method == "GET" && query.isEmpty }
        if path == "/app/v1/prelaunch/character" { return query.isEmpty && ["GET", "PUT"].contains(method) }
        if path == "/app/v1/prelaunch/character/retry" { return method == "POST" && query.isEmpty }
        if prelaunchPortrait(path: path + (query.isEmpty ? "" : "?" + query.map { "\($0.name)=\($0.value ?? "")" }.joined(separator: "&")), method: method) { return true }
        if path == "/app/v1/admin/launch" { return query.isEmpty && ["GET", "PUT"].contains(method) }
        if path == "/app/v1/admin/launch/beta-admission" { return query.isEmpty && ["GET", "PUT"].contains(method) }
        if path == "/app/v1/admin/launch/beta-admission/group" { return query.isEmpty && method == "POST" }
        if path == "/app/v1/admin/launch/groups" { return query.isEmpty && ["GET", "POST"].contains(method) }
        if path == "/app/v1/admin/launch/waiting-room" {
            guard method == "GET" else { return false }
            if query.isEmpty { return true }
            guard query.count == 1, query[0].name == "after", let cursor = query[0].value else { return false }
            return cursor.count == 32 && cursor.allSatisfy { $0.isASCII && "0123456789abcdef".contains($0) }
        }
        let groupParts = path.split(separator: "/").map(String.init)
        if groupParts.count == 7 && Array(groupParts.prefix(5)) == ["app", "v1", "admin", "launch", "waiting-room"]
            && groupParts[6] == "group" && query.isEmpty {
            let userId = groupParts[5]
            return userId.count == 32 && userId.allSatisfy { $0.isASCII && "0123456789abcdef".contains($0) } && method == "PUT"
        }
        if groupParts.count == 7 && Array(groupParts.prefix(5)) == ["app", "v1", "admin", "launch", "groups"]
            && groupParts[6] == "countdown" && query.isEmpty {
            let groupId = groupParts[5]
            return groupId.count == 32 && groupId.allSatisfy { $0.isASCII && "0123456789abcdef".contains($0) } && method == "PUT"
        }
        if groupParts.count == 8 && Array(groupParts.prefix(5)) == ["app", "v1", "admin", "launch", "groups"]
            && groupParts[6] == "countdown" && groupParts[7] == "confirm" && query.isEmpty {
            let groupId = groupParts[5]
            return groupId.count == 32 && groupId.allSatisfy { $0.isASCII && "0123456789abcdef".contains($0) } && method == "POST"
        }
        if groupParts.count == 7 && Array(groupParts.prefix(5)) == ["app", "v1", "admin", "launch", "groups"]
            && groupParts[6] == "start" && query.isEmpty {
            let groupId = groupParts[5]
            return groupId.count == 32 && groupId.allSatisfy { $0.isASCII && "0123456789abcdef".contains($0) } && method == "POST"
        }
        if groupParts.count == 7 && Array(groupParts.prefix(5)) == ["app", "v1", "admin", "launch", "groups"]
            && groupParts[6] == "members" && query.isEmpty {
            let groupId = groupParts[5]
            return groupId.count == 32 && groupId.allSatisfy { $0.isASCII && "0123456789abcdef".contains($0) } && method == "GET"
        }
        if groupParts.count == 8 && Array(groupParts.prefix(5)) == ["app", "v1", "admin", "launch", "groups"]
            && groupParts[6] == "members" && query.isEmpty {
            let groupId = groupParts[5], userId = groupParts[7]
            return groupId.count == 32 && userId.count == 32
                && groupId.allSatisfy { $0.isASCII && "0123456789abcdef".contains($0) }
                && userId.allSatisfy { $0.isASCII && "0123456789abcdef".contains($0) }
                && method == "PUT"
        }
        if groupParts.count == 6 && Array(groupParts.prefix(5)) == ["app", "v1", "admin", "launch", "groups"] {
            return path == "/app/v1/admin/launch/groups/\(groupParts[5])"
                && groupParts[5].count == 32 && groupParts[5].allSatisfy { $0.isASCII && "0123456789abcdef".contains($0) }
                && method == "PUT" && query.isEmpty
        }
        if path == "/app/v1/admin/launch/audit" || path == "/app/v1/admin/launch/artwork" {
            return method == "GET" && query.isEmpty
        }
        if path == "/app/v1/admin/launch/personalization-preview" { return method == "POST" && query.isEmpty }
        let parts = path.split(separator: "/").map(String.init)
        return parts.count == 7 && Array(parts.prefix(4)) == ["app", "v1", "admin", "launch"]
            && parts[4] == "artwork" && validLaunchProfileId(parts[5]) && parts[6] == "retry"
            && method == "POST" && query.isEmpty
    }

    private static func validLaunchProfileId(_ value: String) -> Bool {
        (UUID(uuidString: value) != nil || (value.count == 32 && value.allSatisfy { $0.isASCII && "0123456789abcdefABCDEF".contains($0) }))
    }

    private static func accountArchive(path: String, method: String) -> Bool {
        guard path.hasPrefix("/"), !path.hasSuffix("/"), !path.contains("//") else { return false }
        let parts = path.split(separator: "/").map(String.init)
        let routeParts = parts.map { $0.lowercased() }
        if parts.count == 6, Array(routeParts.prefix(4)) == ["app", "v1", "archive", "owned"],
           routeParts[4] == "deletions", validArchiveUUID(parts[5]) { return method == "GET" }
        guard parts.count >= 5, Array(routeParts.prefix(4)) == ["app", "v1", "archive", "owned"],
              validArchiveUUID(parts[4]) else { return false }
        if parts.count == 5 { return ["GET", "DELETE"].contains(method) }
        if parts.count == 6 {
            let route = routeParts[5]
            if route == "assets" { return false }
            if ["recovery-links", "share-links"].contains(route) { return ["POST", "DELETE"].contains(method) }
            if route == "portrait" { return method == "DELETE" }
            return method == "POST" && ["revoke-access", "portrait-consents", "portrait-uploads", "portraits", "exports", "generation", "wizard-identity", "account-associations"].contains(route)
        }
        if parts.count == 7, routeParts[5] == "assets", method == "GET" {
            return validArchiveUUID(parts[6])
        }
        if parts.count == 8, ["portraits", "generation"].contains(routeParts[5]),
           validArchiveUUID(parts[6]), routeParts[7] == "cancel" { return method == "POST" }
        return false
    }

    private static func wandWorkshopArtwork(path: String, method: String) -> Bool {
        guard method == "GET" else { return false }
        let parts = path.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        return parts.count == 11
            && Array(parts.prefix(7)) == ["", "app", "v1", "customer", "wand", "workshop", "sessions"]
            && parts[7].count == 32 && parts[7].allSatisfy { "0123456789abcdef".contains($0) }
            && parts[8] == "artwork" && parts[9] == "assets" && validArchiveUUID(parts[10])
    }

    private static func wandWorkshop(path: String, method: String) -> Bool {
        guard !path.contains("#"), !path.contains("%"), !path.contains("\\") else { return false }
        let components = path.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
        guard components.count <= 2 else { return false }
        let route = String(components[0])
        let query = components.count == 2 ? String(components[1]) : nil
        if route == "/app/v1/customer/wand/workshop" || route == "/app/v1/customer/wand/workshop/sessions/current" {
            return method == "GET" && (query == nil || query == "upgrade=0" || query == "upgrade=1")
        }
        if query != nil { return false }
        if wandWorkshopArtwork(path: path, method: method) { return true }
        if route == "/app/v1/customer/wand/workshop/sessions" { return method == "POST" }
        let session = "^/app/v1/customer/wand/workshop/sessions/[0-9a-f]{32}"
        if route.range(of: session + "$", options: .regularExpression) != nil { return method == "GET" }
        if route.range(of: session + "/messages/[A-Za-z0-9_-]{1,160}$", options: .regularExpression) != nil {
            return method == "PUT"
        }
        if route.range(of: session + "/(messages|finish|ticket|artwork/repair)$", options: .regularExpression) != nil {
            return method == "POST"
        }
        if route.range(of: session + "/(answers|skip|audio|transcribe|artwork/retry)$", options: .regularExpression) != nil {
            return method == "POST"
        }
        return route.range(of: session + "/answers/[0-9a-f]{32}$", options: .regularExpression) != nil && method == "PUT"
    }
}

enum AccountDataFileFailure: String, LocalizedError {
    case invalidFile = "ACCOUNT_DATA_FILE_INVALID"
    case busy = "ACCOUNT_DATA_SHARE_BUSY"
    case presenterUnavailable = "ACCOUNT_DATA_SHARE_UNAVAILABLE"
    var errorDescription: String? { rawValue }
}

struct AccountDataTemporaryFile: Sendable {
    static let maximumBytes = 32 * 1024 * 1024
    static let filename = "customer-data-export.json"
    private static let ownedDirectoryName = "DildoVanderAccountDataShares"
    let directory: URL
    let url: URL

    static func valid(filename: String, mediaType: String, data: Data) -> Bool {
        // UTF-16/32 ASCII JSON can also decode as UTF-8 containing NULs; Foundation
        // would infer that alternate encoding. Raw NUL is never legal UTF-8 JSON.
        guard filename == Self.filename, mediaType == "application/json", !data.isEmpty,
              data.count <= maximumBytes, !data.contains(0), String(data: data, encoding: .utf8) != nil,
              let object = try? JSONSerialization.jsonObject(with: data), object is [String: Any] else { return false }
        return true
    }

    static func prepare(filename: String, mediaType: String, bodyBase64: String,
                        baseDirectory: URL = FileManager.default.temporaryDirectory) throws -> Self {
        guard bodyBase64.utf8.count <= ((maximumBytes + 2) / 3) * 4,
              let data = Data(base64Encoded: bodyBase64), valid(filename: filename, mediaType: mediaType, data: data)
        else { throw AccountDataFileFailure.invalidFile }
        let owned = baseDirectory.appendingPathComponent(ownedDirectoryName, isDirectory: true)
        let directory = owned.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let file = Self(directory: directory, url: directory.appendingPathComponent(Self.filename))
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            #if os(iOS)
            try data.write(to: file.url, options: [.atomic, .completeFileProtection])
            #else
            try data.write(to: file.url, options: [.atomic])
            #endif
            return file
        } catch {
            try? file.cleanup()
            throw error
        }
    }

    func cleanup() throws {
        if FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
    }

    // Call only at launch or before this plugin admits its single share operation.
    static func removeOrphans(baseDirectory: URL = FileManager.default.temporaryDirectory) throws {
        let owned = baseDirectory.appendingPathComponent(ownedDirectoryName, isDirectory: true)
        guard FileManager.default.fileExists(atPath: owned.path) else { return }
        let entries = try FileManager.default.contentsOfDirectory(at: owned, includingPropertiesForKeys: nil)
        for entry in entries where UUID(uuidString: entry.lastPathComponent) != nil {
            try FileManager.default.removeItem(at: entry)
        }
    }
}

@MainActor final class AccountDataShareLifecycle {
    private var cleanup: (() throws -> Void)?
    private var cleanupInstalled = false
    private var completion: ((Result<Bool, Error>) -> Void)?
    private var dismiss: ((@escaping () -> Void) -> Void)?
    private(set) var isFinished = false
    private(set) var cancellationRequested = false

    func ownCleanup(_ action: @escaping () throws -> Void) throws {
        guard !cleanupInstalled else { throw AccountDataFileFailure.busy }
        cleanupInstalled = true
        if isFinished { try action() }
        else { cleanup = action }
    }

    func begin(completion: @escaping (Result<Bool, Error>) -> Void,
               dismiss: @escaping (@escaping () -> Void) -> Void) throws {
        guard !isFinished, !cancellationRequested else { throw CancellationError() }
        guard self.completion == nil else { throw AccountDataFileFailure.busy }
        self.completion = completion
        self.dismiss = dismiss
    }

    func cancel() {
        guard !isFinished, !cancellationRequested else { return }
        cancellationRequested = true
        if let action = dismiss {
            dismiss = nil
            action { self.finish(.failure(CancellationError())) }
        } else { finish(.failure(CancellationError())) }
    }

    func complete(_ completed: Bool, error: Error? = nil) {
        // Cancellation owns dismissal and settles only after its callback.
        // UIKit may report activity completion while that dismissal is in flight.
        guard !cancellationRequested else { return }
        if let error = error { finish(.failure(error)) }
        else { finish(.success(completed)) }
    }

    func fail(_ error: Error) {
        guard !cancellationRequested else { return }
        finish(.failure(error))
    }

    private func finish(_ proposed: Result<Bool, Error>) {
        guard !isFinished else { return }
        isFinished = true
        var result = proposed
        if let cleanup = cleanup {
            self.cleanup = nil
            do { try cleanup() }
            catch { result = .failure(error) }
        }
        let action = completion
        completion = nil
        dismiss = nil
        action?(result)
    }
}

// Native account HTTP and the complete passkey ceremony share one FIFO lease.
// This is also compiled by the Foundation-only native identity harness.
@MainActor final class AccountRequestScheduler {
    struct Lease: Equatable {
        let id = UUID()
        let epoch: UInt64
        let ceremony: Bool
    }
    enum Failure: String, LocalizedError {
        case contextChanged = "ACCOUNT_CONTEXT_CHANGED"
        case queueFull = "ACCOUNT_QUEUE_FULL"
        case committing = "ACCOUNT_CREDENTIAL_COMMITTING"
        var errorDescription: String? { rawValue }
    }
    private struct Waiter {
        let lease: Lease
        let requestId: UUID?
        let continuation: CheckedContinuation<Lease, Error>
    }
    private(set) var epoch: UInt64 = 0
    private var active: Lease?
    private var waiting: [Waiter] = []
    private var priorityActive: Lease?
    private var priorityWaiting: [Waiter] = []
    private var gameLiveActive: Lease?
    private var committing = false
    var credentialCommitActive: Bool { committing }

    // A held read-only poll must never occupy the account-command FIFO. A
    // second poll is rejected rather than creating another unbounded queue.
    func acquireGameLiveRead() throws -> Lease {
        try Task.checkCancellation()
        guard !committing else { throw Failure.contextChanged }
        guard gameLiveActive == nil else { throw Failure.queueFull }
        let lease = Lease(epoch: epoch, ceremony: false)
        gameLiveActive = lease
        return lease
    }

    func releaseGameLiveRead(_ lease: Lease) {
        if gameLiveActive?.id == lease.id { gameLiveActive = nil }
    }

    func acquire(ceremony: Bool = false, logout: Bool = false, requestId: UUID? = nil) async throws -> Lease {
        try Task.checkCancellation()
        // No old-view job may acquire the next epoch while a credential response
        // is still in flight. JS treats this exact code as obsolete work.
        guard !committing else { throw Failure.contextChanged }
        if logout { invalidateQueuedScope() }
        let lease = Lease(epoch: epoch, ceremony: ceremony)
        if active == nil { active = lease; return lease }
        guard waiting.count < 32 else { throw Failure.queueFull }
        return try await withCheckedThrowingContinuation { continuation in
            let waiter = Waiter(lease: lease, requestId: requestId, continuation: continuation)
            if logout { waiting.insert(waiter, at: 0) } else { waiting.append(waiter) }
        }
    }

    func release(_ lease: Lease) {
        guard active?.id == lease.id else { return }
        active = nil; committing = false
        if !waiting.isEmpty {
            let next = waiting.removeFirst(); active = next.lease
            next.continuation.resume(returning: next.lease)
        }
    }

    func requireCurrent(_ lease: Lease) throws {
        try Task.checkCancellation()
        guard lease.epoch == epoch else { throw Failure.contextChanged }
    }

    func acquirePriority(requestId: UUID? = nil) async throws -> Lease {
        try Task.checkCancellation()
        let lease = Lease(epoch: epoch, ceremony: false)
        if priorityActive == nil { priorityActive = lease; return lease }
        guard priorityWaiting.count < 4 else { throw Failure.queueFull }
        return try await withCheckedThrowingContinuation { continuation in
            priorityWaiting.append(Waiter(lease: lease, requestId: requestId, continuation: continuation))
        }
    }

    func cancelWaiting(requestId: UUID) {
        if let index = waiting.firstIndex(where: { $0.requestId == requestId }) {
            waiting.remove(at: index).continuation.resume(throwing: AccountRequestRegistry.Failure.cancelled)
        }
        if let index = priorityWaiting.firstIndex(where: { $0.requestId == requestId }) {
            priorityWaiting.remove(at: index).continuation.resume(throwing: AccountRequestRegistry.Failure.cancelled)
        }
    }

    func releasePriority(_ lease: Lease) {
        guard priorityActive?.id == lease.id else { return }
        priorityActive = nil
        if !priorityWaiting.isEmpty {
            let next = priorityWaiting.removeFirst(); priorityActive = next.lease
            next.continuation.resume(returning: next.lease)
        }
    }

    func beginCredentialCommit(_ lease: Lease) async throws {
        guard active?.id == lease.id, lease.ceremony, !committing else { throw Failure.committing }
        try requireCurrent(lease)
        committing = true
        // Priority HTTP cannot persist cookies, so it need not hold credential
        // completion hostage. Existing priority results are fenced by this epoch;
        // new Stop requests keep using the published precommit snapshot.
        invalidateQueuedScope()
    }

    func invalidateQueuedScope() {
        epoch &+= 1
        let invalid = waiting + priorityWaiting
        waiting.removeAll(); priorityWaiting.removeAll()
        for waiter in invalid { waiter.continuation.resume(throwing: Failure.contextChanged) }
    }
}

// Shared by the bridge and actual URLSession regression tests. Priority replies
// cannot update any account jar, credential storage, Keychain or cached response.
enum AccountPriorityHTTP {
    static func configuration() -> URLSessionConfiguration {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.httpShouldSetCookies = false
        config.httpCookieAcceptPolicy = .never; config.urlCredentialStorage = nil; config.urlCache = nil
        config.timeoutIntervalForRequest = 5; config.timeoutIntervalForResource = 8
        return config
    }
}

// Poll responses are invalidation frames only. Freeze authentication per read
// and discard Set-Cookie, just like the separate priority cancellation channel.
enum AccountGameLiveHTTP {
    static func configuration() -> URLSessionConfiguration {
        let config = AccountPriorityHTTP.configuration()
        config.timeoutIntervalForRequest = 8; config.timeoutIntervalForResource = 10
        return config
    }
}

struct AccountPrioritySessionSnapshot {
    let binding: String
    let csrf: String
    let cookieHeaders: [String: String]

    func authorize(_ original: URLRequest) -> URLRequest {
        var request = original
        request.httpShouldHandleCookies = false
        for (name, value) in cookieHeaders { request.setValue(value, forHTTPHeaderField: name) }
        request.setValue(csrf, forHTTPHeaderField: "X-DV-CSRF")
        return request
    }
}

// Register before FIFO admission, so cancellation cannot expire while a request
// waits for a long-running ceremony. No credentials enter this bounded ledger.
@MainActor final class AccountRequestRegistry {
    struct Ticket: Equatable { let requestId: UUID; let nonce = UUID() }
    enum Failure: String, LocalizedError {
        case cancelled = "ACCOUNT_REQUEST_CANCELLED"
        case duplicate = "ACCOUNT_REQUEST_DUPLICATE"
        case full = "ACCOUNT_REQUEST_CAPACITY"
        case invalidId = "ACCOUNT_REQUEST_INVALID_ID"
        var errorDescription: String? { rawValue }
    }
    private struct Entry {
        let ticket: Ticket
        let cancelWaiting: () -> Void
        var cancelWork: (() -> Void)?
        var cancelled = false
    }
    private struct Recent { let cancelled: Bool; let expires: TimeInterval }
    private let now: () -> TimeInterval
    private var active: [UUID: Entry] = [:]
    private var recent: [UUID: Recent] = [:]
    private var rejectNewUntil: TimeInterval = 0
    private let ttl: TimeInterval = 60
    private let limit = 128
    var activeCount: Int { active.count }
    var recentCount: Int { prune(); return recent.count }

    init(now: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) { self.now = now }

    func reserve(_ id: UUID, cancelWaiting: @escaping () -> Void) throws -> Ticket {
        prune()
        if let prior = recent[id] { throw prior.cancelled ? Failure.cancelled : Failure.duplicate }
        if active[id] != nil { throw Failure.duplicate }
        // If cancellation markers overflow, reject ALL new admissions until
        // their bounded race window expires rather than forgetting a cancel.
        guard now() >= rejectNewUntil else { throw Failure.cancelled }
        guard active.count < limit else { throw Failure.full }
        let ticket = Ticket(requestId: id)
        active[id] = Entry(ticket: ticket, cancelWaiting: cancelWaiting)
        return ticket
    }

    func attach(_ ticket: Ticket, cancel: @escaping () -> Void) {
        guard var entry = active[ticket.requestId], entry.ticket == ticket else { cancel(); return }
        entry.cancelWork = cancel; active[ticket.requestId] = entry
        if entry.cancelled { cancel() }
    }

    @discardableResult func cancel(_ id: UUID) -> Bool {
        prune()
        if var entry = active[id] {
            entry.cancelled = true; active[id] = entry
            entry.cancelWork?(); entry.cancelWaiting()
            return true
        }
        if let prior = recent[id] {
            if prior.cancelled { recent[id] = Recent(cancelled: true, expires: now() + ttl) }
            return prior.cancelled
        }
        if makeRoom() { recent[id] = Recent(cancelled: true, expires: now() + ttl) }
        else { rejectNewUntil = max(rejectNewUntil, now() + ttl) }
        return true
    }

    func finish(_ ticket: Ticket) {
        guard let entry = active[ticket.requestId], entry.ticket == ticket else { return }
        active.removeValue(forKey: ticket.requestId)
        prune()
        if makeRoom() { recent[ticket.requestId] = Recent(cancelled: entry.cancelled, expires: now() + ttl) }
        else if entry.cancelled { rejectNewUntil = max(rejectNewUntil, now() + ttl) }
    }

    private func prune() { let time = now(); recent = recent.filter { $0.value.expires > time } }
    private func makeRoom() -> Bool {
        if recent.count < limit { return true }
        if let oldest = recent.filter({ !$0.value.cancelled }).min(by: { $0.value.expires < $1.value.expires })?.key {
            recent.removeValue(forKey: oldest); return true
        }
        return false
    }

    nonisolated static func isCancellation(_ error: Error) -> Bool {
        error as? Failure == .cancelled || error is CancellationError || (error as? URLError)?.code == .cancelled
    }
}

// Antiforgery is issued to anonymous clients too: it authorizes the bootstrap
// POST, not an account. A priority cookie snapshot additionally needs identity.
struct AccountSessionResponse {
    let csrfToken: String
    let hasAccount: Bool
    var canPublishPriority: Bool { hasAccount && !csrfToken.isEmpty }

    init(_ object: [String: Any]) {
        if let token = object["csrfToken"] as? String, !token.isEmpty, token.utf8.count <= 4096,
           token.utf8.allSatisfy({ $0 >= 33 && $0 <= 126 }) {
            csrfToken = token
        } else { csrfToken = "" }
        hasAccount = object["account"] as? [String: Any] != nil
    }

    static func authorizeMutation(_ original: URLRequest, csrfToken: String) -> URLRequest {
        var request = original
        if request.httpMethod != "GET" { request.setValue(csrfToken, forHTTPHeaderField: "X-DV-CSRF") }
        return request
    }
}

// This snapshot authorizes only the native local-control UI. Cloud requests
// still need the server cookie and grants; fallback never publishes CSRF.
struct NativeOfflineAccountSession {
    static let maximumAge: TimeInterval = 12 * 60 * 60

    static func envelope(_ object: [String: Any], origin: String, binding: String, now: TimeInterval) -> [String: Any]? {
        guard object["offline"] as? Bool != true, object["enabled"] as? Bool == true,
              object["origin"] as? String == origin,
              let account = object["account"] as? [String: Any],
              let id = account["id"] as? String, !id.isEmpty, id.utf8.count <= 128,
              let name = account["displayName"] as? String, name.utf8.count <= 320,
              let roles = account["roles"] as? [String], !roles.isEmpty, roles.count <= 5,
              roles.allSatisfy({ ["Owner", "Administrator", "Operator", "Viewer", "Customer", "Tester"].contains($0) }),
              let expiry = object["expiresAt"] as? NSNumber,
              expiry.doubleValue.isFinite, expiry.doubleValue > now,
              binding.utf8.count == 64 else { return nil }
        return ["origin": origin, "binding": binding, "verifiedAt": now,
                "expiresAt": min(expiry.doubleValue, now + maximumAge),
                "account": ["id": id, "displayName": name, "roles": roles]]
    }

    static func response(_ envelope: [String: Any], origin: String, binding: String, now: TimeInterval) -> [String: Any]? {
        guard envelope["origin"] as? String == origin, envelope["binding"] as? String == binding,
              let verified = envelope["verifiedAt"] as? NSNumber,
              let expiry = envelope["expiresAt"] as? NSNumber,
              verified.doubleValue.isFinite, expiry.doubleValue.isFinite,
              now >= verified.doubleValue, now < expiry.doubleValue,
              expiry.doubleValue <= verified.doubleValue + maximumAge,
              let account = envelope["account"] as? [String: Any] else { return nil }
        let candidate: [String: Any] = ["enabled": true, "origin": origin,
            "account": account, "expiresAt": expiry]
        guard self.envelope(candidate, origin: origin, binding: binding, now: now) != nil else { return nil }
        return candidate.merging(["csrfToken": "", "offline": true]) { _, new in new }
    }

    static func permitsFallback(_ error: Error) -> Bool {
        guard let failure = error as? URLError else { return false }
        let allowed: Set<URLError.Code> = [.notConnectedToInternet, .networkConnectionLost, .timedOut,
            .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed]
        return allowed.contains(failure.code)
    }
}
