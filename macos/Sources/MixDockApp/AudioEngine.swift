import AVFoundation
import MixDockCore
import os

/// Tiny wrapper so the audio thread never takes a heavyweight lock.
final class UnfairLock {
    private let p: UnsafeMutablePointer<os_unfair_lock>
    init() { p = .allocate(capacity: 1); p.initialize(to: os_unfair_lock()) }
    deinit { p.deinitialize(count: 1); p.deallocate() }
    @inline(__always) func lock() { os_unfair_lock_lock(p) }
    @inline(__always) func unlock() { os_unfair_lock_unlock(p) }
    @inline(__always) func with<T>(_ f: () throws -> T) rethrows -> T { lock(); defer { unlock() }; return try f() }
}

/// Stereo FIFO between the app-audio tap thread (writer) and the engine render thread (reader).
/// Keeps latency bounded: if the reader falls behind it skips ahead instead of drifting.
final class StereoRing {
    private var l: [Float], r: [Float]
    private let cap: Int, maxFill: Int, targetFill: Int
    private var w = 0, rd = 0
    private let lock = UnfairLock()
    init(sampleRate: Double) {
        cap = Int(sampleRate * 2); maxFill = Int(sampleRate * 0.2); targetFill = Int(sampleRate * 0.04)
        l = [Float](repeating: 0, count: cap); r = l
    }
    func write(left: UnsafePointer<Float>, right: UnsafePointer<Float>, frames: Int, stride s: Int) {
        lock.with {
            for i in 0..<frames {
                l[w % cap] = left[i * s]; r[w % cap] = right[i * s]; w += 1
            }
            if w - rd > maxFill { rd = w - targetFill }
        }
    }
    /// Returns how many real frames were read; the rest is silence.
    func read(_ L: UnsafeMutablePointer<Float>, _ R: UnsafeMutablePointer<Float>, frames n: Int) -> Int {
        lock.with {
            let avail = min(n, w - rd)
            for i in 0..<avail { L[i] = l[(rd + i) % cap]; R[i] = r[(rd + i) % cap] }
            for i in max(0, avail)..<n { L[i] = 0; R[i] = 0 }
            rd += max(0, avail)
            return max(0, avail)
        }
    }
    func reset() { lock.with { rd = w } }
}

/// One deck's sound source + its performance tricks, rendered on the audio thread.
/// Everything the deck plays also goes into a 12 s history, so loops, rolls, reverse and brake
/// work the same on a song file and on live app audio (Spotify, a browser).
final class DeckCore {
    enum Effect { case none, roll(start: Int, len: Int), reverse(pos: Int), brake(pos: Double, speed: Double) }
    let sr: Double
    private let lock = UnfairLock()
    // file source
    private var fileL: [Float] = [], fileR: [Float] = []
    private var fileStep = 1.0                        // file sample rate / engine sample rate
    private(set) var fileSampleRate = 0.0
    private var pos = 0.0                             // in file frames
    private var playing = false
    private var rate = 1.0, nudge = 0.0
    // live source
    private var live: StereoRing?
    private var scratchL = [Float](repeating: 0, count: 8192), scratchR = [Float](repeating: 0, count: 8192)
    // history (engine-rate, absolute frame counter hw)
    private let histCap: Int
    private var hL: [Float], hR: [Float]
    private var hw = 0
    private var effect = Effect.none
    private var peakValue: Float = 0

    init(sampleRate: Double) {
        sr = sampleRate; histCap = Int(sampleRate * 12)
        hL = [Float](repeating: 0, count: histCap); hR = hL
    }

    // ---- control (main thread)
    func loadFile(left: [Float], right: [Float], sampleRate: Double) {
        lock.with { fileL = left; fileR = right; fileSampleRate = sampleRate; fileStep = sampleRate / sr; pos = 0; playing = true; live = nil; effect = .none }
    }
    func setLive(_ ring: StereoRing?) { lock.with { live = ring; fileL = []; fileR = []; playing = false; effect = .none; fileSampleRate = 0 } }
    func clear() { setLive(nil) }
    var isFile: Bool { lock.with { !fileL.isEmpty } }
    var isLive: Bool { lock.with { live != nil } }
    var isPlaying: Bool { lock.with { playing } }
    func setPlaying(_ p: Bool) { lock.with { if !fileL.isEmpty { playing = p } } }
    func setRate(_ r: Double) { lock.with { rate = r } }
    func setNudge(_ n: Double) { lock.with { nudge = n } }
    /// Seconds into the file (0 for live).
    var position: Double { lock.with { fileSampleRate > 0 ? pos / fileSampleRate : 0 } }
    var duration: Double { lock.with { fileSampleRate > 0 ? Double(fileL.count) / fileSampleRate : 0 } }
    func seek(seconds: Double) { lock.with { if fileSampleRate > 0 { pos = max(0, min(Double(fileL.count - 2), seconds * fileSampleRate)) } } }
    func scrub(seconds: Double) { lock.with { if fileSampleRate > 0 { pos = max(0, min(Double(fileL.count - 2), pos + seconds * fileSampleRate)) } } }
    func takePeak() -> Float { lock.with { let p = peakValue; peakValue = 0; return p } }

