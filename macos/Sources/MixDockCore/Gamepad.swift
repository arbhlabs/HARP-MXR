import Foundation

/// Xbox buttons as the Mac reports them (GameController framework names in comments).
public struct PadButtons: OptionSet, Hashable, Sendable {
    public let rawValue: UInt32
    public init(rawValue: UInt32) { self.rawValue = rawValue }
    public static let a = PadButtons(rawValue: 1 << 0)
    public static let b = PadButtons(rawValue: 1 << 1)
    public static let x = PadButtons(rawValue: 1 << 2)
    public static let y = PadButtons(rawValue: 1 << 3)
    public static let lb = PadButtons(rawValue: 1 << 4)
    public static let rb = PadButtons(rawValue: 1 << 5)
    public static let up = PadButtons(rawValue: 1 << 6)
    public static let down = PadButtons(rawValue: 1 << 7)
    public static let left = PadButtons(rawValue: 1 << 8)
    public static let right = PadButtons(rawValue: 1 << 9)
    public static let l3 = PadButtons(rawValue: 1 << 10)
    public static let r3 = PadButtons(rawValue: 1 << 11)
    public static let view = PadButtons(rawValue: 1 << 12)   // buttonOptions (⧉)
    public static let menu = PadButtons(rawValue: 1 << 13)   // buttonMenu (≡)
    public static let all: [PadButtons] = [.a, .b, .x, .y, .lb, .rb, .up, .down, .left, .right, .l3, .r3, .view, .menu]
}

/// One controller snapshot. Sticks −1...1 (up = +), triggers 0...1.
public struct PadState: Equatable, Sendable {
    public var lx = 0.0, ly = 0.0, rx = 0.0, ry = 0.0, lt = 0.0, rt = 0.0
    public var buttons: PadButtons = []
    public init(lx: Double = 0, ly: Double = 0, rx: Double = 0, ry: Double = 0, lt: Double = 0, rt: Double = 0, buttons: PadButtons = []) {
        self.lx = lx; self.ly = ly; self.rx = rx; self.ry = ry; self.lt = lt; self.rt = rt; self.buttons = buttons
    }
}

/// What the Xbox controller asks the console to do this frame.
public enum PadCommand: Equatable, Sendable {
    case focus(Int)
    case crossfaderBy(Double)                 // change this frame (rate mode: stops dead at rest)
    case crossfaderCenter
    case filterSpring(deck: Int, Double?)     // −1...1 offset from the knob, nil = released (glide back)
    case filterReset(deck: Int)
    case fx(deck: Int, x: Double, y: Double, amount: Double)
    case echoThrow(deck: Int, Double)
    case roll(deck: Int, beats: Double?)      // nil = off
    case brake(deck: Int, Bool)
    case reverse(deck: Int, Bool)
    case kill(deck: Int, band: Int)           // 0 low, 1 mid, 2 high (toggle)
    case loop(deck: Int)
    case loopSize(deck: Int, double: Bool)
    case fxNext
    case tapBPM(deck: Int)
    case sync(deck: Int)
    case playPause(deck: Int)
    case hotCue(deck: Int, Int)
    case panic
}

/// The Live DJ layout from MixDock for Windows, same feel:
///   L-stick ↕ filter (springs back) · L-stick ↔ crossfader · R-stick FX pad · LT echo throw · RT beat roll (depth = speed)
///   A bass kill · B loop · X roll 1/4 (hold) · Y roll 1/8 (hold) · LB / RB focus deck A / B
///   D-pad ↑↓ loop ×2 ÷2 · ←→ mid / high kill · L3 filter reset · R3 next FX · Menu sync · View tap = tap BPM
///   Hold View = Shift: A–Y hot cues 1–4 · LT brake · RT reverse · Menu play/pause · RB crossfader centre
///   Hold View + Menu ½ s = kill all audio.
public final class GamepadMapper {
    public static let stickDeadzone = 0.15, filterDeadzone = 0.22, filterExpo = 0.6
    public static let tapMaxMs = 350.0, panicHoldMs = 500.0
    public private(set) var focus = 0
    public private(set) var shift = false

