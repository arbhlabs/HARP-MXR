namespace MixDock.Platform;

public enum PadEventKind { Press, Release, Repeat, Panic, PreviousProfile, NextProfile }

/// <summary>One dispatched controller action. Shift = the action came from the shift layer.</summary>
public readonly record struct PadEvent(PadEventKind Kind, string Action, PadButton Button, bool Shift);

/// <summary>
/// Turns raw button states into actions, without hardware or UI (unit-tested).
/// - Normal buttons fire on press (Release follows for hold actions such as loop rolls); "+"/"-" actions repeat while held.
/// - Modifier buttons (the shift button, shift-chord members and panic-chord members) fire their own action on
///   RELEASE as a tap (held &lt; TapMaxMs and not used as a modifier), so holding View or LB+RB never triggers them.
/// - Shift = shift button held, or every shift-chord button held. Every button has a second action in the shift layer.
/// - Panic chord held for PanicHoldMs fires one Panic event.
/// - Debounce: a re-press within DebounceMs of the last press is ignored (contact bounce).
/// </summary>
public sealed class PadMapper
{
    public const int DebounceMs = 40, TapMaxMs = 350, RepeatDelayMs = 350, RepeatEveryMs = 60;
    private readonly Dictionary<PadButton, string> _normal = new(), _shift = new();
    private readonly PadButton _shiftButton, _shiftChord, _panicChord, _modifiers;
    private readonly bool _profileShortcuts;
    private PadButton _profileShoulder;
    private long _profileAt;
    public int PanicHoldMs { get; }

    private PadButton _prev, _consumed;
    private readonly Dictionary<PadButton, long> _pressedAt = new(), _lastPress = new(), _lastFire = new();
    private readonly Dictionary<PadButton, (string Action, bool Shift)> _active = new();
    private long _panicSince = -1; private bool _panicFired;

    public PadButton Held => _prev;
    public bool Shift => IsShift(_prev, 0);
    public bool PanicArmed => _panicSince >= 0 && !_panicFired;

    public PadMapper(IReadOnlyDictionary<string, string> buttons, IReadOnlyDictionary<string, string> shiftButtons,
        string? shiftButton = "Back", string? shiftChord = "LeftShoulder+RightShoulder", string? panicChord = "Back+Start",
        int panicHoldMs = 500, bool profileShortcuts = false)
    {
        foreach (var (k, v) in buttons) if (Parse(k) is PadButton b && b != 0 && !string.IsNullOrWhiteSpace(v)) _normal[b] = v;
        foreach (var (k, v) in shiftButtons) if (Parse(k) is PadButton b && b != 0 && !string.IsNullOrWhiteSpace(v)) _shift[b] = v;
        _shiftButton = ParseAny(shiftButton);
        _shiftChord = Parse(shiftChord) ?? 0;
        if (System.Numerics.BitOperations.PopCount((uint)_shiftChord) < 2) _shiftChord = 0;
        _panicChord = Parse(panicChord) ?? 0;
        if (System.Numerics.BitOperations.PopCount((uint)_panicChord) < 2) _panicChord = 0;
        PanicHoldMs = Math.Clamp(panicHoldMs, 100, 5000);
        _modifiers = _shiftButton | _shiftChord | _panicChord;
        _profileShortcuts = profileShortcuts;
    }

