namespace MixDock.Audio;

/// <summary>
/// UI-facing facade over <see cref="SyncController"/>: SYNC toggles continuous beat sync (the other deck is the master),
/// and the heard tempo / phase helpers the tempo strip, quantize and waveforms use.
/// </summary>
public static class DeckSync
{
    private static readonly double[] Nudge = { 0, 0 };

    /// <summary>Tempo actually heard from this deck.</summary>
    public static double HeardBpm(AudioEngine e, int deck) => e.Sync.HeardBpm(deck);

    /// <summary>Beat phase of what is heard (0 = on the beat), NaN if unknown.</summary>
    public static double HeardPhase(AudioEngine e, int deck) => e.Sync.HeardPhase(deck);

    public static string Name(int d) => d == 0 ? "A" : "B";

    /// <summary>Toggle sync for this deck: on = it follows the other deck; pressing again (either deck) switches it off.</summary>
    public static string Sync(AudioEngine e, int deck)
    {
        var c = e.Sync;
        if (c.Role(deck) != SyncRole.Off) { c.Disable(); return "SYNC off."; }
        if (e.DeckA.ActivePid is null || e.DeckB.ActivePid is null) return "SYNC needs an app or song on both decks.";
        c.Enable(deck);
        var gs = (deck == 0 ? e.DeckA : e.DeckB).Bpm.Grid; var gm = (deck == 0 ? e.DeckB : e.DeckA).Bpm.Grid;
        if (gs == null || gm == null) return "SYNC armed: it locks as soon as MixDock hears the beat on both decks.";
        return $"SYNC: Deck {Name(deck)} ({gs.Bpm:F2} BPM) follows Deck {Name(1 - deck)} ({gm.Bpm:F2} BPM).";
    }

    /// <summary>AutoMix hand-off: 0 = keep the synced tempo, 1 = back at native speed. The incoming deck leaves sync and eases to its own tempo once.</summary>
    public static void Glide(AudioEngine e, int deck, double toward1)
    {
        if (toward1 > 0 && e.Sync.Role(deck) != SyncRole.Off) e.Sync.HandOver(deck, 4);
    }

    /// <summary>Source on this deck changed: leave history mode and normal speed.</summary>
    public static void Reset(AudioEngine e, int deck) { e.Sync.Reset(deck); Nudge[deck] = 0; }

    /// <summary>Manual speed nudge in percent (+-8). Only deck without sync and on the plain path.</summary>
    public static void SetNudge(AudioEngine e, int deck, double percent)
    {
        Nudge[deck] = Math.Clamp(percent, -8, 8);
        var rd = deck == 0 ? e.ReaderA : e.ReaderB;
        if (!rd.HistoryMode) rd.Rate = 1 + Nudge[deck] / 100.0;
    }
}
