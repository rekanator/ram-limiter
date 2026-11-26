// Menu bar app: measures each browser's memory and tells its extension when to unload tabs.
import AppKit
import Network
import ServiceManagement

let port: UInt16 = 47831
let defaults = UserDefaults.standard
// The ID fixed by "key" in the extension's manifest. No other extension is answered.
let extensionOrigin = "chrome-extension://hoblikokhahjeobemjnpnaholafobgki"

/// Processes whose program lives inside the named app bundle.
func processes(in bundle: String) -> [pid_t] {
    var pids = [pid_t](repeating: 0, count: 8192)
    let count = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.size))
    var path = [CChar](repeating: 0, count: 4096)
    return pids.prefix(Int(max(count, 0))).filter {
        proc_pidpath($0, &path, UInt32(path.count)) > 0 && String(cString: path).contains("/\(bundle)/")
    }
}

/// Whether this process holds the client end of a connection to us from the given port.
func holdsConnection(_ pid: pid_t, from clientPort: UInt16) -> Bool {
    let stride = MemoryLayout<proc_fdinfo>.stride
    let size = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, nil, 0)
    guard size > 0 else { return false }
    var files = [proc_fdinfo](repeating: proc_fdinfo(), count: Int(size) / stride + 16)
    let filled = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, &files, Int32(files.count * stride))
    return files.prefix(Int(max(filled, 0)) / stride).contains { file in
        guard file.proc_fdtype == UInt32(PROX_FDTYPE_SOCKET) else { return false }
        var info = socket_fdinfo()
        let length = Int32(MemoryLayout<socket_fdinfo>.size)
        guard proc_pidfdinfo(pid, file.proc_fd, PROC_PIDFDSOCKETINFO, &info, length) == length,
              info.psi.soi_kind == Int32(SOCKINFO_TCP) else { return false }
        let ends = info.psi.soi_proto.pri_tcp.tcpsi_ini
        func number(_ raw: Int32) -> UInt16 { UInt16(truncatingIfNeeded: raw).byteSwapped }
        return number(ends.insi_lport) == clientPort && number(ends.insi_fport) == port
    }
}

/// Almost free, but reads high because shared memory is counted once per process.
/// Used only to move the display between accurate checks.
func quickBytes(_ pids: [pid_t]) -> UInt64 {
    pids.reduce(0) { total, pid in
        var info = rusage_info_v4()
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) { proc_pid_rusage(pid, RUSAGE_INFO_V4, $0) }
        }
        return result == 0 ? total + info.ri_phys_footprint : total
    }
}

/// A browser's total memory, counting memory shared between its processes once.
/// Returns 0 if it can't be measured, so nothing gets unloaded on bad data.
func totalBytes(_ pids: [pid_t]) -> UInt64 {
    if pids.isEmpty { return 0 }

    let tool = Process()
    let pipe = Pipe()
    tool.executableURL = URL(fileURLWithPath: "/usr/bin/footprint")
    tool.arguments = ["-f", "bytes", "--noCategories"] + pids.map { String($0) }
    tool.standardOutput = pipe
    tool.standardError = FileHandle.nullDevice
    guard (try? tool.run()) != nil else { return 0 }
    let output = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
    tool.waitUntilExit()
    // "Summary Footprint: N B", or the single "Footprint: N B" line if only one process.
    let line = output.split(separator: "\n").last { $0.contains("Summary Footprint:") }
        ?? output.split(separator: "\n").first { $0.contains("Footprint:") }
    return line?.components(separatedBy: "Footprint:").last?
        .split(separator: " ").first.flatMap { UInt64($0) } ?? 0
}

let gb = 1_073_741_824.0

/// CPU seconds used so far by finished child processes, i.e. the measurements.
func childCPUSeconds() -> Double {
    var r = rusage()
    getrusage(RUSAGE_CHILDREN, &r)
    func seconds(_ t: timeval) -> Double { Double(t.tv_sec) + Double(t.tv_usec) / 1_000_000 }
    return seconds(r.ru_utime) + seconds(r.ru_stime)
}

/// What the extension sends with each check.
struct Report: Decodable {
    struct Tabs: Decodable { let loaded: Int, total: Int }
    struct Unload: Decodable { let time: Double, title: String, site: String, url: String?, tab: Int? }
    let tabs: Tabs
    let sites: [String]
    let unloaded: [Unload]
    let reloaded: Bool? // the extension was just installed or reloaded
}

/// Sent to the extension when a log entry is clicked.
struct Jump: Encodable { let url: String, tab: Int? }

/// One line of the log, as saved in settings.
typealias LogEntry = [String: String]

/// What the app answers with.
struct Settings: Encodable {
    let enabled: Bool, limit: UInt64, used: UInt64, interval: Int
    let grace: Int, never: [String], protectTyped: Bool, protectBusy: Bool
}

let iconStyles = [("Nap Chip", "nap"), ("Numbers", "numbers"), ("Speedometer", "speedometer"),
                  ("Memory chip", "chip"), ("Ring", "ring"), ("RAM text", "text")]

/// One supported browser: its own settings (saved) and what is currently known about it.
final class Browser {
    let id: String, name: String, bundle: String, bundleID: String
    var used: UInt64 = 0
    var measured = Date.distantPast
    var ratio = 0.0 // accurate reading divided by quick reading, from the last check
    var beforeUnload: UInt64 = 0 // memory just before the unloads being reported
    var tabs: Report.Tabs?
    var openSites: [String] = [] // kept in memory only
    var waiting: NWConnection? // the extension's open request for log clicks
    var lastWait = Date.distantPast

    init(_ id: String, _ name: String, _ bundle: String, _ bundleID: String) {
        (self.id, self.name, self.bundle, self.bundleID) = (id, name, bundle, bundleID)
    }

