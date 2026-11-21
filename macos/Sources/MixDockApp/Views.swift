import SwiftUI
import UniformTypeIdentifiers
import MixDockCore

enum Theme {
    static let bg = Color(red: 0.063, green: 0.071, blue: 0.078)
    static let surface = Color(red: 0.098, green: 0.110, blue: 0.118)
    static let raised = Color(red: 0.133, green: 0.145, blue: 0.157)
    static let edge = Color(red: 0.19, green: 0.21, blue: 0.22)
    static let text = Color(red: 0.925, green: 0.94, blue: 0.97)
    static let muted = Color(red: 0.6, green: 0.65, blue: 0.725)
    static let cyan = Color(red: 0.275, green: 0.847, blue: 0.89)
    static let amber = Color(red: 0.96, green: 0.65, blue: 0.137)
    static let red = Color(red: 0.97, green: 0.41, blue: 0.455)
    static func accent(_ d: Int) -> Color { d == 0 ? cyan : amber }
}

struct ConsoleView: View {
    @EnvironmentObject var c: Console
    @State private var showPad = false
    @State private var showMIDI = false

    var body: some View {
        VStack(spacing: 10) {
            topBar
            HStack(alignment: .top, spacing: 10) {
                DeckView(d: 0)
                MixerView().frame(width: 220)
                DeckView(d: 1)
            }
            Text(c.message).font(.system(size: 12)).foregroundStyle(Theme.muted).lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 4)
        }
        .padding(12)
        .background(Theme.bg)
        .foregroundStyle(Theme.text)
        .sheet(isPresented: $showPad) { PadMapView().environmentObject(c) }
        .sheet(isPresented: $showMIDI) { MIDIView().environmentObject(c) }
        .alert("MixDock Audio Notice", isPresented: Binding(get: { c.startupAlert != nil }, set: { if !$0 { c.startupAlert = nil } })) {
            Button("OK") { c.startupAlert = nil }
        } message: {
            Text(c.startupAlert ?? "")
        }
    }

    private var topBar: some View {
        HStack(spacing: 10) {
            Text("MIXDOCK").font(.system(size: 22, weight: .semibold)).tracking(1)
            Spacer().frame(width: 12)
            status(c.padName.map { "🎮 \($0)" } ?? "🎮 No game controller", on: c.padName != nil)
            status(c.midiNames.isEmpty ? "🎹 No MIDI controller" : "🎹 \(c.midiNames.joined(separator: ", "))", on: !c.midiNames.isEmpty)
            Spacer()
            Text("MASTER").font(.system(size: 11, weight: .semibold)).foregroundStyle(Theme.muted)
            Slider(value: $c.master, in: 0...1).frame(width: 140).tint(Color(red: 0.49, green: 0.886, blue: 0.745))
            Pill("CONTROLLER", Theme.cyan) { showPad = true }
            Pill("MIDI", Theme.cyan) { showMIDI = true }
            Pill(c.muted ? "RESTORE AUDIO" : "KILL AUDIO", Theme.red, active: c.muted) { c.panic() }
        }
    }
    private func status(_ s: String, on: Bool) -> some View {
        Text(s).font(.system(size: 12, weight: .medium)).foregroundStyle(on ? Theme.text : Theme.muted)
            .padding(.horizontal, 10).padding(.vertical, 5).background(Capsule().fill(Theme.surface)).lineLimit(1)
    }
}

struct Pill: View {
    let title: String, color: Color, active: Bool, action: () -> Void
    init(_ title: String, _ color: Color = Theme.muted, active: Bool = false, action: @escaping () -> Void) {
        self.title = title; self.color = color; self.active = active; self.action = action
    }
    var body: some View {
        Button(action: action) {
            Text(title).font(.system(size: 12, weight: .bold)).lineLimit(1).minimumScaleFactor(0.7)
                .padding(.horizontal, 12).frame(minHeight: 30)
                .foregroundStyle(active ? Theme.bg : (color == Theme.muted ? Theme.text : color))
                .background(RoundedRectangle(cornerRadius: 6).fill(active ? color : Theme.raised))
                .overlay(RoundedRectangle(cornerRadius: 6).stroke(active ? color : Theme.edge, lineWidth: 1))
        }.buttonStyle(.plain)
    }
}

