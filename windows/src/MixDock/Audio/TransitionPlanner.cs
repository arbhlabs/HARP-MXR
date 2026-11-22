namespace MixDock.Audio;

public enum TransitionStyle
{
    BassSwap, FilterBlend, EchoOut, DropSwap, DoubleDrop, LoopRoll, TempoRamp, PowerDown, BreakdownEntry
}
public enum EnergyArc { WarmUp, Peak, CoolDown, FollowTheRoom }
public enum LengthBias { Short, Normal, Long }
public enum FxIntensity { Subtle, Normal, Wild }

public sealed record MixPreferences(DjGenre Genre = DjGenre.Auto, EnergyArc Arc = EnergyArc.FollowTheRoom,
    LengthBias Length = LengthBias.Normal, FxIntensity Fx = FxIntensity.Normal, bool Dramatic = false);

public sealed record TransitionPlan(TransitionStyle Style, double StartSec, double LandingSec, double IncomingSec,
    double DurationSec, double BeatSec, double TempoRatio, bool HalfDouble, int StartBar, int Bars, string Why,
    double OutGain, double InGain, double FxDepth, bool ProtectVocals, double DriftBpmPerSec = 0, DjGenre Genre = DjGenre.OpenFormat)
{
    public bool Blends => Style is TransitionStyle.BassSwap or TransitionStyle.FilterBlend or TransitionStyle.DoubleDrop
        or TransitionStyle.LoopRoll or TransitionStyle.TempoRamp or TransitionStyle.BreakdownEntry;
    public double EndSec => StartSec + DurationSec;
    public string Label => TransitionPlanner.Label(Style);
}

/// <summary>How a genre is mixed: extra score per style, typical blend lengths in bars, FX amount and how often a
/// dramatic move (power-down) is allowed when the owner enables them.</summary>
public sealed record GenreProfile(DjGenre Genre, string Name, IReadOnlyDictionary<TransitionStyle, double> Weight,
    int SwapBars, int BlendBars, double FxScale, double DramaticRate)
{
    public double WeightOf(TransitionStyle style) => Weight.TryGetValue(style, out var w) ? w : 0;
}

/// <summary>Ranks only transitions that fit the tracks, with deterministic variation among closely ranked candidates.</summary>
public static class TransitionPlanner
{
    private static readonly TransitionStyle[] Styles = Enum.GetValues<TransitionStyle>();

    private static GenreProfile P(DjGenre g, string name, int swap, int blend, double fx, double dramatic,
        params (TransitionStyle Style, double Weight)[] w) => new(g, name, w.ToDictionary(x => x.Style, x => x.Weight), swap, blend, fx, dramatic);

    public static readonly IReadOnlyDictionary<DjGenre, GenreProfile> Profiles = new[]
    {
        P(DjGenre.House, "House / Tech House", 16, 8, 1, .05, (TransitionStyle.BassSwap, 5), (TransitionStyle.FilterBlend, 7)),
        P(DjGenre.Techno, "Techno", 16, 8, 1.1, .05, (TransitionStyle.FilterBlend, 7), (TransitionStyle.LoopRoll, 8)),
        P(DjGenre.Trance, "Trance", 32, 16, 1, .05, (TransitionStyle.FilterBlend, 7), (TransitionStyle.BreakdownEntry, 4)),
        P(DjGenre.DrumAndBass, "Drum & Bass", 16, 8, 1.1, .15, (TransitionStyle.DropSwap, 19), (TransitionStyle.LoopRoll, 8)),
        P(DjGenre.Dubstep, "Dubstep / Bass", 16, 8, 1.2, .2, (TransitionStyle.DropSwap, 19), (TransitionStyle.PowerDown, 4)),
        P(DjGenre.HipHop, "Hip-Hop / R&B", 16, 4, .8, .1, (TransitionStyle.EchoOut, 9)),
        P(DjGenre.Pop, "Pop / Radio", 16, 4, .8, .1, (TransitionStyle.EchoOut, 9)),
        P(DjGenre.AfroLatin, "Afro / Latin", 16, 8, .9, .05, (TransitionStyle.FilterBlend, 7), (TransitionStyle.BreakdownEntry, 2)),
        P(DjGenre.OpenFormat, "Open Format", 16, 8, .9, .1, (TransitionStyle.EchoOut, 4), (TransitionStyle.FilterBlend, 4)),
    }.ToDictionary(p => p.Genre);

    public static GenreProfile Profile(DjGenre genre) => Profiles.TryGetValue(genre, out var p) ? p : Profiles[DjGenre.OpenFormat];

