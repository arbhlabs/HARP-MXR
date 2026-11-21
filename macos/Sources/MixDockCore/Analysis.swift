import Foundation

public struct BeatGrid: Equatable, Sendable {
    public var bpm: Double
    /// Time of the first beat in seconds from the start of the analysed audio.
    public var offset: Double
    public init(bpm: Double, offset: Double) { self.bpm = bpm; self.offset = offset }
    public var period: Double { 60 / bpm }
    /// 0..<1 position inside the current beat at time t.
    public func phase(at t: Double) -> Double {
        let p = (t - offset) / period
        return p - floor(p)
    }
}

public enum BPMDetector {
    static let envRate = 200.0   // onset envelope sample rate (Hz)

    /// Mono samples → tempo + first beat. nil when there is no steady beat (silence, ambient).
    /// Onset envelope → autocorrelation for the rough lag → fine comb search ±2 BPM at 0.02 BPM steps.
    public static func detect(_ samples: [Float], sampleRate: Double, minBPM: Double = 78, maxBPM: Double = 180) -> BeatGrid? {
        let hop = max(1, Int(sampleRate / envRate))
        let rate = sampleRate / Double(hop)                                 // the real envelope rate (hop is rounded)
        let frames = samples.count / hop
        guard frames > Int(rate * 4) else { return nil }               // need at least 4 s
        var energy = [Double](repeating: 0, count: frames)
        for f in 0..<frames {
            var s = 0.0
            let base = f * hop
            for i in 0..<hop { let v = Double(samples[base + i]); s += v * v }
            energy[f] = sqrt(s / Double(hop))
        }
        var onset = [Double](repeating: 0, count: frames)
        for f in 1..<frames { onset[f] = max(0, energy[f] - energy[f - 1]) }
        let mean = onset.reduce(0, +) / Double(frames)
        guard mean > 1e-6 else { return nil }
        for f in 0..<frames { onset[f] = max(0, onset[f] - mean) }

        // coarse: autocorrelation over the lag range
        let minLag = Int(60 * rate / maxBPM), maxLag = min(frames / 2, Int(60 * rate / minBPM) + 1)
        var scores = [Double](repeating: 0, count: maxLag + 1)
        var bestLag = 0, bestScore = 0.0
        for lag in minLag...maxLag {
            var s = 0.0
            for f in 0..<(frames - lag) { s += onset[f] * onset[f + lag] }
            s /= Double(frames - lag)
            scores[lag] = s
            if s > bestScore { bestScore = s; bestLag = lag }
        }
        guard bestLag > 0, bestScore > 0 else { return nil }
        // a beat also correlates at 2x its period: prefer the fastest lag that scores nearly as well (174 not 87)
        if let fast = (minLag...maxLag).first(where: { scores[$0] >= bestScore * 0.85 && Double(bestLag) / Double($0) > 1.8 }) { bestLag = fast }
        let rough = 60 * rate / Double(bestLag)

        // fine: comb over the whole track, best (bpm, offset)
        var best = BeatGrid(bpm: rough, offset: 0), bestComb = -1.0
        var bpm = rough - 2
        while bpm <= rough + 2 {
            let period = 60 * rate / bpm
            let steps = Int(period)
            var off = 0
            while off < steps {
                var s = 0.0, t = Double(off)
                while t < Double(frames - 1) {
                    let i = Int(t), fr = t - Double(i)
                    s += onset[i] * (1 - fr) + onset[i + 1] * fr
                    t += period
                }
                if s > bestComb { bestComb = s; best = BeatGrid(bpm: bpm, offset: Double(off) / rate) }
                off += 2
            }
            bpm += 0.02
        }
        // round to 0.01 so a 128.00 track reads 128.00
        best.bpm = (best.bpm * 100).rounded() / 100
        return best
    }
}

public enum Waveform {
    /// Peak per bucket (0...1) for the overview strip.
    public static func peaks(_ samples: [Float], buckets: Int) -> [Float] {
        guard buckets > 0, !samples.isEmpty else { return [] }
        let per = max(1, samples.count / buckets)
        var out = [Float](repeating: 0, count: buckets)
        for b in 0..<buckets {
            let start = b * per
            guard start < samples.count else { break }
            let end = min(samples.count, start + per)
            var m: Float = 0
            var i = start
            while i < end { m = max(m, abs(samples[i])); i += 1 }
            out[b] = min(1, m)
        }
        return out
    }
}
