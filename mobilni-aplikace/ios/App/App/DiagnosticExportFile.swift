import Foundation

enum DiagnosticExportFile {
    static let maximumBytes = 1_048_576

    static func write(fileName: String, contents: String, directory: URL = FileManager.default.temporaryDirectory) throws -> URL {
        guard fileName.range(of: "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\\.json$", options: .regularExpression) != nil,
              !fileName.contains(".."),
              let data = contents.data(using: .utf8), data.count <= maximumBytes,
              (try? JSONSerialization.jsonObject(with: data)) != nil else {
            throw DiagnosticExportError.invalidReport
        }

        let folder = directory.appendingPathComponent("KufrDiagnostic-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appendingPathComponent(fileName, isDirectory: false)
        do {
            try data.write(to: file, options: .atomic)
            return file
        } catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
    }
}

enum DiagnosticExportError: Error {
    case invalidReport
}