    var pids: [pid_t] { processes(in: bundle) }
    var location: URL? { NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) }

    subscript(_ key: String) -> Any? {
        get { defaults.object(forKey: "\(id).\(key)") }
        set { defaults.set(newValue, forKey: "\(id).\(key)") }
    }
    var enabled: Bool { self["enabled"] as? Bool ?? true }
    var limitGB: Double { self["limitGB"] as? Double ?? 4 }
    var interval: Int {
        let seconds = self["interval"] as? Int ?? 60
        return [10, 20, 30, 60].contains(seconds) ? seconds : 60
    }
    var grace: Int { self["graceMinutes"] as? Int ?? 3 }
    var iconStyle: String { self["icon"] as? String ?? "nap" }
    var never: [String] { self["neverSites"] as? [String] ?? [] }
    var protectTyped: Bool { self["protectTyped"] as? Bool ?? true }
    var protectBusy: Bool { self["protectBusy"] as? Bool ?? true }
    var removed: Bool { self["removed"] as? Bool ?? false } // stays ignored until its extension is reloaded
    var checkCost: Double { self["checkCost"] as? Double ?? 0 } // CPU seconds per check on this Mac
    var pausedUntil: Date? {
        guard let date = self["pausedUntil"] as? Date, date > Date() else { return nil }
        return date
    }
    var active: Bool { enabled && pausedUntil == nil }
    var lastSeen: Date? { self["lastSeen"] as? Date }
    var connected: Bool { lastSeen.map { -$0.timeIntervalSinceNow < Double(interval + 60) } ?? false }
    var today: String { ISO8601DateFormatter.string(from: Date(), timeZone: .current, formatOptions: .withFullDate) }
    var freedToday: Double { self["freedDay"] as? String == today ? self["freedBytes"] as? Double ?? 0 : 0 }

    func forgetReading() {
        (used, measured, ratio) = (0, .distantPast, 0)
    }
}

let browsers = [
    Browser("opera", "Opera", "Opera.app", "com.operasoftware.Opera"),
    Browser("operagx", "Opera GX", "Opera GX.app", "com.operasoftware.OperaGX"),
    Browser("chrome", "Chrome", "Google Chrome.app", "com.google.Chrome"),
    Browser("edge", "Edge", "Microsoft Edge.app", "com.microsoft.edgemac"),
    Browser("brave", "Brave", "Brave Browser.app", "com.brave.Browser"),
    Browser("vivaldi", "Vivaldi", "Vivaldi.app", "com.vivaldi.Vivaldi"),
]

if CommandLine.arguments.contains("--once") {
    for browser in browsers where !browser.pids.isEmpty {
        let before = childCPUSeconds()
        let used = totalBytes(browser.pids)
        print(String(format: "%@: %.2f GB, %.2f CPU seconds", browser.name, Double(used) / gb, childCPUSeconds() - before))
    }
    exit(0)
}

/// A line in the Browser submenu. Resting the pointer on it previews that browser.
final class BrowserRow: NSView {
    let browser: Browser
    var chosen = false { didSet { needsDisplay = true } }
    var detail = ""
    var onHover: (Bool) -> Void = { _ in }
    var onClick: () -> Void = {}
    var onRemove: () -> Void = {}
    private var hovered = false { didSet { needsDisplay = true } }
    private var cross: NSRect { NSRect(x: bounds.width - 30, y: 2, width: 24, height: bounds.height - 4) }

    init(_ browser: Browser) {
        self.browser = browser
        super.init(frame: NSRect(x: 0, y: 0, width: 220, height: 24))
        autoresizingMask = .width
        setAccessibilityElement(true)
        setAccessibilityRole(.button)
        setAccessibilityLabel(browser.name)
    }
    required init?(coder: NSCoder) { nil }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                       owner: self))
    }
    override func mouseEntered(with event: NSEvent) { hovered = true; onHover(true) }
    override func mouseExited(with event: NSEvent) { hovered = false; onHover(false) }
    override func mouseUp(with event: NSEvent) {
        if cross.contains(convert(event.locationInWindow, from: nil)) { onRemove() } else { onClick() }
    }
    override func accessibilityPerformPress() -> Bool { onClick(); return true }

    override func draw(_ rect: NSRect) {
        if hovered {
            NSColor.selectedContentBackgroundColor.setFill()
            NSBezierPath(roundedRect: bounds.insetBy(dx: 5, dy: 0), xRadius: 5, yRadius: 5).fill()
        }
        let ink: NSColor = hovered ? .selectedMenuItemTextColor : .labelColor
        let faint: NSColor = hovered ? .selectedMenuItemTextColor : .secondaryLabelColor
        func write(_ text: String, _ x: CGFloat, _ color: NSColor) -> CGFloat {
            let styled = NSAttributedString(string: text, attributes: [.font: NSFont.menuFont(ofSize: 0),
                                                                       .foregroundColor: color])
            styled.draw(at: NSPoint(x: x, y: (bounds.height - styled.size().height) / 2))
            return x + styled.size().width
        }
        if chosen { _ = write("✓", 11, ink) }
        let end = write(browser.name, 26, ink)
        _ = write(detail, end + 6, faint)
        _ = write("✕", cross.minX + 7, faint)
    }
}