    private var prev: PadButtons = []
    private var viewDownAt = -1.0, viewUsed = false, menuUsedAsChord = false
    private var panicSince = -1.0, panicFired = false
    private var rollOn: [Double?] = [nil, nil], rollOwner = 0
    private var holdRoll: [PadButtons: Int] = [:]
    private var filterOwner: Int?, filterSmoothed = 0.0
    private var brakeOn = false, reverseOn = false, brakeDeck = 0, reverseDeck = 0
    private var echoOwner: Int?, fxOwner: Int?

    public init() {}

    public func reset(_ held: PadButtons) { prev = held; viewDownAt = -1; panicSince = -1; panicFired = false; shift = false }

    /// Radial deadzone with rescale: inside = exactly 0, outside 0...1 with no jump.
    public static func stick(_ x: Double, _ y: Double, deadzone: Double = stickDeadzone) -> (Double, Double) {
        let m = (x * x + y * y).squareRoot()
        guard m > deadzone else { return (0, 0) }
        let s = min(1, (m - deadzone) / (1 - deadzone))
        return (x / m * s, y / m * s)
    }
    /// The PC's gentle filter curve: bigger deadzone + expo so small moves are subtle.
    public static func filterCurve(_ v: Double) -> Double {
        let a = abs(v)
        guard a > filterDeadzone else { return 0 }
        let x = min(1, (a - filterDeadzone) / (1 - filterDeadzone))
        return (v < 0 ? -1 : 1) * (filterExpo * x * x * x + (1 - filterExpo) * x)
    }

