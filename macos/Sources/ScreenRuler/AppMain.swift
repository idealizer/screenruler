import AppKit
import Darwin

struct DockItem {
    var symbol: String
    var help: String
    var action: Selector
    var active: Bool = false
}

final class RulerPanel: NSPanel {
    override var canBecomeKeyWindow: Bool { true }
    override var canBecomeMainWindow: Bool { true }
}

final class RulerController: NSObject {
    var settings = AppSettings.load()
    var dockOpen = false
    var allowSmall = false
    private var panel: RulerPanel!
    private var ruler: RulerView!
    private var sizePanel: SizePanel!
    private var loupe: LoupePanel!
    private var shot: ShotPanel!
    private let hotKey = SummonHotKey()
    private var statusItem: NSStatusItem!

    func start() {
        let view = RulerView(frame: NSRect(x: 0, y: 0, width: 800, height: 72))
        view.controller = self
        ruler = view
        panel = RulerPanel(contentRect: initialFrame(), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.isFloatingPanel = true
        panel.isMovable = false
        panel.acceptsMouseMovedEvents = true
        panel.contentView = view
        panel.alphaValue = 1
        sizePanel = SizePanel { [weak self] text in self?.applyTyped(text) }
        loupe = LoupePanel { [weak self] point, origin in self?.snap(quartzPoint: point, origin: origin) }
        shot = ShotPanel()
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "ruler", accessibilityDescription: "Screen Ruler")
        let menu = NSMenu()
        menu.addItem(NSMenuItem(title: "Show Screen Ruler", action: #selector(showFromMenu(_:)), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Quit", action: #selector(quit(_:)), keyEquivalent: "q"))
        menu.items.forEach { $0.target = self }
        statusItem.menu = menu
        hotKey.install()
        NotificationCenter.default.addObserver(self, selector: #selector(showFromNote(_:)), name: screenRulerShowNote, object: nil)
        DistributedNotificationCenter.default().addObserver(self, selector: #selector(showFromNote(_:)), name: screenRulerShowNote, object: nil)
        let frame = onScreen(initialFrame())
        allowSmall = savedSizeIsCustom(frame)
        panel.setFrame(frame, display: false)
        panel.orderFrontRegardless()
        panel.makeFirstResponder(ruler)
        ruler.reload()
    }

    private func savedSizeIsCustom(_ frame: NSRect) -> Bool {
        switch settings.mode {
        case .horizontal:
            return frame.width < AppSettings.minSpan || abs(frame.height - AppSettings.barThickness) > 1
        case .vertical:
            return frame.height < AppSettings.minSpan || abs(frame.width - AppSettings.barThickness) > 1
        case .area:
            return frame.width < AppSettings.minArea || frame.height < AppSettings.minArea
        }
    }

    private func onScreen(_ frame: NSRect) -> NSRect {
        if NSScreen.screens.contains(where: { $0.frame.intersects(frame) }) { return frame }
        let screen = NSScreen.main?.visibleFrame ?? frame
        return NSRect(x: screen.midX - frame.width / 2, y: screen.midY - frame.height / 2, width: frame.width, height: frame.height)
    }

    private func initialFrame() -> NSRect {
        if let x = settings.x, let y = settings.y {
            return NSRect(x: x, y: y, width: settings.width, height: settings.height)
        }
        let screen = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        return NSRect(x: screen.midX - 400, y: screen.midY - 36, width: 800, height: AppSettings.barThickness)
    }

    func readoutText() -> String {
        let scale = panel.screen?.backingScaleFactor ?? panel.backingScaleFactor
        let w = settings.physicalPixels ? Int((panel.frame.width * scale).rounded()) : Int(panel.frame.width.rounded())
        let h = settings.physicalPixels ? Int((panel.frame.height * scale).rounded()) : Int(panel.frame.height.rounded())
        let unit = settings.physicalPixels ? "px" : "pt"
        if settings.mode == .area { return "\(w) × \(h) \(unit)" }
        let length = settings.mode == .vertical ? h : w
        return "\(length) \(unit)"
    }

    func refreshReadout() { ruler.reload() }

    func dockSymbols() -> [DockItem] {
        var items = [
            DockItem(symbol: "arrow.triangle.2.circlepath", help: "Flip orientation", action: #selector(flip)),
            DockItem(symbol: "rectangle.dashed", help: "Mark an area", action: #selector(areaMode), active: settings.mode == .area),
            DockItem(symbol: "plus.magnifyingglass", help: "Pixel zoom", action: #selector(toggleZoom), active: loupe?.isVisible == true),
            DockItem(symbol: settings.physicalPixels ? "square.grid.3x3" : "ruler", help: settings.physicalPixels ? "Physical pixels" : "Points", action: #selector(toggleUnit)),
            DockItem(symbol: "sun.max", help: "Light ticks", action: #selector(toggleContrast), active: settings.lightContrast),
            DockItem(symbol: settings.locked ? "lock.fill" : "lock.open", help: "Lock position", action: #selector(toggleLock)),
            DockItem(symbol: "xmark", help: "Hide", action: #selector(hideRuler))
        ]
        if settings.mode == .area {
            items.insert(DockItem(symbol: "camera", help: "Snapshot this area", action: #selector(snapshot)), at: 3)
        }
        return items
    }

    func changeOpacity(by delta: Double) {
        settings.opacity = min(0.90, max(0.30, settings.opacity + delta))
        settings.save()
        ruler.reload()
    }

    func clamped(_ frame: NSRect) -> NSRect {
        var frame = frame
        let floor: CGFloat = allowSmall ? 1 : 0
        switch settings.mode {
        case .horizontal:
            frame.size.height = allowSmall ? max(1, frame.height) : AppSettings.barThickness
            frame.size.width = max(allowSmall ? floor : AppSettings.minSpan, frame.width)
        case .vertical:
            frame.size.width = allowSmall ? max(1, frame.width) : AppSettings.barThickness
            frame.size.height = max(allowSmall ? floor : AppSettings.minSpan, frame.height)
        case .area:
            let minSide: CGFloat = allowSmall ? 1 : AppSettings.minArea
            frame.size.width = max(minSide, frame.width)
            frame.size.height = max(minSide, frame.height)
        }
        return frame
    }

    func persistFrame() {
        let frame = panel.frame
        settings.x = frame.minX
        settings.y = frame.minY
        settings.width = frame.width
        settings.height = frame.height
        settings.save()
    }

    @objc func showFromMenu(_ sender: Any?) { showRuler() }
    @objc func showFromNote(_ notification: Notification) { showRuler() }

    func showRuler() {
        panel.orderFrontRegardless()
        panel.makeFirstResponder(ruler)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc func hideRuler() {
        sizePanel.orderOut(nil)
        loupe.closeLoupe()
        shot.orderOut(nil)
        panel.orderOut(nil)
    }

    @objc func quit(_ sender: Any?) { NSApp.terminate(nil) }

    @objc func flip() {
        guard settings.mode != .area else { return }
        settings.mode = settings.mode == .horizontal ? .vertical : .horizontal
        let frame = panel.frame
        let next = NSRect(x: frame.minX, y: frame.maxY - frame.width, width: frame.height, height: frame.width)
        panel.setFrame(next, display: true)
        persistFrame()
        ruler.reload()
    }

    @objc func areaMode() { setMode(.area) }

    private func setMode(_ mode: MeasureMode) {
        if settings.mode == mode { return }
        let frame = panel.frame
        let top = frame.maxY
        let left = frame.minX
        allowSmall = false
        switch mode {
        case .horizontal:
            let length = settings.mode == .vertical ? frame.height : frame.width
            let width = max(AppSettings.minSpan, length)
            panel.setFrame(NSRect(x: left, y: top - AppSettings.barThickness, width: width, height: AppSettings.barThickness), display: true)
        case .vertical:
            let length = settings.mode == .horizontal ? frame.width : frame.height
            let height = max(AppSettings.minSpan, length)
            panel.setFrame(NSRect(x: left, y: top - height, width: AppSettings.barThickness, height: height), display: true)
        case .area:
            var width = frame.width
            var height = frame.height
            if settings.mode == .horizontal { height = 280 }
            if settings.mode == .vertical { width = 280 }
            width = max(AppSettings.minArea, width)
            height = max(AppSettings.minArea, height)
            panel.setFrame(NSRect(x: left, y: top - height, width: width, height: height), display: true)
        }
        settings.mode = mode
        persistFrame()
        ruler.reload()
    }
    @objc func toggleUnit() {
        settings.physicalPixels.toggle()
        settings.save()
        let scale = panel.backingScaleFactor
        if abs(scale - 1) < 0.05 {
            ruler.showStatus("This display is at 100%, so pixels and points are the same number.")
        }
        ruler.reload()
    }
    @objc func toggleContrast() {
        settings.lightContrast.toggle()
        settings.save()
        ruler.reload()
    }
    @objc func toggleLock() {
        settings.locked.toggle()
        settings.save()
        ruler.reload()
    }
    @objc func toggleZoom() {
        if loupe.isVisible {
            loupe.closeLoupe()
            ruler.reload()
            return
        }
        guard ScreenGrab.requestAccess() else {
            ruler.showStatus("Allow Screen Recording, then open zoom again.")
            return
        }
        let frame = panel.frame
        let origin = ScreenGeom.quartzOrigin(of: frame)
        let anchor: CGPoint
        switch settings.mode {
        case .horizontal: anchor = CGPoint(x: origin.x, y: origin.y + frame.height / 2)
        case .vertical: anchor = CGPoint(x: origin.x + frame.width / 2, y: origin.y)
        case .area: anchor = origin
        }
        loupe.open(anchorQuartz: anchor, windowNumber: panel.windowNumber, scale: panel.backingScaleFactor)
        ruler.reload()
    }

    @objc func snapshot() {
        guard settings.mode == .area else {
            ruler.showStatus("Switch to area, then snapshot the box.")
            return
        }
        guard ScreenGrab.requestAccess() else {
            ruler.showStatus("Allow Screen Recording, then snapshot again.")
            return
        }
        loupe.closeLoupe()
        let frame = panel.frame
        let rect = CGRect(origin: ScreenGeom.quartzOrigin(of: frame), size: frame.size)
        panel.orderOut(nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.18) { [weak self] in
            guard let self else { return }
            ScreenGrab.image(quartzRect: rect, below: 0) { [weak self] image in
                guard let self else { return }
                self.panel.orderFrontRegardless()
                guard let image, let data = ScreenGrab.pngData(from: image) else {
                    self.ruler.showStatus("Couldn't capture that area.")
                    return
                }
                let folder = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first?
                    .appendingPathComponent("ScreenRuler", isDirectory: true)
                guard let folder else { return }
                try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "")
                let url = folder.appendingPathComponent("area-\(stamp).png")
                do {
                    try data.write(to: url)
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.writeObjects([NSImage(data: data) ?? NSImage()])
                    self.shot.present(url: url, image: NSImage(data: data) ?? NSImage(), near: frame)
                } catch {
                    self.ruler.showStatus("Couldn't save that snapshot.")
                }
            }
        }
    }

    func beginSizeEdit() {
        guard !settings.locked else {
            ruler.showStatus("Unlock to move the edges.")
            return
        }
        sizePanel.present(near: panel.frame, text: readoutText().replacingOccurrences(of: " px", with: "").replacingOccurrences(of: " pt", with: ""))
    }

    func applyTyped(_ text: String) {
        sizePanel.orderOut(nil)
        let cleaned = text.lowercased()
            .replacingOccurrences(of: "×", with: "x")
            .replacingOccurrences(of: "*", with: "x")
            .replacingOccurrences(of: "by", with: "x")
            .replacingOccurrences(of: "'", with: "")
            .replacingOccurrences(of: ",", with: "")
        let parts = cleaned.split(whereSeparator: { $0 == "x" || $0 == " " }).map { String($0) }.filter { !$0.isEmpty }
        guard let width = Int(parts.first ?? ""), width > 0 else {
            ruler.showStatus("Type width × height, for example 800 × 600.")
            return
        }
        let height = parts.count > 1 ? Int(parts[1]) : nil
        apply(width: width, height: height)
    }

    private func apply(width: Int, height: Int?) {
        guard !settings.locked else {
            ruler.showStatus("Unlock to move the edges.")
            return
        }
        let scale = panel.backingScaleFactor
        func points(_ value: Int) -> CGFloat {
            settings.physicalPixels ? CGFloat(value) / scale : CGFloat(value)
        }
        let frame = panel.frame
        var mode = settings.mode
        var newW = points(width)
        var newH = frame.height
        let thick = AppSettings.barThickness
        if let height {
            let h = points(height)
            if mode == .horizontal && abs(h - thick) <= 1 {
                newW = points(width)
                newH = thick
            } else if mode == .vertical && abs(points(width) - thick) <= 1 {
                newW = thick
                newH = h
            } else {
                mode = .area
                newW = points(width)
                newH = h
            }
        } else if mode == .vertical {
            newW = thick
            newH = points(width)
        } else if mode == .horizontal {
            newW = points(width)
            newH = thick
        } else {
            newW = points(width)
        }
        settings.mode = mode
        allowSmall = true
        let next = NSRect(x: frame.minX, y: frame.maxY - newH, width: max(1, newW), height: max(1, newH))
        panel.setFrame(next, display: true)
        persistFrame()
        ruler.reload()
    }

    func snap(quartzPoint: CGPoint, origin: Bool) {
        guard !settings.locked else {
            ruler.showStatus("Unlock to move the edges.")
            return
        }
        let scale = panel.backingScaleFactor
        let frame = panel.frame
        var left = frame.minX
        var top = ScreenGeom.quartzOrigin(of: frame).y
        var right = frame.maxX
        var bottom = top + frame.height
        let px = quartzPoint.x
        let py = quartzPoint.y
        if settings.mode != .vertical {
            if origin {
                let width = right - left
                left = px
                right = left + max(width, 1 / scale)
            } else {
                right = px + 1 / scale
                if right - left < 1 / scale { left = right - 1 / scale }
            }
        }
        if settings.mode != .horizontal {
            if origin {
                let height = bottom - top
                top = py
                bottom = top + max(height, 1 / scale)
            } else {
                bottom = py + 1 / scale
                if bottom - top < 1 / scale { top = bottom - 1 / scale }
            }
        }
        allowSmall = true
        panel.setFrame(ScreenGeom.appKitFrame(quartzX: left, quartzY: top, width: max(1, right - left), height: max(1, bottom - top)), display: true)
        persistFrame()
        ruler.reload()
        if loupe.isVisible {
            toggleZoom()
            toggleZoom()
        }
    }

    func handleKey(_ event: NSEvent) {
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let command = flags.contains(.command)
        let control = flags.contains(.control)
        let chars = event.charactersIgnoringModifiers?.lowercased() ?? ""
        if event.keyCode == 53 {
            if sizePanel.isVisible { sizePanel.orderOut(nil); return }
            if shot.isVisible { shot.orderOut(nil); return }
            if loupe.isVisible { loupe.closeLoupe(); ruler.reload(); return }
            hideRuler()
            return
        }
        if (command || control) && chars == "f" { flip(); return }
        if command && chars == "1" { setMode(.horizontal); return }
        if command && chars == "2" { setMode(.vertical); return }
        if command && chars == "3" { setMode(.area); return }
        let blocked: NSEvent.ModifierFlags = [.command, .control, .option]
        if chars == "z" && flags.isDisjoint(with: blocked) { toggleZoom(); return }
        if command && chars == "c" {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(readoutText(), forType: .string)
            ruler.showStatus("Copied \(readoutText()).")
            return
        }
        let shift = flags.contains(.shift) ? 10 : 1
        let step = CGFloat(shift) / panel.backingScaleFactor
        var frame = panel.frame
        switch event.keyCode {
        case 123: frame.origin.x -= step
        case 124: frame.origin.x += step
        case 125: frame.origin.y -= step
        case 126: frame.origin.y += step
        default: return
        }
        guard !settings.locked else { ruler.showStatus("Unlock to move the edges."); return }
        panel.setFrame(frame, display: true)
        persistFrame()
        ruler.reload()
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    let controller = RulerController()
    private var lockFD: Int32 = -1

    func applicationDidFinishLaunching(_ notification: Notification) {
        if !acquireLock() {
            DistributedNotificationCenter.default().post(name: screenRulerShowNote, object: nil)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { NSApp.terminate(nil) }
            return
        }
        NSApp.setActivationPolicy(.accessory)
        controller.start()
    }

    private func acquireLock() -> Bool {
        let url = AppSettings.fileURL.deletingLastPathComponent().appendingPathComponent("instance.lock")
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        lockFD = Darwin.open(url.path, O_CREAT | O_RDWR, mode_t(0o644))
        if lockFD < 0 { return true }
        return flock(lockFD, LOCK_EX | LOCK_NB) == 0
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
