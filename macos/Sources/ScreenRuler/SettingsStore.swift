import Foundation

enum MeasureMode: String, Codable {
    case horizontal
    case vertical
    case area
}

struct AppSettings: Codable {
    var x: Double?
    var y: Double?
    var width: Double = 800
    var height: Double = 72
    var mode: MeasureMode = .horizontal
    var opacity: Double = 0.60
    var physicalPixels: Bool = true
    var lightContrast: Bool = false
    var locked: Bool = false

    static let barThickness: CGFloat = 72
    static let minSpan: CGFloat = 320
    static let minArea: CGFloat = 180

    static var fileURL: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory())
        return base.appendingPathComponent("ScreenRuler/settings.json")
    }

    static func load() -> AppSettings {
        guard let data = try? Data(contentsOf: fileURL),
              let settings = try? JSONDecoder().decode(AppSettings.self, from: data) else {
            return AppSettings()
        }
        return settings
    }

    func save() {
        var copy = self
        copy.opacity = min(0.90, max(0.30, (copy.opacity * 100).rounded() / 100))
        let url = Self.fileURL
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard let data = try? JSONEncoder.pretty.encode(copy) else { return }
        try? data.write(to: url)
    }
}

private extension JSONEncoder {
    static var pretty: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }
}