    /// <summary>Genre guess for a stream that has only a tempo and an energy rating (live decks, long DJ mixes).</summary>
    public static DjGenre GuessGenre(double bpm, double energy)
    {
        if (!(bpm is >= 60 and <= 200)) return DjGenre.OpenFormat;
        if (bpm >= 155) return DjGenre.DrumAndBass;
        if (bpm >= 134) return energy >= 7 ? DjGenre.Techno : DjGenre.Trance;
        if (bpm <= 103) return DjGenre.HipHop;
        if (bpm is >= 113 and <= 133) return energy >= 7.5 ? DjGenre.Techno : DjGenre.House;
        return DjGenre.Pop;
    }

    public static string Label(TransitionStyle style) => style switch
    {
        TransitionStyle.BassSwap => "Long blend · bass swap",
        TransitionStyle.FilterBlend => "Filter blend",
        TransitionStyle.EchoOut => "Echo out",
        TransitionStyle.DropSwap => "Drop swap · on the one",
        TransitionStyle.DoubleDrop => "Double drop",
        TransitionStyle.LoopRoll => "Loop roll out",
        TransitionStyle.TempoRamp => "Tempo ramp",
        TransitionStyle.PowerDown => "Power-down",
        _ => "Breakdown entry"
    };

    public static (double Rate, bool HalfDouble) TempoRatio(double outgoing, double incoming)
    {
        if (!(outgoing > 0 && incoming > 0)) return (1, false);
        var direct = outgoing / incoming;
        var best = direct;
        foreach (double octave in new[] { 2d, .5 })
        {
            double candidate = direct * octave;
            if (Math.Abs(Math.Log(candidate)) < Math.Abs(Math.Log(best))) best = candidate;
        }
        return (best, best != direct);
    }

    // The +2 move is directional. It can raise energy with stronger phrase and tempo agreement.
    public static int Harmony(MusicalKey? outgoing, MusicalKey? incoming)
    {
        if (outgoing is not MusicalKey a || incoming is not MusicalKey b) return 0;
        if (MusicalKey.Compatible(a, b)) return a == b ? 3 : 2;
        return a.Minor == b.Minor && (b.CamelotNumber - a.CamelotNumber + 12) % 12 == 2 ? 1 : -3;
    }

    public static double TargetEnergy(EnergyArc arc, int played, double current)
    {
        double progress = Math.Clamp(played / 8.0, 0, 1);
        return arc switch
        {
            EnergyArc.WarmUp => 3.5 + progress * 3,
            EnergyArc.Peak => 7.5 + progress * 2,
            EnergyArc.CoolDown => 7.5 - progress * 4,
            _ => Math.Clamp(current, 1, 10)
        };
    }

    public static double NextTrackScore(TrackAnalysis outgoing, TrackAnalysis incoming, MixPreferences preferences, int played)
    {
        var (rate, _) = TempoRatio(outgoing.Tempo?.Bpm ?? 0, incoming.Tempo?.Bpm ?? 0);
        double tempo = outgoing.Tempo == null || incoming.Tempo == null ? -4 : -65 * Math.Abs(Math.Log(rate));
        var current = At(outgoing, outgoing.MixOutSec)?.Energy ?? 5;
        var next = At(incoming, incoming.MixInSec)?.Energy ?? 5;
        int harmony = Harmony(ReliableKey(outgoing), ReliableKey(incoming));
        return tempo + 3 * harmony - 1.8 * Math.Abs(next - TargetEnergy(preferences.Arc, played, current));
    }

