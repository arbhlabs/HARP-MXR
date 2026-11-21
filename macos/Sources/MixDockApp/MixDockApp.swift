import SwiftUI
import AppKit

enum AppLog {
    static let url: URL = {
        let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/MixDock", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("mixdock.log")
    }()

    static func write(_ msg: String) {
        let ts = ISO8601DateFormatter().string(from: Date())
        let line = "[\(ts)] \(msg)\n"
        print(line, terminator: "")
        guard let data = line.data(using: .utf8) else { return }
        if FileManager.default.fileExists(atPath: url.path), let fh = try? FileHandle(forWritingTo: url) {
            defer { try? fh.close() }
            _ = try? fh.seekToEnd()
            try? fh.write(contentsOf: data)
        } else {
            try? data.write(to: url, options: .atomic)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillFinishLaunching(_ notification: Notification) {
        let v = ProcessInfo.processInfo.operatingSystemVersionString
        let bundleVer = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev"
        AppLog.write("MixDock \(bundleVer) launching on macOS \(v) · args=\(CommandLine.arguments.joined(separator: " "))")
        NSApp.setActivationPolicy(.regular)       // also when run as a bare binary during development
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
            NSApp.activate(ignoringOtherApps: true)
        for w in NSApp.windows {
            if let screen = w.screen ?? NSScreen.main {
                let vf = screen.visibleFrame
                var f = w.frame
                f.size.width = min(f.size.width, vf.size.width - 16)
                f.size.height = min(f.size.height, vf.size.height - 16)
                f.origin.x = vf.origin.x + (vf.size.width - f.size.width) / 2
                f.origin.y = vf.origin.y + (vf.size.height - f.size.height) / 2
                w.setFrame(f, display: true)
            }
            w.makeKeyAndOrderFront(nil)
        }
            AppLog.write("Window activation complete (windows=\(NSApp.windows.count))")
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        for w in NSApp.windows { w.makeKeyAndOrderFront(nil) }
        return true
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

@main
struct MixDockApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var console = Console()
    private let isSmoke = CommandLine.arguments.contains("--smoke")

    var body: some Scene {
        WindowGroup("MixDock") {
            ConsoleView()
                .environmentObject(console)
                .frame(minWidth: 1000, minHeight: 620)
                .preferredColorScheme(.dark)
                .onAppear {
                    console.startIfNeeded()
                    if isSmoke { console.runSmokeTest() }
                }
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 1120, height: 680)
    }
}