struct DeckView: View {
    @EnvironmentObject var c: Console
    let d: Int
    var s: DeckState { c.decks[d] }
    var accent: Color { Theme.accent(d) }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                Text(d == 0 ? "A" : "B").font(.system(size: 24, weight: .bold)).frame(width: 40, height: 40)
                    .foregroundStyle(c.focus == d ? Theme.bg : accent)
                    .background(RoundedRectangle(cornerRadius: 6).fill(c.focus == d ? accent : Theme.raised))
                    .onTapGesture { c.setFocus(d) }
                    .help("Highlighted = the deck your controller is driving (LB / RB)")
                VStack(alignment: .leading, spacing: 2) {
                    Text(s.title).font(.system(size: 17, weight: .semibold)).lineLimit(1)
                    HStack(spacing: 12) {
                        Text(s.bpm > 0 ? String(format: "%.1f BPM", s.bpm) : "— BPM").foregroundStyle(accent)
                        Text(s.time).foregroundStyle(Theme.muted)
                        if !s.move.isEmpty { Text(s.move).foregroundStyle(accent).fontWeight(.bold) }
                    }.font(.system(size: 13, design: .rounded)).monospacedDigit()
                }
                Spacer()
                sourceMenu
            }
            WaveformView(peaks: s.peaks, progress: s.progress, live: s.isLive, level: s.level, accent: accent) { f in
                c.graph.decks[d].seek(seconds: c.graph.decks[d].duration * f)
            }.frame(height: 86)
            HStack(spacing: 8) {
                Pill(s.playing ? "PAUSE" : "PLAY", accent, active: s.playing) { c.togglePlay(d) }
                Pill("CUE", accent) { c.cuePress(d, down: true); c.cuePress(d, down: false) }
                Pill("SYNC", accent) { c.sync(d) }
                Pill("TAP", accent) { c.tapBPM(d) }
                Spacer()
                Pill(s.loopOn ? "LOOP \(MixMath.beatsLabel(s.loopBeats))" : "LOOP", accent, active: s.loopOn) { c.toggleLoop(d) }
                Pill("÷2") { c.loopSize(d, double: false) }
                Pill("×2") { c.loopSize(d, double: true) }
            }
            HStack(spacing: 8) {
                ForEach(0..<4, id: \.self) { i in
                    Pill(s.hotCues[i] == nil ? "CUE \(i + 1)" : "● \(i + 1)", accent, active: s.hotCues[i] != nil) { c.hotCue(d, i) }
                }
                Spacer()
                ForEach([0.25, 0.125, 0.0625], id: \.self) { b in
                    HoldPill(title: "ROLL \(MixMath.beatsLabel(b))", color: accent) { down in c.roll(d, down ? b : nil) }
                }
            }
            if s.isFile {
                HStack {
                    Text("TEMPO").font(.system(size: 11, weight: .semibold)).foregroundStyle(Theme.muted)
                    Slider(value: Binding(get: { c.decks[d].tempo }, set: { c.decks[d].tempo = $0; c.applyRate(d) }), in: 0...1).tint(accent)
                    Text(String(format: "%+.1f%%", (MixMath.tempoRate(s.tempo) - 1) * 100)).monospacedDigit().frame(width: 56)
                    Pill("0") { c.decks[d].tempo = 0.5; c.applyRate(d) }
                }.font(.system(size: 12))
            }
            Spacer(minLength: 0)
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: 10).fill(Theme.surface))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(c.focus == d ? accent.opacity(0.7) : Theme.edge, lineWidth: c.focus == d ? 2 : 1))
        .onDrop(of: [.fileURL], isTargeted: nil) { providers in
            guard let p = providers.first else { return false }
            _ = p.loadObject(ofClass: URL.self) { url, _ in if let url { Task { @MainActor in c.loadSong(d, url) } } }
            return true
        }
    }

    private var sourceMenu: some View {
        Menu {
            Button("Load a song…") { c.loadSongDialog(d) }
            Divider()
            if AppAudioList.supported {
                if c.apps.isEmpty { Text("No app is playing sound — press Refresh after starting Spotify / a browser") }
                ForEach(c.apps) { app in Button("Use \(app.name) (live)") { c.useApp(d, app) } }
                Button("Refresh playing apps") { c.refreshApps() }
            } else { Text("Live apps need macOS 14.4 or newer") }
            if s.isFile || s.isLive { Divider(); Button("Release deck") { c.release(d) } }
        } label: {
            Text("SOURCE").font(.system(size: 12, weight: .bold)).padding(.horizontal, 12).frame(height: 30)
                .background(RoundedRectangle(cornerRadius: 6).fill(Theme.raised))
        }
        .menuStyle(.borderlessButton).fixedSize()
        .simultaneousGesture(TapGesture().onEnded { c.refreshApps() })
    }
}

