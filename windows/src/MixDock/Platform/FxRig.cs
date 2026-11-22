using MixDock.Audio;

namespace MixDock.Platform;

/// <summary>Where the FX pad (controller 2) plays: the mixer's focus deck, deck A, deck B, or both decks (the master).</summary>
public enum FxTarget { Focus, A, B, Both }

/// <summary>
/// UI-thread model of the stick FX: the current set of each controller, the 4-direction stick pads and the momentary
/// holds, turned into per-deck <see cref="FxRack.Controls"/> every poll. Pure (no audio, no UI), unit-tested.
/// Per poll: <see cref="Begin"/>, then every stick reports (<see cref="Stick"/> / <see cref="Side"/>), then
/// <see cref="Build"/> per deck. Holds persist until released.
/// </summary>
public sealed class FxRig
{
    public sealed class StickState
    {
        public readonly double[] Shares = new double[4];   // up, down, left, right (side pads use left/right only)
        public double Amount; public int Set; public int Mask; public bool Side; public bool Active; public long ActiveAt;
        /// <summary>Strongest slot right now (0..3 = up/down/left/right on a pad; 4/5 = side left/right).</summary>
        public int Lead;
        public StickFx LeadFx => FxCatalog.Sets[FxCatalog.WrapSet(Set)].Slot(Lead);
    }
    private sealed record Hold(int Mask, StickFx Fx, float Param);

    public int Set { get; private set; }
    public int PadSet { get; private set; } = 1;
    public FxTarget Target { get; private set; }
    public double Deadzone { get; set; } = FxCurves.DefaultDeadzone;
    private readonly Dictionary<string, StickState> _sticks = new();
    private readonly Dictionary<string, Hold> _holds = new();
    private readonly float[,] _drive = new float[2, FxCatalog.Count], _mix = new float[2, FxCatalog.Count], _param = new float[2, FxCatalog.Count];
    private readonly int[] _subDrops = new int[2];
    public IReadOnlyDictionary<string, StickState> Sticks => _sticks;
    public int HoldCount => _holds.Count;

    public FxRig(int set = 0, int padSet = 1) { Set = FxCatalog.WrapSet(set); PadSet = FxCatalog.WrapSet(padSet); }

    public int StepSet(int delta) => Set = FxCatalog.WrapSet(Set + delta);
    public int StepPadSet(int delta) => PadSet = FxCatalog.WrapSet(PadSet + delta);
    public FxTarget StepTarget(int delta) => Target = (FxTarget)((((int)Target + delta) % 4 + 4) % 4);
    public void SetTarget(FxTarget t) => Target = t;

    /// <summary>Deck bit mask: 1 = A, 2 = B, 3 = both.</summary>
    public static int Mask(FxTarget t, int focus) => t switch { FxTarget.A => 1, FxTarget.B => 2, FxTarget.Both => 3, _ => focus == 0 ? 1 : 2 };
    public static string TargetName(FxTarget t) => t switch { FxTarget.A => "DECK A", FxTarget.B => "DECK B", FxTarget.Both => "BOTH DECKS", _ => "FOCUS DECK" };

    public void Begin()
    {
        Array.Clear(_drive); Array.Clear(_mix); Array.Clear(_param);
        foreach (var s in _sticks.Values) s.Active = false;
    }

    /// <summary>A 4-direction pad: distance = amount (radial deadzone + soft curve), direction shares morph between the two
    /// neighbouring slots; near the centre the last direction is kept, so letting go never flips the effect.</summary>
    public StickState Stick(string id, int mask, int set, double x, double y, long nowMs = 0)
    {
        var s = State(id); s.Set = FxCatalog.WrapSet(set); s.Mask = mask; s.Side = false;
        s.Amount = FxCurves.Amount(x, y, Deadzone);
        Span<double> sh = stackalloc double[4];
        if (StickMorph.Directions(x, y, sh, Math.Max(.05, Deadzone))) sh.CopyTo(s.Shares);
        Engage(s, nowMs);
        var fs = FxCatalog.Sets[s.Set];
        if (s.Amount > 0) for (int k = 0; k < 4; k++) Apply(mask, fs.Slot(k), s.Amount, s.Shares[k]);
        s.Lead = s.Shares[0] >= s.Shares[1] && s.Shares[0] >= s.Shares[2] && s.Shares[0] >= s.Shares[3] ? 0 : s.Shares[1] >= s.Shares[2] && s.Shares[1] >= s.Shares[3] ? 1 : s.Shares[2] >= s.Shares[3] ? 2 : 3;
        return s;
    }

