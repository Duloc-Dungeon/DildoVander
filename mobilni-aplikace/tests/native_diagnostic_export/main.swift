import Foundation

func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
    if !condition() { fatalError(message) }
}

let root = FileManager.default.temporaryDirectory.appendingPathComponent("diagnostic-export-test-\(UUID().uuidString)")
defer { try? FileManager.default.removeItem(at: root) }
try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)

let file = try DiagnosticExportFile.write(fileName: "tester-diagnostics.json", contents: "{\"schemaVersion\":2,\"requests\":[]}", directory: root)
expect(file.lastPathComponent == "tester-diagnostics.json", "The requested report name must be retained")
let writtenData = try Data(contentsOf: file)
expect(writtenData.count > 0, "A native share must receive a materialized report file")

for invalidName in ["../private.json", "report.txt", "report/other.json"] {
    do {
        _ = try DiagnosticExportFile.write(fileName: invalidName, contents: "{}", directory: root)
        fatalError("Unsafe filename accepted: \(invalidName)")
    } catch is DiagnosticExportError {
    }
}

do {
    _ = try DiagnosticExportFile.write(fileName: "report.json", contents: "not json", directory: root)
    fatalError("Invalid JSON accepted")
} catch is DiagnosticExportError {
}

do {
    _ = try DiagnosticExportFile.write(fileName: "report.json", contents: String(repeating: "x", count: DiagnosticExportFile.maximumBytes + 1), directory: root)
    fatalError("Oversized report accepted")
} catch is DiagnosticExportError {
}

print("Native diagnostic file validation passed")
