import Foundation

public enum MixMath {
    /// Equal-power crossfader: 0 = only A, 0.5 = both at −3 dB, 1 = only B. Constant loudness across the fade.
    public static func crossfader(_ x: Double) -> (a: Double, b: Double) {
        let t = min(1, max(0, x)) * Double.pi / 2
        return (cos(t), sin(t))
    }

    /// EQ knob 0...1 → dB: centre 0 dB, full left −26 dB (near kill), full right +6 dB.
    public static func eqDB(_ v: Double) -> Double {
        let x = min(1, max(0, v))
        return x < 0.5 ? (x * 2 - 1) * 26 : (x * 2 - 1) * 6
    }

    public enum FilterShape: Equatable { case off, lowPass(Double), highPass(Double) }

    /// One-knob DJ filter: centre (±3 %) = off; left = low-pass sweeping 20 kHz → 120 Hz; right = high-pass 20 Hz → 6 kHz.
    /// Exponential so every millimetre sounds like the same musical step.
    public static func filter(_ v: Double) -> FilterShape {
        let x = min(1, max(0, v))
        if abs(x - 0.5) <= 0.03 { return .off }
        if x < 0.5 {
            let t = (0.47 - x) / 0.47                 // 0 at the dead zone edge, 1 at full left
            return .lowPass(20000 * pow(120.0 / 20000, t))
        }
        let t = (x - 0.53) / 0.47
        return .highPass(20 * pow(6000.0 / 20, t))
    }

    /// Tempo fader 0...1 → playback rate, ±range (0.08 = ±8 %). Centre = 1.0.
    public static func tempoRate(_ v: Double, range: Double = 0.08) -> Double {
        1 + (min(1, max(0, v)) - 0.5) * 2 * range
    }

    /// Rate that makes `bpm` play at `targetBPM`, clamped to ±range. Picks half/double time when that is closer.
    public static func syncRate(bpm: Double, targetBPM: Double, range: Double = 0.16) -> Double? {
        guard bpm > 0, targetBPM > 0 else { return nil }
        let candidates = [targetBPM, targetBPM * 2, targetBPM / 2].map { $0 / bpm }
        guard let best = candidates.min(by: { abs($0 - 1) < abs($1 - 1) }), abs(best - 1) <= range else { return nil }
        return best
    }

    /// Beat roll length from trigger depth: light press 1/4 beat … full press 1/32.
    public static func rollBeats(_ depth: Double) -> Double {
        let d = min(1, max(0, depth))
        if d < 0.35 { return 0.25 }
        if d < 0.6 { return 0.125 }
        if d < 0.85 { return 0.0625 }
        return 0.03125
    }

    public static func beatsLabel(_ beats: Double) -> String {
        if beats >= 1 { return beats == beats.rounded() ? "\(Int(beats))" : String(format: "%.1f", beats) }
        return "1/\(Int((1 / beats).rounded()))"
    }
}

/// Tap tempo: average of the last taps; a pause over 2 s starts over.
public struct TapTempo: Sendable {
    private var taps: [Double] = []
    public private(set) var bpm: Double = 0
    public init() {}
    public mutating func tap(at t: Double) -> Double {
        if let last = taps.last, t - last > 2 { taps.removeAll() }
        taps.append(t)
        if taps.count > 8 { taps.removeFirst() }
        if taps.count >= 2 {
            let span = taps.last! - taps.first!
            bpm = 60 * Double(taps.count - 1) / span
        }
        return taps.count >= 2 ? bpm : 0
    }
}
