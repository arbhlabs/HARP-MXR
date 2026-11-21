import AppKit
import Combine
import AVFoundation
import MixDockCore

enum FxType: String, CaseIterable { case space = "SPACE", echo = "ECHO", reverb = "REVERB" }

struct DeckState {
    var title = "Choose a source"
    var source: String? = nil
    var isLive = false, isFile = false, playing = false
    var bpm = 0.0
    var time = "— / —"
    var progress = 0.0
    var peaks: [Float] = []
    var level: Float = 0
    var gain = 0.5, high = 0.5, mid = 0.5, low = 0.5, filter = 0.5, volume = 0.85, tempo = 0.5
    var kills = [false, false, false]
    var loopOn = false, loopBeats = 4.0
    var move = ""
    var hotCues: [Double?] = [nil, nil, nil, nil]
    var cue = 0.0
}

/// The console: owns the audio graph, both controllers and every deck's state. All UI and controller input lands here.
@MainActor
final class Console: ObservableObject {
    @Published var decks = [DeckState(), DeckState()]
    @Published var crossfader = 0.5 { didSet { if crossfader != oldValue { crossfader = min(1, max(0, crossfader)); applyVolumes() } } }
    @Published var master = 0.9 { didSet { applyMaster() } }
    @Published var focus = 0
    @Published var fx = FxType.space
    @Published var muted = false { didSet { applyMaster() } }
    @Published var message = "Pick a song or a playing app on each deck. Connect a controller any time."
    @Published var padName: String?
    @Published var midiNames: [String] = []
    @Published var learn: Target?
    @Published var learnShift = false
    @Published var lastMIDI = ""
    @Published var bindings: [MIDIBinding] = []
    @Published var apps: [AudioApp] = []
    @Published var startupAlert: String?

    let graph = AudioGraph()
    private let midi = MIDIService()
    private let pad = GamepadService()
    private let mapper = GamepadMapper()
    private let mapping = MappingEngine()
    private var timer: Timer?
    private var started = false
    private var tick = 0
    private var lastTick = Date().timeIntervalSince1970
    private var springs: [Double?] = [nil, nil]
    private var fxPad = [(x: 0.5, y: 0.5, a: 0.0), (x: 0.5, y: 0.5, a: 0.0)]
    private var echo = [0.0, 0.0]
    private var taps = [TapTempo(), TapTempo()]
    private var grids: [BeatGrid?] = [nil, nil]
    private var nudge = [0.0, 0.0]
    private var rolling: [Double?] = [nil, nil]
    private var cuePreview = [false, false]
    private var liveTaps: [AnyObject?] = [nil, nil]
    private var liveBPMBusy = false
    private var moveAt = [0.0, 0.0]

    init() {
        if let data = UserDefaults.standard.data(forKey: "midi.bindings"), let f = try? JSONDecoder().decode(MappingFile.self, from: data) {
            mapping.replace(f.bindings)
        }
        bindings = mapping.bindings
        for d in 0..<2 { applyDeck(d) }
        applyVolumes(); applyMaster()
        if let err = graph.startupError {
            startupAlert = err
            say(err)
        }
        AppLog.write("Console initialized (sr=\(graph.sr), running=\(graph.engine.isRunning))")
    }