    /// Loop the last `frames` of what was heard (slip: the song keeps running underneath). nil = back to live.
    func setRoll(frames: Int?) {
        lock.with {
            guard let f = frames, f > 32 else { if case .roll = effect { effect = .none }; return }
            let len = min(f, histCap - 1)
            if case .roll(let start, _) = effect { effect = .roll(start: start, len: len) }   // resize keeps the start
            else { effect = .roll(start: hw, len: len) }
        }
    }
    func setReverse(_ on: Bool) { lock.with { if on { effect = .reverse(pos: hw - 1) } else if case .reverse = effect { effect = .none } } }
    func setBrake(_ on: Bool) { lock.with { if on { effect = .brake(pos: Double(hw - 1), speed: 1) } else if case .brake = effect { effect = .none } } }

    /// Last `seconds` of heard audio as mono at half rate, for live BPM detection.
    func recentMono(seconds: Double) -> [Float] {
        lock.with {
            let n = min(Int(seconds * sr), histCap - 1, hw)
            var out = [Float](); out.reserveCapacity(n / 2)
            var i = hw - n
            while i < hw { out.append((hL[i % histCap] + hR[i % histCap]) * 0.5); i += 2 }
            return out
        }
    }

    // ---- audio thread
    func render(frames n: Int, _ L: UnsafeMutablePointer<Float>, _ R: UnsafeMutablePointer<Float>) {
        lock.lock(); defer { lock.unlock() }
        var gotLive = 0
        if let ring = live { gotLive = ring.read(&scratchL, &scratchR, frames: min(n, scratchL.count)) }
        let step = fileStep * (rate + nudge)
        var peak = peakValue
        for i in 0..<n {
            // 1) the deck's own signal
            var a: Float = 0, b: Float = 0
            if live != nil {
                if i < gotLive { a = scratchL[i]; b = scratchR[i] }
            } else if playing && !fileL.isEmpty {
                let k = Int(pos)
                if k + 1 < fileL.count {
                    let f = Float(pos - Double(k))
                    a = fileL[k] + (fileL[k + 1] - fileL[k]) * f
                    b = fileR[k] + (fileR[k + 1] - fileR[k]) * f
                    pos += step
                } else { playing = false }
            }
            hL[hw % histCap] = a; hR[hw % histCap] = b; hw += 1
            // 2) performance effect replaces what you hear (the source keeps running)
            switch effect {
            case .none: break
            case .roll(let start, let len):
                let j = start + (hw - 1 - start) % len
                a = hL[j % histCap]; b = hR[j % histCap]
            case .reverse(let p):
                if hw - p < histCap - 1 && p > 0 {
                    a = hL[p % histCap]; b = hR[p % histCap]; effect = .reverse(pos: p - 1)
                } else { a = 0; b = 0 }
            case .brake(let p, let s):
                if s > 0.001 {
                    let k = Int(p), f = Float(p - Double(k))
                    a = hL[k % histCap] + (hL[(k + 1) % histCap] - hL[k % histCap]) * f
                    b = hR[k % histCap] + (hR[(k + 1) % histCap] - hR[k % histCap]) * f
                    effect = .brake(pos: p + s, speed: s - 1 / (sr * 0.9))   // ~0.9 s to a full stop
                } else { a = 0; b = 0 }
            }
            L[i] = a; R[i] = b
            peak = max(peak, abs(a), abs(b))
        }
        peakValue = peak
    }
}

/// The two decks and their effect chains: source → EQ (3 bands + DJ filter) → echo → reverb → channel volume → master.
final class AudioGraph {
    let engine = AVAudioEngine()
    let sr: Double
    let decks: [DeckCore]
    let eqs: [AVAudioUnitEQ]
    let delays: [AVAudioUnitDelay]
    let reverbs: [AVAudioUnitReverb]
    let channels: [AVAudioMixerNode]
    private(set) var startupError: String?
    private var sources: [AVAudioSourceNode] = []
    private var configObserver: NSObjectProtocol?