    /// <summary>"A", "LeftShoulder+RightShoulder" -> flags. null when any part is unknown.</summary>
    public static PadButton? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        PadButton r = 0;
        foreach (var part in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<PadButton>(part, true, out var b) || !Enum.IsDefined(b)) return null;
            r |= b;
        }
        return r;
    }

    /// <summary>"Back,Extra1" -> either button is the shift button (a pad with a paddle keeps View as shift too).</summary>
    public static PadButton ParseAny(string? s)
    {
        PadButton r = 0;
        foreach (var part in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Parse(part) is PadButton b) r |= b;
        return r;
    }

    public bool IsModifier(PadButton b) => (_modifiers & b) != 0;
    public string? ActionFor(PadButton b, bool shift)
        => shift ? (_shift.TryGetValue(b, out var s) ? s : null) : (_normal.TryGetValue(b, out var n) ? n : null);

    private bool IsShift(PadButton held, PadButton exclude)
        => (_shiftButton != 0 && (held & ~exclude & _shiftButton) != 0)
        || (_shiftChord != 0 && (_shiftChord & exclude) == 0 && (held & _shiftChord) == _shiftChord);

    public static bool IsRepeatable(string action) => action.EndsWith('+') || action.EndsWith('-');

    /// <summary>Forget everything; buttons already held count as held (no phantom presses on reconnect).</summary>
    public void Reset(PadButton current)
    {
        _prev = current; _consumed = current; _active.Clear(); _pressedAt.Clear(); _panicSince = -1; _panicFired = false;
        _profileShoulder = 0;
    }

    public List<PadEvent> Update(PadButton buttons, long nowMs)
    {
        var events = new List<PadEvent>();
        var pressed = buttons & ~_prev; var released = _prev & ~buttons;
        var reserved = _profileShortcuts ? ProfileChord(buttons, pressed, nowMs, events) : 0;

        foreach (PadButton b in Enum.GetValues<PadButton>())
        {
            if ((pressed & b) == 0 || (reserved & b) != 0) continue;
            if (_lastPress.TryGetValue(b, out long lp) && nowMs - lp < DebounceMs) { _consumed |= b; continue; }
            _lastPress[b] = nowMs; _pressedAt[b] = nowMs; _consumed &= ~b;
            // other held modifiers become modifiers now (their tap is cancelled); forming a chord consumes all its members
            var others = (buttons & ~b) & _modifiers;
            _consumed |= others;
            if (_shiftChord != 0 && (_shiftChord & b) != 0 && (buttons & _shiftChord) == _shiftChord) _consumed |= _shiftChord;
            if (IsModifier(b)) continue;                               // fires on release as a tap
            bool shift = IsShift(buttons, b);
            var action = ActionFor(b, shift);
            if (action is null) continue;
            _active[b] = (action, shift); _lastFire[b] = nowMs;
            events.Add(new(PadEventKind.Press, action, b, shift));
        }

        // panic chord
        if (_panicChord != 0 && (buttons & _panicChord) == _panicChord)
        {
            if (_panicSince < 0) _panicSince = nowMs;
            if (!_panicFired && nowMs - _panicSince >= PanicHoldMs)
            {
                _panicFired = true; _consumed |= _panicChord;
                events.Add(new(PadEventKind.Panic, "Panic", _panicChord, false));
            }
        }
        else { _panicSince = -1; _panicFired = false; }

        foreach (PadButton b in Enum.GetValues<PadButton>())
        {
            if ((released & b) == 0) continue;
            if (_active.Remove(b, out var a)) events.Add(new(PadEventKind.Release, a.Action, b, a.Shift));
            else if (IsModifier(b))
            {
                bool tap = (_consumed & b) == 0 && _pressedAt.TryGetValue(b, out long at) && nowMs - at <= TapMaxMs;
                _consumed &= ~b;
                if (!tap) continue;
                bool shift = IsShift(buttons, b);
                var action = ActionFor(b, shift);
                if (action is null) continue;
                events.Add(new(PadEventKind.Press, action, b, shift));
                events.Add(new(PadEventKind.Release, action, b, shift));
            }
            _consumed &= ~b;
        }

        // held repeat for +/- nudges
        foreach (var (b, a) in _active)
        {
            if ((buttons & b) == 0 || !IsRepeatable(a.Action)) continue;
            if (nowMs - _pressedAt[b] > RepeatDelayMs && nowMs - _lastFire[b] >= RepeatEveryMs)
            {
                _lastFire[b] = nowMs;
                events.Add(new(PadEventKind.Repeat, a.Action, b, a.Shift));
            }
        }
        _prev = buttons;
        return events;
    }

    private PadButton ProfileChord(PadButton buttons, PadButton pressed, long now, List<PadEvent> events)
    {
        const PadButton View = PadButton.Back;
        if (_profileShoulder == 0)
        {
            var shoulder = buttons & ~View;
            if ((buttons & View) == 0 || (pressed & shoulder) == 0 ||
                shoulder is not (PadButton.LeftShoulder or PadButton.RightShoulder)) return 0;
            _profileShoulder = shoulder;
            _profileAt = now;
            _consumed |= buttons;
            return buttons;
        }

        var pending = _profileShoulder;
        var chord = View | pending;
        if ((buttons & ~chord) != 0)
        {
            _profileShoulder = 0;
            return 0;
        }
        if (buttons == chord && now - _profileAt < TapMaxMs) return chord;
        _profileShoulder = 0;
        if (now - _profileAt < TapMaxMs)
        {
            events.Add(new(pending == PadButton.LeftShoulder ? PadEventKind.PreviousProfile : PadEventKind.NextProfile, "", pending, false));
        }
        else
        {
            bool shift = IsShift(chord, pending);
            if (ActionFor(pending, shift) is string action)
            {
                events.Add(new(PadEventKind.Press, action, pending, shift));
                if ((buttons & pending) == 0) events.Add(new(PadEventKind.Release, action, pending, shift));
                else
                {
                    _active[pending] = (action, shift);
                    _pressedAt[pending] = _lastFire[pending] = now;
                }
            }
        }
        return chord;
    }
}
