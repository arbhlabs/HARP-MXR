import Foundation

/// Everything a controller (MIDI or Xbox) can drive. Deck actions need a deck (0 = A, 1 = B); global ones use deck -1.
public enum DJAction: String, Codable, CaseIterable, Sendable {
    // transport
    case play, cue, sync, tapBPM
    case hotCue1, hotCue2, hotCue3, hotCue4
    // loops / performance
    case loop, loopHalve, loopDouble, roll, echoThrow, brake, reverse
    // mixer
    case volume, gain, eqHigh, eqMid, eqLow, killHigh, killMid, killLow, filter
    case tempo, jog
    // global
    case crossfader, master, fxAmount, fxNext, shift, focusA, focusB

    public var isGlobal: Bool {
        switch self {
        case .crossfader, .master, .fxAmount, .fxNext, .shift, .focusA, .focusB: return true
        default: return false
        }
    }
    /// Faders / knobs: an absolute 0...1 value.
    public var isContinuous: Bool {
        switch self {
        case .volume, .gain, .eqHigh, .eqMid, .eqLow, .filter, .tempo, .crossfader, .master, .fxAmount, .echoThrow: return true
        default: return false
        }
    }
    /// Endless encoders / jog wheels: a signed delta.
    public var isRelative: Bool { self == .jog }

    public var label: String {
        switch self {
        case .play: return "Play / pause"
        case .cue: return "Cue"
        case .sync: return "Sync"
        case .tapBPM: return "Tap BPM"
        case .hotCue1: return "Hot cue 1"
        case .hotCue2: return "Hot cue 2"
        case .hotCue3: return "Hot cue 3"
        case .hotCue4: return "Hot cue 4"
        case .loop: return "Loop on/off"
        case .loopHalve: return "Loop ÷2"
        case .loopDouble: return "Loop ×2"
        case .roll: return "Beat roll (hold)"
        case .echoThrow: return "Echo throw"
        case .brake: return "Brake (hold)"
        case .reverse: return "Reverse (hold)"
        case .volume: return "Channel fader"
        case .gain: return "Gain / trim"
        case .eqHigh: return "EQ high"
        case .eqMid: return "EQ mid"
        case .eqLow: return "EQ low"
        case .killHigh: return "Kill high"
        case .killMid: return "Kill mid"
        case .killLow: return "Kill bass"
        case .filter: return "Filter"
        case .tempo: return "Tempo fader"
        case .jog: return "Jog wheel"
        case .crossfader: return "Crossfader"
        case .master: return "Master volume"
        case .fxAmount: return "FX amount"
        case .fxNext: return "Next FX"
        case .shift: return "Shift (hold)"
        case .focusA: return "Focus deck A"
        case .focusB: return "Focus deck B"
        }
    }
}

public struct Target: Hashable, Codable, Sendable {
    public var action: DJAction
    public var deck: Int
    public init(_ action: DJAction, deck: Int = -1) { self.action = action; self.deck = action.isGlobal ? -1 : deck }
    public var label: String { deck < 0 ? action.label : "\(deck == 0 ? "A" : "B") · \(action.label)" }
}

public enum ControlMode: String, Codable, CaseIterable, Sendable {
    case absolute          // fader / knob 0...127 (or 14-bit, or pitch bend)
    case relativeTwos      // encoder: 1...63 = +, 127...65 = − (Pioneer, Numark, Hercules jogs)
    case relativeOffset64  // encoder: 64 = rest, 65 = +1, 63 = −1 (Native Instruments, Allen & Heath)
    case button            // note / CC: ≥ 64 or note-on = down, 0 / note-off = up
}

public struct MIDIBinding: Codable, Hashable, Identifiable, Sendable {
    public var id = UUID()
    public var control: ControlID
    public var target: Target
    public var mode: ControlMode
    public var invert = false
    /// Second CC carrying the fine 7 bits of a 14-bit fader (MSB cc n, LSB cc n+32). Found automatically.
    public var lsb: UInt8?
    /// Only active while Shift is held.
    public var shiftLayer = false
    public init(control: ControlID, target: Target, mode: ControlMode, invert: Bool = false, lsb: UInt8? = nil, shiftLayer: Bool = false) {
        self.control = control; self.target = target; self.mode = mode; self.invert = invert; self.lsb = lsb; self.shiftLayer = shiftLayer
    }
}

public enum ControlEvent: Equatable, Sendable {
    case value(Target, Double)   // 0...1
    case delta(Target, Double)   // encoder ticks, + = clockwise
    case press(Target, Bool)     // down / up
}

/// MIDI → actions, with click-then-move learn. Works with any class-compliant USB controller: nothing is hard-coded.
public final class MappingEngine {
    public private(set) var bindings: [MIDIBinding]
    public private(set) var shiftHeld = false
    public private(set) var learning: (target: Target, shift: Bool)?
    private var msb: [ControlID: Int] = [:]
    private var activePress: [ControlID: MIDIBinding] = [:]

