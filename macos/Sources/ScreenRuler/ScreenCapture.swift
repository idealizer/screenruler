import AppKit
import CoreGraphics

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

    static func image(quartzRect: CGRect, below windowNumber: Int) -> CGImage? {
        let windowID = CGWindowID(windowNumber)
        let list: CGWindowListOption = windowID == 0 ? .optionOnScreenOnly : .optionOnScreenBelowWindow
        return CGWindowListCreateImage(
            quartzRect,
            list,
            windowID,
            [.bestResolution, .boundsIgnoreFraming]
        )
    }

    static func pngData(from image: CGImage) -> Data? {
        let rep = NSBitmapImageRep(cgImage: image)
        return rep.representation(using: .png, properties: [:])
    }
}
