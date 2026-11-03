import AppKit
import CoreGraphics
import Darwin
import ScreenCaptureKit

enum ScreenGeom {
    static func primaryHeight() -> CGFloat {
        let primary = NSScreen.screens.first { $0.frame.origin == .zero } ?? NSScreen.main
        return primary?.frame.height ?? 0
    }

    static func quartzOrigin(of frame: NSRect) -> CGPoint {
        CGPoint(x: frame.minX, y: primaryHeight() - frame.maxY)
    }

    static func appKitFrame(quartzX: CGFloat, quartzY: CGFloat, width: CGFloat, height: CGFloat) -> NSRect {
        NSRect(x: quartzX, y: primaryHeight() - quartzY - height, width: width, height: height)
    }
}

enum ScreenGrab {
    static func requestAccess() -> Bool {
        if CGPreflightScreenCaptureAccess() { return true }
        return CGRequestScreenCaptureAccess()
    }

    static func image(quartzRect: CGRect, below windowNumber: Int, completion: @escaping (CGImage?) -> Void) {
        guard quartzRect.width > 0, quartzRect.height > 0 else {
            completion(nil)
            return
        }
        if #available(macOS 14.0, *) {
            Task {
                let image = await ModernCapture.image(quartzRect: quartzRect)
                DispatchQueue.main.async { completion(image) }
            }
        } else {
            completion(legacyImage(quartzRect: quartzRect, below: CGWindowID(windowNumber)))
        }
    }

    static func pngData(from image: CGImage) -> Data? {
        let rep = NSBitmapImageRep(cgImage: image)
        return rep.representation(using: .png, properties: [:])
    }

    /// macOS 13 only. Looked up at runtime so a newer system can drop the symbol without blocking launch.
    private static func legacyImage(quartzRect: CGRect, below windowID: CGWindowID) -> CGImage? {
        guard let symbol = legacyCapture else { return nil }
        let list: UInt32 = windowID == 0
            ? CGWindowListOption.optionOnScreenOnly.rawValue
            : CGWindowListOption.optionOnScreenBelowWindow.rawValue
        let options = CGWindowImageOption.bestResolution.rawValue | CGWindowImageOption.boundsIgnoreFraming.rawValue
        guard let pointer = symbol(quartzRect, list, windowID, options) else { return nil }
        return Unmanaged<CGImage>.fromOpaque(pointer).takeRetainedValue()
    }

    private static let legacyCapture: LegacyCapture? = {
        guard let handle = dlopen("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", RTLD_NOW) else { return nil }
        guard let symbol = dlsym(handle, "CGWindowListCreateImage") else { return nil }
        return unsafeBitCast(symbol, to: LegacyCapture.self)
    }()
}

private typealias LegacyCapture = @convention(c) (CGRect, UInt32, UInt32, UInt32) -> UnsafeMutableRawPointer?

@available(macOS 14.0, *)
private enum ModernCapture {
    static var content: SCShareableContent?
    static var fetched = Date.distantPast

    static func image(quartzRect: CGRect) async -> CGImage? {
        do {
            if content == nil || Date().timeIntervalSince(fetched) > 1.5 {
                content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                fetched = Date()
            }
            guard let content else { return nil }
            let appKit = ScreenGeom.appKitFrame(
                quartzX: quartzRect.minX,
                quartzY: quartzRect.minY,
                width: quartzRect.width,
                height: quartzRect.height
            )
            guard let display = content.displays.first(where: { $0.frame.intersects(appKit) }) ?? content.displays.first else {
                return nil
            }
            let pid = pid_t(ProcessInfo.processInfo.processIdentifier)
            let ours = content.windows.filter { $0.owningApplication?.processID == pid }
            let filter = SCContentFilter(display: display, excludingWindows: ours)
            let local = CGRect(
                x: appKit.minX - display.frame.minX,
                y: display.frame.maxY - appKit.maxY,
                width: appKit.width,
                height: appKit.height
            )
            let scale = NSScreen.screens.first { $0.frame.intersects(appKit) }?.backingScaleFactor ?? 2
            let config = SCStreamConfiguration()
            config.sourceRect = local
            config.width = max(1, Int((local.width * scale).rounded()))
            config.height = max(1, Int((local.height * scale).rounded()))
            config.showsCursor = false
            return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
        } catch {
            content = nil
            return nil
        }
    }
}
