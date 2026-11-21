import CoreMIDI
import GameController
import MixDockCore

/// Every USB / Bluetooth MIDI controller, connected automatically (plug in any time).
final class MIDIService {
    var onMessage: ((MIDIMessage) -> Void)?
    var onDevices: (([String]) -> Void)?
    private var client = MIDIClientRef()
    private var port = MIDIPortRef()
    private var connected: Set<MIDIEndpointRef> = []

    func start() {
        let status = MIDIClientCreateWithBlock("MixDock" as CFString, &client) { [weak self] note in
            if note.pointee.messageID == .msgSetupChanged { DispatchQueue.main.async { self?.connectAll() } }
        }
        guard status == noErr else { return }
        MIDIInputPortCreateWithBlock(client, "MixDock in" as CFString, &port) { [weak self] packets, _ in
            var msgs: [MIDIMessage] = []
            for p in packets.unsafeSequence() {
                let len = Int(p.pointee.length)
                let bytes = withUnsafeBytes(of: p.pointee.data) { Array($0.prefix(min(len, 256))) }
                msgs += MIDIMessage.parse(bytes)
            }
            guard !msgs.isEmpty else { return }
            DispatchQueue.main.async { msgs.forEach { self?.onMessage?($0) } }
        }
        connectAll()
    }

    func connectAll() {
        var names: [String] = []
        var now: Set<MIDIEndpointRef> = []
        for i in 0..<MIDIGetNumberOfSources() {
            let src = MIDIGetSource(i)
            guard src != 0 else { continue }
            now.insert(src)
            if !connected.contains(src) { MIDIPortConnectSource(port, src, nil) }
            names.append(Self.name(src))
        }
        connected = now
        onDevices?(names)
    }

    static func name(_ obj: MIDIObjectRef) -> String {
        var s: Unmanaged<CFString>?
        MIDIObjectGetStringProperty(obj, kMIDIPropertyDisplayName, &s)
        return (s?.takeRetainedValue() as String?) ?? "MIDI device"
    }
}

/// Xbox One S / Series / PlayStation pads via Apple's GameController framework (Bluetooth or USB, no driver).
final class GamepadService {
    var onChange: ((String?) -> Void)?
    private(set) var name: String?
    private var started = false

    func start() {
        guard !started else { return }
        started = true
        GCController.shouldMonitorBackgroundEvents = true   // keep working while another window has focus
        let nc = NotificationCenter.default
        nc.addObserver(forName: .GCControllerDidConnect, object: nil, queue: .main) { [weak self] _ in self?.refresh() }
        nc.addObserver(forName: .GCControllerDidDisconnect, object: nil, queue: .main) { [weak self] _ in self?.refresh() }
        GCController.startWirelessControllerDiscovery {}
        refresh()
    }

    private func refresh() {
        name = GCController.controllers().first { $0.extendedGamepad != nil }?.vendorName ?? nil
        if GCController.controllers().contains(where: { $0.extendedGamepad != nil }) && name == nil { name = "Game controller" }
        onChange?(name)
    }

    func read() -> PadState? {
        guard let g = GCController.controllers().first(where: { $0.extendedGamepad != nil })?.extendedGamepad else { return nil }
        var b: PadButtons = []
        if g.buttonA.isPressed { b.insert(.a) }
        if g.buttonB.isPressed { b.insert(.b) }
        if g.buttonX.isPressed { b.insert(.x) }
        if g.buttonY.isPressed { b.insert(.y) }
        if g.leftShoulder.isPressed { b.insert(.lb) }
        if g.rightShoulder.isPressed { b.insert(.rb) }
        if g.dpad.up.isPressed { b.insert(.up) }
        if g.dpad.down.isPressed { b.insert(.down) }
        if g.dpad.left.isPressed { b.insert(.left) }
        if g.dpad.right.isPressed { b.insert(.right) }
        if g.leftThumbstickButton?.isPressed == true { b.insert(.l3) }
        if g.rightThumbstickButton?.isPressed == true { b.insert(.r3) }
        if g.buttonOptions?.isPressed == true { b.insert(.view) }
        if g.buttonMenu.isPressed { b.insert(.menu) }
        return PadState(lx: Double(g.leftThumbstick.xAxis.value), ly: Double(g.leftThumbstick.yAxis.value),
                        rx: Double(g.rightThumbstick.xAxis.value), ry: Double(g.rightThumbstick.yAxis.value),
                        lt: Double(g.leftTrigger.value), rt: Double(g.rightTrigger.value), buttons: b)
    }
}
