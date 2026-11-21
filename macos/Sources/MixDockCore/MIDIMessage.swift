import Foundation

public enum MIDIKind: String, Codable, Hashable, Sendable {
    case note, cc, pitchBend
}

/// One channel-voice message from a controller. Values: note velocity / CC value 0...127, pitch bend 0...16383.
public struct MIDIMessage: Equatable, Sendable {
    public var kind: MIDIKind
    public var channel: UInt8      // 0...15
    public var number: UInt8       // note / CC number (0 for pitch bend)
    public var value: Int
    public var isNoteOff: Bool

    public init(kind: MIDIKind, channel: UInt8, number: UInt8, value: Int, isNoteOff: Bool = false) {
        self.kind = kind; self.channel = channel; self.number = number; self.value = value; self.isNoteOff = isNoteOff
    }

    public var control: ControlID { ControlID(kind: kind, channel: channel, number: kind == .pitchBend ? 0 : number) }

    /// Parses a raw MIDI 1.0 byte stream (one packet may hold several messages, running status, realtime bytes, SysEx).
    /// Only note on/off, CC and pitch bend come back; everything else is skipped.
    public static func parse(_ bytes: [UInt8]) -> [MIDIMessage] {
        var out: [MIDIMessage] = []
        var i = 0
        var running: UInt8 = 0
        while i < bytes.count {
            let b = bytes[i]
            if b >= 0xF8 { i += 1; continue }                       // realtime (clock, start, stop...) can sit anywhere
            if b == 0xF0 {                                           // SysEx: skip to F7
                i += 1
                while i < bytes.count && bytes[i] != 0xF7 { i += 1 }
                i += 1; running = 0; continue
            }
            if b >= 0xF0 { i += 1; running = 0; continue }          // other system common
            var status = running
            if b >= 0x80 { status = b; running = b; i += 1 }
            guard status >= 0x80 else { i += 1; continue }           // stray data byte, no running status
            let type = status & 0xF0, ch = status & 0x0F
            let dataCount = (type == 0xC0 || type == 0xD0) ? 1 : 2
            guard i + dataCount <= bytes.count else { break }
            let d1 = bytes[i] & 0x7F, d2 = dataCount == 2 ? bytes[i + 1] & 0x7F : 0
            i += dataCount
            switch type {
            case 0x80: out.append(MIDIMessage(kind: .note, channel: ch, number: d1, value: 0, isNoteOff: true))
            case 0x90: out.append(MIDIMessage(kind: .note, channel: ch, number: d1, value: Int(d2), isNoteOff: d2 == 0))
            case 0xB0: out.append(MIDIMessage(kind: .cc, channel: ch, number: d1, value: Int(d2)))
            case 0xE0: out.append(MIDIMessage(kind: .pitchBend, channel: ch, number: 0, value: Int(d2) << 7 | Int(d1)))
            default: break
            }
        }
        return out
    }
}

/// A physical control: message type + channel + number. Pitch bend uses number 0.
public struct ControlID: Hashable, Codable, Sendable, CustomStringConvertible {
    public var kind: MIDIKind
    public var channel: UInt8
    public var number: UInt8
    public init(kind: MIDIKind, channel: UInt8, number: UInt8) { self.kind = kind; self.channel = channel; self.number = number }
    public var description: String {
        switch kind {
        case .note: return "Ch \(channel + 1) · Note \(number)"
        case .cc: return "Ch \(channel + 1) · CC \(number)"
        case .pitchBend: return "Ch \(channel + 1) · Pitch bend"
        }
    }
}