/// A clickable menu line that leaves the menu open. It draws its menu item, which
/// keeps its action so the keyboard still works.
final class MenuRow: NSView {
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                       owner: self))
    }
    override func mouseEntered(with event: NSEvent) { needsDisplay = true }
    override func mouseExited(with event: NSEvent) { needsDisplay = true }
    override func mouseUp(with event: NSEvent) { _ = accessibilityPerformPress() }

    override func isAccessibilityElement() -> Bool { true }
    override func accessibilityRole() -> NSAccessibility.Role? { .menuItem }
    override func accessibilityLabel() -> String? {
        guard let item = enclosingMenuItem else { return nil }
        let name = item.title.isEmpty ? item.image?.accessibilityDescription ?? "" : item.title
        return item.state == .on ? "\(name), ticked" : name
    }
    override func accessibilityPerformPress() -> Bool {
        guard let item = enclosingMenuItem, item.isEnabled, let action = item.action else { return false }
        return NSApp.sendAction(action, to: item.target, from: item)
    }

    private func text(_ item: NSMenuItem, _ color: NSColor?) -> NSAttributedString {
        let font = NSFont.menuFont(ofSize: 0)
        let own = item.attributedTitle?.length ?? 0 > 0
            ? item.attributedTitle?.attribute(.foregroundColor, at: 0, effectiveRange: nil) as? NSColor : nil
        return NSAttributedString(string: item.title, attributes: [.font: font,
                                                                   .foregroundColor: color ?? own ?? .labelColor])
    }

    /// Widens the row to fit. It never narrows, so the menu doesn't jump around.
    func fit(_ item: NSMenuItem) {
        let width = 26 + (item.image?.size.width ?? 0) + text(item, nil).size().width + 24
        if width > frame.width { setFrameSize(NSSize(width: width.rounded(.up), height: 24)) }
        needsDisplay = true
    }

    override func draw(_ rect: NSRect) {
        guard let item = enclosingMenuItem else { return }
        let lit = item.isHighlighted && item.isEnabled
        if lit {
            NSColor.selectedContentBackgroundColor.setFill()
            NSBezierPath(roundedRect: bounds.insetBy(dx: 5, dy: 0), xRadius: 5, yRadius: 5).fill()
        }
        let color: NSColor? = lit ? .selectedMenuItemTextColor : item.isEnabled ? nil : .tertiaryLabelColor
        func write(_ string: NSAttributedString, _ x: CGFloat) {
            string.draw(at: NSPoint(x: x, y: (bounds.height - string.size().height) / 2))
        }
        if item.state == .on {
            write(NSAttributedString(string: "✓", attributes: [
                .font: NSFont.menuFont(ofSize: 0), .foregroundColor: color ?? .labelColor,
            ]), 11)
        }
        var x: CGFloat = 26
        if let image = item.image {
            let place = NSRect(x: x, y: (bounds.height - image.size.height) / 2,
                               width: image.size.width, height: image.size.height)
            // Template images are drawn in the text colour, as a normal menu item would.
            let shown = !image.isTemplate ? image : NSImage(size: image.size, flipped: false) { area in
                image.draw(in: area)
                (color ?? .labelColor).set()
                area.fill(using: .sourceAtop)
                return true
            }
            shown.draw(in: place)
            x = place.maxX
        }
        write(text(item, color), x)
    }
}

/// Yellow from 80% of the limit, orange from 90%, red from 100%.
func warningColor(_ fraction: Double) -> NSColor? {
    fraction >= 1 ? .systemRed : fraction >= 0.9 ? .systemOrange : fraction >= 0.8 ? .systemYellow : nil
}

/// The logo for the menu bar: a white chip with a sleeping moon on a purple tile. It never changes.
func napChip() -> NSImage {
    let image = NSImage(size: NSSize(width: 22, height: 18), flipped: false) { _ in
        let scale = NSAffineTransform() // draw on the logo's 100-unit grid
        scale.translateX(by: 2, yBy: 0)
        scale.scale(by: 0.18)
        scale.concat()
        NSColor(srgbRed: 0x53 / 255, green: 0x4A / 255, blue: 0xB7 / 255, alpha: 1).setFill()
        NSBezierPath(roundedRect: NSRect(x: 0, y: 0, width: 100, height: 100), xRadius: 22, yRadius: 22).fill()
        NSColor.white.set()
        let chip = NSBezierPath(roundedRect: NSRect(x: 24, y: 24, width: 52, height: 52), xRadius: 10, yRadius: 10)
        chip.lineWidth = 7
        chip.stroke()
        let pins = NSBezierPath()
        for offset in [37.0, 50, 63] {
            for (from, to) in [(NSPoint(x: offset, y: 88), NSPoint(x: offset, y: 78)),
                               (NSPoint(x: offset, y: 22), NSPoint(x: offset, y: 12)),
                               (NSPoint(x: 12, y: offset), NSPoint(x: 22, y: offset)),
                               (NSPoint(x: 78, y: offset), NSPoint(x: 88, y: offset))] {
                pins.move(to: from)
                pins.line(to: to)
            }
        }
        pins.lineWidth = 6
        pins.lineCapStyle = .round
        pins.stroke()
        let moon = CGPath(ellipseIn: CGRect(x: 33, y: 33, width: 34, height: 34), transform: nil)
            .subtracting(CGPath(ellipseIn: CGRect(x: 45, y: 41, width: 29, height: 29), transform: nil))
        NSBezierPath(cgPath: moon).fill()
        return true
    }
    image.isTemplate = false
    return image
}

/// Drawn icon showing how full the limit is (0 to 1). Below the warning colours it is
/// a template image that follows the menu bar's colour.
func gauge(_ style: String, _ fraction: Double) -> NSImage {
    if style == "nap" { return napChip() }
    let f = CGFloat(min(max(fraction, 0), 1))
    let tint = warningColor(fraction)
    let image = NSImage(size: NSSize(width: 22, height: 18), flipped: false) { _ in
        let ink = tint ?? NSColor.black, faint = ink.withAlphaComponent(0.3)
        func stroke(_ path: NSBezierPath, _ width: CGFloat, _ color: NSColor) {
            path.lineWidth = width
            path.lineCapStyle = .round
            color.setStroke()
            path.stroke()
        }
        switch style {
        case "speedometer": // empty is far left, the limit is far right
            let centre = NSPoint(x: 11, y: 4.5)
            let dial = NSBezierPath()
            dial.appendArc(withCenter: centre, radius: 9.5, startAngle: 0, endAngle: 180)
            stroke(dial, 2, ink)
            let angle = CGFloat.pi * (1 - f)
            let needle = NSBezierPath()
            needle.move(to: centre)
            needle.line(to: NSPoint(x: centre.x + 7 * cos(angle), y: centre.y + 7 * sin(angle)))
            stroke(needle, 1.5, ink)
            ink.setFill()
            NSBezierPath(ovalIn: NSRect(x: centre.x - 2, y: centre.y - 2, width: 4, height: 4)).fill()
        case "ring":
            let centre = NSPoint(x: 11, y: 9)
            stroke(NSBezierPath(ovalIn: NSRect(x: 4, y: 2, width: 14, height: 14)), 3, faint)
            if f > 0 {
                let arc = NSBezierPath()
                arc.appendArc(withCenter: centre, radius: 7, startAngle: 90, endAngle: 90 - 360 * f, clockwise: true)
                stroke(arc, 3, ink)
            }
        default: // memory chip: the middle square fills from the bottom
            stroke(NSBezierPath(roundedRect: NSRect(x: 4.5, y: 2.5, width: 13, height: 13), xRadius: 2, yRadius: 2),
                   1.2, ink)
            for offset in [5.5, 9.0, 12.5] as [CGFloat] {
                let pins = NSBezierPath()
                pins.move(to: NSPoint(x: 1.5, y: offset))
                pins.line(to: NSPoint(x: 4, y: offset))
                pins.move(to: NSPoint(x: 18, y: offset))
                pins.line(to: NSPoint(x: 20.5, y: offset))
                stroke(pins, 1.2, ink)
            }
            ink.setFill()
            NSRect(x: 6.5, y: 4.5, width: 9, height: 9 * f).fill()
        }
        return true
    }
    image.isTemplate = tint == nil
    return image
}