/// Press-and-hold button (loop roll).
struct HoldPill: View {
    let title: String, color: Color, onChange: (Bool) -> Void
    @State private var down = false
    var body: some View {
        Text(title).font(.system(size: 12, weight: .bold)).lineLimit(1).minimumScaleFactor(0.7)
            .padding(.horizontal, 10).frame(minHeight: 30)
            .foregroundStyle(down ? Theme.bg : color)
            .background(RoundedRectangle(cornerRadius: 6).fill(down ? color : Theme.raised))
            .overlay(RoundedRectangle(cornerRadius: 6).stroke(Theme.edge))
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { _ in if !down { down = true; onChange(true) } }
                .onEnded { _ in down = false; onChange(false) })
    }
}

struct WaveformView: View {
    let peaks: [Float], progress: Double, live: Bool, level: Float, accent: Color
    let seek: (Double) -> Void
    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                RoundedRectangle(cornerRadius: 6).fill(Theme.bg)
                if !peaks.isEmpty {
                    Canvas { ctx, size in
                        let n = peaks.count, w = size.width / CGFloat(n), mid = size.height / 2
                        var played = Path(), rest = Path()
                        for i in 0..<n {
                            let h = CGFloat(peaks[i]) * mid * 0.95
                            let r = CGRect(x: CGFloat(i) * w, y: mid - h, width: max(1, w), height: max(1, h * 2))
                            if Double(i) / Double(n) < progress { played.addRect(r) } else { rest.addRect(r) }
                        }
                        ctx.fill(played, with: .color(accent.opacity(0.45)))
                        ctx.fill(rest, with: .color(accent))
                    }
                    Rectangle().fill(Color.white).frame(width: 2).offset(x: g.size.width * progress)
                } else if live {
                    RoundedRectangle(cornerRadius: 3).fill(accent.opacity(0.8))
                        .frame(width: max(4, g.size.width * CGFloat(min(1, level))), height: 10)
                        .padding(.leading, 0)
                    Text("LIVE").font(.system(size: 13, weight: .bold)).foregroundStyle(Theme.muted).frame(maxWidth: .infinity)
                } else {
                    Text("Drop a song here, or choose SOURCE").font(.system(size: 13)).foregroundStyle(Theme.muted).frame(maxWidth: .infinity)
                }
            }
            .contentShape(Rectangle())
            .onTapGesture(coordinateSpace: .local) { p in if !peaks.isEmpty { seek(min(1, max(0, p.x / g.size.width))) } }
        }
    }
}

