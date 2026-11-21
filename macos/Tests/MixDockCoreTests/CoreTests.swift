import XCTest
@testable import MixDockCore

final class MIDIParseTests: XCTestCase {
    func testRunningStatusRealtimeAndSysex() {
        let bytes: [UInt8] = [0xF0, 0x7E, 0x01, 0xF7, 0xB0, 0x07, 0x40, 0xF8, 0x08, 0x10, 0x91, 0x0B, 0x7F, 0x0B, 0x00, 0xE2, 0x00, 0x40]
        let m = MIDIMessage.parse(bytes)
        XCTAssertEqual(m.count, 5)
        XCTAssertEqual(m[0], MIDIMessage(kind: .cc, channel: 0, number: 7, value: 64))
        XCTAssertEqual(m[1], MIDIMessage(kind: .cc, channel: 0, number: 8, value: 16))   // running status across a clock byte
        XCTAssertEqual(m[2].kind, .note); XCTAssertEqual(m[2].channel, 1); XCTAssertFalse(m[2].isNoteOff)
        XCTAssertTrue(m[3].isNoteOff)                                                    // note-on velocity 0 = off
        XCTAssertEqual(m[4], MIDIMessage(kind: .pitchBend, channel: 2, number: 0, value: 8192))
    }
    func testTruncatedPacketDoesNotCrash() {
        XCTAssertEqual(MIDIMessage.parse([0x90, 0x3C]).count, 0)
        XCTAssertEqual(MIDIMessage.parse([0x40, 0x40]).count, 0)
    }
}

final class MappingTests: XCTestCase {
    let playA = Target(.play, deck: 0)

    func testLearnButtonPressRelease() {
        let e = MappingEngine()
        e.startLearn(playA)
        XCTAssertEqual(e.handle(MIDIMessage(kind: .note, channel: 0, number: 11, value: 127)), [])
        XCTAssertEqual(e.bindings.first?.mode, .button)
        XCTAssertEqual(e.handle(MIDIMessage(kind: .note, channel: 0, number: 11, value: 127)), [.press(playA, true)])
        XCTAssertEqual(e.handle(MIDIMessage(kind: .note, channel: 0, number: 11, value: 0, isNoteOff: true)), [.press(playA, false)])
    }
    func testLearnIgnoresNoteOff() {
        let e = MappingEngine(); e.startLearn(playA)
        e.learn(MIDIMessage(kind: .note, channel: 0, number: 11, value: 0, isNoteOff: true))
        XCTAssertTrue(e.bindings.isEmpty); XCTAssertNotNil(e.learning)
    }
    func testFaderAnd14Bit() {
        let vol = Target(.volume, deck: 1)
        let e = MappingEngine(); e.startLearn(vol)
        e.learn(MIDIMessage(kind: .cc, channel: 1, number: 19, value: 10))
        XCTAssertEqual(e.bindings.first?.mode, .absolute)
        XCTAssertEqual(e.handle(MIDIMessage(kind: .cc, channel: 1, number: 19, value: 127)), [.value(vol, 1)])
        // LSB on cc 51 refines it and is found automatically
        let fine = e.handle(MIDIMessage(kind: .cc, channel: 1, number: 51, value: 0))
        XCTAssertEqual(fine, [.value(vol, Double(127 << 7) / 16383)])
        XCTAssertEqual(e.bindings.first?.lsb, 51)
    }
    func testJogEncodings() {
        XCTAssertEqual(MappingEngine.decode(1, .relativeTwos), 1)
        XCTAssertEqual(MappingEngine.decode(127, .relativeTwos), -1)
        XCTAssertEqual(MappingEngine.decode(65, .relativeOffset64), 1)
        XCTAssertEqual(MappingEngine.decode(63, .relativeOffset64), -1)
        let jog = Target(.jog, deck: 0)
        let e = MappingEngine(); e.startLearn(jog)
        e.learn(MIDIMessage(kind: .cc, channel: 0, number: 34, value: 65))
        XCTAssertEqual(e.bindings.first?.mode, .relativeOffset64)
        let e2 = MappingEngine(); e2.startLearn(jog)
        e2.learn(MIDIMessage(kind: .cc, channel: 0, number: 34, value: 127))
        XCTAssertEqual(e2.bindings.first?.mode, .relativeTwos)
        XCTAssertEqual(e2.handle(MIDIMessage(kind: .cc, channel: 0, number: 34, value: 126)), [.delta(jog, -2)])
    }
    func testShiftLayerFallsBack() {
        let shift = Target(.shift), cueA = Target(.cue, deck: 0), hot = Target(.hotCue1, deck: 0)
        let e = MappingEngine(bindings: [
            MIDIBinding(control: ControlID(kind: .note, channel: 0, number: 63), target: shift, mode: .button),
            MIDIBinding(control: ControlID(kind: .note, channel: 0, number: 12), target: cueA, mode: .button),
            MIDIBinding(control: ControlID(kind: .note, channel: 0, number: 12), target: hot, mode: .button, shiftLayer: true),
            MIDIBinding(control: ControlID(kind: .note, channel: 0, number: 11), target: playA, mode: .button),
        ])
        _ = e.handle(MIDIMessage(kind: .note, channel: 0, number: 63, value: 127))
        XCTAssertTrue(e.shiftHeld)
        XCTAssertEqual(e.handle(MIDIMessage(kind: .note, channel: 0, number: 12, value: 127)), [.press(hot, true)])
        XCTAssertEqual(e.handle(MIDIMessage(kind: .note, channel: 0, number: 11, value: 127)), [.press(playA, true)], "no shift binding: normal layer")
        _ = e.handle(MIDIMessage(kind: .note, channel: 0, number: 63, value: 0, isNoteOff: true))
        XCTAssertFalse(e.shiftHeld)
        XCTAssertEqual(e.handle(MIDIMessage(kind: .note, channel: 0, number: 12, value: 0, isNoteOff: true)), [.press(hot, false)], "release pairs with its press")
    }
    func testRelearnReplaces() {
        let e = MappingEngine(); let c = ControlID(kind: .note, channel: 0, number: 5)
        e.startLearn(playA); e.learn(MIDIMessage(kind: .note, channel: 0, number: 5, value: 100))
        e.startLearn(Target(.cue, deck: 0)); e.learn(MIDIMessage(kind: .note, channel: 0, number: 5, value: 100))
        XCTAssertEqual(e.bindings.count, 1); XCTAssertEqual(e.bindings[0].control, c); XCTAssertEqual(e.bindings[0].target, Target(.cue, deck: 0))
    }
    func testMappingFileRoundTrip() throws {
        let f = MappingFile(device: "DDJ-FLX4", bindings: [MIDIBinding(control: ControlID(kind: .cc, channel: 0, number: 7), target: Target(.eqHigh, deck: 0), mode: .absolute)])
        let back = try JSONDecoder().decode(MappingFile.self, from: JSONEncoder().encode(f))
        XCTAssertEqual(back.bindings, f.bindings)
    }
}