    func startIfNeeded() {
        guard !started else { return }
        started = true
        AppLog.write("Starting controllers & 60 Hz frame loop")
        midi.onDevices = { [weak self] n in
            Task { @MainActor [weak self] in
                self?.midiNames = n
                if !n.isEmpty { self?.say("MIDI: \(n.joined(separator: ", ")) connected.") }
            }
        }
        midi.onMessage = { [weak self] m in
            Task { @MainActor [weak self] in self?.handleMIDI(m) }
        }
        midi.start()
        pad.onChange = { [weak self] n in
            Task { @MainActor [weak self] in
                self?.padName = n
                if let n { self?.say("\(n) connected. Left stick ↕ = filter, ↔ = crossfader. Press CONTROLLER for the full map.") }
            }
        }
        pad.start()
        padName = pad.name
        timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in self?.frame() }
        }
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor [weak self] in self?.releaseAll() }
        }
    }

    func say(_ s: String) { message = s; AppLog.write("Status: \(s)") }

    // ================================================================ sources
    func refreshApps() { apps = AppAudioList.playing() }

    func loadSongDialog(_ d: Int) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.audio]
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url { loadSong(d, url) }
    }

    func loadSong(_ d: Int, _ url: URL) {
        releaseLive(d)
        decks[d].title = "Loading \(url.lastPathComponent)…"
        Task.detached(priority: .userInitiated) {
            do {
                let file = try AVAudioFile(forReading: url)
                let fmt = file.processingFormat
                guard let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(file.length)) else { throw CocoaError(.fileReadCorruptFile) }
                try file.read(into: buf)
                let n = Int(buf.frameLength)
                guard let ch = buf.floatChannelData else { throw CocoaError(.fileReadCorruptFile) }
                let left = Array(UnsafeBufferPointer(start: ch[0], count: n))
                let right = fmt.channelCount > 1 ? Array(UnsafeBufferPointer(start: ch[1], count: n)) : left
                var mono = [Float](); mono.reserveCapacity(n / 2)
                var i = 0
                while i < n { mono.append((left[i] + right[i]) * 0.5); i += 2 }
                let grid = BPMDetector.detect(mono, sampleRate: fmt.sampleRate / 2)
                let peaks = Waveform.peaks(mono, buckets: 1200)
                await MainActor.run {
                    self.graph.decks[d].loadFile(left: left, right: right, sampleRate: fmt.sampleRate)
                    self.grids[d] = grid
                    var s = self.decks[d]
                    s.title = url.deletingPathExtension().lastPathComponent; s.source = "file"
                    s.isFile = true; s.isLive = false; s.peaks = peaks; s.bpm = grid?.bpm ?? 0
                    s.hotCues = [nil, nil, nil, nil]; s.cue = 0; s.tempo = 0.5; s.loopOn = false
                    self.decks[d] = s
                    self.applyRate(d)
                    self.say("Playing \(s.title) on deck \(d == 0 ? "A" : "B")\(grid.map { String(format: " · %.1f BPM", $0.bpm) } ?? "").")
                }
            } catch {
                await MainActor.run { self.decks[d].title = "Could not open that file"; self.say("Could not open \(url.lastPathComponent): \(error.localizedDescription)") }
            }
        }
    }

    func useApp(_ d: Int, _ app: AudioApp) {
        guard #available(macOS 14.4, *) else { say(AppAudioError.unsupported.localizedDescription); return }
        if let other = liveTaps[1 - d] as? AppAudioTap, other.app.bundleID == app.bundleID { say("\(app.name) is already on the other deck."); return }
        releaseLive(d)
        let ring = StereoRing(sampleRate: graph.sr)
        do {
            let tap = try AppAudioTap(app: app, ring: ring)
            liveTaps[d] = tap
            graph.decks[d].setLive(ring)
            grids[d] = nil
            var s = decks[d]
            s.title = app.name; s.source = app.bundleID; s.isLive = true; s.isFile = false; s.peaks = []; s.bpm = 0; s.time = "LIVE"; s.loopOn = false
            decks[d] = s
            say("\(app.name) now plays through MixDock (its own sound is muted until you release it).")
        } catch { say(error.localizedDescription) }
    }

    func release(_ d: Int) {
        releaseLive(d)
        graph.decks[d].clear()
        decks[d] = DeckState(gain: decks[d].gain, high: decks[d].high, mid: decks[d].mid, low: decks[d].low, filter: decks[d].filter, volume: decks[d].volume)
        say("Deck \(d == 0 ? "A" : "B") released.")
    }

    private func releaseLive(_ d: Int) {
        if #available(macOS 14.4, *), let t = liveTaps[d] as? AppAudioTap { t.stop() }
        liveTaps[d] = nil
    }
    func releaseAll() { releaseLive(0); releaseLive(1) }

    // ================================================================ mixer
    func applyDeck(_ d: Int) {
        let s = decks[d]
        graph.setEQ(deck: d, low: s.low, mid: s.mid, high: s.high, kills: s.kills)
        let f = springs[d].map { min(1, max(0, s.filter + $0 * 0.5)) } ?? s.filter
        graph.setFilter(deck: d, f)
        applyVolumes()
    }
    func applyVolumes() {
        let (a, b) = MixMath.crossfader(crossfader)
        for d in 0..<2 {
            let s = decks[d]
            let gain = pow(10, (s.gain - 0.5) * 24 / 20)          // trim ±12 dB
            graph.channels[d].outputVolume = Float(gain * pow(s.volume, 1.6) * (d == 0 ? a : b) * 1.4)
        }
    }
    func applyMaster() { graph.master = muted ? 0 : Float(master) }
    func applyRate(_ d: Int) { graph.decks[d].setRate(decks[d].isFile ? MixMath.tempoRate(decks[d].tempo) : 1) }
    func effectiveBPM(_ d: Int) -> Double {
        let bpm = grids[d]?.bpm ?? (taps[d].bpm > 0 ? taps[d].bpm : 0)
        return decks[d].isFile ? bpm * MixMath.tempoRate(decks[d].tempo) : bpm
    }
    private func beatFrames(_ d: Int, _ beats: Double) -> Int {
        let bpm = effectiveBPM(d) > 0 ? effectiveBPM(d) : 120
        return Int(beats * 60 / bpm * graph.sr)
    }
    func applyFx(_ d: Int) {
        let p = fxPad[d]
        var delayWet = 0.0, reverbWet = 0.0
        switch fx {
        case .space: reverbWet = p.a * p.y; delayWet = p.a * (1 - p.y)
        case .echo: delayWet = p.a
        case .reverb: reverbWet = p.a
        }
        let throwAmt = echo[d]
        let beats = throwAmt > max(0, delayWet) ? 0.5 : [0.125, 0.25, 0.375, 0.5, 0.75, 1][min(5, Int(p.x * 6))]
        let bpm = effectiveBPM(d) > 0 ? effectiveBPM(d) : 120
        graph.delays[d].delayTime = min(2, beats * 60 / bpm)
        graph.delays[d].feedback = Float(throwAmt > 0 ? 55 : 35 + 30 * p.a)
        graph.delays[d].wetDryMix = Float(max(delayWet, throwAmt) * 55)
        graph.reverbs[d].wetDryMix = Float(reverbWet * 65)
    }

    // ================================================================ moves (shared by mouse, Xbox and MIDI)
    func togglePlay(_ d: Int) {
        guard decks[d].isFile else { say(decks[d].isLive ? "Live apps play in their own window; use the app to pause." : "Load a song first."); return }
        graph.decks[d].setPlaying(!graph.decks[d].isPlaying)
    }
    func cuePress(_ d: Int, down: Bool) {
        guard decks[d].isFile else { return }
        let core = graph.decks[d]
        if down {
            if core.isPlaying { core.setPlaying(false); core.seek(seconds: decks[d].cue) }
            else if abs(core.position - decks[d].cue) < 0.05 { cuePreview[d] = true; core.setPlaying(true) }
            else { decks[d].cue = core.position; say("Cue set.") }
        } else if cuePreview[d] { cuePreview[d] = false; core.setPlaying(false); core.seek(seconds: decks[d].cue) }
    }
    func hotCue(_ d: Int, _ i: Int) {
        guard decks[d].isFile else { say("Hot cues need a song file."); return }
        if let t = decks[d].hotCues[i] { graph.decks[d].seek(seconds: t); if !graph.decks[d].isPlaying { graph.decks[d].setPlaying(true) } }
        else { decks[d].hotCues[i] = graph.decks[d].position; say("Hot cue \(i + 1) set.") }
    }
    func sync(_ d: Int) {
        let other = 1 - d
        guard decks[d].isFile else { say(decks[d].isLive ? "A live app can't change tempo. Sync the other deck to it instead." : "Load a song first."); return }
        let mine = grids[d]?.bpm ?? 0, target = effectiveBPM(other)
        guard let r = MixMath.syncRate(bpm: mine, targetBPM: target) else { say("Sync needs a beat on both decks (tap BPM with View if it's missing)."); return }
        decks[d].tempo = min(1, max(0, (r - 1) / 0.16 + 0.5))
        // tempo fader range is ±8 %; sync may need more, so drive the rate directly
        graph.decks[d].setRate(r)
        say(String(format: "Deck %@ synced to %.1f BPM.", d == 0 ? "A" : "B", target))
    }
    func toggleKill(_ d: Int, _ band: Int) { decks[d].kills[band].toggle(); applyDeck(d); flash(d, ["BASS", "MID", "HIGH"][band] + (decks[d].kills[band] ? " KILL" : " BACK")) }
    func toggleLoop(_ d: Int) {
        decks[d].loopOn.toggle()
        if rolling[d] == nil { graph.decks[d].setRoll(frames: decks[d].loopOn ? beatFrames(d, decks[d].loopBeats) : nil) }
        flash(d, decks[d].loopOn ? "LOOP \(MixMath.beatsLabel(decks[d].loopBeats))" : "LOOP OFF")
    }
    func loopSize(_ d: Int, double: Bool) {
        decks[d].loopBeats = min(16, max(0.125, decks[d].loopBeats * (double ? 2 : 0.5)))
        if decks[d].loopOn && rolling[d] == nil { graph.decks[d].setRoll(frames: beatFrames(d, decks[d].loopBeats)) }
        flash(d, "LOOP \(MixMath.beatsLabel(decks[d].loopBeats))")
    }
    func roll(_ d: Int, _ beats: Double?) {
        rolling[d] = beats
        if let b = beats { graph.decks[d].setRoll(frames: beatFrames(d, b)); flash(d, "ROLL \(MixMath.beatsLabel(b))") }
        else { graph.decks[d].setRoll(frames: nil); if decks[d].loopOn { graph.decks[d].setRoll(frames: beatFrames(d, decks[d].loopBeats)) } }
    }
    func tapBPM(_ d: Int) {
        let b = taps[d].tap(at: Date().timeIntervalSince1970)
        if b > 0 { if decks[d].isLive || grids[d] == nil { decks[d].bpm = b }; flash(d, String(format: "TAP %.1f", b)) } else { flash(d, "TAP…") }
    }
    func nextFx() { let all = FxType.allCases; fx = all[(all.firstIndex(of: fx)! + 1) % all.count]; say("Right stick FX: \(fx.rawValue).") }
    func setFocus(_ d: Int) { focus = d }
    func panic() { muted.toggle(); say(muted ? "ALL AUDIO KILLED. Hold View + Menu again (or press RESTORE) to bring it back." : "Audio restored.") }
    private func flash(_ d: Int, _ s: String) { decks[d].move = s; moveAt[d] = Date().timeIntervalSince1970 }

    // ================================================================ Xbox
    private func perform(_ c: PadCommand) {
        switch c {
        case .focus(let d): setFocus(d)
        case .crossfaderBy(let x): crossfader += x
        case .crossfaderCenter: crossfader = 0.5
        case .filterSpring(let d, let v): springs[d] = v; applyDeck(d)
        case .filterReset(let d): decks[d].filter = 0.5; applyDeck(d)
        case .fx(let d, let x, let y, let a): fxPad[d] = (x, y, a); applyFx(d)
        case .echoThrow(let d, let v): echo[d] = v; applyFx(d)
        case .roll(let d, let b): roll(d, b)
        case .brake(let d, let on): graph.decks[d].setBrake(on); if on { flash(d, "BRAKE") }
        case .reverse(let d, let on): graph.decks[d].setReverse(on); if on { flash(d, "REVERSE") }
        case .kill(let d, let band): toggleKill(d, band)
        case .loop(let d): toggleLoop(d)
        case .loopSize(let d, let dbl): loopSize(d, double: dbl)
        case .fxNext: nextFx()
        case .tapBPM(let d): tapBPM(d)
        case .sync(let d): sync(d)
        case .playPause(let d): togglePlay(d)
        case .hotCue(let d, let i): hotCue(d, i)
        case .panic: panic()
        }
    }

    // ================================================================ MIDI
    private func handleMIDI(_ m: MIDIMessage) {
        lastMIDI = "\(m.control) = \(m.value)"
        if let t = learn {
            if let b = mapping.learn(m) {
                bindings = mapping.bindings; saveBindings()
                say("\(t.label) → \(b.control) (\(b.mode.rawValue)).")
                learn = nil
            }
            return
        }
        for e in mapping.handle(m) { apply(e) }
        if bindings != mapping.bindings { bindings = mapping.bindings; saveBindings() }   // 14-bit LSB found
    }

    func startLearn(_ t: Target) { learn = t; mapping.startLearn(t, shift: learnShift); say("Move or press the control for \(t.label) now…") }
    func cancelLearn() { learn = nil; mapping.cancelLearn() }
    func removeBinding(_ b: MIDIBinding) { mapping.remove(b.id); bindings = mapping.bindings; saveBindings() }
    func clearBindings() { mapping.replace([]); bindings = []; saveBindings() }
    func setMode(_ b: MIDIBinding, _ mode: ControlMode) { var c = b; c.mode = mode; mapping.update(c); bindings = mapping.bindings; saveBindings() }
    func toggleInvert(_ b: MIDIBinding) { var c = b; c.invert.toggle(); mapping.update(c); bindings = mapping.bindings; saveBindings() }
    private func saveBindings() {
        if let data = try? JSONEncoder().encode(MappingFile(device: "all", bindings: mapping.bindings)) { UserDefaults.standard.set(data, forKey: "midi.bindings") }
    }

    private func apply(_ e: ControlEvent) {
        switch e {
        case .value(let t, let v):
            let d = max(0, t.deck)
            switch t.action {
            case .volume: decks[d].volume = v; applyVolumes()
            case .gain: decks[d].gain = v; applyVolumes()
            case .eqHigh: decks[d].high = v; applyDeck(d)
            case .eqMid: decks[d].mid = v; applyDeck(d)
            case .eqLow: decks[d].low = v; applyDeck(d)
            case .filter: decks[d].filter = v; applyDeck(d)
            case .tempo: decks[d].tempo = v; applyRate(d)
            case .crossfader: crossfader = v
            case .master: master = v
            case .fxAmount: fxPad[focus].a = v; applyFx(focus)
            case .echoThrow: echo[d] = v; applyFx(d)
            default: if v >= 0.5 { apply(.press(t, true)) }
            }
        case .delta(let t, let n):
            let d = max(0, t.deck)
            if t.action == .jog {
                if graph.decks[d].isPlaying { nudge[d] = max(-0.3, min(0.3, nudge[d] + n * 0.004)) }
                else { graph.decks[d].scrub(seconds: n * 0.012) }
            }
        case .press(let t, let down):
            let d = max(0, t.deck)
            switch t.action {
            case .cue: cuePress(d, down: down)
            case .roll: roll(d, down ? 0.25 : nil)
            case .brake: graph.decks[d].setBrake(down)
            case .reverse: graph.decks[d].setReverse(down)
            default: break
            }
            guard down else { return }
            switch t.action {
            case .play: togglePlay(d)
            case .sync: sync(d)
            case .tapBPM: tapBPM(d)
            case .hotCue1: hotCue(d, 0)
            case .hotCue2: hotCue(d, 1)
            case .hotCue3: hotCue(d, 2)
            case .hotCue4: hotCue(d, 3)
            case .loop: toggleLoop(d)
            case .loopHalve: loopSize(d, double: false)
            case .loopDouble: loopSize(d, double: true)
            case .killHigh: toggleKill(d, 2)
            case .killMid: toggleKill(d, 1)
            case .killLow: toggleKill(d, 0)
            case .fxNext: nextFx()
            case .focusA: setFocus(0)
            case .focusB: setFocus(1)
            default: break
            }
        }
    }

    // ================================================================ 60 Hz frame
    private func frame() {
        let now = Date().timeIntervalSince1970
        let dt = min(0.1, max(0.001, now - lastTick)); lastTick = now
        tick += 1
        if let s = pad.read() {
            if padName == nil { padName = pad.name ?? "Game controller" }
            for c in mapper.update(s, dt: dt, now: now * 1000) { perform(c) }
            if mapper.focus != focus { focus = mapper.focus }
        }
        for d in 0..<2 where nudge[d] != 0 {
            nudge[d] *= 0.88; if abs(nudge[d]) < 0.0005 { nudge[d] = 0 }
            graph.decks[d].setNudge(nudge[d])
        }
        if tick % 4 == 0 {
            for d in 0..<2 {
                let core = graph.decks[d]
                var s = decks[d]
                s.playing = core.isPlaying
                s.level = max(core.takePeak(), s.level * 0.6)
                if s.isFile {
                    let p = core.position, dur = core.duration
                    s.progress = dur > 0 ? p / dur : 0
                    s.time = "\(Self.mmss(p)) / \(Self.mmss(dur))"
                    if grids[d] != nil { s.bpm = effectiveBPM(d) }
                }
                if !s.move.isEmpty && now - moveAt[d] > 1.4 { s.move = "" }
                decks[d] = s
            }
        }
        if tick % 180 == 0 { detectLiveBPM() }
    }

    /// Live decks: listen to the last 10 s every 3 s and update the tempo (tap tempo takes over when nothing is found).
    private func detectLiveBPM() {
        guard !liveBPMBusy else { return }
        let jobs = (0..<2).filter { decks[$0].isLive }
        guard !jobs.isEmpty else { return }
        liveBPMBusy = true
        let samples = jobs.map { graph.decks[$0].recentMono(seconds: 10) }
        let rate = graph.sr / 2
        Task.detached(priority: .utility) {
            let grids = samples.map { BPMDetector.detect($0, sampleRate: rate) }
            await MainActor.run {
                for (i, d) in jobs.enumerated() where self.decks[d].isLive {
                    if let g = grids[i] { self.grids[d] = g; self.decks[d].bpm = g.bpm }
                }
                self.liveBPMBusy = false
            }
        }
    }

    static func mmss(_ t: Double) -> String { let s = Int(max(0, t)); return "\(s / 60):" + String(format: "%02d", s % 60) }

    // ================================================================ Smoke test (--smoke)
    private var smokeStarted = false
    func runSmokeTest() {
        guard !smokeStarted else { return }
        smokeStarted = true
        print("[MixDock Smoke] Starting 5s smoke test (sr=\(graph.sr), engine.isRunning=\(graph.engine.isRunning))")
        let sr = graph.sr
        let n = Int(sr * 6)
        let sine = (0..<n).map { i -> Float in
            let t = Double(i) / sr
            let beat = exp(-((t.truncatingRemainder(dividingBy: 0.5)) * 18))
            return Float(sin(2 * .pi * 220.0 * t) * 0.4 + sin(2 * .pi * 80.0 * t) * beat * 0.4)
        }
        let peaks = Waveform.peaks(sine, buckets: 1200)
        graph.decks[0].loadFile(left: sine, right: sine, sampleRate: sr)
        var s = decks[0]
        s.title = "Smoke Test 220 Hz Sine"
        s.source = "file"; s.isFile = true; s.isLive = false; s.playing = true
        s.bpm = 120.0; s.peaks = peaks
        decks[0] = s

        // Render 2 seconds directly into buffers to guarantee DeckCore audio rendering works even on headless/dummy output
        var outL = [Float](repeating: 0, count: 1024)
        var outR = [Float](repeating: 0, count: 1024)
        var maxRendered: Float = 0
        for _ in 0..<Int((sr * 2) / 1024) {
            graph.decks[0].render(frames: 1024, &outL, &outR)
            for v in outL { maxRendered = max(maxRendered, abs(v)) }
        }
        // Reset position back to 0 so the live engine & UI waveform progress from the beginning during the 5s poll
        graph.decks[0].seek(seconds: 0)
        graph.decks[0].setPlaying(true)

        // Exercise controller & MIDI code paths
        _ = pad.read()
        midi.connectAll()
        for cmd in mapper.update(PadState(lx: 0.4, ly: 0.5, rx: 0.3, ry: -0.3, lt: 0.2, rt: 0.2, buttons: [.a]), dt: 0.016, now: 1000) {
            perform(cmd)
        }
        for cmd in mapper.update(PadState(), dt: 0.016, now: 1016) {
            perform(cmd)
        }

        print("[MixDock Smoke] Direct render peak=\(maxRendered). Polling UI + GameController + MIDI for 5s...")
        DispatchQueue.main.asyncAfter(deadline: .now() + 5.0) { [weak self] in
            guard let self else { exit(1) }
            let pos = self.graph.decks[0].position
            print("[MixDock Smoke] Finished 5s poll: maxRendered=\(maxRendered), deck0.pos=\(pos), tick=\(self.tick)")
            guard maxRendered > 0.1 else {
                fputs("[MixDock Smoke] FAIL: Rendered sine peak was too low (\(maxRendered))\n", stderr)
                exit(1)
            }
            exit(0)
        }
    }
}