struct MixerView: View {
    @EnvironmentObject var c: Console
    var body: some View {
        VStack(spacing: 10) {
            Text("MIXER").font(.system(size: 13, weight: .semibold))
            HStack(alignment: .top, spacing: 14) { channel(0); channel(1) }
            VStack(spacing: 4) {
                HStack { Text("A").foregroundStyle(Theme.cyan); Spacer(); Text("CROSSFADER").foregroundStyle(Theme.muted); Spacer(); Text("B").foregroundStyle(Theme.amber) }
                    .font(.system(size: 11, weight: .bold))
                Slider(value: $c.crossfader, in: 0...1).tint(Theme.muted)
                    .onTapGesture(count: 2) { c.crossfader = 0.5 }
            }
            HStack(spacing: 6) {
                Text("R-STICK FX").font(.system(size: 10, weight: .semibold)).foregroundStyle(Theme.muted)
                ForEach(FxType.allCases, id: \.self) { f in Pill(f.rawValue, Theme.cyan, active: c.fx == f) { c.fx = f } }
            }
            Spacer(minLength: 0)
        }
        .padding(12).frame(maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 10).fill(Theme.surface))
    }

    private func channel(_ d: Int) -> some View {
        let a = Theme.accent(d)
        return VStack(spacing: 6) {
            Text(d == 0 ? "A" : "B").font(.system(size: 13, weight: .bold)).foregroundStyle(a)
            Knob(label: "GAIN", value: bind(d, \.gain), accent: a)
            knobKill(d, "HIGH", \.high, 2)
            knobKill(d, "MID", \.mid, 1)
            knobKill(d, "LOW", \.low, 0)
            Knob(label: "FILTER", value: bind(d, \.filter), accent: a, filter: true)
            HStack(alignment: .bottom, spacing: 6) {
                Meter(level: c.decks[d].level, accent: a).frame(width: 6, height: 110)
                VSlider(value: bind(d, \.volume), accent: a).frame(width: 30, height: 110)
            }
        }
    }
    private func knobKill(_ d: Int, _ label: String, _ kp: WritableKeyPath<DeckState, Double>, _ band: Int) -> some View {
        VStack(spacing: 2) {
            Knob(label: label, value: bind(d, kp), accent: Theme.accent(d))
            Pill(c.decks[d].kills[band] ? "KILLED" : "KILL", Theme.red, active: c.decks[d].kills[band]) { c.toggleKill(d, band) }
                .scaleEffect(0.8).frame(height: 22)
        }
    }
    private func bind(_ d: Int, _ kp: WritableKeyPath<DeckState, Double>) -> Binding<Double> {
        Binding(get: { c.decks[d][keyPath: kp] }, set: { c.decks[d][keyPath: kp] = $0; c.applyDeck(d) })
    }
}

/// Drag up/down to turn, double-click to centre.
struct Knob: View {
    let label: String
    @Binding var value: Double
    let accent: Color
    var filter = false
    @State private var start: Double?
    var body: some View {
        VStack(spacing: 2) {
            ZStack {
                Circle().stroke(Theme.edge, lineWidth: 4)
                Circle().trim(from: 0, to: 0.75).stroke(Theme.edge, style: StrokeStyle(lineWidth: 4, lineCap: .round)).rotationEffect(.degrees(135))
                arc.stroke(accent, style: StrokeStyle(lineWidth: 4, lineCap: .round)).rotationEffect(.degrees(135))
                Circle().fill(Theme.raised).padding(6)
                Rectangle().fill(Theme.text).frame(width: 2, height: 9).offset(y: -12).rotationEffect(.degrees(-135 + value * 270))
            }
            .frame(width: 44, height: 44)
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 1)
                .onChanged { g in
                    if start == nil { start = value }
                    var v = min(1, max(0, start! - Double(g.translation.height) / 180))
                    if abs(v - 0.5) < 0.015 { v = 0.5 }
                    value = v
                }
                .onEnded { _ in start = nil })
            .onTapGesture(count: 2) { value = 0.5 }
            Text(caption).font(.system(size: 10, weight: .semibold)).foregroundStyle(Theme.muted).lineLimit(1)
        }
    }
    private var arc: Path {
        Path { p in
            let lo = filter || label != "GAIN" ? min(0.5, value) : 0, hi = filter || label != "GAIN" ? max(0.5, value) : value
            p.addArc(center: CGPoint(x: 22, y: 22), radius: 20, startAngle: .degrees(lo * 270), endAngle: .degrees(hi * 270), clockwise: false)
        }
    }
    private var caption: String {
        guard filter else { return label }
        switch MixMath.filter(value) {
        case .off: return "FILTER"
        case .lowPass: return "LOW-PASS"
        case .highPass: return "HIGH-PASS"
        }
    }
}

