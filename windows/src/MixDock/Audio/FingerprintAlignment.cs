namespace MixDock.Audio;

/// <summary>Landmark index for one cached edit. Binary search avoids allocating one object per hash.</summary>
internal sealed class FingerprintAlignment
{
    private const int TicksPerBlock = (int)AcousticFingerprint.TicksPerSecond;
    private const int MaxReferencesPerHash = 96;
    private const int MaxQueryBlocks = 30;
    private const int MinConsistentBlocks = 5;
    private const int ToleranceTicks = 1;
    private readonly FingerprintPoint[] _ordered;

    internal FingerprintAlignment(FingerprintPoint[] points)
    {
        _ordered = points.ToArray();
        Array.Sort(_ordered, static (a, b) => a.Hash != b.Hash ? a.Hash.CompareTo(b.Hash) : a.Tick.CompareTo(b.Tick));
    }

    internal readonly record struct Hit(int OffsetTicks, double Confidence, double Coverage);

    internal Hit? Find(ReadOnlySpan<FingerprintPoint> query, int? nearOffset = null)
    {
        if (query.Length < 50 || _ordered.Length < 50) return null;
        int start = int.MaxValue, end = 0;
        foreach (var point in query)
        {
            if (point.Tick < 0) continue;
            start = Math.Min(start, point.Tick);
            end = Math.Max(end, point.Tick);
        }
        if (end - start < (MinConsistentBlocks - 1) * TicksPerBlock) return null;
        start = Math.Max(start, end - MaxQueryBlocks * TicksPerBlock + 1);
        int blockCount = (end - start) / TicksPerBlock + 1;
        if (nearOffset is int offset) return Refine(query, start, blockCount, offset, requireRecent: true);

        var votes = new Dictionary<int, double>();
        foreach (var point in query)
        {
            if (point.Tick < start) continue;
            var (first, last) = FindHash(point.Hash);
            int frequency = last - first;
            if (frequency == 0 || frequency > MaxReferencesPerHash) continue;
            double weight = 1 / Math.Sqrt(frequency);
            for (int i = first; i < last; i++)
            {
                int delta = _ordered[i].Tick - point.Tick;
                votes[delta] = votes.GetValueOrDefault(delta) + weight;
            }
        }

        if (votes.Count == 0) return null;
        double Vote(int delta) => votes.GetValueOrDefault(delta) + .5 *
            (votes.GetValueOrDefault(delta - 1) + votes.GetValueOrDefault(delta + 1));
        int[] candidates = votes.Keys.OrderByDescending(Vote).Take(12).ToArray();
        Hit? best = null;
        double strength = 0;
        foreach (int candidate in candidates)
        {
            if (best is { } prev && Math.Abs((long)prev.OffsetTicks - candidate) <= 2) continue;
            Hit? hit = Refine(query, start, blockCount, candidate, requireRecent: false);
            if (hit == null) continue;
            double score = hit.Value.Confidence * (.6 + .4 * hit.Value.Coverage);
            if (best is { } chosen && score >= strength * .96 && score <= strength * 1.04 &&
                Math.Abs((long)chosen.OffsetTicks - hit.Value.OffsetTicks) >= 5)
                return null; // Repeating audio can identify a song without locating its playhead.
            if (score <= strength) continue;
            best = hit;
            strength = score;
        }
        return best;
    }

    private Hit? Refine(ReadOnlySpan<FingerprintPoint> query, int start, int blockCount, int offset, bool requireRecent)
    {
        Span<int> possible = stackalloc int[blockCount];
        Span<int> matches = stackalloc int[blockCount];
        Span<int> errors = stackalloc int[3];
        possible.Clear();
        matches.Clear();
        errors.Clear();

        int distinctTicks = 0, lastTick = -1;
        foreach (var point in query)
        {
            if (point.Tick < start) continue;
            int block = (point.Tick - start) / TicksPerBlock;
            possible[block]++;
            var (first, last) = FindHash(point.Hash);
            if (last == first) continue;
            long target = (long)point.Tick + offset;
            int matchIndex = LowerBoundTick(first, last, target - ToleranceTicks);
            if (matchIndex >= last || _ordered[matchIndex].Tick > target + ToleranceTicks) continue;
            if (matchIndex + 1 < last && _ordered[matchIndex + 1].Tick == target) matchIndex++;
            matches[block]++;
            errors[(int)(_ordered[matchIndex].Tick - target) + ToleranceTicks]++;
            if (point.Tick != lastTick)
            {
                distinctTicks++;
                lastTick = point.Tick;
            }
        }

        int longest = 0, longestHits = 0, longestPossible = 0, lastGood = -1;
        int run = 0, runHits = 0, runPossible = 0, allHits = 0, allPossible = 0;
        for (int i = 0; i < blockCount; i++)
        {
            int count = possible[i], hits = matches[i];
            allHits += hits;
            allPossible += count;
            if (hits < Math.Max(9, count * .42))
            {
                run = runHits = runPossible = 0;
                continue;
            }
            run++;
            runHits += hits;
            runPossible += count;
            lastGood = i;
            if (run > longest || (run == longest && runHits > longestHits))
            {
                longest = run;
                longestHits = runHits;
                longestPossible = runPossible;
            }
        }
        if (distinctTicks < 40 || longest < MinConsistentBlocks || longestHits < 65) return null;
        double coverage = (double)allHits / allPossible;
        if (requireRecent && (coverage < .58 || lastGood < blockCount - 2)) return null;
        // Prefer the central error on broad clusters of repeat votes. In both directions one
        // neighbouring tick is still accepted to account for sub-hop capture start offsets.
        int shift = errors[0] > errors[1] + errors[2] / 2 ? -1
            : errors[2] > errors[1] + errors[0] / 2 ? 1 : 0;
        return new Hit(offset + shift, (double)longestHits / longestPossible, coverage);
    }

    private (int First, int Last) FindHash(uint hash) => (LowerBoundHash(hash), LowerBoundHash((ulong)hash + 1));

    private int LowerBoundHash(ulong hash)
    {
        int left = 0, right = _ordered.Length;
        while (left < right)
        {
            int mid = left + (right - left) / 2;
            if (_ordered[mid].Hash < hash) left = mid + 1;
            else right = mid;
        }
        return left;
    }

    private int LowerBoundTick(int left, int right, long tick)
    {
        while (left < right)
        {
            int mid = left + (right - left) / 2;
            if (_ordered[mid].Tick < tick) left = mid + 1;
            else right = mid;
        }
        return left;
    }
}