    /// <summary>The two side slots of a set on one axis (left stick X): left = slot 4, right = slot 5.</summary>
    public StickState Side(string id, int mask, int set, double x, long nowMs = 0)
    {
        var s = State(id); s.Set = FxCatalog.WrapSet(set); s.Mask = mask; s.Side = true;
        s.Amount = FxCurves.Amount(x, 0, Deadzone);
        if (s.Amount > 0) { var (l, r) = StickMorph.Side(x); s.Shares[2] = l; s.Shares[3] = r; s.Shares[0] = s.Shares[1] = 0; }
        Engage(s, nowMs);
        var fs = FxCatalog.Sets[s.Set];
        if (s.Amount > 0) { Apply(mask, fs.SideLeft, s.Amount, s.Shares[2]); Apply(mask, fs.SideRight, s.Amount, s.Shares[3]); }
        s.Lead = s.Shares[2] >= s.Shares[3] ? 4 : 5;
        return s;
    }

    private StickState State(string id) { if (!_sticks.TryGetValue(id, out var s)) _sticks[id] = s = new StickState(); return s; }
    private static void Engage(StickState s, long now) { bool was = s.ActiveAt > 0; s.Active = s.Amount > 0; if (s.Active && !was) s.ActiveAt = Math.Max(1, now); else if (!s.Active) s.ActiveAt = 0; }

    private void Apply(int mask, StickFx fx, double drive, double mix)
    {
        int u = (int)fx;
        for (int d = 0; d < 2; d++)
        {
            if ((mask & (1 << d)) == 0) continue;
            _drive[d, u] = Math.Max(_drive[d, u], (float)drive);
            _mix[d, u] = Math.Max(_mix[d, u], (float)mix);
        }
    }

    /// <summary>Button actions that are rack effects: stutters, transformer gates, cut, grain freeze, sub drop.</summary>
    public static bool TryHold(string control, out StickFx fx, out float param)
    {
        param = 0; fx = StickFx.Cut;
        switch (control)
        {
            case "Cut": fx = StickFx.Cut; return true;
            case "GrainFreeze": fx = StickFx.Freeze; return true;
            case "SubDrop": fx = StickFx.SubDrop; return true;
        }
        // note values: 1/4 = one beat, 1/8 = half a beat, 1/16 = a quarter beat ... (param = length / period in beats)
        if (control.StartsWith("Stutter:")) { fx = StickFx.BeatRepeat; param = (float)(Note(control[8..]) * 4); return param > 0; }
        if (control.StartsWith("Transformer:")) { fx = StickFx.Transformer; param = (float)(Note(control[12..]) * 4); return param > 0; }
        return false;

        static double Note(string s)
        {
            var p = s.Split('/');
            return p.Length == 2 && double.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out var n) &&
                   double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0 ? n / m : 0;
        }
    }

    /// <summary>Starts a momentary effect (held until <see cref="Release"/>). A sub drop fires once per press.</summary>
    public bool Press(string holdId, int mask, string control)
    {
        if (!TryHold(control, out var fx, out float param)) return false;
        if (fx == StickFx.SubDrop) for (int d = 0; d < 2; d++) if ((mask & (1 << d)) != 0) _subDrops[d]++;
        _holds[holdId] = new Hold(mask, fx, param);
        return true;
    }
    public void Release(string holdId) => _holds.Remove(holdId);
    /// <summary>Releases every hold whose id starts with the prefix (a controller that disconnected, its role prefix).</summary>
    public void ReleaseAll(string prefix) { foreach (var k in _holds.Keys.Where(k => k.StartsWith(prefix)).ToList()) _holds.Remove(k); }
    /// <summary>FX clear: every hold released and every stick forgotten (the rack then glides back to dry).</summary>
    public void Clear() { _holds.Clear(); _sticks.Clear(); Begin(); }

    public FxRack.Controls Build(int deck, double bpm, double phase, long stamp, double smoothMs)
    {
        var c = new FxRack.Controls { Bpm = bpm, Phase = phase, Stamp = stamp, SmoothMs = smoothMs };
        for (int u = 0; u < FxCatalog.Count; u++) { c.Drive[u] = _drive[deck, u]; c.Mix[u] = _mix[deck, u]; }
        foreach (var h in _holds.Values)
        {
            if ((h.Mask & (1 << deck)) == 0) continue;
            int u = (int)h.Fx;
            c.Drive[u] = 1; c.Mix[u] = 1;
            if (h.Param > 0) c.Param[u] = h.Param;
        }
        c.Param[(int)StickFx.SubDrop] = _subDrops[deck];
        return c;
    }

    /// <summary>True when any stick or hold drives an effect on the deck.</summary>
    public bool Any(int deck)
    {
        for (int u = 0; u < FxCatalog.Count; u++) if (_drive[deck, u] > 0) return true;
        return _holds.Values.Any(h => (h.Mask & (1 << deck)) != 0);
    }
}
