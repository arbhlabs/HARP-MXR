// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "MixDock",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "MixDock", targets: ["MixDockApp"])],
    targets: [
        // pure logic: MIDI parsing, controller mapping + learn, mix math, BPM / waveform analysis (unit tested)
        .target(name: "MixDockCore"),
        // the app: AVAudioEngine decks, CoreMIDI, SwiftUI console
        .executableTarget(name: "MixDockApp", dependencies: ["MixDockCore"]),
        .testTarget(name: "MixDockCoreTests", dependencies: ["MixDockCore"]),
    ]
)