final class MixMathTests: XCTestCase {
    func testCrossfaderEqualPower() {
        for x in stride(from: 0.0, through: 1.0, by: 0.1) {
            let (a, b) = MixMath.crossfader(x)
            XCTAssertEqual(a * a + b * b, 1, accuracy: 1e-9)
        }
        XCTAssertEqual(MixMath.crossfader(0).b, 0, accuracy: 1e-12)
    }
    func testFilterAndEQ() {
        XCTAssertEqual(MixMath.filter(0.5), .off)
        XCTAssertEqual(MixMath.filter(0.52), .off)
        if case .lowPass(let f) = MixMath.filter(0) { XCTAssertEqual(f, 120, accuracy: 0.5) } else { XCTFail() }
        if case .highPass(let f) = MixMath.filter(1) { XCTAssertEqual(f, 6000, accuracy: 1) } else { XCTFail() }
        XCTAssertEqual(MixMath.eqDB(0.5), 0); XCTAssertEqual(MixMath.eqDB(0), -26); XCTAssertEqual(MixMath.eqDB(1), 6)
    }
    func testSync() {
        XCTAssertEqual(MixMath.syncRate(bpm: 125, targetBPM: 128)!, 128.0 / 125, accuracy: 1e-9)
        XCTAssertEqual(MixMath.syncRate(bpm: 87, targetBPM: 174)!, 1, accuracy: 1e-9, "half time")
        XCTAssertNil(MixMath.syncRate(bpm: 100, targetBPM: 140))
    }
    func testTapTempo() {
        var t = TapTempo()
        _ = t.tap(at: 0); _ = t.tap(at: 0.5); XCTAssertEqual(t.tap(at: 1.0), 120, accuracy: 1e-9)
        _ = t.tap(at: 5); XCTAssertEqual(t.tap(at: 5.6), 100, accuracy: 1e-9)
    }
}

final class AnalysisTests: XCTestCase {
    func clicks(bpm: Double, seconds: Double, sr: Double = 22050, offset: Double = 0.1) -> [Float] {
        var s = [Float](repeating: 0, count: Int(seconds * sr))
        var t = offset
        while t < seconds {
            let i = Int(t * sr)
            for k in 0..<Int(sr * 0.03) where i + k < s.count { s[i + k] = Float(sin(Double(k) * 0.3) * exp(-Double(k) / (sr * 0.008))) }
            t += 60 / bpm
        }
        return s
    }
    func testDetects128And174() {
        let g = BPMDetector.detect(clicks(bpm: 128, seconds: 20), sampleRate: 22050)
        XCTAssertNotNil(g); XCTAssertEqual(g!.bpm, 128, accuracy: 0.15)
        XCTAssertEqual(g!.offset.truncatingRemainder(dividingBy: g!.period), 0.1, accuracy: 0.03)
        let d = BPMDetector.detect(clicks(bpm: 174, seconds: 20), sampleRate: 22050)
        XCTAssertNotNil(d); XCTAssertEqual(d!.bpm, 174, accuracy: 0.2)
    }
    func testSilenceHasNoBeat() {
        XCTAssertNil(BPMDetector.detect([Float](repeating: 0, count: 22050 * 10), sampleRate: 22050))
    }
    func testWaveformPeaks() {
        let p = Waveform.peaks([0, 0.5, -1, 0.2], buckets: 2)
        XCTAssertEqual(p, [0.5, 1])
    }
}