struct VSlider: View {
    @Binding var value: Double
    let accent: Color
    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .bottom) {
                RoundedRectangle(cornerRadius: 3).fill(Theme.bg).frame(width: 6)
                RoundedRectangle(cornerRadius: 3).fill(accent.opacity(0.6)).frame(width: 6, height: g.size.height * CGFloat(value))
                RoundedRectangle(cornerRadius: 4).fill(Theme.raised).overlay(RoundedRectangle(cornerRadius: 4).stroke(Theme.text.opacity(0.8)))
                    .frame(width: g.size.width, height: 16).offset(y: CGFloat(-value) * (g.size.height - 16))
            }
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0).onChanged { v in value = min(1, max(0, 1 - Double(v.location.y / g.size.height))) })
        }
    }
}

struct Meter: View {
    let level: Float, accent: Color
    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .bottom) {
                Rectangle().fill(Theme.bg)
                Rectangle().fill(level > 0.95 ? Theme.red : accent).frame(height: g.size.height * CGFloat(min(1, level)))
            }
        }
    }
}

/// The Xbox map, exactly what each input does.
struct PadMapView: View {
    @EnvironmentObject var c: Console
    @Environment(\.dismiss) var dismiss
    let rows: [(String, String, String)] = [
        ("Left stick ↕", "Filter sweep (springs back to where the knob is)", "—"),
        ("Left stick ↔", "Crossfader", "—"),
        ("Right stick", "FX pad: push further = more (SPACE: up reverb, down echo)", "—"),
        ("LT", "Echo throw", "Brake to a stop"),
        ("RT", "Beat roll: light 1/4 … full 1/32", "Reverse"),
        ("A", "Bass kill", "Hot cue 1"),
        ("B", "Loop on / off", "Hot cue 2"),
        ("X", "Roll 1/4 (hold)", "Hot cue 3"),
        ("Y", "Roll 1/8 (hold)", "Hot cue 4"),
        ("LB / RB", "Control deck A / deck B", "RB: crossfader to centre"),
        ("D-pad ↑ ↓", "Loop size ×2 / ÷2", "—"),
        ("D-pad ← →", "Mid kill / high kill", "—"),
        ("L3 (press stick)", "Filter back to off", "—"),
        ("R3 (press stick)", "Next FX type", "—"),
        ("Menu ≡", "Sync to the other deck", "Play / pause (songs)"),
        ("View ⧉", "Tap = tap tempo · hold = SHIFT", "Hold View + Menu ½ s = kill all audio"),
    ]
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("XBOX CONTROLLER").font(.system(size: 18, weight: .bold))
                Spacer()
                Text(c.padName.map { "Connected: \($0)" } ?? "Not connected").foregroundStyle(c.padName == nil ? Theme.muted : Theme.cyan)
            }
            Text("Pair it once: hold the pairing button on top of the controller until the Xbox logo flashes fast, then pick it in System Settings › Bluetooth. Same layout as MixDock on Windows.")
                .font(.system(size: 12)).foregroundStyle(Theme.muted).fixedSize(horizontal: false, vertical: true)
            Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 7) {
                GridRow { Text("INPUT"); Text("DOES"); Text("WITH VIEW HELD (SHIFT)") }.font(.system(size: 11, weight: .bold)).foregroundStyle(Theme.muted)
                ForEach(rows, id: \.0) { r in
                    GridRow {
                        Text(r.0).fontWeight(.semibold)
                        Text(r.1)
                        Text(r.2).foregroundStyle(Theme.muted)
                    }.font(.system(size: 13))
                }
            }
            HStack { Spacer(); Pill("DONE", Theme.cyan) { dismiss() } }
        }
        .padding(22).frame(width: 820).background(Theme.bg).foregroundStyle(Theme.text)
    }
}

/// MIDI learn: click LEARN next to a function, then move the knob / press the button on the controller.
struct MIDIView: View {
    @EnvironmentObject var c: Console
    @Environment(\.dismiss) var dismiss
    @State private var deck = 0

