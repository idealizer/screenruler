import AppKit

final class SizePanel: NSPanel {
    private let field = NSTextField(string: "")
    private let onApply: (String) -> Void

    init(onApply: @escaping (String) -> Void) {
        self.onApply = onApply
        super.init(contentRect: NSRect(x: 0, y: 0, width: 460, height: 360), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isFloatingPanel = true
        level = .popUpMenu
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        isOpaque = false
        backgroundColor = .clear
        hidesOnDeactivate = false
        let root = NSVisualEffectView(frame: NSRect(x: 0, y: 0, width: 460, height: 360))
        root.material = .hudWindow
        root.state = .active
        root.wantsLayer = true
        root.layer?.cornerRadius = 12
        contentView = root

        let title = NSTextField(labelWithString: "Width × height")
        title.font = NSFont.systemFont(ofSize: 12, weight: .medium)
        title.textColor = .white
        title.frame = NSRect(x: 16, y: 324, width: 200, height: 18)
        root.addSubview(title)

        field.frame = NSRect(x: 16, y: 284, width: 340, height: 32)
        field.font = NSFont.monospacedDigitSystemFont(ofSize: 16, weight: .semibold)
        field.alignment = .center
        field.target = self
        field.action = #selector(applyField)
        root.addSubview(field)

        let set = NSButton(title: "Set", target: self, action: #selector(applyField))
        set.frame = NSRect(x: 366, y: 284, width: 78, height: 32)
        set.bezelStyle = .rounded
        root.addSubview(set)

        addGroup(root, title: "Screens", y: 250, columns: 3, items: [
            ("1920×1080", "16:9", "1920x1080"),
            ("1366×768", "Laptop", "1366x768"),
            ("1080×1920", "9:16", "1080x1920"),
            ("768×1024", "Tablet", "768x1024"),
            ("390×800", "Mobile", "390x800")
        ])
        addGroup(root, title: "Ads", y: 92, columns: 4, items: [
            ("728×90", "Leaderboard", "728x90"),
            ("300×250", "Rectangle", "300x250"),
            ("300×600", "Half-page", "300x600"),
            ("160×600", "Skyscraper", "160x600")
        ])
    }

    private func addGroup(_ root: NSView, title: String, y: CGFloat, columns: Int, items: [(String, String, String)]) {
        let label = NSTextField(labelWithString: title)
        label.font = NSFont.systemFont(ofSize: 11, weight: .medium)
        label.textColor = NSColor(calibratedWhite: 1, alpha: 0.7)
        label.frame = NSRect(x: 16, y: y, width: 200, height: 16)
        root.addSubview(label)
        let cellW: CGFloat = (428 / CGFloat(columns))
        for (index, item) in items.enumerated() {
            let col = index % columns
            let row = index / columns
            let button = NSButton(title: "\(item.0)  \(item.1)", target: self, action: #selector(preset(_:)))
            button.identifier = NSUserInterfaceItemIdentifier(item.2)
            button.bezelStyle = .rounded
            button.font = NSFont.systemFont(ofSize: 11, weight: .semibold)
            button.frame = NSRect(x: 16 + CGFloat(col) * cellW, y: y - 48 - CGFloat(row) * 46, width: cellW - 6, height: 42)
            root.addSubview(button)
        }
    }

    func present(near frame: NSRect, text: String) {
        field.stringValue = text
        let height: CGFloat = 360
        var origin = NSPoint(x: frame.minX, y: frame.minY - height - 12)
        if origin.y < 20 { origin.y = frame.maxY + 12 }
        setFrame(NSRect(x: origin.x, y: origin.y, width: 460, height: height), display: true)
        NSApp.activate(ignoringOtherApps: true)
        orderFrontRegardless()
        makeKey()
        field.becomeFirstResponder()
    }

    override var canBecomeKeyWindow: Bool { true }

    @objc private func applyField() { onApply(field.stringValue) }
    @objc private func preset(_ sender: NSButton) { onApply(sender.identifier?.rawValue.replacingOccurrences(of: "x", with: " × ") ?? "") }
    override func cancelOperation(_ sender: Any?) { orderOut(nil) }
}

final class LoupePanel: NSPanel {
    private let canvas = LoupeCanvas()
    private var timer: Timer?
    private let onPick: (CGPoint, Bool) -> Void
    var zeroMode = true

    init(onPick: @escaping (CGPoint, Bool) -> Void) {
        self.onPick = onPick
        super.init(contentRect: NSRect(x: 0, y: 0, width: 220, height: 250), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isFloatingPanel = true
        level = .popUpMenu
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        isOpaque = false
        backgroundColor = NSColor(srgbRed: 0.06, green: 0.09, blue: 0.16, alpha: 0.94)
        hidesOnDeactivate = false
        canvas.frame = NSRect(x: 16, y: 16, width: 188, height: 188)
        canvas.onPick = { [weak self] point in
            guard let self else { return }
            self.onPick(point, self.zeroMode)
        }
        contentView?.addSubview(canvas)
        let start = NSButton(title: "Start", target: self, action: #selector(setStart))
        start.frame = NSRect(x: 16, y: 212, width: 70, height: 26)
        start.bezelStyle = .rounded
        let end = NSButton(title: "End", target: self, action: #selector(setEnd))
        end.frame = NSRect(x: 92, y: 212, width: 70, height: 26)
        end.bezelStyle = .rounded
        contentView?.addSubview(start)
        contentView?.addSubview(end)
    }

    func open(anchorQuartz: CGPoint, windowNumber: Int, scale: CGFloat) {
        zeroMode = true
        canvas.scale = scale
        canvas.windowNumber = windowNumber
        canvas.anchor = anchorQuartz
        let side: CGFloat = 21 / max(scale, 1) * 8 + 40
        _ = side
        let centerY = ScreenGeom.primaryHeight() - anchorQuartz.y
        setFrame(NSRect(x: anchorQuartz.x + 24, y: centerY - 125, width: 220, height: 250), display: false)
        orderFrontRegardless()
        refresh()
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in self?.refresh() }
    }

    func closeLoupe() {
        timer?.invalidate()
        timer = nil
        orderOut(nil)
    }

    @objc private func setStart() { zeroMode = true }
    @objc private func setEnd() { zeroMode = false }

    private func refresh() {
        if !zeroMode {
            let mouse = NSEvent.mouseLocation
            canvas.anchor = CGPoint(x: mouse.x, y: ScreenGeom.primaryHeight() - mouse.y)
            let centerY = ScreenGeom.primaryHeight() - canvas.anchor.y
            setFrameOrigin(NSPoint(x: canvas.anchor.x + 28, y: centerY - 125))
        }
        let cells: CGFloat = 21
        let scale = max(canvas.scale, 1)
        let size = cells / scale
        let rect = CGRect(x: canvas.anchor.x - size / 2, y: canvas.anchor.y - size / 2, width: size, height: size)
        canvas.image = ScreenGrab.image(quartzRect: rect, below: canvas.windowNumber)
        canvas.needsDisplay = true
    }

    override var canBecomeKeyWindow: Bool { true }
}

final class LoupeCanvas: NSView {
    var image: CGImage?
    var scale: CGFloat = 2
    var windowNumber = 0
    var anchor = CGPoint.zero
    var onPick: ((CGPoint) -> Void)?
    override var isFlipped: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.setFill()
        bounds.fill()
        guard let image else { return }
        let rep = NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
        NSGraphicsContext.current?.imageInterpolation = .none
        rep.draw(in: bounds, from: .zero, operation: .copy, fraction: 1)
        NSColor(calibratedWhite: 1, alpha: 0.25).setStroke()
        let step = bounds.width / 21
        for i in 0...21 {
            let line = NSBezierPath()
            line.move(to: NSPoint(x: CGFloat(i) * step, y: 0))
            line.line(to: NSPoint(x: CGFloat(i) * step, y: bounds.height))
            line.move(to: NSPoint(x: 0, y: CGFloat(i) * step))
            line.line(to: NSPoint(x: bounds.width, y: CGFloat(i) * step))
            line.stroke()
        }
        NSColor(srgbRed: 0.18, green: 0.83, blue: 0.75, alpha: 1).setStroke()
        NSBezierPath(rect: NSRect(x: step * 10, y: step * 10, width: step, height: step)).stroke()
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        let step = bounds.width / 21
        let col = min(20, max(0, Int(point.x / step)))
        let row = min(20, max(0, Int(point.y / step)))
        let pixel = CGPoint(x: anchor.x + CGFloat(col - 10) / max(scale, 1), y: anchor.y + CGFloat(row - 10) / max(scale, 1))
        onPick?(pixel)
    }
}

final class ShotPanel: NSPanel {
    private let imageView = NSImageView()
    private var fileURL: URL?

    init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 280, height: 250), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isFloatingPanel = true
        level = .popUpMenu
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        isOpaque = false
        backgroundColor = NSColor(srgbRed: 0.06, green: 0.09, blue: 0.16, alpha: 0.96)
        hidesOnDeactivate = false
        imageView.frame = NSRect(x: 12, y: 78, width: 256, height: 150)
        imageView.imageScaling = .scaleProportionallyUpOrDown
        contentView?.addSubview(imageView)
        let open = NSButton(title: "Open", target: self, action: #selector(openFile))
        open.frame = NSRect(x: 12, y: 16, width: 80, height: 28)
        open.bezelStyle = .rounded
        let finder = NSButton(title: "Show", target: self, action: #selector(reveal))
        finder.toolTip = "Show in Finder"
        finder.frame = NSRect(x: 100, y: 16, width: 80, height: 28)
        finder.bezelStyle = .rounded
        let close = NSButton(title: "Close", target: self, action: #selector(closeCard))
        close.frame = NSRect(x: 188, y: 16, width: 80, height: 28)
        close.bezelStyle = .rounded
        contentView?.addSubview(open)
        contentView?.addSubview(finder)
        contentView?.addSubview(close)
    }

    func present(url: URL, image: NSImage, near frame: NSRect) {
        fileURL = url
        imageView.image = image
        let name = NSTextField(labelWithString: url.lastPathComponent)
        name.textColor = .white
        name.font = NSFont.systemFont(ofSize: 12, weight: .semibold)
        name.frame = NSRect(x: 12, y: 50, width: 256, height: 18)
        contentView?.subviews.filter { $0 is NSTextField }.forEach { $0.removeFromSuperview() }
        contentView?.addSubview(name)
        var originY = frame.minY - 262
        if originY < 20 { originY = frame.maxY + 12 }
        setFrame(NSRect(x: frame.minX, y: originY, width: 280, height: 250), display: true)
        orderFrontRegardless()
    }

    @objc private func openFile() { if let fileURL { NSWorkspace.shared.open(fileURL) } }
    @objc private func reveal() { if let fileURL { NSWorkspace.shared.activateFileViewerSelecting([fileURL]) } }
    @objc private func closeCard() { orderOut(nil) }
    override func cancelOperation(_ sender: Any?) { orderOut(nil) }
    override var canBecomeKeyWindow: Bool { true }
}