final class GamepadTests: XCTestCase {
    func run(_ m: GamepadMapper, _ s: PadState, _ t: inout Double) -> [PadCommand] { t += 16; return m.update(s, dt: 0.016, now: t) }

    func testFaceButtonsAndFocus() {
        let m = GamepadMapper(); var t = 0.0
        XCTAssertEqual(run(m, PadState(buttons: [.a]), &t), [.kill(deck: 0, band: 0)])
        _ = run(m, PadState(), &t)
        XCTAssertEqual(run(m, PadState(buttons: [.rb]), &t), [.focus(1)])
        _ = run(m, PadState(), &t)
        XCTAssertEqual(run(m, PadState(buttons: [.b]), &t), [.loop(deck: 1)])
    }
    func testViewTapIsTapBPMAndHoldIsShift() {
        let m = GamepadMapper(); var t = 0.0
        _ = run(m, PadState(buttons: [.view]), &t)
        XCTAssertEqual(run(m, PadState(), &t), [.tapBPM(deck: 0)])
        _ = run(m, PadState(buttons: [.view]), &t)
        XCTAssertEqual(run(m, PadState(buttons: [.view, .a]), &t), [.hotCue(deck: 0, 0)])
        _ = run(m, PadState(buttons: [.view]), &t)
        XCTAssertEqual(run(m, PadState(), &t), [], "View used as Shift: no tap BPM on release")
    }
    func testPanicOnceAndNoSync() {
        let m = GamepadMapper(); var t = 0.0; var all: [PadCommand] = []
        all += run(m, PadState(buttons: [.view]), &t)
        for _ in 0..<40 { all += run(m, PadState(buttons: [.view, .menu]), &t) }
        all += run(m, PadState(), &t)
        XCTAssertEqual(all.filter { $0 == .panic }.count, 1)
        XCTAssertFalse(all.contains(.sync(deck: 0))); XCTAssertFalse(all.contains(.playPause(deck: 0)))
    }
    func testMenuTapSyncs() {
        let m = GamepadMapper(); var t = 0.0
        _ = run(m, PadState(buttons: [.menu]), &t)
        XCTAssertEqual(run(m, PadState(), &t), [.sync(deck: 0)])
    }
    func testFilterSpringAndRelease() {
        let m = GamepadMapper(); var t = 0.0; var last: PadCommand?
        for _ in 0..<30 { last = run(m, PadState(ly: 1), &t).last }
        guard case .filterSpring(deck: 0, let v?) = last else { return XCTFail("\(String(describing: last))") }
        XCTAssertGreaterThan(v, 0.9)
        var released = false
        for _ in 0..<60 { if run(m, PadState(), &t).contains(.filterSpring(deck: 0, nil)) { released = true } }
        XCTAssertTrue(released, "glides back then lets go")
        XCTAssertEqual(run(m, PadState(), &t), [], "at rest: nothing written")
    }
    func testSmallNudgeIgnoredAndAxisLock() {
        let m = GamepadMapper(); var t = 0.0
        XCTAssertEqual(run(m, PadState(ly: 0.14), &t), [])
        let c = run(m, PadState(lx: 0.9, ly: 0.2), &t)
        XCTAssertTrue(c.contains { if case .crossfaderBy(let d) = $0 { return d > 0 } else { return false } })
        XCTAssertFalse(c.contains { if case .filterSpring = $0 { return true } else { return false } })
    }
    func testRollTriggerAndHeldButton() {
        let m = GamepadMapper(); var t = 0.0
        XCTAssertEqual(run(m, PadState(rt: 0.2), &t), [.roll(deck: 0, beats: 0.25)])
        XCTAssertEqual(run(m, PadState(rt: 1), &t), [.roll(deck: 0, beats: 0.03125)])
        XCTAssertEqual(run(m, PadState(), &t), [.roll(deck: 0, beats: nil)])
        XCTAssertEqual(run(m, PadState(buttons: [.x]), &t), [.roll(deck: 0, beats: 0.25)])
        XCTAssertEqual(run(m, PadState(), &t), [.roll(deck: 0, beats: nil)])
    }
    func testEchoThrowAndShiftBrake() {
        let m = GamepadMapper(); var t = 0.0
        XCTAssertEqual(run(m, PadState(lt: 0.7), &t), [.echoThrow(deck: 0, 0.7)])
        XCTAssertEqual(run(m, PadState(), &t), [.echoThrow(deck: 0, 0)])
        _ = run(m, PadState(buttons: [.view]), &t)
        XCTAssertEqual(run(m, PadState(lt: 1, buttons: [.view]), &t), [.brake(deck: 0, true)])
        XCTAssertEqual(run(m, PadState(buttons: [.view]), &t), [.brake(deck: 0, false)])
    }
}