    public static string? PickNext(TrackAnalysis outgoing, MixPreferences preferences, int played,
        IEnumerable<(string Path, string Artist, TrackAnalysis Analysis)> library,
        IReadOnlyList<string> recent, string? currentArtist, IReadOnlySet<string> loaded)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        return library.Where(track => !loaded.Contains(track.Path))
            .OrderBy(track => recent.Contains(track.Path, comparer))
            .ThenBy(track => !string.IsNullOrWhiteSpace(currentArtist) && comparer.Equals(currentArtist, track.Artist))
            .ThenByDescending(track => NextTrackScore(outgoing, track.Analysis, preferences, played))
            .ThenBy(track => track.Path, comparer)
            .Select(track => track.Path)
            .FirstOrDefault();
    }

    public static TransitionPlan? Choose(TrackAnalysis? outgoing, TrackAnalysis? incoming, double outgoingSec,
        double incomingSec, MixPreferences preferences, bool now = false, int seed = 0, TransitionStyle? skip = null,
        bool canCueIncoming = true, double driftBpmPerSec = 0)
    {
        if (outgoing?.Tempo is not { Bpm: >= 60 and <= 200, Confidence: >= .05 } tempo || !double.IsFinite(outgoingSec)) return null;
        double origin = (tempo.DownKnown ? tempo.OriginFrame : tempo.PhraseStartFrame) / 44100;
        if (!double.IsFinite(origin)) origin = tempo.OriginFrame / 44100;
        if (!double.IsFinite(driftBpmPerSec)) driftBpmPerSec = 0;
        // A drifting stream (a long DJ mix) is planned on its integrated beat clock, not a straight grid.
        var clock = new DriftClock(origin, tempo.Bpm, driftBpmPerSec);
        double localBpm = Math.Clamp(clock.BpmAt(outgoingSec), 60, 200);
        double beat = 60 / localBpm;
        double bar = beat * 4;
        double remaining = outgoing.DurationSec > 0 ? outgoing.DurationSec - outgoingSec : double.PositiveInfinity;
        if (remaining < beat * .35) return null;
        var (rate, halfDouble) = TempoRatio(localBpm, incoming?.Tempo?.Bpm ?? 0);
        double gap = Math.Abs(rate - 1);
        bool grids = incoming?.Tempo is { Confidence: >= .1 };
        int harmony = Harmony(ReliableKey(outgoing), ReliableKey(incoming));
        var genre = preferences.Genre == DjGenre.Auto ? outgoing.Genre : preferences.Genre;
        if (genre == DjGenre.Auto) genre = GuessGenre(localBpm, At(outgoing, outgoingSec)?.Energy ?? 6);
        var profile = Profile(genre);
        bool bassGenre = genre is DjGenre.DrumAndBass or DjGenre.Dubstep;
        bool club = genre is DjGenre.House or DjGenre.Techno or DjGenre.Trance;
        double lead = now ? Math.Min(beat, remaining * .25) : beat * 1.5;
        double earliest = outgoingSec + lead;
        double target = now ? earliest : Math.Max(earliest, outgoing.MixOutSec > 0 ? outgoing.MixOutSec : earliest);
        double phrase = clock.NextBoundary(target, 32);
        bool canReachPhrase = phrase < outgoingSec + remaining - beat * .5;
        double aligned = canReachPhrase ? phrase : clock.NextBoundary(earliest, tempo.DownKnown ? 4 : 1);
        if (aligned >= outgoingSec + remaining) aligned = clock.NextBoundary(earliest, 1);
        if (aligned >= outgoingSec + remaining) return null;
        double quietGoal = TargetEnergy(preferences.Arc, Math.Abs(seed % 9), At(outgoing, aligned)?.Energy ?? 5);
        var entries = Entries(incoming, incomingSec, canCueIncoming).ToArray();
        var candidates = new List<(double Score, TransitionPlan Plan)>();

        foreach (var (entry, section) in entries)
        foreach (var style in Styles)
        {
            if (style == skip) continue;
            bool drop = section == DjSection.Drop;
            bool canDoubleDrop = bassGenre && drop && gap <= .02 && harmony >= 2;
            var outBuild = outgoing.Segments.FirstOrDefault(s => s.Kind == DjSection.Build && s.EndSec >= earliest &&
                Math.Abs(s.EndSec - clock.NextBoundary(s.EndSec - beat * .1, 32)) <= beat * .35);
            if (style == TransitionStyle.BassSwap && (!club || now || gap > .06 || harmony < 1 || !grids)) continue;
            if (style == TransitionStyle.FilterBlend && (!grids || gap > .065 || harmony < 0)) continue;
            if (style == TransitionStyle.TempoRamp && (!grids || !club || now || gap <= .06 || gap > .12 || harmony < 0)) continue;
            if (style == TransitionStyle.DoubleDrop && (!canDoubleDrop || outBuild == null || now)) continue;
            if (style == TransitionStyle.DropSwap && (!drop || outBuild == null)) continue;
            if (style == TransitionStyle.BreakdownEntry && (!grids || gap > .06 || harmony < 0 || At(outgoing, aligned)?.Kind != DjSection.Breakdown)) continue;
            if (style == TransitionStyle.LoopRoll && (!grids || gap > .06 || harmony < 0)) continue;
            if (style == TransitionStyle.PowerDown && (!preferences.Dramatic || (gap < .12 && harmony >= 0) ||
                Roll(seed) >= Math.Min(1, profile.DramaticRate * (preferences.Fx == FxIntensity.Wild ? 8 : 4)))) continue;

            double landing = style is TransitionStyle.DoubleDrop or TransitionStyle.DropSwap ? outBuild!.EndSec : aligned;
            // Echo and cut effects start before the landing downbeat. The transition handover is on that downbeat.
            bool cut = style is TransitionStyle.EchoOut or TransitionStyle.DropSwap or TransitionStyle.PowerDown;
            double start = cut ? Math.Max(outgoingSec, landing - beat) : landing;
            double pre = landing - start;
            int bars = BarsFor(style, preferences.Length, now, profile);
            double duration = cut ? pre + (style == TransitionStyle.EchoOut ? .75 * beat : .1 * beat) : bars * bar;
            double outgoingLimit = outgoing.DurationSec > 0 ? outgoing.DurationSec - start : double.PositiveInfinity;
            double incomingLimit = incoming is { DurationSec: > 0 } ? (incoming.DurationSec - entry) / Math.Max(rate, .8) : double.PositiveInfinity;
            int fit = (int)Math.Min(32, Math.Floor(Math.Min(outgoingLimit, incomingLimit) / bar));
            if (!cut && fit < bars)
            {
                int min = style == TransitionStyle.BassSwap ? 16 : style == TransitionStyle.TempoRamp ? 8 : style == TransitionStyle.DoubleDrop ? 4 : 1;
                if (fit < min) continue;
                bars = new[] { 32, 16, 8, 4, 2, 1 }.First(b => b <= fit);
                duration = bar * bars;
            }
            if (cut && (outgoingLimit < pre || incomingLimit < beat)) continue;
            bool vocals = OverlapVocals(outgoing, incoming, cut ? landing - beat : start, entry, cut ? beat * 2 : duration, rate);
            if (vocals && !cut) continue;
            if (!canCueIncoming && entry - incomingSec > .12 && cut) continue;

            var outSection = At(outgoing, landing);
            var inSection = At(incoming, entry);
            double score = Score(style, genre, harmony, gap, grids, outSection, inSection, drop, now, preferences) + profile.WeightOf(style);
            score -= (Math.Abs((inSection?.Energy ?? quietGoal) - quietGoal)) * 1.2;
            if (section == DjSection.Intro && style is not (TransitionStyle.DoubleDrop or TransitionStyle.DropSwap)) score += 3;
            if (vocals) score -= 4;
            if (!canReachPhrase) score -= 4;
            if (skip != null) score += (int)style / 100.0;
            double depth = Math.Clamp(profile.FxScale * preferences.Fx switch { FxIntensity.Subtle => .48, FxIntensity.Wild => .92, _ => .69 }, .2, 1);
            string key = outgoing.Key is { } a && incoming?.Key is { } b ? $"{a.Camelot}→{b.Camelot}" : "key unconfirmed";
            string speed = grids ? $"{(incoming!.Tempo!.Bpm * (halfDouble ? localBpm < incoming.Tempo.Bpm ? .5 : 2 : 1) - localBpm):+0.0;-0.0;0} BPM" : "no incoming beat grid";
            if (halfDouble) speed += " · half/double time";
            string choice = vocals ? "separates vocals" : style switch
            {
                TransitionStyle.BassSwap => $"bass swap at bar {BarNumber(landing + duration / 2, clock)}",
                TransitionStyle.EchoOut when harmony < 0 => "keys clash",
                TransitionStyle.EchoOut when gap > .12 => "tempo gap",
                TransitionStyle.DropSwap or TransitionStyle.DoubleDrop => "drop lands on the phrase",
                TransitionStyle.BreakdownEntry => "space in the breakdown",
                TransitionStyle.TempoRamp => "gradual speed match",
                _ => canReachPhrase ? "phrase downbeat" : "next available downbeat"
            };
            double landingBeat = 60 / Math.Clamp(clock.BpmAt(landing), 60, 200);
            var (landingRate, _) = TempoRatio(60 / landingBeat, incoming?.Tempo?.Bpm ?? 0);
            candidates.Add((score, new TransitionPlan(style, start, landing, entry, duration, landingBeat, cut ? 1 : landingRate,
                halfDouble, BarNumber(landing, clock), bars, $"{key} · {speed} · {choice}",
                Gain(outSection), Gain(inSection), depth, vocals, driftBpmPerSec, genre)));
        }

        if (candidates.Count == 0) return null;
        double bestScore = candidates.Max(c => c.Score);
        return candidates.Where(c => c.Score >= bestScore - 2.5)
            .OrderByDescending(c => c.Score + Variety(seed, c.Plan.Style)).First().Plan;
    }

    private static int BarsFor(TransitionStyle style, LengthBias length, bool now, GenreProfile profile)
    {
        int Bias(int bars, int min) => Math.Clamp(length == LengthBias.Long ? bars * 2 : length == LengthBias.Short ? bars / 2 : bars, min, 32);
        return style switch
        {
            TransitionStyle.BassSwap => Bias(profile.SwapBars, 16),
            TransitionStyle.FilterBlend => now ? 1 : Bias(profile.BlendBars, 4),
            TransitionStyle.TempoRamp => now ? 1 : Bias(8, 8),
            TransitionStyle.BreakdownEntry => now ? 1 : Bias(profile.BlendBars, 4),
            TransitionStyle.DoubleDrop => length == LengthBias.Long ? 16 : 8,
            TransitionStyle.LoopRoll => now ? 1 : length == LengthBias.Long ? 4 : 2,
            _ => 0
        };
    }

    private static double Score(TransitionStyle style, DjGenre genre, int harmony, double gap, bool grids,
        TrackSegment? outgoing, TrackSegment? incoming, bool drop, bool now, MixPreferences pref)
    {
        // Genre preferences come from the profile (added by the caller); these are the musical facts.
        double baseScore = style switch
        {
            TransitionStyle.BassSwap => 31 + harmony * 2,
            TransitionStyle.FilterBlend => 22 + harmony,
            TransitionStyle.EchoOut => 15 + (harmony < 0 ? 22 : 0) + (gap > .12 ? 17 : 0) + (!grids ? 12 : 0),
            TransitionStyle.DropSwap => drop ? 44 : 0,
            TransitionStyle.DoubleDrop => 85 + harmony,
            TransitionStyle.LoopRoll => 21 + (pref.Fx == FxIntensity.Wild ? 7 : 0),
            TransitionStyle.TempoRamp => 49,
            TransitionStyle.PowerDown => 26 + (pref.Fx == FxIntensity.Wild ? 10 : 0) + (gap > .23 ? 8 : 0),
            TransitionStyle.BreakdownEntry => 48,
            _ => 0
        };
        if (now && style is TransitionStyle.EchoOut or TransitionStyle.DropSwap) baseScore += 8;
        if (outgoing?.Kind == DjSection.Outro && incoming?.Kind == DjSection.Intro && style == TransitionStyle.BassSwap) baseScore += 5;
        return baseScore;
    }

    public static bool OverlapVocals(TrackAnalysis? outgoing, TrackAnalysis? incoming, double startOut,
        double startIn, double duration, double rate)
    {
        if (outgoing == null || incoming == null) return false;
        foreach (var a in outgoing.Segments)
        {
            if (a.VocalLikelihood < .58) continue;
            double first = Math.Max(0, a.StartSec - startOut), last = Math.Min(duration, a.EndSec - startOut);
            foreach (var b in incoming.Segments)
            {
                if (b.VocalLikelihood >= .58 && Math.Min(last, (b.EndSec - startIn) / rate) >
                    Math.Max(first, (b.StartSec - startIn) / rate)) return true;
            }
        }
        return false;
    }

    private static IEnumerable<(double Sec, DjSection Kind)> Entries(TrackAnalysis? track, double current, bool canCue)
    {
        if (!canCue || track == null)
        {
            yield return (Math.Max(0, current), At(track, current)?.Kind ?? DjSection.Intro);
            yield break;
        }
        yield return (track.MixInSec, At(track, track.MixInSec)?.Kind ?? DjSection.Intro);
        foreach (var s in track.Segments.Where(s => s.Kind == DjSection.Drop || (s.Kind == DjSection.Intro && s.VocalLikelihood < .35)).Take(5))
            if (Math.Abs(s.StartSec - track.MixInSec) > .08) yield return (s.StartSec, s.Kind);
    }

    private static double Gain(TrackSegment? section) => section is { LoudnessLufs: > -40 and < 0 }
        ? Math.Pow(10, Math.Clamp((-14 - section.LoudnessLufs) / 20, -.45, .35)) : 1;
    private static MusicalKey? ReliableKey(TrackAnalysis? track) => track is { KeyConfidence: >= .18 } ? track.Key : null;
    private static TrackSegment? At(TrackAnalysis? track, double seconds) =>
        track?.Segments.FirstOrDefault(s => seconds >= s.StartSec && seconds < s.EndSec);
    private static int BarNumber(double t, DriftClock clock) => Math.Max(1, (int)Math.Round(clock.BeatsAt(t) / 4) + 1);
    private static double Roll(int seed)
    {
        uint hash = unchecked((uint)seed * 2654435761u);
        hash ^= hash >> 15;
        return (hash & 1023) / 1024d;
    }
    private static double Variety(int seed, TransitionStyle style)
    {
        uint hash = unchecked((uint)(seed * 747796405 + ((int)style + 1) * 2891336453L));
        hash ^= hash >> 16;
        return (hash & 255) / 255d * 1.8;
    }
}