    public func update(_ s: PadState, dt: Double, now: Double) -> [PadCommand] {
        var out: [PadCommand] = []
        let held = s.buttons
        let pressed = held.subtracting(prev), released = prev.subtracting(held)

        // ---- View: hold = Shift, quick tap alone = tap BPM; View+Menu held = panic
        if pressed.contains(.view) { viewDownAt = now; viewUsed = false }
        shift = held.contains(.view)
        if held.contains(.view) && held.contains(.menu) {
            if panicSince < 0 { panicSince = now }
            viewUsed = true; menuUsedAsChord = true
            if !panicFired && now - panicSince >= Self.panicHoldMs { panicFired = true; out.append(.panic) }
        } else { panicSince = -1; panicFired = false }
        if released.contains(.view) {
            if !viewUsed && viewDownAt >= 0 && now - viewDownAt <= Self.tapMaxMs { out.append(.tapBPM(deck: focus)) }
            viewDownAt = -1
        }

        // ---- buttons
        for b in PadButtons.all where pressed.contains(b) {
            if b == .view { continue }
            if shift { viewUsed = true }
            if b == .menu { continue }   // Menu fires on release so View+Menu (panic) never syncs
            out.append(contentsOf: press(b, shift: shift))
        }
        if released.contains(.menu) {
            if !menuUsedAsChord { out.append(shift ? .playPause(deck: focus) : .sync(deck: focus)) }
            menuUsedAsChord = false
        }
        for (b, d) in holdRoll where released.contains(b) {
            holdRoll[b] = nil
            if holdRoll.isEmpty && s.rt <= 0.1 { out.append(.roll(deck: d, beats: nil)); rollOn[d] = nil }
        }

        // ---- left stick: ↕ filter spring, ↔ crossfader (axis lock: only the dominant direction counts)
        let (lx, ly) = Self.stick(s.lx, s.ly)
        let fy = abs(ly) >= abs(lx) * 1.25 ? ly : 0, fx = abs(lx) >= abs(ly) * 1.25 ? lx : 0
        let target = Self.filterCurve(fy)
        filterSmoothed += (target - filterSmoothed) * (1 - exp(-dt / 0.06))
        if target == 0 && abs(filterSmoothed) < 0.01 { filterSmoothed = 0 }
        if filterSmoothed != 0 || target != 0 {
            if filterOwner == nil { filterOwner = focus }
            out.append(.filterSpring(deck: filterOwner!, filterSmoothed))
        } else if let o = filterOwner { out.append(.filterSpring(deck: o, nil)); filterOwner = nil }
        if fx != 0 { out.append(.crossfaderBy(fx * abs(fx) * 0.6 * dt)) }   // full tilt ≈ 1.7 s end to end

        // ---- right stick: FX pad (amount = how far, x/y = character)
        let (rx, ry) = Self.stick(s.rx, s.ry, deadzone: 0.2)
        let amount = min(1, (rx * rx + ry * ry).squareRoot())
        if amount > 0 {
            if fxOwner == nil { fxOwner = focus }
            out.append(.fx(deck: fxOwner!, x: (rx + 1) / 2, y: (ry + 1) / 2, amount: amount))
        } else if let o = fxOwner { out.append(.fx(deck: o, x: 0.5, y: 0.5, amount: 0)); fxOwner = nil }

        // ---- triggers: LT echo throw / brake (shift); RT beat roll / reverse (shift)
        let lt = s.lt > 0.08 ? s.lt : 0, rt = s.rt > 0.08 ? s.rt : 0
        if shift || brakeOn {
            let on = lt > 0.5
            if on != brakeOn { brakeOn = on; if on { brakeDeck = focus }; out.append(.brake(deck: brakeDeck, on)) }
        }
        if !shift || echoOwner != nil {
            if lt > 0 && !brakeOn { if echoOwner == nil { echoOwner = focus }; out.append(.echoThrow(deck: echoOwner!, lt)) }
            else if let o = echoOwner { out.append(.echoThrow(deck: o, 0)); echoOwner = nil }
        }
        if shift || reverseOn {
            let on = rt > 0.3
            if on != reverseOn { reverseOn = on; if on { reverseDeck = focus }; out.append(.reverse(deck: reverseDeck, on)) }
        }
        if !reverseOn && !(shift && rt > 0) {
            if rt > 0.1 {
                let beats = MixMath.rollBeats(rt)
                if rollOn[rollOwner] == nil && holdRoll.isEmpty { rollOwner = focus }
                if rollOn[rollOwner] != beats { rollOn[rollOwner] = beats; out.append(.roll(deck: rollOwner, beats: beats)) }
            } else if rollOn[rollOwner] != nil && holdRoll.isEmpty {
                rollOn[rollOwner] = nil; out.append(.roll(deck: rollOwner, beats: nil))
            }
        }
        prev = held
        return out
    }

    private func press(_ b: PadButtons, shift: Bool) -> [PadCommand] {
        let d = focus
        if shift {
            switch b {
            case .a: return [.hotCue(deck: d, 0)]
            case .b: return [.hotCue(deck: d, 1)]
            case .x: return [.hotCue(deck: d, 2)]
            case .y: return [.hotCue(deck: d, 3)]
            case .rb: return [.crossfaderCenter]
            case .lb: focus = 0; return [.focus(0)]
            default: break   // the rest keep their normal job under Shift
            }
        }
        switch b {
        case .a: return [.kill(deck: d, band: 0)]
        case .b: return [.loop(deck: d)]
        case .x: holdRoll[.x] = d; rollOn[d] = 0.25; rollOwner = d; return [.roll(deck: d, beats: 0.25)]
        case .y: holdRoll[.y] = d; rollOn[d] = 0.125; rollOwner = d; return [.roll(deck: d, beats: 0.125)]
        case .lb: focus = 0; return [.focus(0)]
        case .rb: focus = 1; return [.focus(1)]
        case .up: return [.loopSize(deck: d, double: true)]
        case .down: return [.loopSize(deck: d, double: false)]
        case .left: return [.kill(deck: d, band: 1)]
        case .right: return [.kill(deck: d, band: 2)]
        case .l3: return [.filterReset(deck: d)]
        case .r3: return [.fxNext]
        default: return []
        }
    }
}
