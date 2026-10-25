import Foundation

let valid: [String: Any] = ["v": 1, "deviceId": "kufr-1",
    "bootId": "12345678-1234-4234-8234-123456789abc",
    "challenge": "22345678-1234-4234-8234-123456789abc",
    "state": "idle", "capabilities": ["customerStart": 1]]
func data(_ object: [String: Any]) -> Data { try! JSONSerialization.data(withJSONObject: object) }
precondition(CustomerBleProtocol.decode(data(valid)) != nil)
for (key, value) in [("v", 2 as Any), ("v", true as Any), ("v", 1.5 as Any), ("sessionId", 42 as Any), ("bootId", "bad" as Any),
                     ("state", "provisioning" as Any), ("challenge", "bad" as Any),
                     ("deviceId", "" as Any), ("capabilities", ["customerStart": 2] as Any),
                     ("operationId", "bad" as Any), ("sessionId", "bad" as Any)] {
    var invalid = valid; invalid[key] = value
    precondition(CustomerBleProtocol.decode(data(invalid)) == nil)
}
precondition(CustomerBleProtocol.decode(Data(repeating: 0x20, count: 513)) == nil)
var disconnected = valid; disconnected["challenge"] = ""; disconnected["state"] = "recovering"
precondition(CustomerBleProtocol.decode(data(disconnected)) != nil)
precondition(AccountTransportPolicy.account(path: "/app/v1/customer/character", method: "PUT"))
precondition(AccountTransportPolicy.account(path: "/app/v1/customer/enrollment/complete", method: "POST"))
precondition(AccountTransportPolicy.account(path: "/app/v1/customer/admin/characters", method: "GET"))
precondition(AccountTransportPolicy.account(path: "/app/v1/customer/admin/sessions/12345678-1234-4234-8234-123456789abc/end", method: "POST"))
precondition(AccountTransportPolicy.account(path: "/app/v1/customer/character?expectedRevision=1&expectedProfileId=12345678123442348234123456789abc", method: "DELETE"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/customer/character?expectedRevision=1&expectedProfileId=12345678123442348234123456789abc&extra=1", method: "DELETE"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/customer/character?expectedRevision=-1&expectedProfileId=12345678123442348234123456789abc", method: "DELETE"))
precondition(AccountTransportPolicy.credentialCommit(path: "/app/v1/customer/enrollment/complete", method: "POST"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/customer/sessions/not-a-uuid/end", method: "POST"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/customer/../admin/users", method: "GET"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/customer/start-tickets", method: "GET"))
precondition(AccountTransportPolicy.account(path: "/app/v1/launch", method: "GET"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/launch?extra=1", method: "GET"))
precondition(AccountTransportPolicy.account(path: "/app/v1/prelaunch/character", method: "GET"))
precondition(AccountTransportPolicy.account(path: "/app/v1/prelaunch/character", method: "PUT"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/prelaunch/character", method: "POST"))
precondition(AccountTransportPolicy.account(path: "/app/v1/prelaunch/character/retry", method: "POST"))
precondition(AccountTransportPolicy.account(path: "/app/v1/prelaunch/character/portrait?revision=2", method: "GET"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/prelaunch/character/portrait?revision=0", method: "GET"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/prelaunch/character/portrait?revision=2&other=1", method: "GET"))
precondition(AccountTransportPolicy.account(path: "/app/v1/admin/launch", method: "GET"))
precondition(AccountTransportPolicy.account(path: "/app/v1/admin/launch", method: "PUT"))
precondition(AccountTransportPolicy.account(path: "/app/v1/admin/launch/artwork", method: "GET"))
precondition(AccountTransportPolicy.account(path: "/app/v1/admin/launch/audit", method: "GET"))
precondition(AccountTransportPolicy.account(path: "/app/v1/admin/launch/personalization-preview", method: "POST"))
precondition(AccountTransportPolicy.account(path: "/app/v1/admin/launch/artwork/12345678-1234-4234-8234-123456789abc/retry", method: "POST"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/admin/launch/artwork/../retry", method: "POST"))
precondition(AccountTransportPolicy.accountResponseLimit(path: "/app/v1/prelaunch/character/portrait?revision=2", method: "GET") == 8 * 1024 * 1024)
precondition(AccountTransportPolicy.accountResponseAllowed(path: "/app/v1/prelaunch/character/portrait?revision=2", method: "GET", size: 8 * 1024 * 1024))
precondition(!AccountTransportPolicy.accountResponseAllowed(path: "/app/v1/prelaunch/character/portrait?revision=2", method: "GET", size: 8 * 1024 * 1024 + 1))
precondition(AccountTransportPolicy.accountResponseContentTypeAllowed(path: "/app/v1/prelaunch/character/portrait?revision=2", method: "GET", contentType: "image/png"))
precondition(AccountTransportPolicy.accountResponseContentTypeAllowed(path: "/app/v1/prelaunch/character/portrait?revision=2", method: "GET", contentType: "image/webp; charset=binary"))
precondition(!AccountTransportPolicy.accountResponseContentTypeAllowed(path: "/app/v1/prelaunch/character/portrait?revision=2", method: "GET", contentType: "text/html"))
precondition(AccountTransportPolicy.publicResourceLimit(path: "/models/active.json") == 128 * 1024)
precondition(AccountTransportPolicy.publicResourceLimit(path: "/downloads/index.json") == 256 * 1024)
precondition(AccountTransportPolicy.publicResourceLimit(path: "/models/bundles/\(String(repeating: "a", count: 64))/nested/model.data") == 96 * 1024 * 1024)
precondition(AccountTransportPolicy.publicResourceLimit(path: "/models/bundles/\(String(repeating: "a", count: 64))/model-bundle.json") == 128 * 1024)
precondition(AccountTransportPolicy.publicResourceLimit(path: "/models/bundles/\(String(repeating: "a", count: 64))/../secret") == nil)
precondition(AccountTransportPolicy.publicResourceMIME("application/json", path: "/models/active.json"))
precondition(AccountTransportPolicy.publicResourceMIME("application/octet-stream", path: "/models/bundles/\(String(repeating: "a", count: 64))/model.data"))
precondition(!AccountTransportPolicy.publicResourceMIME("application/octet-stream", path: "/models/active.json"))
precondition(AccountTransportPolicy.authenticatedDownloadLimit(path: "/downloads/releases/\(String(repeating: "a", count: 64))/model.zip") == 2_147_483_648)
precondition(AccountTransportPolicy.authenticatedDownloadMIME("application/zip", path: "/downloads/releases/\(String(repeating: "a", count: 64))/model.zip"))
precondition(AccountTransportPolicy.authenticatedDownloadLimit(path: "/downloads/releases/\(String(repeating: "a", count: 64))/../secret.zip") == nil)
print("Customer BLE codec and account allowlist passed")
let testID = String(repeating: "a", count: 32)
precondition(AccountTransportPolicy.account(path: "/app/v1/testing-sessions", method: "POST"))
precondition(AccountTransportPolicy.account(path: "/app/v1/testing-sessions/\(testID)/character", method: "POST"))
precondition(AccountTransportPolicy.account(path: "/app/v1/testing-sessions/\(testID)/mode", method: "PUT"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/testing-sessions/real-device/stop", method: "POST"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/testing-sessions/\(testID)/admin", method: "POST"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/testing-sessions/\(testID)/end/extra", method: "POST"))
