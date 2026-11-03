import AppKit

enum EdgeDrag {
    case none, move
    case left, right, top, bottom
    case topLeft, topRight, bottomLeft, bottomRight
}

final class RulerView: NSView {
    weak var controller: RulerController?
    private var drag = EdgeDrag.none
    private var dragAnchor = NSPoint.zero
    private var dragFrame = NSRect.zero
    private var pointer: NSPoint?
    private var status = ""
    private var statusTimer: Timer?
    private let readout = NSTextField(labelWithString: "")
    private let statusLabel = NSTextField(labelWithString: "")
    private var dock: [NSButton] = []
    private var tracking: NSTrackingArea?

    let grip: CGFloat = 8

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    override func hitTest(_ point: NSPoint) -> NSView? {
        let hit = super.hitTest(point)
        if hit === readout || hit === statusLabel { return self }
        return hit
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        readout.font = NSFont.systemFont(ofSize: 15, weight: .semibold)
        readout.textColor = NSColor(calibratedWhite: 0.97, alpha: 1)
        readout.alignment = .center
        readout.isBezeled = false
        readout.drawsBackground = false
        readout.isEditable = false
        readout.isSelectable = false
        addSubview(readout)
        statusLabel.font = NSFont.systemFont(ofSize: 11, weight: .medium)
        statusLabel.textColor = NSColor(calibratedWhite: 0.97, alpha: 1)
        statusLabel.isHidden = true
        addSubview(statusLabel)
    }

    required init?(coder: NSCoder) { nil }

    func reload() {
        needsDisplay = true
        layoutDock()
        updateReadout()
    }