    var deckActions: [DJAction] { DJAction.allCases.filter { !$0.isGlobal } }
    var globalActions: [DJAction] { DJAction.allCases.filter { $0.isGlobal } }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("MIDI CONTROLLER").font(.system(size: 18, weight: .bold))
                Spacer()
                Text(c.midiNames.isEmpty ? "Plug in a USB DJ controller" : c.midiNames.joined(separator: ", "))
                    .foregroundStyle(c.midiNames.isEmpty ? Theme.muted : Theme.cyan).lineLimit(1)
            }
            Text("Any USB DJ controller works: press LEARN next to a function, then move that fader or press that button. It saves by itself.")
                .font(.system(size: 12)).foregroundStyle(Theme.muted)
            HStack(spacing: 10) {
                Picker("", selection: $deck) { Text("Deck A").tag(0); Text("Deck B").tag(1) }.pickerStyle(.segmented).frame(width: 200)
                Toggle("Learn into the SHIFT layer", isOn: $c.learnShift).toggleStyle(.checkbox)
                Spacer()
                Text(c.lastMIDI.isEmpty ? "Last message: —" : "Last message: \(c.lastMIDI)").font(.system(size: 12, design: .monospaced)).foregroundStyle(Theme.muted)
            }
            if let t = c.learn {
                HStack {
                    Text("Now move or press the control for  \(t.label)").foregroundStyle(Theme.amber).fontWeight(.bold)
                    Spacer(); Pill("CANCEL") { c.cancelLearn() }
                }.padding(8).background(RoundedRectangle(cornerRadius: 6).fill(Theme.raised))
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 4) {
                    section("DECK \(deck == 0 ? "A" : "B")")
                    ForEach(deckActions, id: \.self) { a in row(Target(a, deck: deck)) }
                    section("MIXER / GLOBAL")
                    ForEach(globalActions, id: \.self) { a in row(Target(a)) }
                }
            }
            HStack {
                Pill("CLEAR ALL", Theme.red) { c.clearBindings() }
                Spacer()
                Pill("DONE", Theme.cyan) { c.cancelLearn(); dismiss() }
            }
        }
        .padding(22).frame(width: 760, height: 640).background(Theme.bg).foregroundStyle(Theme.text)
    }

    private func section(_ s: String) -> some View {
        Text(s).font(.system(size: 11, weight: .bold)).foregroundStyle(Theme.muted).padding(.top, 8)
    }
    private func row(_ t: Target) -> some View {
        let mine = c.bindings.filter { $0.target == t }
        return HStack(spacing: 10) {
            Text(t.action.label).frame(width: 170, alignment: .leading)
            if mine.isEmpty { Text("not set").foregroundStyle(Theme.muted) }
            ForEach(mine) { b in
                HStack(spacing: 6) {
                    Text("\(b.shiftLayer ? "SHIFT + " : "")\(b.control.description)").font(.system(size: 12, design: .monospaced))
                    if b.mode != .button {
                        Menu(b.mode.rawValue) {
                            ForEach(ControlMode.allCases, id: \.self) { m in Button(m.rawValue) { c.setMode(b, m) } }
                        }.menuStyle(.borderlessButton).fixedSize().font(.system(size: 11))
                        Button(b.invert ? "inverted" : "invert") { c.toggleInvert(b) }.buttonStyle(.plain).font(.system(size: 11)).foregroundStyle(b.invert ? Theme.amber : Theme.muted)
                    }
                    Button("✕") { c.removeBinding(b) }.buttonStyle(.plain).foregroundStyle(Theme.red)
                }
                .padding(.horizontal, 8).padding(.vertical, 3).background(Capsule().fill(Theme.raised))
            }
            Spacer()
            Pill(c.learn == t ? "LISTENING…" : "LEARN", Theme.amber, active: c.learn == t) { c.learn == t ? c.cancelLearn() : c.startLearn(t) }
        }
        .font(.system(size: 13)).padding(.vertical, 2)
    }
}