    init() {
        let outRate = engine.outputNode.outputFormat(forBus: 0).sampleRate
        let rate = (outRate >= 8000 && outRate <= 384000 && outRate.isFinite) ? outRate : 48000
        sr = rate
        let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: 2)
            ?? AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48000, channels: 2, interleaved: false)!
        decks = [DeckCore(sampleRate: rate), DeckCore(sampleRate: rate)]
        eqs = [AVAudioUnitEQ(numberOfBands: 4), AVAudioUnitEQ(numberOfBands: 4)]
        delays = [AVAudioUnitDelay(), AVAudioUnitDelay()]
        reverbs = [AVAudioUnitReverb(), AVAudioUnitReverb()]
        channels = [AVAudioMixerNode(), AVAudioMixerNode()]
        for d in 0..<2 {
            let core = decks[d]
            let src = AVAudioSourceNode(format: format) { _, _, frameCount, abl -> OSStatus in
                let list = UnsafeMutableAudioBufferListPointer(abl)
                guard list.count >= 2, let l = list[0].mData?.assumingMemoryBound(to: Float.self),
                      let r = list[1].mData?.assumingMemoryBound(to: Float.self) else { return noErr }
                core.render(frames: Int(frameCount), l, r)
                return noErr
            }
            sources.append(src)
            let eq = eqs[d]
            let bands = eq.bands
            bands[0].filterType = .lowShelf; bands[0].frequency = 180
            bands[1].filterType = .parametric; bands[1].frequency = 1000; bands[1].bandwidth = 1.6
            bands[2].filterType = .highShelf; bands[2].frequency = 4000
            bands[3].filterType = .lowPass; bands[3].frequency = 20000; bands[3].bypass = true
            for i in 0..<3 { bands[i].gain = 0; bands[i].bypass = false }
            delays[d].wetDryMix = 0; delays[d].feedback = 45; delays[d].delayTime = 0.25; delays[d].lowPassCutoff = 9000
            reverbs[d].loadFactoryPreset(.largeHall2); reverbs[d].wetDryMix = 0
            for node in [src, eq, delays[d], reverbs[d], channels[d]] as [AVAudioNode] { engine.attach(node) }
            engine.connect(src, to: eq, format: format)
            engine.connect(eq, to: delays[d], format: format)
            engine.connect(delays[d], to: reverbs[d], format: format)
            engine.connect(reverbs[d], to: channels[d], format: format)
            engine.connect(channels[d], to: engine.mainMixerNode, format: format)
        }
        engine.prepare()
        do {
            try engine.start()
            AppLog.write("AudioGraph started (sr=\(rate) Hz)")
        } catch {
            startupError = "Could not start Mac audio output (\(error.localizedDescription)). Check your speaker or headphone selection in System Settings › Sound."
            AppLog.write("AudioGraph start error: \(error)")
        }
        // headphones plugged / output changed: macOS stops the engine; start it again
        configObserver = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main) { [weak self] _ in
            guard let self else { return }
            if !self.engine.isRunning {
                do {
                    try self.engine.start()
                    self.startupError = nil
                    AppLog.write("AudioGraph restarted after device change")
                } catch {
                    AppLog.write("AudioGraph restart failed: \(error)")
                }
            }
        }
    }

    var master: Float {
        get { engine.mainMixerNode.outputVolume }
        set { engine.mainMixerNode.outputVolume = newValue }
    }

    func setEQ(deck d: Int, low: Double, mid: Double, high: Double, kills: [Bool]) {
        let b = eqs[d].bands
        b[0].gain = kills[0] ? -60 : Float(MixMath.eqDB(low))
        b[1].gain = kills[1] ? -60 : Float(MixMath.eqDB(mid))
        b[2].gain = kills[2] ? -60 : Float(MixMath.eqDB(high))
    }

    func setFilter(deck d: Int, _ v: Double) {
        let band = eqs[d].bands[3]
        switch MixMath.filter(v) {
        case .off: band.bypass = true
        case .lowPass(let f): band.filterType = .lowPass; band.frequency = Float(f); band.bypass = false
        case .highPass(let f): band.filterType = .highPass; band.frequency = Float(f); band.bypass = false
        }
    }
}