    func showStatus(_ text: String) {
        status = text
        statusLabel.stringValue = text
        statusLabel.isHidden = text.isEmpty
        statusLabel.sizeToFit()
        statusTimer?.invalidate()
        statusTimer = Timer.scheduledTimer(withTimeInterval: 3, repeats: false) { [weak self] _ in
            self?.statusLabel.isHidden = true
        }
        needsLayout = true
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: bounds, options: [.activeAlways, .mouseMoved, .mouseEnteredAndExited, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(area)
        tracking = area
    }

    override func layout() {
        super.layout()
        layoutDock()
        updateReadout()
        let statusSize = statusLabel.intrinsicContentSize
        statusLabel.frame = NSRect(x: 10, y: 8, width: min(bounds.width - 20, statusSize.width + 8), height: statusSize.height)
    }

    private func layoutDock() {
        guard let controller else { return }
        let symbols = controller.dockSymbols()
        if dock.count != symbols.count {
            dock.forEach { $0.removeFromSuperview() }
            dock = symbols.map { item in
                let button = NSButton()
                button.bezelStyle = .inline
                button.isBordered = false
                button.image = NSImage(systemSymbolName: item.symbol, accessibilityDescription: item.help)
                button.imageScaling = .scaleProportionallyDown
                button.contentTintColor = iconColor(active: item.active)
                button.target = controller
                button.action = item.action
                button.toolTip = item.help
                button.setButtonType(.momentaryChange)
                addSubview(button)
                return button
            }
        } else {
            for (button, item) in zip(dock, symbols) {
                button.image = NSImage(systemSymbolName: item.symbol, accessibilityDescription: item.help)
                button.action = item.action
                button.toolTip = item.help
                button.contentTintColor = iconColor(active: item.active)
            }
        }
        let narrow = bounds.width < 180
        let cell: CGFloat = 30
        let columns = narrow ? 2 : dock.count
        let rows = Int(ceil(Double(dock.count) / Double(max(columns, 1))))
        let width = CGFloat(columns) * cell
        let height = CGFloat(rows) * cell
        let origin = NSPoint(x: bounds.width - width - 8, y: max(6, (bounds.height - height) / 2))
        for (index, button) in dock.enumerated() {
            let col = index % columns
            let row = index / columns
            button.frame = NSRect(x: origin.x + CGFloat(col) * cell, y: origin.y + CGFloat(row) * cell, width: cell, height: cell)
        }
        dockVisible(controller.dockOpen)
    }

    func dockVisible(_ open: Bool) {
        dock.forEach { $0.isHidden = !open }
    }

    private func iconColor(active: Bool) -> NSColor {
        if active { return NSColor(srgbRed: 0.18, green: 0.83, blue: 0.75, alpha: 1) }
        let light = controller?.settings.lightContrast == true
        return light ? NSColor(calibratedWhite: 0.12, alpha: 1) : .white
    }

    private func updateReadout() {
        guard let controller else { return }
        readout.stringValue = controller.readoutText()
        let light = controller.settings.lightContrast
        let ink = light ? NSColor(calibratedWhite: 0.1, alpha: 1) : NSColor(calibratedWhite: 0.97, alpha: 1)
        readout.textColor = ink
        statusLabel.textColor = ink
        readout.sizeToFit()
        let pad = NSSize(width: readout.frame.width + 22, height: readout.frame.height + 10)
        readout.frame = NSRect(
            x: (bounds.width - pad.width) / 2,
            y: (bounds.height - pad.height) / 2,
            width: pad.width,
            height: pad.height
        )
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let controller else { return }
        let settings = controller.settings
        let light = settings.lightContrast
        let scale = window?.backingScaleFactor ?? 2
        let fillAlpha = settings.opacity
        let fill = light
            ? NSColor(calibratedWhite: 0.96, alpha: fillAlpha)
            : NSColor(srgbRed: 0.06, green: 0.09, blue: 0.16, alpha: fillAlpha)
        fill.setFill()
        bounds.fill()

        let teal = NSColor(srgbRed: 0.18, green: 0.83, blue: 0.75, alpha: light ? 0.85 : 1)
        let amber = NSColor(srgbRed: 0.98, green: 0.75, blue: 0.14, alpha: 1)
        let minor = (light ? NSColor(calibratedWhite: 0.25, alpha: 0.55) : NSColor(calibratedWhite: 1, alpha: 0.45))
        let units = settings.physicalPixels ? scale : 1
        let mode = settings.mode

        if mode != .vertical {
            drawTicks(horizontal: true, length: bounds.width, units: units, scale: scale, minor: minor, mid: teal, major: teal, labels: true)
            drawTicks(horizontal: true, length: bounds.width, units: units, scale: scale, minor: minor, mid: teal, major: teal, labels: false, far: true)
        }
        if mode != .horizontal {
            drawTicks(horizontal: false, length: bounds.height, units: units, scale: scale, minor: minor, mid: teal, major: teal, labels: true)
            drawTicks(horizontal: false, length: bounds.height, units: units, scale: scale, minor: minor, mid: teal, major: teal, labels: false, far: true)
        }

        amber.setStroke()
        let origin = NSBezierPath()
        origin.lineWidth = max(1.5, 2 / scale)
        if mode != .vertical {
            origin.move(to: NSPoint(x: 1, y: 0))
            origin.line(to: NSPoint(x: 1, y: bounds.height))
        }
        if mode != .horizontal {
            origin.move(to: NSPoint(x: 0, y: 1))
            origin.line(to: NSPoint(x: bounds.width, y: 1))
        }
        origin.stroke()

        teal.setStroke()
        let edge = NSBezierPath(rect: bounds.insetBy(dx: 0.5, dy: 0.5))
        edge.lineWidth = mode == .area ? 2 : 1
        edge.stroke()

        if let pointer {
            amber.setStroke()
            let hair = NSBezierPath()
            hair.lineWidth = 1
            if mode != .vertical {
                hair.move(to: NSPoint(x: pointer.x, y: 0))
                hair.line(to: NSPoint(x: pointer.x, y: bounds.height))
            }
            if mode != .horizontal {
                hair.move(to: NSPoint(x: 0, y: pointer.y))
                hair.line(to: NSPoint(x: bounds.width, y: pointer.y))
            }
            hair.stroke()
        }

        if !readout.isHidden {
            let pill = readout.frame.insetBy(dx: -2, dy: -2)
            let bg = light ? NSColor(calibratedWhite: 1, alpha: 0.92) : NSColor(srgbRed: 0.06, green: 0.09, blue: 0.16, alpha: 0.92)
            bg.setFill()
            NSBezierPath(roundedRect: pill, xRadius: 9, yRadius: 9).fill()
            teal.withAlphaComponent(0.7).setStroke()
            let ring = NSBezierPath(roundedRect: pill, xRadius: 9, yRadius: 9)
            ring.lineWidth = 1
            ring.stroke()
        }
    }

    private func drawTicks(horizontal: Bool, length: CGFloat, units: CGFloat, scale: CGFloat, minor: NSColor, mid: NSColor, major: NSColor, labels: Bool, far: Bool = false) {
        let span = max(0, Int((length * units).rounded()))
        let font = NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .medium)
        let attrs: [NSAttributedString.Key: Any] = [
            .font: font,
            .foregroundColor: minor
        ]
        var unit = 0
        while unit <= span {
            let along = min(length, CGFloat(unit) / units)
            let isMajor = unit % 100 == 0
            let isMid = !isMajor && unit % 50 == 0
            let mark: CGFloat = isMajor ? 16 : isMid ? 11 : 6
            let color = isMajor ? major : isMid ? mid : minor
            color.setStroke()
            let path = NSBezierPath()
            path.lineWidth = max(1 / scale, isMajor ? 1.2 : 1)
            if horizontal {
                let y = far ? bounds.height : 0
                let end = far ? y - mark : y + mark
                path.move(to: NSPoint(x: along, y: y))
                path.line(to: NSPoint(x: along, y: end))
            } else {
                let x = far ? bounds.width : 0
                let end = far ? x - mark : x + mark
                path.move(to: NSPoint(x: x, y: along))
                path.line(to: NSPoint(x: end, y: along))
            }
            path.stroke()
            if labels && isMajor && unit > 0 {
                let text = NSAttributedString(string: "\(unit)", attributes: attrs)
                let size = text.size()
                if horizontal {
                    text.draw(at: NSPoint(x: along + 3, y: far ? bounds.height - size.height - 4 : 18))
                } else {
                    text.draw(at: NSPoint(x: far ? bounds.width - size.width - 18 : 18, y: along + 2))
                }
            }
            unit += 10
        }
    }

