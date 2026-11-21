import AppKit
import CoreAudio
import AudioToolbox

/// An app that is playing sound right now (helpers such as "Google Chrome Helper" are folded into their app).
struct AudioApp: Identifiable, Hashable {
    var id: String { bundleID }
    let name: String
    let bundleID: String
    let objects: [AudioObjectID]
}

enum AppAudioError: LocalizedError {
    case unsupported, noProcess, status(String, OSStatus)
    var errorDescription: String? {
        switch self {
        case .unsupported: return "Live app audio needs macOS 14.4 or newer. Song files work on this Mac."
        case .noProcess: return "That app is not playing sound any more."
        case .status(let what, let s): return "\(what) failed (\(s)). Allow MixDock in System Settings › Privacy & Security › Screen & System Audio Recording, then try again."
        }
    }
}

enum AudioHW {
    static func get<T>(_ obj: AudioObjectID, _ selector: AudioObjectPropertySelector, _ initial: T,
                       scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> T? {
        var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        var value = initial
        var size = UInt32(MemoryLayout<T>.size)
        let s = withUnsafeMutablePointer(to: &value) { AudioObjectGetPropertyData(obj, &addr, 0, nil, &size, $0) }
        return s == noErr ? value : nil
    }
    static func getArray(_ obj: AudioObjectID, _ selector: AudioObjectPropertySelector) -> [AudioObjectID] {
        var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(obj, &addr, 0, nil, &size) == noErr, size > 0 else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(obj, &addr, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }
    static func string(_ obj: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<CFString?>.size)
        var value: Unmanaged<CFString>?
        let s = withUnsafeMutablePointer(to: &value) { AudioObjectGetPropertyData(obj, &addr, 0, nil, &size, $0) }
        guard s == noErr, let v = value else { return nil }
        return v.takeRetainedValue() as String
    }
    static var defaultOutputUID: String? {
        guard let dev = get(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultOutputDevice, AudioObjectID(0)), dev != 0 else { return nil }
        return string(dev, kAudioDevicePropertyDeviceUID)
    }
}

enum AppAudioList {
    static var supported: Bool { if #available(macOS 14.4, *) { return true } else { return false } }

    /// Apps with sound playing right now, MixDock itself excluded.
    static func playing() -> [AudioApp] {
        guard #available(macOS 14.4, *) else { return [] }
        let me = Bundle.main.bundleIdentifier ?? "com.arbhlabs.mixdock"
        let running = NSWorkspace.shared.runningApplications.filter { $0.bundleIdentifier != nil }
        var groups: [String: (name: String, objs: [AudioObjectID])] = [:]
        for obj in AudioHW.getArray(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyProcessObjectList) {
            guard let out = AudioHW.get(obj, kAudioProcessPropertyIsRunningOutput, UInt32(0)), out != 0,
                  let bundle = AudioHW.string(obj, kAudioProcessPropertyBundleID), !bundle.isEmpty, bundle != me else { continue }
            // fold "com.google.Chrome.helper" into "com.google.Chrome"
            let owner = running.filter { bundle.hasPrefix($0.bundleIdentifier!) }.max { $0.bundleIdentifier!.count < $1.bundleIdentifier!.count }
            let key = owner?.bundleIdentifier ?? bundle
            let name = owner?.localizedName ?? bundle.components(separatedBy: ".").last ?? bundle
            groups[key, default: (name, [])].objs.append(obj)
        }
        return groups.map { AudioApp(name: $0.value.name, bundleID: $0.key, objects: $0.value.objs) }.sorted { $0.name < $1.name }
    }
}

/// Captures one app through a Core Audio process tap. The app's own output is muted while tapped
/// (so you only hear MixDock's processed version) and comes back the moment the tap stops, MixDock quits or crashes.
@available(macOS 14.4, *)
final class AppAudioTap {
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private let ring: StereoRing
    let app: AudioApp

    init(app: AudioApp, ring: StereoRing) throws {
        self.app = app; self.ring = ring
        guard !app.objects.isEmpty else { throw AppAudioError.noProcess }
        let desc = CATapDescription(stereoMixdownOfProcesses: app.objects)
        desc.uuid = UUID()
        desc.muteBehavior = .mutedWhenTapped
        var s = AudioHardwareCreateProcessTap(desc, &tapID)
        guard s == noErr else { throw AppAudioError.status("Audio capture", s) }

        guard let outUID = AudioHW.defaultOutputUID else { stop(); throw AppAudioError.status("Finding the output", -1) }
        let agg: [String: Any] = [
            kAudioAggregateDeviceNameKey: "MixDock tap",
            kAudioAggregateDeviceUIDKey: "com.arbhlabs.mixdock.tap.\(UUID().uuidString)",
            kAudioAggregateDeviceMainSubDeviceKey: outUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outUID]],
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapDriftCompensationKey: true, kAudioSubTapUIDKey: desc.uuid.uuidString]],
        ]
        s = AudioHardwareCreateAggregateDevice(agg as CFDictionary, &aggregateID)
        guard s == noErr else { stop(); throw AppAudioError.status("Audio capture device", s) }

        let ring = self.ring
        s = AudioDeviceCreateIOProcIDWithBlock(&procID, aggregateID, nil) { _, input, _, _, _ in
            let list = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))
            guard list.count > 0, let d0 = list[0].mData?.assumingMemoryBound(to: Float.self) else { return }
            if list.count >= 2, let d1 = list[1].mData?.assumingMemoryBound(to: Float.self) {
                ring.write(left: d0, right: d1, frames: Int(list[0].mDataByteSize) / 4, stride: 1)       // non-interleaved
            } else {
                let ch = max(1, Int(list[0].mNumberChannels))
                let frames = Int(list[0].mDataByteSize) / 4 / ch
                ring.write(left: d0, right: ch > 1 ? d0 + 1 : d0, frames: frames, stride: ch)           // interleaved
            }
        }
        guard s == noErr, let procID else { stop(); throw AppAudioError.status("Audio callback", s) }
        s = AudioDeviceStart(aggregateID, procID)
        guard s == noErr else { stop(); throw AppAudioError.status("Starting capture", s) }
    }

    /// Gives the app its own sound back.
    func stop() {
        if aggregateID != kAudioObjectUnknown {
            if let p = procID { AudioDeviceStop(aggregateID, p); AudioDeviceDestroyIOProcID(aggregateID, p) }
            AudioHardwareDestroyAggregateDevice(aggregateID)
        }
        if tapID != kAudioObjectUnknown { AudioHardwareDestroyProcessTap(tapID) }
        procID = nil; aggregateID = AudioObjectID(kAudioObjectUnknown); tapID = AudioObjectID(kAudioObjectUnknown)
    }
    deinit { stop() }
}