// Writes a picture of the drawn icons at several fill levels, for checking them.
if let flag = CommandLine.arguments.firstIndex(of: "--icons"), CommandLine.arguments.count > flag + 1 {
    let sheet = NSImage(size: NSSize(width: 576, height: 216), flipped: false) { rect in
        NSColor.white.setFill()
        rect.fill()
        for (row, style) in ["speedometer", "chip", "ring"].enumerated() {
            for (column, level) in [0, 0.25, 0.5, 0.8, 0.9, 1.1].enumerated() {
                gauge(style, level).draw(in: NSRect(x: column * 96 + 4, y: (2 - row) * 72, width: 88, height: 72))
            }
        }
        return true
    }
    let bitmap = NSBitmapImageRep(data: sheet.tiffRepresentation!)!
    try! bitmap.representation(using: .png, properties: [:])!
        .write(to: URL(fileURLWithPath: CommandLine.arguments[flag + 1]))
    exit(0)
}

final class App: NSObject, NSApplicationDelegate, NSMenuDelegate, NSTextViewDelegate {
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    let usage = NSMenuItem()
    let link = NSMenuItem()
    let tabCount = NSMenuItem()
    let freed = NSMenuItem()
    let browserItem = NSMenuItem(title: "Browser", action: nil, keyEquivalent: "")
    let browserLine = NSMenuItem.separator()
    let browserList = NSMenu()
    var rows: [BrowserRow] = []
    var menuRows: [NSMenuItem] = [] // items drawn by a MenuRow
    var peeked: Browser? // the browser under the pointer in the Browser submenu
    var shownLog: [LogEntry] = []
    let toggle = NSMenuItem(title: "Limiter on", action: #selector(flip), keyEquivalent: "")
    let pauseMenu = NSMenuItem(title: "Pause", action: nil, keyEquivalent: "")
    let resume = NSMenuItem(title: "Resume now", action: #selector(resumeNow), keyEquivalent: "")
    let typed = NSMenuItem(title: "Protect unsaved text", action: #selector(flipTyped), keyEquivalent: "")
    let busy = NSMenuItem(title: "Protect calls and uploads", action: #selector(flipBusy), keyEquivalent: "")
    let login = NSMenuItem(title: "Start at login", action: #selector(flipLogin), keyEquivalent: "")
    let neverList = NSMenu()
    var limitItems: [NSMenuItem] = []
    var intervalItems: [NSMenuItem] = []
    var graceItems: [NSMenuItem] = []
    var iconItems: [NSMenuItem] = []
    var listener: NWListener?
    var serverError = false
    var shown = 0.0 // bytes on the menu bar right now
    var target = 0.0 // bytes the menu bar is gliding towards
    var glide: Timer?
    var lastDrawn = "" // what the menu bar currently shows, to skip identical redraws
    let worker = DispatchQueue(label: "measure")
    var logWindow: NSWindow?
    var logText: NSTextView?

    var log: [LogEntry] { defaults.array(forKey: "logEntries") as? [LogEntry] ?? [] }

    /// Browsers whose extension has connected at some point, and haven't been removed.
    var known: [Browser] {
        let ids = defaults.stringArray(forKey: "browsers") ?? []
        return browsers.filter { ids.contains($0.id) }
    }
    var selected: Browser {
        let id = defaults.string(forKey: "selected")
        return known.first { $0.id == id } ?? known.first
            ?? browsers.first { $0.location != nil } ?? browsers[0]
    }
    /// What the menu and menu bar show: the previewed browser, else the chosen one.
    var current: Browser { peeked ?? selected }

    // Measuring costs CPU, so it only happens while a browser's limiter is active, off the main thread.
    func measure(_ browser: Browser, _ done: @escaping () -> Void = {}) {
        guard browser.active else {
            browser.forgetReading()
            if browser === current { stopGlide(); shown = 0 }
            refresh()
            return done()
        }
        worker.async {
            let fresh = DispatchQueue.main.sync { -browser.measured.timeIntervalSinceNow < 2 }
            let pids = fresh ? [] : browser.pids
            let before = childCPUSeconds()
            let value = fresh ? nil : totalBytes(pids)
            let cost = childCPUSeconds() - before
            let quick = fresh ? 0 : quickBytes(pids)
            DispatchQueue.main.async {
                if let value, browser.active {
                    browser.used = value
                    browser.measured = value > 0 ? Date() : .distantPast // 0: not open
                    browser.ratio = value > 0 && quick > 0 ? Double(value) / Double(quick) : 0
                    if browser === self.current { self.show(Double(value)) }
                }
                // Smoothed, so one unusual check doesn't swing the figure.
                if cost > 0 {
                    browser["checkCost"] = browser.checkCost == 0 ? cost : browser.checkCost * 0.8 + cost * 0.2
                }
                self.refresh()
                done()
            }
        }
    }

    func stopGlide() {
        glide?.invalidate()
        glide = nil
    }

    // Switches the menu and menu bar to whichever browser is now current.
    func showCurrent() {
        stopGlide()
        shown = current.active ? Double(current.used) : 0
        refresh()
        measure(current)
    }

    func peek(_ browser: Browser?) {
        guard browser !== peeked else { return }
        peeked = browser
        showCurrent()
    }

    func select(_ browser: Browser) {
        defaults.set(browser.id, forKey: "selected")
        peeked = nil
        for row in rows { row.chosen = row.browser === browser }
        showCurrent()
    }

    func remove(_ browser: Browser) {
        browser["removed"] = true
        defaults.set(known.filter { $0 !== browser }.map(\.id), forKey: "browsers")
        if defaults.string(forKey: "selected") == browser.id { defaults.removeObject(forKey: "selected") }
        peeked = nil
        if let index = rows.firstIndex(where: { $0.browser === browser }) {
            browserList.removeItem(at: index)
            rows.remove(at: index)
        }
        for row in rows { row.chosen = row.browser === selected }
        showCurrent()
    }

    // Moves the menu bar smoothly to a new reading instead of jumping.
    func show(_ bytes: Double) {
        target = bytes
        // Changes too small to see aren't animated.
        if shown == 0 || abs(bytes - shown) < 40_000_000 {
            shown = bytes
            return showIcon(shown / gb, bytes > 0)
        }
        guard glide == nil else { return }
        let timer = Timer(timeInterval: 0.1, repeats: true) { [weak self] timer in
            guard let self else { return }
            self.shown += (self.target - self.shown) * 0.4
            if abs(self.target - self.shown) < 40_000_000 {
                self.shown = self.target
                timer.invalidate()
                self.glide = nil
            }
            self.showIcon(self.shown / gb, true)
        }
        RunLoop.main.add(timer, forMode: .common)
        glide = timer
    }

    // Once a second: estimate from the quick reading. Unloading never uses this.
    func sample() {
        let browser = current
        guard browser.active, browser.ratio > 0 else { return }
        let quick = quickBytes(browser.pids)
        if quick > 0 { return show(Double(quick) * browser.ratio) }
        browser.forgetReading() // it was quit
        stopGlide()
        shown = 0
        refresh()
    }

    func menuWillOpen(_ menu: NSMenu) {
        if menu === item.menu { measure(current) }
    }

    // Leaving the submenu without clicking goes back to the chosen browser.
    func menuDidClose(_ menu: NSMenu) {
        if menu === browserList || menu === item.menu { peek(nil) }
    }

    // The browser and site lists are rebuilt each time their submenus open.
    func menuNeedsUpdate(_ menu: NSMenu) {
        if menu === browserList { return listBrowsers() }
        guard menu === neverList else { return }
        menu.removeAllItems()
        let browser = current
        let chosen = browser.never.sorted()
        let others = browser.connected ? browser.openSites.filter { !chosen.contains($0) }.sorted() : []
        for site in chosen + others {
            if site == others.first && !chosen.isEmpty { menu.addItem(.separator()) }
            let box = NSButton(checkboxWithTitle: site, target: self, action: #selector(flipSite))
            box.font = .menuFont(ofSize: 0)
            box.state = chosen.contains(site) ? .on : .off
            box.sizeToFit()
            let row = NSView(frame: NSRect(x: 0, y: 0, width: box.frame.width + 28, height: 24))
            box.setFrameOrigin(NSPoint(x: 14, y: (24 - box.frame.height) / 2))
            row.addSubview(box)
            let entry = NSMenuItem()
            entry.view = row
            menu.addItem(entry)
        }
        if others.isEmpty && !browser.connected {
            menu.addItem(NSMenuItem(title: "Open sites appear when \(browser.name) is connected", action: nil,
                                    keyEquivalent: ""))
        } else if chosen.isEmpty && others.isEmpty {
            menu.addItem(NSMenuItem(title: "No sites open", action: nil, keyEquivalent: ""))
        }
    }

    func listBrowsers() {
        browserList.removeAllItems()
        rows = known.map { browser in
            let row = BrowserRow(browser)
            row.chosen = browser === selected
            row.detail = browser.pids.isEmpty ? "(not open)" : ""
            row.onHover = { [weak self] inside in
                guard let self else { return }
                if inside { self.peek(browser) } else if self.peeked === browser { self.peek(nil) }
            }
            row.onClick = { [weak self] in self?.select(browser) }
            row.onRemove = { [weak self] in self?.remove(browser) }
            let entry = NSMenuItem()
            entry.view = row
            browserList.addItem(entry)
            return row
        }
    }

    // Settings saved when only Opera was supported become Opera's settings.
    func adoptOperaSettings() {
        guard defaults.object(forKey: "browsers") == nil,
              defaults.object(forKey: "limitGB") != nil || defaults.object(forKey: "lastExtensionCheck") != nil
        else { return }
        let opera = browsers[0]
        for key in ["enabled", "limitGB", "interval", "graceMinutes", "protectTyped", "neverSites",
                    "pausedUntil", "icon", "checkCost", "freedDay", "freedBytes"] {
            guard let value = defaults.object(forKey: key) else { continue }
            opera[key] = value
            defaults.removeObject(forKey: key)
        }
        if let seen = defaults.object(forKey: "lastExtensionCheck") { opera["lastSeen"] = seen }
        defaults.removeObject(forKey: "lastExtensionCheck")
        defaults.set([opera.id], forKey: "browsers")
        defaults.set(opera.id, forKey: "selected")
    }

    func applicationDidFinishLaunching(_ note: Notification) {
        adoptOperaSettings()

        let menu = NSMenu()
        menu.autoenablesItems = false
        for info in [usage, link, freed, tabCount] { info.isEnabled = false }
        func action(_ entry: NSMenuItem) {
            entry.target = self
            menu.addItem(entry)
        }
        func submenu(_ parent: NSMenuItem, _ child: NSMenu) {
            parent.submenu = child
            menu.addItem(parent)
        }
        func choices(_ title: String, _ options: [(String, Any)], _ selector: Selector) -> [NSMenuItem] {
            let child = NSMenu()
            let entries = options.map { name, value in
                let entry = NSMenuItem(title: name, action: selector, keyEquivalent: "")
                entry.target = self
                entry.representedObject = value
                child.addItem(entry)
                return entry
            }
            submenu(NSMenuItem(title: title, action: nil, keyEquivalent: ""), child)
            return entries
        }

        browserList.delegate = self
        submenu(browserItem, browserList)
        menu.addItem(browserLine)
        action(toggle)
        limitItems = choices("Limit", [2.0, 3.0, 4.0, 5.0, 6.0, 8.0, 10.0, 12.0].map { ("\(Int($0)) GB", $0) }, #selector(setLimit))
        intervalItems = choices("Check intervals", [10, 20, 30, 60].map { (intervalName($0), $0) },
                                #selector(setCheckInterval))
        graceItems = choices("Grace period", [("Off", 0)] + [2, 3, 5, 10].map { ("\($0) minutes", $0) },
                             #selector(setGrace))
        iconItems = choices("Icon", iconStyles.map { ($0.0, $0.1) }, #selector(setIcon))
        // Each choice shows what it looks like; the name appears on hover.
        for (entry, style) in zip(iconItems, iconStyles) {
            entry.toolTip = style.0
            if !["numbers", "text"].contains(style.1) {
                entry.title = ""
                entry.image = gauge(style.1, 0.65)
                entry.image?.accessibilityDescription = style.0
            } else if style.1 == "text" {
                entry.title = "RAM"
            }
        }
        neverList.delegate = self
        submenu(NSMenuItem(title: "Never unload", action: nil, keyEquivalent: ""), neverList)

        let pauses = NSMenu()
        pauses.autoenablesItems = false
        for (name, minutes) in [("30 minutes", 30), ("1 hour", 60), ("2 hours", 120)] {
            let entry = NSMenuItem(title: name, action: #selector(pause), keyEquivalent: "")
            entry.target = self
            entry.representedObject = minutes
            pauses.addItem(entry)
        }
        pauses.addItem(.separator())
        resume.target = self
        pauses.addItem(resume)
        submenu(pauseMenu, pauses)
        action(typed)
        action(busy)

        menu.addItem(.separator())
        menu.addItem(usage)
        menu.addItem(link)
        menu.addItem(freed)
        menu.addItem(tabCount)
        menu.addItem(.separator())
        action(login)
        action(NSMenuItem(title: "Log", action: #selector(showLog), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Quit", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        menu.delegate = self
        item.menu = menu
        // Settings keep the menu open. Log opens a window and Quit quits, so those close it.
        keepOpen(menu, except: [#selector(showLog), #selector(NSApplication.terminate(_:))])

        // Log lines saved by the earlier version have no address.
        if let old = defaults.stringArray(forKey: "log") {
            defaults.set(old.map { ["text": $0] } + log, forKey: "logEntries")
            defaults.removeObject(forKey: "log")
        }

        // Turn on start at login the first time; afterwards the menu choice is respected.
        if !defaults.bool(forKey: "loginConfigured"), (try? SMAppService.mainApp.register()) != nil {
            defaults.set(true, forKey: "loginConfigured")
        }
        defaults.set(SMAppService.mainApp.status == .enabled, forKey: "loginEnabled")

        startServer()
        measure(current)
        // Menu title and status only; keeps updating while the menu is open.
        let timer = Timer(timeInterval: 5, repeats: true) { [weak self] _ in self?.refresh() }
        RunLoop.main.add(timer, forMode: .common)
        let live = Timer(timeInterval: 1, repeats: true) { [weak self] _ in self?.sample() }
        RunLoop.main.add(live, forMode: .common)
    }

    func keepOpen(_ menu: NSMenu, except: [Selector]) {
        for entry in menu.items {
            if let child = entry.submenu {
                keepOpen(child, except: except)
            } else if let action = entry.action, !except.contains(action) {
                let row = MenuRow(frame: NSRect(x: 0, y: 0, width: 120, height: 24))
                row.autoresizingMask = .width
                entry.view = row
                menuRows.append(entry)
                row.fit(entry)
            }
        }
    }

    func intervalName(_ seconds: Int) -> String {
        seconds < 60 ? "\(seconds) seconds" : seconds == 60 ? "1 minute" : "\(seconds / 60) minutes"
    }

    func refresh() {
        let browser = current
        let used = shown / gb
        let measuredYet = browser.measured != .distantPast
        let open = !browser.pids.isEmpty
        showIcon(used, measuredYet)
        browserItem.isHidden = known.count < 2
        browserLine.isHidden = browserItem.isHidden
        let font = NSFont.menuFont(ofSize: 0)
        let title = NSMutableAttributedString(string: "Browser   ", attributes: [.font: font])
        title.append(NSAttributedString(string: browser.name, attributes: [
            .font: font, .foregroundColor: NSColor.secondaryLabelColor,
        ]))
        browserItem.attributedTitle = title
        toggle.state = browser.enabled ? .on : .off
        for entry in iconItems {
            entry.state = entry.representedObject as? String == browser.iconStyle ? .on : .off
        }
        for entry in limitItems { entry.state = entry.representedObject as? Double == browser.limitGB ? .on : .off }
        for entry in graceItems { entry.state = entry.representedObject as? Int == browser.grace ? .on : .off }
        for entry in intervalItems {
            let seconds = entry.representedObject as? Int ?? 60
            entry.state = seconds == browser.interval ? .on : .off
            // Share of this Mac's whole processor: CPU time per check, over all cores for the interval.
            let cores = Double(ProcessInfo.processInfo.processorCount)
            let percent = browser.checkCost / (Double(seconds) * cores) * 100
            entry.title = intervalName(seconds)
                + (browser.checkCost > 0 ? String(format: " (~%.1f%% CPU)", percent) : "")
        }
        pauseMenu.isEnabled = browser.enabled
        resume.isEnabled = browser.pausedUntil != nil
        pauseMenu.title = browser.pausedUntil.map {
            "Paused until " + DateFormatter.localizedString(from: $0, dateStyle: .none, timeStyle: .short)
        } ?? "Pause"
        typed.state = browser.protectTyped ? .on : .off
        busy.state = browser.protectBusy ? .on : .off
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        if !browser.active {
            usage.title = "Not measuring (limiter \(browser.enabled ? "paused" : "off"))"
        } else if !open {
            usage.title = "\(browser.name) is not open"
        } else {
            usage.title = measuredYet ? String(format: "%@ is using %.1f GB", browser.name, used) : "Measuring…"
        }
        let freedToday = browser.freedToday
        freed.title = "Memory Freed Today: " + (freedToday < gb
            ? String(format: "%.0f MB", freedToday / 1_048_576) : String(format: "%.1f GB", freedToday / gb))
        tabCount.isHidden = !browser.connected || browser.tabs == nil
        if let tabs = browser.tabs { tabCount.title = "\(tabs.loaded) of \(tabs.total) tabs loaded (at last check)" }
        if serverError {
            link.title = "Error: port \(port) unavailable"
        } else {
            link.title = "\(browser.name) extension: \(browser.connected ? "connected" : "not connected")"
        }
        for entry in menuRows { (entry.view as? MenuRow)?.fit(entry) }
    }

    // Coloured near the limit; lighter when off or paused, which also empties drawn icons.
    func showIcon(_ used: Double, _ measuredYet: Bool) {
        guard let button = item.button else { return }
        let browser = current
        let style = browser.iconStyle, limit = browser.limitGB, paused = browser.pausedUntil != nil
        let drawn = !["numbers", "text"].contains(style)
        let level = browser.active && measuredYet ? used / limit : 0
        // Redraw only when something visible changes (0.1 GB, or 1% of the dial).
        let key = "\(browser.id) \(style) \(browser.enabled) \(paused) \(measuredYet) \(Int(level * 100)) \(level > 1) "
            + String(format: "%.1f %.0f", used, limit)
        if key == lastDrawn { return }
        lastDrawn = key
        if drawn {
            button.image = gauge(style, level)
            button.title = paused && browser.enabled ? " ❚❚" : ""
        } else {
            button.image = nil
            if style == "text" {
                button.title = "RAM"
            } else { // off, paused or no reading yet: an empty reading, greyed out when off or paused
                let reading = browser.active && measuredYet
                button.title = (reading ? String(format: "%.1f", used) : "0") + String(format: "/%.0f", limit)
                    + (paused && browser.enabled ? " ❚❚" : "")
            }
        }
        button.imagePosition = !drawn ? .noImage : button.title.isEmpty ? .imageOnly : .imageLeft
        if let color = warningColor(level), !button.title.isEmpty {
            button.attributedTitle = colored(button.title, color)
        }
        updateIconChoices(level, used, measuredYet)
        button.appearsDisabled = !browser.active
        button.toolTip = !browser.active ? "\(browser.name): limiter \(browser.enabled ? "paused" : "off")"
            : String(format: "%@: %.1f of %.0f GB", browser.name, used, limit)
    }

    func colored(_ text: String, _ color: NSColor) -> NSAttributedString {
        NSAttributedString(string: text, attributes: [.foregroundColor: color, .font: NSFont.menuBarFont(ofSize: 0)])
    }

    // The choices in the Icon menu show the live level too.
    func updateIconChoices(_ level: Double, _ used: Double, _ measuredYet: Bool) {
        for entry in iconItems {
            guard let style = entry.representedObject as? String else { continue }
            if ["numbers", "text"].contains(style) {
                let title = style == "text" ? "RAM"
                    : current.active && measuredYet ? String(format: "%.1f/%.0f", used, current.limitGB)
                    : String(format: "0/%.0f", current.limitGB)
                entry.title = title
                entry.attributedTitle = warningColor(level).map { colored(title, $0) }
            } else {
                let description = entry.image?.accessibilityDescription
                entry.image = gauge(style, level)
                entry.image?.accessibilityDescription = description
            }
        }
    }

    // Every setting here belongs to the current browser.
    func change(_ key: String, _ value: Any?) {
        current[key] = value
        refresh()
        measure(current)
    }

    @objc func setIcon(_ sender: NSMenuItem) { change("icon", sender.representedObject) }
    @objc func setLimit(_ sender: NSMenuItem) { change("limitGB", sender.representedObject) }
    @objc func setCheckInterval(_ sender: NSMenuItem) { change("interval", sender.representedObject) }
    @objc func setGrace(_ sender: NSMenuItem) { change("graceMinutes", sender.representedObject) }
    @objc func flipTyped() { change("protectTyped", !current.protectTyped) }
    @objc func flipBusy() { change("protectBusy", !current.protectBusy) }
    @objc func resumeNow() { change("pausedUntil", nil) }

    @objc func flip() {
        current["pausedUntil"] = nil
        change("enabled", !current.enabled)
    }

    @objc func pause(_ sender: NSMenuItem) {
        let minutes = sender.representedObject as? Int ?? 30
        change("pausedUntil", Date().addingTimeInterval(Double(minutes) * 60))
    }

    @objc func flipSite(_ sender: NSButton) {
        var sites = current.never.filter { $0 != sender.title }
        if sender.state == .on { sites.append(sender.title) }
        current["neverSites"] = sites
    }

    @objc func flipLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            item.menu?.cancelTracking() // the message needs the menu out of the way
            DispatchQueue.main.async { NSAlert(error: error).runModal() }
        }
        refresh()
    }

    // Entries with a web address are links.
    func renderLog() {
        shownLog = log
        let plain: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 12),
                                                    .foregroundColor: NSColor.labelColor]
        let content = NSMutableAttributedString()
        if shownLog.isEmpty { content.append(NSAttributedString(string: "No tabs unloaded yet.", attributes: plain)) }
        for (index, entry) in shownLog.enumerated() {
            var style = plain
            if entry["url"] != nil { style[.link] = String(index) }
            content.append(NSAttributedString(string: entry["text"] ?? "", attributes: style))
            content.append(NSAttributedString(string: "\n", attributes: plain))
        }
        logText?.textStorage?.setAttributedString(content)
    }

    func textView(_ textView: NSTextView, clickedOnLink link: Any, at charIndex: Int) -> Bool {
        guard let index = (link as? String).flatMap({ Int($0) }), shownLog.indices.contains(index),
              let address = shownLog[index]["url"], let url = URL(string: address),
              ["http", "https"].contains(url.scheme ?? "") else { return true }
        // Entries from before other browsers were supported are Opera's.
        let id = shownLog[index]["browser"] ?? "opera"
        guard let browser = browsers.first(where: { $0.id == id }), let app = browser.location else { return true }
        if let connection = browser.waiting, -browser.lastWait.timeIntervalSinceNow < 25 {
            browser.waiting = nil
            send(connection, Jump(url: address, tab: shownLog[index]["tab"].flatMap { Int($0) }))
            NSWorkspace.shared.openApplication(at: app, configuration: NSWorkspace.OpenConfiguration())
        } else { // extension not listening: just open the page
            NSWorkspace.shared.open([url], withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration())
        }
        return true
    }

    @objc func showLog() {
        if logWindow == nil {
            let scroll = NSTextView.scrollableTextView()
            let text = scroll.documentView as! NSTextView
            text.isEditable = false
            text.delegate = self
            text.textContainerInset = NSSize(width: 8, height: 8)
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 400),
                                  styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
            window.title = "Unload Log (newest first, click to reopen)"
            window.contentView = scroll
            window.isReleasedWhenClosed = false
            window.center()
            logWindow = window
            logText = text
        }
        renderLog()
        NSApp.activate(ignoringOtherApps: true)
        logWindow?.makeKeyAndOrderFront(nil)
    }

    func record(_ report: Report, from browser: Browser) {
        if report.reloaded == true { browser["removed"] = nil }
        if browser.removed { return }
        browser["lastSeen"] = Date()
        if !known.contains(where: { $0 === browser }) {
            defaults.set(known.map(\.id) + [browser.id], forKey: "browsers")
            if defaults.string(forKey: "selected") == nil { defaults.set(browser.id, forKey: "selected") }
        }
        browser.tabs = report.tabs
        browser.openSites = Array(Set(report.sites))
        if report.unloaded.isEmpty { return }
        browser.beforeUnload = browser.used
        let format = DateFormatter()
        format.dateFormat = "MMM d, h:mm a"
        let entries = report.unloaded.reversed().map { item -> LogEntry in
            let when = format.string(from: Date(timeIntervalSince1970: item.time / 1000))
            var entry = ["text": "\(when)   \(browser.name)   \(item.title)   (\(item.site))", "browser": browser.id]
            if let url = item.url, url.hasPrefix("http") { entry["url"] = url }
            if let tab = item.tab { entry["tab"] = String(tab) }
            return entry
        }
        defaults.set(Array((entries + log).prefix(200)), forKey: "logEntries")
        renderLog()
    }

    // Loopback only, and only our extension in a supported browser is answered:
    // neither web pages nor other extensions can send its origin.
    func startServer() {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!)
        guard let server = try? NWListener(using: params) else { serverError = true; return }
        server.stateUpdateHandler = { [weak self] state in
            if case .failed = state { self?.serverError = true }
        }
        server.newConnectionHandler = { [weak self] connection in
            connection.start(queue: .main)
            self?.read(connection, Data())
        }
        server.start(queue: .main)
        listener = server
    }

    func read(_ connection: NWConnection, _ soFar: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, done, error in
            let request = soFar + (data ?? Data())
            if let split = request.range(of: Data("\r\n\r\n".utf8)) {
                let head = String(decoding: request[..<split.lowerBound], as: UTF8.self).components(separatedBy: "\r\n")
                var headers: [String: String] = [:]
                for line in head.dropFirst() {
                    let parts = line.split(separator: ":", maxSplits: 1)
                    if parts.count == 2 { headers[parts[0].lowercased()] = parts[1].trimmingCharacters(in: .whitespaces) }
                }
                guard head[0].hasPrefix("POST /status ") || head[0].hasPrefix("POST /jump "),
                      headers["origin"] == extensionOrigin, let browser = self.browser(behind: connection),
                      let length = headers["content-length"].flatMap({ Int($0) }), (0...1_000_000).contains(length)
                else { return connection.cancel() }
                let body = request[split.upperBound...]
                if body.count >= length {
                    if head[0].hasPrefix("POST /jump ") { return self.hold(connection, for: browser) }
                    guard let report = try? JSONDecoder().decode(Report.self, from: Data(body.prefix(length)))
                    else { return connection.cancel() }
                    self.record(report, from: browser)
                    return self.answer(connection, browser)
                }
            }
            if done || error != nil || request.count > 1_100_000 { return connection.cancel() }
            self.read(connection, request)
        }
    }

    // Browsers share the extension, so the process on the other end says which one this is.
    func browser(behind connection: NWConnection) -> Browser? {
        guard case let .hostPort(_, from) = connection.endpoint else { return nil }
        return browsers.first { $0.pids.contains { holdsConnection($0, from: from.rawValue) } }
    }

    // Held open until a log entry is clicked, so the jump happens at once.
    func hold(_ connection: NWConnection, for browser: Browser) {
        browser.waiting?.cancel()
        browser.waiting = connection
        browser.lastWait = Date()
        DispatchQueue.main.asyncAfter(deadline: .now() + 20) { [weak self] in
            guard let self, browser.waiting === connection else { return }
            browser.waiting = nil
            self.send(connection, [String: String]())
        }
    }

    func send<T: Encodable>(_ connection: NWConnection, _ value: T) {
        guard let body = try? JSONEncoder().encode(value) else { return connection.cancel() }
        let head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nCache-Control: no-store\r\n"
            + "Content-Length: \(body.count)\r\nConnection: close\r\n\r\n"
        connection.send(content: Data(head.utf8) + body, completion: .contentProcessed { _ in connection.cancel() })
    }

    // Approximate: the drop in the browser's memory across the unload.
    func countFreed(_ browser: Browser) {
        defer { browser.beforeUnload = 0 }
        guard browser.beforeUnload > browser.used, browser.used > 0 else { return }
        browser["freedBytes"] = browser.freedToday + Double(browser.beforeUnload - browser.used)
        browser["freedDay"] = browser.today
        refresh()
    }

    func answer(_ connection: NWConnection, _ browser: Browser) {
        func reply() {
            send(connection, Settings(
                enabled: browser.active && !browser.removed, limit: UInt64(browser.limitGB * gb), used: browser.used,
                interval: browser.interval, grace: browser.grace * 60, never: browser.never,
                protectTyped: browser.protectTyped, protectBusy: browser.protectBusy))
        }
        if browser.removed { return reply() } // nothing to measure or unload
        measure(browser) {
            self.countFreed(browser)
            reply()
        }
    }
}

let app = NSApplication.shared
let delegate = App()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