    override func mouseEntered(with event: NSEvent) {
        controller?.dockOpen = true
        dockVisible(true)
    }

    override func mouseExited(with event: NSEvent) {
        controller?.dockOpen = false
        dockVisible(false)
        pointer = nil
        needsDisplay = true
    }

    override func mouseMoved(with event: NSEvent) {
        pointer = convert(event.locationInWindow, from: nil)
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        let point = convert(event.locationInWindow, from: nil)
        if event.clickCount == 2, readout.frame.insetBy(dx: -6, dy: -6).contains(point) {
            controller?.beginSizeEdit()
            return
        }
        drag = hit(point)
        dragAnchor = event.locationInWindow
        dragFrame = window?.frame ?? .zero
    }

    override func mouseDragged(with event: NSEvent) {
        guard drag != .none, let window, controller?.settings.locked != true else {
            if controller?.settings.locked == true { showStatus("Unlock to move the edges.") }
            return
        }
        let dx = event.locationInWindow.x - dragAnchor.x
        let dy = event.locationInWindow.y - dragAnchor.y
        var frame = dragFrame
        switch drag {
        case .move:
            frame.origin.x += dx
            frame.origin.y += dy
        case .left:
            frame.origin.x += dx
            frame.size.width -= dx
        case .right:
            frame.size.width += dx
        case .bottom:
            frame.origin.y += dy
            frame.size.height -= dy
        case .top:
            frame.size.height += dy
        case .bottomLeft:
            frame.origin.x += dx
            frame.size.width -= dx
            frame.origin.y += dy
            frame.size.height -= dy
        case .bottomRight:
            frame.size.width += dx
            frame.origin.y += dy
            frame.size.height -= dy
        case .topLeft:
            frame.origin.x += dx
            frame.size.width -= dx
            frame.size.height += dy
        case .topRight:
            frame.size.width += dx
            frame.size.height += dy
        case .none:
            break
        }
        window.setFrame(controller?.clamped(frame) ?? frame, display: true)
        controller?.refreshReadout()
    }

    override func mouseUp(with event: NSEvent) {
        if drag != .none { controller?.persistFrame() }
        drag = .none
    }

    override func scrollWheel(with event: NSEvent) {
        let delta = event.scrollingDeltaY
        guard abs(delta) > 0.1 else { return }
        controller?.changeOpacity(by: delta > 0 ? 0.05 : -0.05)
    }

    override func keyDown(with event: NSEvent) {
        controller?.handleKey(event)
    }

    private func hit(_ point: NSPoint) -> EdgeDrag {
        let left = point.x <= grip
        let right = point.x >= bounds.width - grip
        let top = point.y <= grip
        let bottom = point.y >= bounds.height - grip
        if top && left { return .topLeft }
        if top && right { return .topRight }
        if bottom && left { return .bottomLeft }
        if bottom && right { return .bottomRight }
        if left { return .left }
        if right { return .right }
        if top { return .top }
        if bottom { return .bottom }
        return .move
    }
}