    public init(bindings: [MIDIBinding] = []) { self.bindings = bindings }

    public func replace(_ b: [MIDIBinding]) { bindings = b; msb.removeAll(); activePress.removeAll(); shiftHeld = false }
    public func remove(_ id: UUID) { bindings.removeAll { $0.id == id } }
    public func update(_ b: MIDIBinding) { if let i = bindings.firstIndex(where: { $0.id == b.id }) { bindings[i] = b } }
    public func startLearn(_ target: Target, shift: Bool = false) { learning = (target, shift) }
    public func cancelLearn() { learning = nil }

    /// Picks the mode for a freshly learned control from the message that arrived and what the target is.
    public static func defaultMode(for target: Target, message m: MIDIMessage) -> ControlMode {
        if m.kind == .note { return .button }
        if m.kind == .pitchBend { return .absolute }
        if target.action.isRelative {
            if (57...71).contains(m.value) { return .relativeOffset64 }
            return .relativeTwos
        }
        return target.action.isContinuous ? .absolute : .button
    }

    /// Returns the learned binding when this message completed a learn.
    @discardableResult
    public func learn(_ m: MIDIMessage) -> MIDIBinding? {
        guard let l = learning else { return nil }
        if m.kind == .note && m.isNoteOff { return nil }                 // learn on the press, not the release
        if m.kind == .cc, m.number >= 32, m.number < 64,                 // the fine half of a 14-bit fader we already know
           bindings.contains(where: { $0.control == ControlID(kind: .cc, channel: m.channel, number: m.number - 32) && $0.mode == .absolute }) { return nil }
        let b = MIDIBinding(control: m.control, target: l.target, mode: Self.defaultMode(for: l.target, message: m), shiftLayer: l.shift)
        bindings.removeAll { ($0.control == b.control && $0.shiftLayer == b.shiftLayer) || ($0.target == b.target && $0.shiftLayer == b.shiftLayer) }
        bindings.append(b)
        learning = nil
        return b
    }

    public func handle(_ m: MIDIMessage) -> [ControlEvent] {
        if learning != nil { learn(m); return [] }
        let id = m.control
        // 14-bit LSB of an absolute fader
        if m.kind == .cc, m.number >= 32, m.number < 64 {
            let msbID = ControlID(kind: .cc, channel: m.channel, number: m.number - 32)
            if let i = bindings.firstIndex(where: { $0.control == msbID && $0.mode == .absolute }), !bindings.contains(where: { $0.control == id }) {
                if bindings[i].lsb == nil { bindings[i].lsb = m.number }
                guard let hi = msb[msbID] else { return [] }
                let b = bindings[i]
                let v = Double(hi << 7 | m.value) / 16383
                return [.value(b.target, b.invert ? 1 - v : v)]
            }
        }
        // a button release goes to whatever its press triggered, even if Shift changed in between
        let isRelease = (m.kind == .note && m.isNoteOff)
        if isRelease || (m.kind == .cc && m.value < 64 && activePress[id] != nil) {
            guard let b = activePress.removeValue(forKey: id) else { return [] }
            if b.target.action == .shift { shiftHeld = false }
            return [.press(b.target, false)]
        }
        let candidates = bindings.filter { $0.control == id }
        guard !candidates.isEmpty else { return [] }
        // shift layer first while Shift is held; fall back to the normal layer
        let layer = candidates.filter { $0.shiftLayer == shiftHeld }
        let chosen = layer.isEmpty ? candidates.filter { !$0.shiftLayer } : layer
        var out: [ControlEvent] = []
        for b in chosen {
            switch b.mode {
            case .button:
                let down = m.kind == .note ? !m.isNoteOff : m.value >= 64
                guard down else { continue }
                activePress[id] = b
                if b.target.action == .shift { shiftHeld = true }
                out.append(.press(b.target, true))
            case .absolute:
                var v: Double
                switch m.kind {
                case .pitchBend: v = Double(m.value) / 16383
                case .cc:
                    msb[id] = m.value
                    v = Double(m.value) / 127
                case .note: v = Double(m.value) / 127
                }
                if b.invert { v = 1 - v }
                out.append(.value(b.target, v))
            case .relativeTwos, .relativeOffset64:
                var d = Double(Self.decode(m.value, b.mode))
                if b.invert { d = -d }
                if d != 0 { out.append(.delta(b.target, d)) }
            }
        }
        return out
    }

    public static func decode(_ v: Int, _ mode: ControlMode) -> Int {
        switch mode {
        case .relativeTwos: return v < 64 ? v : v - 128
        case .relativeOffset64: return v - 64
        default: return 0
        }
    }
}

/// What gets saved per controller (keyed by the device name macOS reports).
public struct MappingFile: Codable, Sendable {
    public var device: String
    public var bindings: [MIDIBinding]
    public init(device: String, bindings: [MIDIBinding]) { self.device = device; self.bindings = bindings }
}
